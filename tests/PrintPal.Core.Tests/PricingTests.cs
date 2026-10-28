using PrintPal.Core;
using Xunit;

namespace PrintPal.Core.Tests;

public sealed class PricingTests
{
    private static PriceRule A4Mono() => DefaultPriceRules.Create()
        .Single(rule => rule.PaperSize == "A4" && rule.ColorMode == ColorMode.Monochrome);

    private static PriceRule A4Color() => DefaultPriceRules.Create()
        .Single(rule => rule.PaperSize == "A4" && rule.ColorMode == ColorMode.Color);

    [Fact]
    public void Simplex_3_pages_2_copies_A4_mono()
    {
        var quote = PriceCalculator.Calculate(A4Mono(), 3, 2, DuplexMode.Simplex);

        Assert.Equal(0, quote.DuplexSheetsPerCopy);
        Assert.Equal(3, quote.SimplexSheetsPerCopy);
        Assert.Equal(0, quote.TotalDuplexSheets);
        Assert.Equal(6, quote.TotalSimplexSheets);
        Assert.Equal(6, quote.TotalSheets);
        Assert.Equal(6, quote.BillableSides);
        Assert.Equal(20, quote.UnitPriceCents);
        Assert.Equal(120, quote.AmountCents);
        Assert.Contains("1.20", quote.Breakdown);
    }

    [Fact]
    public void Duplex_even_4_pages_3_copies_A4_mono()
    {
        var quote = PriceCalculator.Calculate(
            A4Mono(), pagesPerCopy: 4, copies: 3, DuplexMode.TwoSidedLongEdge);

        Assert.Equal(2, quote.DuplexSheetsPerCopy);
        Assert.Equal(0, quote.SimplexSheetsPerCopy);
        Assert.Equal(6, quote.TotalDuplexSheets);
        Assert.Equal(0, quote.TotalSimplexSheets);
        Assert.Equal(6, quote.TotalSheets);
        Assert.Equal(12, quote.BillableSides);
        Assert.Equal(30, quote.UnitPriceCents);
        Assert.Equal(180, quote.AmountCents);
    }

    [Fact]
    public void Duplex_odd_5_pages_3_copies_uses_simplex_price_for_last_sheet()
    {
        var quote = PriceCalculator.Calculate(
            A4Mono(), pagesPerCopy: 5, copies: 3, DuplexMode.TwoSidedLongEdge);

        Assert.Equal(2, quote.DuplexSheetsPerCopy);
        Assert.Equal(1, quote.SimplexSheetsPerCopy);
        Assert.Equal(6, quote.TotalDuplexSheets);
        Assert.Equal(3, quote.TotalSimplexSheets);
        Assert.Equal(9, quote.TotalSheets);
        Assert.Equal(15, quote.BillableSides);
        Assert.Equal(30, quote.UnitPriceCents);

        // Correct: (2*30 + 1*20) * 3 = 240.
        // Wrong (treat 15 pages as one document): 7*30 + 1*20 = 230.
        Assert.Equal(240, quote.AmountCents);
        Assert.Contains("2.40", quote.Breakdown);
    }

    [Fact]
    public void Duplex_odd_3_pages_2_copies_does_not_combine_single_sheets_across_copies()
    {
        var quote = PriceCalculator.Calculate(
            A4Mono(), pagesPerCopy: 3, copies: 2, DuplexMode.TwoSidedShortEdge);

        Assert.Equal(1, quote.DuplexSheetsPerCopy);
        Assert.Equal(1, quote.SimplexSheetsPerCopy);
        Assert.Equal(2, quote.TotalDuplexSheets);
        Assert.Equal(2, quote.TotalSimplexSheets);
        Assert.Equal(4, quote.TotalSheets);
        Assert.Equal(6, quote.BillableSides);

        // Correct: (1*30 + 1*20) * 2 = 100. Wrong (6 pages as one doc): 3*30 = 90.
        Assert.Equal(100, quote.AmountCents);
    }

    [Fact]
    public void Duplex_color_5_pages_2_copies_A4()
    {
        var quote = PriceCalculator.Calculate(
            A4Color(), pagesPerCopy: 5, copies: 2, DuplexMode.TwoSidedLongEdge);

        Assert.Equal(120, quote.DuplexPriceCents);
        Assert.Equal(80, quote.SimplexPriceCents);
        Assert.Equal(4, quote.TotalDuplexSheets);
        Assert.Equal(2, quote.TotalSimplexSheets);
        Assert.Equal(6, quote.TotalSheets);

        // (2*120 + 1*80) * 2 = 640.
        Assert.Equal(640, quote.AmountCents);
    }

    [Fact]
    public void Unknown_duplex_mode_is_treated_as_simplex()
    {
        var quote = PriceCalculator.Calculate(A4Mono(), 5, 1, DuplexMode.Unknown);

        Assert.Equal(5, quote.SimplexSheetsPerCopy);
        Assert.Equal(0, quote.DuplexSheetsPerCopy);
        Assert.Equal(100, quote.AmountCents);
    }

    [Fact]
    public void Invalid_page_or_copy_counts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PriceCalculator.Calculate(A4Mono(), 0, 1, DuplexMode.Simplex));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PriceCalculator.Calculate(A4Mono(), 1, 0, DuplexMode.Simplex));
    }

    [Fact]
    public void Default_table_has_expected_prices()
    {
        var rules = DefaultPriceRules.Create();

        AssertRule(rules, PaperSize.A4, ColorMode.Monochrome, 20, 30);
        AssertRule(rules, PaperSize.A4, ColorMode.Color, 80, 120);
        AssertRule(rules, PaperSize.A3, ColorMode.Monochrome, 40, 60);
        AssertRule(rules, PaperSize.A3, ColorMode.Color, 160, 240);
        AssertRule(rules, PaperSize.A5, ColorMode.Monochrome, 15, 22);
        AssertRule(rules, PaperSize.A5, ColorMode.Color, 60, 90);
    }

    [Fact]
    public void Resolver_prefers_specific_rule_over_wildcard()
    {
        var rules = DefaultPriceRules.Create();

        var a4Color = PriceResolver.Resolve(rules, PaperSize.A4, ColorMode.Color);
        Assert.NotNull(a4Color);
        Assert.Equal(80, a4Color!.SimplexPriceCents);

        // Letter is not in the specific table; the wildcard fallback uses A4 prices.
        var letterMono = PriceResolver.Resolve(rules, PaperSize.Letter, ColorMode.Monochrome);
        Assert.NotNull(letterMono);
        Assert.Equal(20, letterMono!.SimplexPriceCents);
        Assert.Null(letterMono.PaperSize);
    }

    [Fact]
    public void Resolver_returns_null_when_no_rule_matches()
    {
        var rules = new[]
        {
            new PriceRule
            {
                PaperSize = "A3",
                ColorMode = ColorMode.Color,
                SimplexPriceCents = 1,
                DuplexPriceCents = 2,
            },
        };

        Assert.Null(PriceResolver.Resolve(rules, PaperSize.A4, ColorMode.Monochrome));
    }

    [Fact]
    public void Resolver_skips_disabled_rules()
    {
        var rules = new[]
        {
            new PriceRule
            {
                PaperSize = "A4",
                ColorMode = ColorMode.Monochrome,
                SimplexPriceCents = 1,
                DuplexPriceCents = 2,
                Enabled = false,
            },
        };

        Assert.Null(PriceResolver.Resolve(rules, PaperSize.A4, ColorMode.Monochrome));
    }

    [Fact]
    public void ApplyQuote_copies_breakdown_and_marks_priced()
    {
        var job = new PrintJob { PageCount = 5, Copies = 3, DuplexMode = DuplexMode.TwoSidedLongEdge };
        var quote = PriceCalculator.Calculate(A4Mono(), job.PageCount, job.Copies, job.DuplexMode);

        job.ApplyQuote(quote);

        Assert.Equal(240, job.AmountCents);
        Assert.Equal(9, job.TotalSheets);
        Assert.Equal(15, job.BillableSides);
        Assert.NotNull(job.PricingJson);
        Assert.NotNull(job.PricedAtUtc);
        Assert.Contains("2.40", job.PricingJson);
    }

    private static void AssertRule(
        IEnumerable<PriceRule> rules,
        PaperSize paper,
        ColorMode color,
        long simplex,
        long duplex)
    {
        var rule = rules.Single(r =>
            r.PaperSize == paper.Name && r.ColorMode == color);

        Assert.Equal(simplex, rule.SimplexPriceCents);
        Assert.Equal(duplex, rule.DuplexPriceCents);
    }
}
