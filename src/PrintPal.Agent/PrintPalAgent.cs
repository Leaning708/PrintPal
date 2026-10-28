using PrintPal.Capture;
using PrintPal.Core;
using PrintPal.Forwarding;
using PrintPal.Ipp;
using PrintPal.Storage;

namespace PrintPal.Agent;

/// <summary>
/// Hosts the IPP virtual printer, the capture pipeline, the forwarding worker
/// and SQLite. The WPF app hosts this in P3/P4.
/// </summary>
public sealed class PrintPalAgent : IPrintPalBackend, IAsyncDisposable
{
    private readonly AgentOptions _options;
    private PrintPalDatabase? _database;
    private DocumentStore? _documentStore;
    private ForwardingWorker? _forwarding;
    private PrintJobPipeline? _pipeline;
    private IppServer? _server;
    private CancellationTokenSource? _scannerCts;
    private Task? _scannerTask;

    public PrintPalAgent(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AgentOptions Options => _options;

    public PrintPalDatabase Database =>
        _database ?? throw new InvalidOperationException("The agent has not been started.");

    public string IppUri => _server?.IppUri ??
                            $"ipp://{_options.AdvertiseHost}:{_options.IppPort}" +
                            IppServerOptions.NormalizePath(_options.ResourcePath);

    public string HttpUri => _server?.HttpUri ??
                             $"http://{_options.AdvertiseHost}:{_options.IppPort}" +
                             IppServerOptions.NormalizePath(_options.ResourcePath);

    public bool IsRunning => _server is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("The agent is already running.");
        }

        Directory.CreateDirectory(_options.DataDirectory);
        Directory.CreateDirectory(_options.LogDirectory);

        _database = new PrintPalDatabase(_options.DatabasePath);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _documentStore = new DocumentStore(_options.DocumentDirectory);

        await SeedAsync(cancellationToken).ConfigureAwait(false);

        _forwarding = new ForwardingWorker(
            _database,
            _documentStore,
            new PrintJobForwarder(),
            _options);
        await _forwarding.StartAsync(cancellationToken).ConfigureAwait(false);

        _pipeline = new PrintJobPipeline(
            _database,
            _documentStore,
            _forwarding,
            _options);

        _scannerCts = new CancellationTokenSource();
        _scannerTask = Task.Run(() => ScanPendingAsync(_scannerCts.Token), CancellationToken.None);

        var uuid = await _database.GetSettingAsync("printerUuid", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uuid))
        {
            uuid = Guid.NewGuid().ToString("D");
            await _database.SetSettingAsync("printerUuid", uuid, cancellationToken).ConfigureAwait(false);
        }

        _server = new IppServer(
            new IppServerOptions
            {
                Name = _options.PrinterName,
                MakeAndModel = "PrintPal Virtual Printer",
                HostName = _options.AdvertiseHost,
                Port = _options.IppPort,
                ResourcePath = _options.ResourcePath,
                BindAddress = _options.ResolveBindAddress(),
                Capabilities = PrinterCapabilities.CreateDefault(_options.PrinterName),
                MaxDocumentBytes = _options.MaxDocumentBytes,
                PrinterLocation = "Local",
                PrinterUuid = uuid,
            },
            new PrintPalIppJobHandler(_pipeline));

        await _server.StartAsync(cancellationToken).ConfigureAwait(false);

        // Jobs that were priced but not yet forwarded before the previous shutdown
        // are resumed. Held/Unknown/Failed jobs need an explicit user action.
        await ResumePendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enqueue an existing job for forwarding, optionally overriding the target.
    /// Used by the UI's "retry" / "choose target" actions.
    /// </summary>
    public async Task EnqueueForwardAsync(
        Guid jobId,
        Guid? targetPrinterId = null,
        CancellationToken cancellationToken = default)
    {
        var forwarding = _forwarding ??
                         throw new InvalidOperationException("The agent has not been started.");

        var job = await PrintJobActions.PrepareManualForwardAsync(
            Database,
            jobId,
            targetPrinterId,
            cancellationToken).ConfigureAwait(false);

        forwarding.Enqueue(job.Id);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var scannerCts = _scannerCts;
        var scannerTask = _scannerTask;
        _scannerCts = null;
        _scannerTask = null;
        scannerCts?.Cancel();

        if (scannerTask is not null)
        {
            try
            {
                await scannerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        scannerCts?.Dispose();

        var server = _server;
        _server = null;

        try
        {
            if (server is not null)
            {
                await server.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            var forwarding = _forwarding;
            _forwarding = null;
            _pipeline = null;

            if (forwarding is not null)
            {
                await forwarding.StopAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task ScanPendingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var database = _database;
                var forwarding = _forwarding;
                if (database is not null && forwarding is not null)
                {
                    var jobs = await database.GetJobsAsync(500, cancellationToken).ConfigureAwait(false);
                    foreach (var job in jobs.Where(job =>
                                 job.Status == JobStatus.Priced &&
                                 job.TargetPrinterId is not null))
                    {
                        forwarding.Enqueue(job.Id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] pending scanner: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ResumePendingAsync(CancellationToken cancellationToken)
    {
        if (_forwarding is null)
        {
            return;
        }

        var database = _database!;
        var jobs = await database.GetJobsAsync(1000, cancellationToken).ConfigureAwait(false);

        // A crash/force-kill can leave a job in Forwarding. Its outcome is unknown;
        // never auto-retry it as if nothing happened.
        foreach (var interrupted in jobs.Where(job => job.Status == JobStatus.Forwarding))
        {
            interrupted.Status = JobStatus.Unknown;
            interrupted.StatusReason = "上次转发中断，结果未知；请人工确认后再重试";
            await database.SaveJobAsync(interrupted, cancellationToken).ConfigureAwait(false);
        }

        var pending = jobs
            .Where(job => job.Status == JobStatus.Priced && job.TargetPrinterId is not null)
            .ToList();

        foreach (var job in pending)
        {
            _forwarding.Enqueue(job.Id);
        }

        if (pending.Count > 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] resumed {pending.Count} pending forward job(s)");
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var database = _database!;

        var priceRules = await database.GetPriceRulesAsync(cancellationToken).ConfigureAwait(false);
        if (priceRules.Count == 0)
        {
            foreach (var rule in DefaultPriceRules.Create())
            {
                await database.SavePriceRuleAsync(rule, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!_options.SeedDemoPrinters)
        {
            return;
        }

        var printers = await database.GetPrintersAsync(cancellationToken).ConfigureAwait(false);
        if (printers.Count == 0)
        {
            var color = CreateIppPrinter("Fake-A4-Color", 8631);
            var mono = CreateIppPrinter("Fake-A4-Mono", 8632);
            mono.IsDefault = true;
            var a3 = CreateIppPrinter("Fake-A3", 8633);

            foreach (var printer in new[] { color, mono, a3 })
            {
                await database.SavePrinterAsync(printer, cancellationToken).ConfigureAwait(false);
            }

            printers = new List<Printer> { color, mono, a3 };
        }

        var routingRules = await database.GetRoutingRulesAsync(cancellationToken).ConfigureAwait(false);
        if (routingRules.Count > 0)
        {
            return;
        }

        var colorPrinter = printers.FirstOrDefault(p => p.Name == "Fake-A4-Color");
        var monoPrinter = printers.FirstOrDefault(p => p.Name == "Fake-A4-Mono");
        var a3Printer = printers.FirstOrDefault(p => p.Name == "Fake-A3");

        if (colorPrinter is not null)
        {
            await database.SaveRoutingRuleAsync(new RoutingRule
            {
                Name = "A4 彩色",
                Priority = 10,
                MatchPaperSizes = { "A4" },
                MatchColorModes = { ColorMode.Color },
                TargetPrinterId = colorPrinter.Id,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (monoPrinter is not null)
        {
            await database.SaveRoutingRuleAsync(new RoutingRule
            {
                Name = "A4 黑白",
                Priority = 20,
                MatchPaperSizes = { "A4" },
                MatchColorModes = { ColorMode.Monochrome },
                TargetPrinterId = monoPrinter.Id,
            }, cancellationToken).ConfigureAwait(false);
        }

        if (a3Printer is not null)
        {
            await database.SaveRoutingRuleAsync(new RoutingRule
            {
                Name = "A3",
                Priority = 30,
                MatchPaperSizes = { "A3" },
                TargetPrinterId = a3Printer.Id,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Printer CreateIppPrinter(string name, int port) => new()
    {
        Name = name,
        Protocol = PrinterProtocol.Ipp,
        Host = "127.0.0.1",
        Port = port,
        ResourcePath = "/ipp/print",
        Enabled = true,
        Capabilities = PrinterCapabilities.CreateDefault(name),
    };
}

public sealed class PrintPalIppJobHandler : IIppJobHandler
{
    private readonly PrintJobPipeline _pipeline;

    public PrintPalIppJobHandler(PrintJobPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    public async Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken)
    {
        await _pipeline.ProcessAsync(context, cancellationToken).ConfigureAwait(false);
        return IppJobResult.Success(IppJobState.Completed);
    }
}
