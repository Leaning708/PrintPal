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

        var bill = await _database.EnsureActiveBillAsync(cancellationToken).ConfigureAwait(false);

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

        // 长边/短边不再作为参数：双面统一归一为长边
        duplex = duplex.Canonical();

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
            BillId = bill.Id,
            BillNameSnapshot = bill.Name,
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
            job.StatusReason = "未配置该纸张/颜色的价格";
            await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
            Log($"JOB {job.Id} NeedsReview (no price rule)");
            return job;
        }

        job.ApplyQuote(PriceCalculator.Calculate(priceRule, job.PageCount, job.Copies, job.DuplexMode));

        var printers = await _database.GetPrintersAsync(cancellationToken).ConfigureAwait(false);
        // 打印机自己声明能力：先算候选集，再由“同单亲和 + 跨账单均衡”挑一台。
        var candidates = RoutingEngine.FindCandidates(job, printers);
        if (!candidates.IsRouted)
        {
            job.Status = JobStatus.Unrouted;
            job.StatusReason = candidates.Reason;
            await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
            Log($"JOB {job.Id} Unrouted: {candidates.Reason}");
            return job;
        }

        var affinityEnabled = true;
        var storedAffinity = await _database.GetSettingAsync("billAffinity", cancellationToken)
            .ConfigureAwait(false);
        if (bool.TryParse(storedAffinity, out var storedAffinityEnabled))
        {
            affinityEnabled = storedAffinityEnabled;
        }

        var usage = await _database.GetPrinterUsageSnapshotAsync(job.BillId, cancellationToken)
            .ConfigureAwait(false);
        var choice = AffinityPolicy.Choose(candidates.Printers, usage, affinityEnabled);
        if (choice is null)
        {
            job.Status = JobStatus.Unrouted;
            job.StatusReason = "没有可用的候选打印机";
            await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
            Log($"JOB {job.Id} Unrouted: no usable candidate");
            return job;
        }

        job.TargetPrinterId = choice.Printer.Id;

        var autoForward = _options.AutoForward;
        var storedAutoForward = await _database.GetSettingAsync("autoForward", cancellationToken)
            .ConfigureAwait(false);
        if (bool.TryParse(storedAutoForward, out var storedEnabled))
        {
            autoForward = storedEnabled;
        }

        job.Status = autoForward ? JobStatus.Priced : JobStatus.Held;
        job.StatusReason = autoForward ? choice.Reason : "等待人工确认";

        await _database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);

        if (autoForward)
        {
            _forwarding.Enqueue(job.Id);
        }

        Log(
            $"JOB {job.Id} [{job.BillNameSnapshot}] {job.DocumentName} {job.PageCount}p x{job.Copies} " +
            $"{job.PaperSize.Name} {job.ColorMode} {job.DuplexMode} = {Money.FormatCents(job.AmountCents)} " +
            $"-> {choice.Printer.Name} [{job.Status}] {candidates.Reason}；{choice.Reason}");

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
