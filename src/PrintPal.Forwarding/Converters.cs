using PrintPal.Core;
using PrintPal.Ipp;

namespace PrintPal.Forwarding;

public sealed record ForwardResult(bool Success, string? Message, IppJobResult? IppResult = null)
{
    public static ForwardResult Ok(IppJobResult? result = null) => new(true, null, result);

    public static ForwardResult Fail(string message) => new(false, message);
}

public interface IForwarder
{
    PrinterProtocol Protocol { get; }

    Task<ForwardResult> ForwardAsync(
        PrintJob job,
        Printer printer,
        byte[] document,
        CancellationToken cancellationToken = default);
}

public sealed class IppForwarder : IForwarder
{
    private readonly Func<IppClient> _clientFactory;

    public IppForwarder(Func<IppClient>? clientFactory = null)
    {
        _clientFactory = clientFactory ?? (() => new IppClient());
    }

    public PrinterProtocol Protocol => PrinterProtocol.Ipp;

    public async Task<ForwardResult> ForwardAsync(
        PrintJob job,
        Printer printer,
        byte[] document,
        CancellationToken cancellationToken = default)
    {
        if (document.Length == 0)
        {
            return ForwardResult.Fail("Document is empty.");
        }

        if (job.DocumentFormat == DocumentFormat.Unknown)
        {
            return ForwardResult.Fail("Document format is unknown.");
        }

        try
        {
            using var client = _clientFactory();

            var baseName = string.IsNullOrWhiteSpace(job.DocumentName) ? "PrintPal job" : job.DocumentName;
            var idempotentName = $"{baseName} [PrintPal:{job.Id:N}]";

            var request = new IppPrintJobRequest(
                new Uri(printer.Uri),
                idempotentName,
                string.IsNullOrWhiteSpace(job.UserName) ? "PrintPal" : job.UserName,
                job.DocumentFormat,
                document,
                Copies: Math.Max(1, job.Copies),
                PaperSize: job.PaperSize,
                ColorMode: job.ColorMode,
                DuplexMode: job.DuplexMode);

            var result = await client.PrintJobAsync(request, cancellationToken).ConfigureAwait(false);
            return ForwardResult.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ForwardResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public sealed class Raw9100Forwarder : IForwarder
{
    private readonly TimeSpan _timeout;

    public Raw9100Forwarder(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public PrinterProtocol Protocol => PrinterProtocol.Raw;

    public async Task<ForwardResult> ForwardAsync(
        PrintJob job,
        Printer printer,
        byte[] document,
        CancellationToken cancellationToken = default)
    {
        if (document.Length == 0)
        {
            return ForwardResult.Fail("Document is empty.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(printer.Host, printer.Port, timeout.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            await stream.WriteAsync(document, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            return ForwardResult.Ok();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                  !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Raw forward timed out.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ForwardResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public sealed class PrintJobForwarder
{
    private readonly IppForwarder _ipp;
    private readonly Raw9100Forwarder _raw;

    public PrintJobForwarder(IppForwarder? ipp = null, Raw9100Forwarder? raw = null)
    {
        _ipp = ipp ?? new IppForwarder();
        _raw = raw ?? new Raw9100Forwarder();
    }

    public Task<ForwardResult> ForwardAsync(
        PrintJob job,
        Printer printer,
        byte[] document,
        CancellationToken cancellationToken = default)
    {
        return printer.Protocol switch
        {
            PrinterProtocol.Ipp => _ipp.ForwardAsync(job, printer, document, cancellationToken),
            _ => _raw.ForwardAsync(job, printer, document, cancellationToken),
        };
    }
}
