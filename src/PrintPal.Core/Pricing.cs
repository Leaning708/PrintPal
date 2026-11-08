using System.Globalization;
using System.Text.Json;

namespace PrintPal.Core;

public static class Money
{
    public static decimal ToYuan(long cents) => cents / 100m;

    public static long FromYuan(decimal yuan) => (long)Math.Round(yuan * 100m, MidpointRounding.AwayFromZero);

    public static string FormatCents(long cents) =>
        "¥" + (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
}

public sealed class PriceRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? Name { get; set; }

    /// <summary>Canonical paper name, or null/""/"*" for any paper.</summary>
    public string? PaperSize { get; set; }

    /// <summary>Null or Unknown means any colour mode.</summary>
    public ColorMode? ColorMode { get; set; }

    /// <summary>Price for one sheet printed on one side, in cents.</summary>
    public long SimplexPriceCents { get; set; }

    /// <summary>Price for one sheet printed on both sides, in cents.</summary>
    public long DuplexPriceCents { get; set; }

    public bool Enabled { get; set; } = true;

    public int Priority { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPaperSpecific =>
        !string.IsNullOrWhiteSpace(PaperSize) && PaperSize != "*";

    public bool IsColorSpecific =>
        ColorMode is not null && ColorMode != Core.ColorMode.Unknown;

    public bool Matches(PaperSize paper, ColorMode color)
    {
        if (!Enabled)
        {
            return false;
        }

        if (IsPaperSpecific &&
            !string.Equals(PaperSize, paper.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsColorSpecific && ColorMode != color)
        {
            return false;
        }

        return true;
    }
}

public sealed record PriceQuote(
    long SimplexPriceCents,
    long DuplexPriceCents,
    int DuplexSheetsPerCopy,
    int SimplexSheetsPerCopy,
    int TotalDuplexSheets,
    int TotalSimplexSheets,
    int TotalSheets,
    int BillableSides,
    long UnitPriceCents,
    long AmountCents,
    string Breakdown)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}

public static class PriceCalculator
{
    /// <summary>
    /// Calculates a quote using per-sheet prices. For duplex jobs the last sheet of
    /// an odd-paged copy is a single-sided sheet and is charged at the simplex price.
    /// The calculation is per copy and only then multiplied by copies: single-sided
    /// sheets from different copies must never be combined into one duplex sheet.
    /// </summary>
    public static PriceQuote Calculate(
        PriceRule rule,
        int pagesPerCopy,
        int copies,
        DuplexMode duplexMode)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (pagesPerCopy <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pagesPerCopy), "Pages per copy must be greater than zero.");
        }

        if (copies <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(copies), "Copies must be greater than zero.");
        }

        var isDuplex = duplexMode.IsDuplex();

        var duplexSheetsPerCopy = isDuplex ? pagesPerCopy / 2 : 0;
        var simplexSheetsPerCopy = isDuplex ? pagesPerCopy % 2 : pagesPerCopy;

        var amountPerCopy =
            (long)duplexSheetsPerCopy * rule.DuplexPriceCents +
            (long)simplexSheetsPerCopy * rule.SimplexPriceCents;

        var totalDuplexSheets = duplexSheetsPerCopy * copies;
        var totalSimplexSheets = simplexSheetsPerCopy * copies;
        var totalSheets = totalDuplexSheets + totalSimplexSheets;
        var billableSides = pagesPerCopy * copies;
        var amount = amountPerCopy * copies;
        var unitPrice = isDuplex ? rule.DuplexPriceCents : rule.SimplexPriceCents;

        var breakdown = BuildBreakdown(
            pagesPerCopy, copies, duplexMode, duplexSheetsPerCopy, simplexSheetsPerCopy,
            rule.SimplexPriceCents, rule.DuplexPriceCents, amountPerCopy, amount);

        return new PriceQuote(
            rule.SimplexPriceCents,
            rule.DuplexPriceCents,
            duplexSheetsPerCopy,
            simplexSheetsPerCopy,
            totalDuplexSheets,
            totalSimplexSheets,
            totalSheets,
            billableSides,
            unitPrice,
            amount,
            breakdown);
    }

    private static string BuildBreakdown(
        int pagesPerCopy,
        int copies,
        DuplexMode duplexMode,
        int duplexSheetsPerCopy,
        int simplexSheetsPerCopy,
        long simplexPrice,
        long duplexPrice,
        long amountPerCopy,
        long amount)
    {
        var head = $"{pagesPerCopy} 页 × {copies} 份";

        if (!duplexMode.IsDuplex())
        {
            return $"{head}，单面：{pagesPerCopy} 张单面 × {Money.FormatCents(simplexPrice)} = " +
                   $"{Money.FormatCents(amountPerCopy)}/份；合计 {Money.FormatCents(amount)}";
        }

        var parts = new List<string>();
        if (duplexSheetsPerCopy > 0)
        {
            parts.Add($"{duplexSheetsPerCopy} 张双面 × {Money.FormatCents(duplexPrice)}");
        }

        if (simplexSheetsPerCopy > 0)
        {
            parts.Add($"{simplexSheetsPerCopy} 张单面 × {Money.FormatCents(simplexPrice)}");
        }

        return $"{head}，{duplexMode.DisplayName()}：每份 " + string.Join(" + ", parts) +
               $" = {Money.FormatCents(amountPerCopy)}/份；合计 {Money.FormatCents(amount)}";
    }
}

public static class PriceResolver
{
    public static PriceRule? Resolve(
        IEnumerable<PriceRule> rules,
        PaperSize paper,
        ColorMode color)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(paper);

        return rules
            .Where(rule => rule.Matches(paper, color))
            .OrderByDescending(rule => rule.IsPaperSpecific)
            .ThenByDescending(rule => rule.IsColorSpecific)
            .ThenBy(rule => rule.Priority)
            .ThenBy(rule => rule.Id)
            .FirstOrDefault();
    }
}

public static class DefaultPriceRules
{
    /// <summary>
    /// Default CNY price table. Prices are per sheet:
    /// A4 mono 0.20 / 0.30, A4 colour 0.80 / 1.20,
    /// A3 mono 0.40 / 0.60, A3 colour 1.60 / 2.40,
    /// A5 mono 0.15 / 0.22, A5 colour 0.60 / 0.90.
    /// A wildcard-paper fallback uses the A4 prices for Letter/Legal/custom.
    /// </summary>
    public static IReadOnlyList<PriceRule> Create() => new List<PriceRule>
    {
        Specific(PaperSize.A4, ColorMode.Monochrome, 20, 30, 10, "A4 黑白"),
        Specific(PaperSize.A4, ColorMode.Color, 80, 120, 11, "A4 彩色"),
        Specific(PaperSize.A3, ColorMode.Monochrome, 40, 60, 20, "A3 黑白"),
        Specific(PaperSize.A3, ColorMode.Color, 160, 240, 21, "A3 彩色"),
        Specific(PaperSize.A5, ColorMode.Monochrome, 15, 22, 30, "A5 黑白"),
        Specific(PaperSize.A5, ColorMode.Color, 60, 90, 31, "A5 彩色"),
        Wildcard(ColorMode.Monochrome, 20, 30, 1000, "其他纸张 黑白（按 A4）"),
        Wildcard(ColorMode.Color, 80, 120, 1001, "其他纸张 彩色（按 A4）"),
    };

    private static PriceRule Specific(
        PaperSize paper,
        ColorMode color,
        long simplexCents,
        long duplexCents,
        int priority,
        string name) => new()
    {
        Name = name,
        PaperSize = paper.Name,
        ColorMode = color,
        SimplexPriceCents = simplexCents,
        DuplexPriceCents = duplexCents,
        Priority = priority,
        Enabled = true,
    };

    private static PriceRule Wildcard(
        ColorMode color,
        long simplexCents,
        long duplexCents,
        int priority,
        string name) => new()
    {
        Name = name,
        PaperSize = null,
        ColorMode = color,
        SimplexPriceCents = simplexCents,
        DuplexPriceCents = duplexCents,
        Priority = priority,
        Enabled = true,
    };
}
