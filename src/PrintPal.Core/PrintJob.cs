namespace PrintPal.Core;

/// <summary>One captured print job, including the pieces needed for billing.</summary>
public sealed class PrintJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string DocumentName { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    /// <summary>Only available from the optional PSA backend; null on the IPP path.</summary>
    public string? SourceApp { get; set; }

    public string VirtualPrinterName { get; set; } = "PrintPal Virtual Printer";

    public string Backend { get; set; } = "Ipp";

    public string? BackendJobId { get; set; }

    public DocumentFormat DocumentFormat { get; set; }

    public string? DocumentPath { get; set; }

    public long DocumentSize { get; set; }

    public string? DocumentSha256 { get; set; }

    public PaperSize PaperSize { get; set; } = PaperSize.A4;

    public ColorMode ColorMode { get; set; } = ColorMode.Monochrome;

    public DuplexMode DuplexMode { get; set; } = DuplexMode.Simplex;

    /// <summary>Copies requested by the user.</summary>
    public int Copies { get; set; } = 1;

    /// <summary>Pages per copy (printed sides). 0 means unknown.</summary>
    public int PageCount { get; set; }

    /// <summary>Total sides across all copies.</summary>
    public int TotalPages => PageCount * Copies;

    public long SimplexPriceCents { get; set; }

    public long DuplexPriceCents { get; set; }

    public int DuplexSheetsPerCopy { get; set; }

    public int SimplexSheetsPerCopy { get; set; }

    public int TotalDuplexSheets { get; set; }

    public int TotalSimplexSheets { get; set; }

    public int TotalSheets { get; set; }

    public int BillableSides => PageCount * Copies;

    /// <summary>Primary price shown in the list (duplex price for duplex jobs).</summary>
    public long UnitPriceCents { get; set; }

    public long AmountCents { get; set; }

    public string? PricingJson { get; set; }

    public Guid? TargetPrinterId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Captured;

    public string? StatusReason { get; set; }

    /// <summary>账单归属；null only for legacy rows captured before billing existed.</summary>
    public Guid? BillId { get; set; }

    /// <summary>账单名快照（归属在捕获瞬间确定，账单改名不回写历史任务）。</summary>
    public string? BillNameSnapshot { get; set; }

    public bool IsEstimated { get; set; }

    public bool IsAdjusted { get; set; }

    public bool IsDeleted { get; set; }

    public string? RawAttributesJson { get; set; }

    public DateTimeOffset? PricedAtUtc { get; set; }

    public DateTimeOffset? ForwardedAtUtc { get; set; }

    public void ApplyQuote(PriceQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        SimplexPriceCents = quote.SimplexPriceCents;
        DuplexPriceCents = quote.DuplexPriceCents;
        DuplexSheetsPerCopy = quote.DuplexSheetsPerCopy;
        SimplexSheetsPerCopy = quote.SimplexSheetsPerCopy;
        TotalDuplexSheets = quote.TotalDuplexSheets;
        TotalSimplexSheets = quote.TotalSimplexSheets;
        TotalSheets = quote.TotalSheets;
        UnitPriceCents = quote.UnitPriceCents;
        AmountCents = quote.AmountCents;
        PricingJson = quote.ToJson();
        PricedAtUtc = DateTimeOffset.UtcNow;
    }

    public override string ToString() =>
        $"{DocumentName} {PaperSize.Name} {ColorMode} {DuplexMode} {PageCount}p x{Copies} = {Money.FormatCents(AmountCents)}";
}
