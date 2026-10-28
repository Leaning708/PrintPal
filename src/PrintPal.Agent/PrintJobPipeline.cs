using System.Globalization;
using System.Text.Json;
using PrintPal.Capture;
using PrintPal.Core;
using PrintPal.Ipp;
using PrintPal.Storage;

namespace PrintPal.Agent;

/// <summary>
/// The capture → price → route → queue pipeline. Forwarding itself runs in
/// <see cref="ForwardingWorker"/> so the IPP request returns without waiting for
/// the target printer or retry delays.
/// </summary>
public sealed class PrintJobPipeline
{
    private readonly PrintPalDatabase _database;
    private readonly DocumentStore _documentStore;
    private readonly ForwardingWorker _forwarding;
    private readonly AgentOptions _options;

    public PrintJobPipeline(
        PrintPalDatabase database,
        DocumentStore documentStore,
        ForwardingWorker forwarding,
        AgentOptions options)
    {
        _database = database;
        _documentStore = documentStore;
        _forwarding = forwarding;
        _options = options;
    }

    public async Task<PrintJob> ProcessAsync(IppJobContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stored = await _documentStore.SaveAsync(context.Document, context.Format, cancellationToken)
            .ConfigureAwait(false);

        var estimated = false;

        var paper = context.GetPaperSize();
        if (!paper.IsKnown)
        {
            paper = PaperSize.A4;
            estimated = true;
        }

        var color = context.GetColorMode();
        if (color == ColorMode.Unknown)
        {
            color = ColorMode.Monochrome;
            estimated = true;
        }

        var duplex = context.GetDuplexMode();
        if (duplex == DuplexMode.Unknown)
        {
            duplex = DuplexMode.Simplex;
            estimated = true;
        }

        if (!context.Attributes.ContainsKey("copies") ||
            (!context.Attributes.ContainsKey("media") && context.GetCollection("media-col") is null) ||
            !context.Attributes.ContainsKey("sides") ||
            !context.Attributes.ContainsKey("print-color-mode"))
        {
            estimated = true;
        }

        var job = new PrintJob
        {
            DocumentName = string.IsNullOrWhiteSpace(context.JobName) ? "PrintPal job" : context.JobName!,
            UserName = context.UserName ?? string.Empty,
            VirtualPrinterName = _options.PrinterName,
            Backend = "Ipp",
            BackendJobId = context.JobId.ToString(CultureInfo.InvariantCulture),
            DocumentFormat = stored.Format,
            DocumentPath = stored.Path,
            DocumentSize = stored.Size,
            DocumentSha256 = stored.Sha256,
            PaperSize = paper,
            ColorMode = color,
            DuplexMode = duplex,
            Copies = context.GetCopies(),
            PageCount = stored.PageCount.Pages,
            IsEstimated = estimated,
            RawAttributesJson = SerializeAttributes(context),
            Status = JobStatus.Captured,
        };

        if (job.PageCount <= 0)
        {
            job.Status = JobStatus.NeedsReview;
            job.StatusReason = $"无法确定页数：{stored.PageCount.Details}";
            await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
            Log($"JOB {job.Id} NeedsReview (page count unknown)");
            return job;
        }

        var priceRules = await _database.GetPriceRulesAsync(cancellationToken).ConfigureAwait(false);
        var priceRule = PriceResolver.Resolve(priceRules, job.PaperSize, job.ColorMode);

        if (priceRule is null)
        {
            job.Status = JobStatus.NeedsReview;
            job.StatusReason = "没有匹配的价格规则";
            await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
            Log($"JOB {job.Id} NeedsReview (no price rule)");
            return job;
        }

        job.ApplyQuote(PriceCalculator.Calculate(priceRule, job.PageCount, job.Copies, job.DuplexMode));

        var printers = await _database.GetPrintersAsync(cancellationToken).ConfigureAwait(false);
        var routingRules = await _database.GetRoutingRulesAsync(cancellationToken).ConfigureAwait(false);
        var defaultPrinter = printers.FirstOrDefault(printer => printer.Enabled && printer.IsDefault);

        var route = RoutingEngine.Route(job, routingRules, printers, defaultPrinter?.Id);
        if (!route.IsRouted)
        {
            job.Status = JobStatus.Unrouted;
            job.StatusReason = route.Reason;
            await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
            Log($"JOB {job.Id} Unrouted: {route.Reason}");
            return job;
        }

        job.TargetPrinterId = route.Printer!.Id;

        var autoForward = _options.AutoForward;
        var storedAutoForward = await _database.GetSettingAsync("autoForward", cancellationToken)
            .ConfigureAwait(false);
        if (bool.TryParse(storedAutoForward, out var storedEnabled))
        {
            autoForward = storedEnabled;
        }

        job.Status = autoForward ? JobStatus.Priced : JobStatus.Held;
        job.StatusReason = autoForward ? route.Reason : "等待人工确认";

        await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);

        if (autoForward)
        {
            _forwarding.Enqueue(job.Id);
        }

        Log(
            $"JOB {job.Id} {job.DocumentName} {job.PageCount}p x{job.Copies} " +
            $"{job.PaperSize.Name} {job.ColorMode} {job.DuplexMode} = {Money.FormatCents(job.AmountCents)} " +
            $"-> {route.Printer.Name} [{job.Status}]");

        return job;
    }

    private static string SerializeAttributes(IppJobContext context)
    {
        try
        {
            var values = context.Attributes.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.StringValue,
                StringComparer.OrdinalIgnoreCase);

            return JsonSerializer.Serialize(values);
        }
        catch
        {
            return "{}";
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
