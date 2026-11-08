using System.Text;
using PrintPal.Agent;

namespace PrintPal.AgentHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = new AgentOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "--help" or "-h" or "/?":
                    PrintHelp();
                    return 0;

                case "--port":
                    options.IppPort = int.Parse(RequireValue(args, ref i, arg));
                    break;

                case "--data":
                    options.DataDirectory = Path.GetFullPath(RequireValue(args, ref i, arg));
                    break;

                case "--bind":
                    options.BindAddress = RequireValue(args, ref i, arg);
                    break;

                case "--host":
                    options.AdvertiseHost = RequireValue(args, ref i, arg);
                    break;

                case "--seed-demo":
                    options.SeedDemoPrinters = true;
                    break;

                case "--no-auto-forward":
                    options.AutoForward = false;
                    break;

                default:
                    Console.Error.WriteLine($"Unknown option: {arg}");
                    PrintHelp();
                    return 2;
            }
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Redirected consoles may reject the encoding change.
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var agent = new PrintPalAgent(options);

        try
        {
            await agent.StartAsync(cancellation.Token);

            Console.WriteLine();
            Console.WriteLine("=== PrintPal Agent ===");
            Console.WriteLine($"  IPP        : {agent.IppUri}");
            Console.WriteLine($"  HTTP       : {agent.HttpUri}");
            Console.WriteLine($"  Health     : http://{options.AdvertiseHost}:{options.IppPort}/health");
            Console.WriteLine($"  Data       : {options.DataDirectory}");
            Console.WriteLine($"  AutoForward: {options.AutoForward}");
            Console.WriteLine($"  SeedDemo   : {options.SeedDemoPrinters}");
            Console.WriteLine("  Press Ctrl+C to stop.");
            Console.WriteLine();

            await Task.Delay(Timeout.Infinite, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AGENT FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            await agent.StopAsync();
        }

        Console.WriteLine("PrintPal agent stopped.");
        return 0;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[++index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            PrintPal Agent - IPP virtual printer + capture pipeline.

            Usage:
              PrintPal.AgentHost.exe [options]

            Options:
              --port <port>        IPP port (default: 8630)
              --data <dir>         Data directory (default: %LOCALAPPDATA%\PrintPal)
              --bind <ip>          Bind address (default: 127.0.0.1)
              --host <host>        Host advertised in IPP URIs (default: 127.0.0.1)
              --seed-demo          Seed three FakePrinters and routing rules
              --no-auto-forward    Capture/price/route only; do not forward
              --help               Show this text
            """);
    }
}
