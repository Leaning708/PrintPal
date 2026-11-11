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

    /// <summary>展示用名称，由 UI 按"纸张 颜色"自动生成。</summary>
    public string? Name { get; set; }

    /// <summary>Canonical paper name；价格表不再支持通配纸张（必须有具体纸型）。</summary>
    public string? PaperSize { get; set; }

    /// <summary>价格必须精确匹配颜色；null 视为未配置（迁移后不再产生）。</summary>
    public ColorMode? ColorMode { get; set; }

    /// <summary>Price for one sheet printed on one side, in cents.</summary>
    public long SimplexPriceCents { get; set; }

    /// <summary>Price for one sheet printed on both sides, in cents.</summary>
    public long DuplexPriceCents { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsPaperSpecific =>
        !string.IsNullOrWhiteSpace(PaperSize) && PaperSize != "*";

    public bool IsColorSpecific =>
        ColorMode is not null && ColorMode != Core.ColorMode.Unknown;

    /// <summary>精确匹配：纸张名相同 且 颜色相同（价格表不允许通配/重复）。</summary>
    public bool Matches(PaperSize paper, ColorMode color)
    {
        if (!IsPaperSpecific ||
            !string.Equals(PaperSize, paper.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ColorMode == color;
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
    /// <summary>
    /// 精确匹配 (纸张, 颜色)。价格表不允许通配与重复，找不到就返回 null（任务进 NeedsReview，
    /// 收银台阻止结账并提示补价格）。
    /// </summary>
    public static PriceRule? Resolve(
        IEnumerable<PriceRule> rules,
        PaperSize paper,
        ColorMode color)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(paper);

        return rules.FirstOrDefault(rule => rule.Matches(paper, color));
    }
}

public static class DefaultPriceRules
{
    /// <summary>
    /// Default CNY price table. Prices are per sheet:
    /// A4 mono 0.20 / 0.30, A4 colour 0.80 / 1.20,
    /// A3 mono 0.40 / 0.60, A3 colour 1.60 / 2.40,
    /// A5 mono 0.15 / 0.22, A5 colour 0.60 / 0.90.
    /// 不再有“其他纸张”通配行：未配置的纸型会明确提示补价格。
    /// </summary>
    public static IReadOnlyList<PriceRule> Create() => new List<PriceRule>
    {
        Specific(PaperSize.A4, ColorMode.Monochrome, 20, 30),
        Specific(PaperSize.A4, ColorMode.Color, 80, 120),
        Specific(PaperSize.A3, ColorMode.Monochrome, 40, 60),
        Specific(PaperSize.A3, ColorMode.Color, 160, 240),
        Specific(PaperSize.A5, ColorMode.Monochrome, 15, 22),
        Specific(PaperSize.A5, ColorMode.Color, 60, 90),
    };

    private static PriceRule Specific(
        PaperSize paper,
        ColorMode color,
        long simplexCents,
        long duplexCents) => new()
    {
        Name = PriceRuleName(paper.Name, color),
        PaperSize = paper.Name,
        ColorMode = color,
        SimplexPriceCents = simplexCents,
        DuplexPriceCents = duplexCents,
    };

    /// <summary>价格行名称统一按"纸张 颜色"生成，避免手填不一致。</summary>
    public static string PriceRuleName(string paperName, ColorMode color) =>
        $"{paperName} {color.DisplayName()}";
}
