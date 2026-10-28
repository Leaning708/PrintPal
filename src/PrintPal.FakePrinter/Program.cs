using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PrintPal.Capture;
using PrintPal.Core;
using PrintPal.Ipp;

namespace PrintPal.FakePrinter;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = FakePrinterOptions.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(FakePrinterOptions.HelpText);
            return 0;
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected consoles may reject the encoding change.
        }

        var outputRoot = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputRoot);

        var store = new DocumentStore(Path.Combine(outputRoot, SanitizeFileName(options.Name)));
        var handler = new FakePrinterJobHandler(options, store);
        var capabilities = BuildCapabilities(options);

        var server = new IppServer(
            new IppServerOptions
            {
                Name = options.Name,
                MakeAndModel = "PrintPal Fake Printer",
                HostName = options.AdvertiseHost,
                Port = options.IppPort,
                ResourcePath = "/ipp/print",
                BindAddress = options.BindAddress,
                Capabilities = capabilities,
                PrinterLocation = "Fake",
                PrinterUuid = Guid.NewGuid().ToString("D"),
            },
            handler);

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        RawTcpServer? rawServer = null;

        try
        {
            await server.StartAsync(cancellation.Token);

            if (options.RawPort > 0)
            {
                rawServer = new RawTcpServer(options.BindAddress, options.RawPort, store, options.Json);
                rawServer.Start();
            }

            PrintBanner(options, outputRoot);

            if (options.Once)
            {
                await Task.WhenAny(handler.FirstJob.Task, Task.Delay(Timeout.Infinite, cancellation.Token));
            }
            else
            {
                await Task.Delay(Timeout.Infinite, cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or --once.
        }
        finally
        {
            if (rawServer is not null)
            {
                await rawServer.StopAsync();
            }

            await server.StopAsync();
        }

        Console.WriteLine("Fake printer stopped.");
        return 0;
    }

    private static PrinterCapabilities BuildCapabilities(FakePrinterOptions options)
    {
        var capabilities = PrinterCapabilities.CreateDefault(options.Name);

        if (options.MediaSizes.Count > 0)
        {
            capabilities.MediaSizes = options.MediaSizes;
        }

        if (options.ColorModes.Count > 0)
        {
            capabilities.ColorModes = options.ColorModes;
        }

        if (options.DuplexModes.Count > 0)
        {
            capabilities.DuplexModes = options.DuplexModes;
        }

        capabilities.DocumentFormats = new List<DocumentFormat> { DocumentFormat.Pdf, DocumentFormat.Pwg };
        return capabilities;
    }

    private static void PrintBanner(FakePrinterOptions options, string outputRoot)
    {
        Console.WriteLine();
        Console.WriteLine("=== PrintPal Fake Printer ===");
        Console.WriteLine($"  Name    : {options.Name}");
        Console.WriteLine($"  IPP     : ipp://{options.AdvertiseHost}:{options.IppPort}/ipp/print");
        Console.WriteLine($"  HTTP    : http://{options.AdvertiseHost}:{options.IppPort}/ipp/print");
        if (options.RawPort > 0)
        {
            Console.WriteLine($"  RAW     : {options.AdvertiseHost}:{options.RawPort}");
        }
        Console.WriteLine($"  Output  : {outputRoot}");
        Console.WriteLine($"  Media   : {string.Join(", ", options.MediaSizes.Select(size => size.Name))}");
        Console.WriteLine($"  Colour  : {string.Join(", ", options.ColorModes)}");
        Console.WriteLine($"  Sides   : {string.Join(", ", options.DuplexModes)}");
        if (options.FailRate > 0 || options.Reject)
        {
            Console.WriteLine($"  Failure : reject={options.Reject} rate={options.FailRate:0.00}");
        }

        Console.WriteLine(options.Once
            ? "  Waiting for one job ..."
            : "  Press Ctrl+C to stop.");
        Console.WriteLine();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}

internal sealed class FakePrinterJobHandler : IIppJobHandler
{
    private readonly FakePrinterOptions _options;
    private readonly DocumentStore _store;
    private readonly object _consoleLock = new();

    public FakePrinterJobHandler(FakePrinterOptions options, DocumentStore store)
    {
        _options = options;
        _store = store;
    }

    public TaskCompletionSource FirstJob { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (_options.DelayMs > 0)
            {
                await Task.Delay(_options.DelayMs, cancellationToken).ConfigureAwait(false);
            }

            var fail = _options.Reject ||
                       (_options.FailRate > 0 && Random.Shared.NextDouble() < _options.FailRate);

            if (fail)
            {
                Log($"SIMULATED FAILURE job={context.JobId} printer=\"{_options.Name}\"");
                FirstJob.TrySetResult();
                return IppJobResult.Failure(IppStatus.ServerErrorDeviceError, "Simulated printer failure");
            }

            var stored = await _store.SaveAsync(context.Document, context.Format, cancellationToken)
                .ConfigureAwait(false);

            var paper = context.GetPaperSize();
            var copies = context.GetCopies();
            var sides = context.GetDuplexMode();
            var color = context.GetColorMode();

            var record = new
            {
                time = DateTimeOffset.Now,
                printer = _options.Name,
                protocol = "IPP",
                jobId = context.JobId,
                jobName = context.JobName,
                user = context.UserName,
                copies,
                sides = sides.ToString(),
                color = color.ToString(),
                paper = paper.Name,
                paperDimensions = paper.IsKnown
                    ? $"{paper.WidthHundredthMm}x{paper.HeightHundredthMm}/100mm"
                    : null,
                format = stored.Format.ToString(),
                bytes = stored.Size,
                pages = stored.PageCount.Pages,
                sha256 = stored.Sha256,
                saved = stored.Path,
                compression = context.Compression,
                result = "OK",
            };

            lock (_consoleLock)
            {
                if (_options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(record));
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RECEIVED job={context.JobId} " +
                                      $"printer=\"{_options.Name}\" proto=IPP");
                    Console.WriteLine($"  job-name        : {context.JobName ?? "-"}");
                    Console.WriteLine($"  requesting-user : {context.UserName ?? "-"}");
                    Console.WriteLine($"  copies          : {copies}");
                    Console.WriteLine($"  sides           : {sides}");
                    Console.WriteLine($"  print-color-mode: {color}");
                    Console.WriteLine($"  paper           : {paper}");
                    Console.WriteLine($"  document-format : {stored.Format.ToMimeType()} ({stored.Format})");
                    Console.WriteLine($"  bytes           : {stored.Size:N0}");
                    Console.WriteLine($"  sha256          : {stored.Sha256}");
                    Console.WriteLine($"  saved           : {stored.Path}");
                    Console.WriteLine($"  page-count      : {stored.PageCount.Pages} ({stored.PageCount.Details})");
                    Console.WriteLine("  RESULT          : OK");
                    Console.WriteLine();
                }
            }

            FirstJob.TrySetResult();
            return IppJobResult.Success();
        }
        catch (Exception ex)
        {
            Log($"JOB FAILED job={context.JobId}: {ex.GetType().Name}: {ex.Message}");
            FirstJob.TrySetResult();
            return IppJobResult.Failure(IppStatus.ServerErrorInternalError, ex.Message);
        }
    }

    private void Log(string message)
    {
        lock (_consoleLock)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
    }
}

internal sealed class RawTcpServer
{
    private readonly IPAddress _bindAddress;
    private readonly int _port;
    private readonly DocumentStore _store;
    private readonly bool _json;
    private readonly CancellationTokenSource _cancellation = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public RawTcpServer(IPAddress bindAddress, int port, DocumentStore store, bool json)
    {
        _bindAddress = bindAddress;
        _port = port;
        _store = store;
        _json = json;
    }

    public void Start()
    {
        _listener = new TcpListener(_bindAddress, _port);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public async Task StopAsync()
    {
        _cancellation.Cancel();
        _listener?.Stop();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Expected on shutdown.
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;

        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(_cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
                await using var stream = client.GetStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, _cancellation.Token).ConfigureAwait(false);

                var bytes = buffer.ToArray();
                var stored = await _store.SaveAsync(bytes, DocumentFormat.Unknown, _cancellation.Token)
                    .ConfigureAwait(false);

                if (_json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        time = DateTimeOffset.Now,
                        protocol = "RAW",
                        peer = endpoint,
                        format = stored.Format.ToString(),
                        bytes = stored.Size,
                        pages = stored.PageCount.Pages,
                        sha256 = stored.Sha256,
                        saved = stored.Path,
                        result = "OK",
                    }));
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] RECEIVED RAW from {endpoint}");
                    Console.WriteLine($"  format   : {stored.Format} ({stored.Format.ToMimeType()})");
                    Console.WriteLine($"  bytes    : {stored.Size:N0}");
                    Console.WriteLine($"  sha256   : {stored.Sha256}");
                    Console.WriteLine($"  saved    : {stored.Path}");
                    Console.WriteLine($"  page-count: {stored.PageCount.Pages} ({stored.PageCount.Details})");
                    Console.WriteLine("  RESULT   : OK");
                    Console.WriteLine();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RAW CLIENT FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

internal sealed class FakePrinterOptions
{
    public string Name { get; private set; } = "Fake Printer";

    public int IppPort { get; private set; } = 8631;

    public int RawPort { get; private set; } = 9101;

    public string AdvertiseHost { get; private set; } = "127.0.0.1";

    public IPAddress BindAddress { get; private set; } = IPAddress.Loopback;

    public string OutputDirectory { get; private set; } = "received";

    public List<PaperSize> MediaSizes { get; } = new();

    public List<ColorMode> ColorModes { get; } = new();

    public List<DuplexMode> DuplexModes { get; } = new();

    public bool Once { get; private set; }

    public bool Json { get; private set; }

    public bool Reject { get; private set; }

    public double FailRate { get; private set; }

    public int DelayMs { get; private set; }

    public bool ShowHelp { get; private set; }

    public static string HelpText =>
        """
        PrintPal Fake Printer - an IPP/Raw printer emulator for testing PrintPal.

        Usage:
          PrintPal.FakePrinter.exe [options]

        Options:
          --name <name>            Printer name (default: Fake Printer)
          --ipp-port <port>        IPP port (default: 8631; 0 disables IPP)
          --raw-port <port>        Raw 9100 port (default: 9101; 0 disables Raw)
          --host <ip>              Bind address (default: 127.0.0.1; 0.0.0.0 for all)
          --advertise-host <host>  Host advertised in printer-uri (default: 127.0.0.1)
          --output <dir>           Output directory (default: received)
          --media <list>           IPP media keywords, comma separated
                                   (default: A4, A3, A5, Letter, Legal)
          --color <list>           color,monochrome (default: both)
          --sides <list>           one-sided,two-sided-long-edge,two-sided-short-edge
          --once                   Stop after the first job
          --json                   JSON Lines output
          --fail-rate <0..1>       Random failure rate
          --reject                 Fail every job
          --delay-ms <ms>          Delay before accepting a job
          --help                   Show this text

        Examples:
          PrintPal.FakePrinter.exe --name "Fake-A4-Color" --ipp-port 8631 --raw-port 9101
          PrintPal.FakePrinter.exe --name "Fake-A3" --ipp-port 8632 --raw-port 9102 --media iso_a3_297x420mm
          PrintPal.FakePrinter.exe --name "Fake-Fail" --ipp-port 8633 --reject --once
        """;

    public static FakePrinterOptions Parse(string[] args)
    {
        var options = new FakePrinterOptions();
        var media = new List<string>();
        var colors = new List<string>();
        var sides = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    return options;

                case "--once":
                    options.Once = true;
                    break;

                case "--json":
                    options.Json = true;
                    break;

                case "--reject":
                    options.Reject = true;
                    break;

                case "--name":
                    options.Name = RequireValue(args, ref i, arg);
                    break;

                case "--ipp-port":
                    options.IppPort = ParseInt(RequireValue(args, ref i, arg), arg);
                    break;

                case "--raw-port":
                    options.RawPort = ParseInt(RequireValue(args, ref i, arg), arg);
                    break;

                case "--host":
                    {
                        var value = RequireValue(args, ref i, arg);
                        options.BindAddress = ParseBindAddress(value);
                        if (options.AdvertiseHost == "127.0.0.1")
                        {
                            options.AdvertiseHost = value.Equals("0.0.0.0", StringComparison.Ordinal)
                                ? "127.0.0.1"
                                : value;
                        }

                        break;
                    }

                case "--advertise-host":
                    options.AdvertiseHost = RequireValue(args, ref i, arg);
                    break;

                case "--output":
                    options.OutputDirectory = RequireValue(args, ref i, arg);
                    break;

                case "--media":
                    media.AddRange(RequireValue(args, ref i, arg)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--color":
                    colors.AddRange(RequireValue(args, ref i, arg)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--sides":
                    sides.AddRange(RequireValue(args, ref i, arg)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--fail-rate":
                    options.FailRate = ParseDouble(RequireValue(args, ref i, arg), arg);
                    break;

                case "--delay-ms":
                    options.DelayMs = ParseInt(RequireValue(args, ref i, arg), arg);
                    break;

                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        foreach (var keyword in media)
        {
            var paper = PaperSize.FromIppKeyword(keyword);
            if (paper.IsKnown && !options.MediaSizes.Contains(paper))
            {
                options.MediaSizes.Add(paper);
            }
        }

        if (options.MediaSizes.Count == 0)
        {
            options.MediaSizes.AddRange(PaperSize.Standards);
        }

        foreach (var keyword in colors)
        {
            var color = ColorModeExtensions.FromIppKeyword(keyword);
            if (color != ColorMode.Unknown && !options.ColorModes.Contains(color))
            {
                options.ColorModes.Add(color);
            }
        }

        if (options.ColorModes.Count == 0)
        {
            options.ColorModes.Add(ColorMode.Monochrome);
            options.ColorModes.Add(ColorMode.Color);
        }

        foreach (var keyword in sides)
        {
            var side = DuplexModeExtensions.FromIppKeyword(keyword);
            if (side != DuplexMode.Unknown && !options.DuplexModes.Contains(side))
            {
                options.DuplexModes.Add(side);
            }
        }

        if (options.DuplexModes.Count == 0)
        {
            options.DuplexModes.Add(DuplexMode.Simplex);
            options.DuplexModes.Add(DuplexMode.TwoSidedLongEdge);
            options.DuplexModes.Add(DuplexMode.TwoSidedShortEdge);
        }

        if (options.IppPort is < 1 or > 65535)
        {
            throw new ArgumentException("--ipp-port must be between 1 and 65535.");
        }

        if (options.RawPort is < 0 or > 65535)
        {
            throw new ArgumentException("--raw-port must be between 0 and 65535.");
        }

        if (options.FailRate is < 0 or > 1)
        {
            throw new ArgumentException("--fail-rate must be between 0 and 1.");
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[++index];
    }

    private static int ParseInt(string value, string option) =>
        int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {option}: {value}");

    private static double ParseDouble(string value, string option) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {option}: {value}");

    private static IPAddress ParseBindAddress(string value)
    {
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(value, out var address)
            ? address
            : throw new ArgumentException($"Invalid bind address: {value}");
    }
}
