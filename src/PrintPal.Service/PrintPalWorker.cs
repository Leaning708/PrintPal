using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintPal.Agent;

namespace PrintPal.Service;

internal sealed class PrintPalWorker : BackgroundService
{
    private readonly ILogger<PrintPalWorker> _logger;
    private PrintPalAgent? _agent;
    private StreamWriter? _logWriter;

    public PrintPalWorker(ILogger<PrintPalWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PrintPal");
        var logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        var logStream = new FileStream(
            Path.Combine(logDirectory, "service.log"),
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        _logWriter = new StreamWriter(logStream) { AutoFlush = true };
        Console.SetOut(_logWriter);
        Console.SetError(_logWriter);

        _logger.LogInformation("PrintPal service starting. Data={DataDirectory}", dataDirectory);

        // The WPF UI reads/writes the shared ProgramData database and documents.
        // Grant local users Modify on the data tree (SID is locale-neutral).
        try
        {
            using var icacls = Process.Start(new ProcessStartInfo(
                "icacls.exe",
                $"\"{dataDirectory}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            icacls?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not grant users access to {DataDirectory}", dataDirectory);
        }

        var options = new AgentOptions
        {
            DataDirectory = dataDirectory,
            IppPort = 8630,
            BindAddress = "127.0.0.1",
            AdvertiseHost = "127.0.0.1",
            ResourcePath = "/ipp/print",
            AutoForward = true,
            SeedDemoPrinters = false,
        };

        _agent = new PrintPalAgent(options);

        try
        {
            await _agent.StartAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("PrintPal service agent started on {IppUri}", _agent.IppUri);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Service stopping.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service stopping during startup.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PrintPal service failed to start.");
            throw;
        }
        finally
        {
            if (_agent is not null)
            {
                try
                {
                    await _agent.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PrintPal service agent stop failed.");
                }
            }

            _logWriter?.Flush();
            _logWriter?.Dispose();
            _logWriter = null;
        }
    }
}
