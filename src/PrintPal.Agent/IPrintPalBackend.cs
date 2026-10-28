using PrintPal.Storage;

namespace PrintPal.Agent;

/// <summary>
/// Shared surface used by the WPF UI for both the in-process agent (portable)
/// and the Windows service (installed) modes.
/// </summary>
public interface IPrintPalBackend
{
    AgentOptions Options { get; }

    PrintPalDatabase Database { get; }

    string IppUri { get; }

    string HttpUri { get; }

    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task EnqueueForwardAsync(
        Guid jobId,
        Guid? targetPrinterId = null,
        CancellationToken cancellationToken = default);
}
