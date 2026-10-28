using PrintPal.Core;
using PrintPal.Ipp;
using PrintPal.Storage;

namespace PrintPal.Agent;

/// <summary>
/// UI-side backend used when the installed Windows service owns the IPP agent
/// and SQLite. The UI reads/writes the shared ProgramData database directly;
/// the service's pending-job scanner picks up Priced jobs and forwards them.
/// </summary>
public sealed class ServiceAgentProxy : IPrintPalBackend, IAsyncDisposable
{
    private readonly AgentOptions _options;
    private PrintPalDatabase? _database;

    public ServiceAgentProxy(AgentOptions options)
    {
        _options = options;
    }

    public AgentOptions Options => _options;

    public PrintPalDatabase Database =>
        _database ?? throw new InvalidOperationException("The service proxy has not been started.");

    public string IppUri =>
        $"ipp://{_options.AdvertiseHost}:{_options.IppPort}" +
        IppServerOptions.NormalizePath(_options.ResourcePath);

    public string HttpUri =>
        $"http://{_options.AdvertiseHost}:{_options.IppPort}" +
        IppServerOptions.NormalizePath(_options.ResourcePath);

    public bool IsRunning => _database is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        _database = new PrintPalDatabase(_options.DatabasePath);
        await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task EnqueueForwardAsync(
        Guid jobId,
        Guid? targetPrinterId = null,
        CancellationToken cancellationToken = default)
    {
        await PrintJobActions.PrepareManualForwardAsync(
            Database,
            jobId,
            targetPrinterId,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
