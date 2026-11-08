using PrintPal.Core;
using Xunit;

namespace PrintPal.Core.Tests;

public sealed class RoutingTests
{
    private static Printer IppPrinter(string name, bool isDefault = false) => new()
    {
        Name = name,
        Protocol = PrinterProtocol.Ipp,
        Host = "127.0.0.1",
        Port = 8631,
        IsDefault = isDefault,
        Capabilities = PrinterCapabilities.CreateDefault(name),
    };

    private static Printer RawPrinter(string name, DocumentFormat preference, bool isDefault = false) => new()
    {
        Name = name,
        Protocol = PrinterProtocol.Raw,
        Host = "127.0.0.1",
        Port = 9100,
        FormatPreference = preference,
        IsDefault = isDefault,
        Capabilities = PrinterCapabilities.CreateDefault(name),
    };

    private static PrintJob Job(
        PaperSize? paper = null,
        ColorMode color = ColorMode.Monochrome,
        DuplexMode duplex = DuplexMode.Simplex,
        int pages = 3,
        string documentName = "report.docx",
        string? sourceApp = null) => new()
    {
        PaperSize = paper ?? PaperSize.A4,
        ColorMode = color,
        DuplexMode = duplex,
        PageCount = pages,
        DocumentName = documentName,
        SourceApp = sourceApp,
        DocumentFormat = DocumentFormat.Pdf,
    };

    [Fact]
    public void First_matching_rule_by_priority_wins()
    {
        var first = IppPrinter("first");
        var second = IppPrinter("second");

        var rules = new[]
        {
            new RoutingRule { Name = "low", Priority = 20, TargetPrinterId = second.Id },
            new RoutingRule { Name = "high", Priority = 10, TargetPrinterId = first.Id },
        };

        var result = RoutingEngine.Route(Job(), rules, new[] { first, second });

        Assert.True(result.IsRouted);
        Assert.Equal(first.Id, result.Printer!.Id);
        Assert.Equal("high", result.Rule!.Name);
    }

    [Fact]
    public void Rule_conditions_are_anded()
    {
        var target = IppPrinter("target");
        var rule = new RoutingRule
        {
            Name = "A4 colour duplex",
            TargetPrinterId = target.Id,
            MatchPaperSizes = { "A4" },
            MatchColorModes = { ColorMode.Color },
            MatchDuplexModes = { DuplexMode.TwoSidedLongEdge },
        };

        Assert.True(rule.Matches(Job(color: ColorMode.Color, duplex: DuplexMode.TwoSidedLongEdge)));
        Assert.False(rule.Matches(Job(color: ColorMode.Monochrome, duplex: DuplexMode.TwoSidedLongEdge)));
        Assert.False(rule.Matches(Job(paper: PaperSize.A3, color: ColorMode.Color, duplex: DuplexMode.TwoSidedLongEdge)));
        Assert.False(rule.Matches(Job(color: ColorMode.Color, duplex: DuplexMode.Simplex)));
    }

    [Fact]
    public void Page_range_uses_pages_per_copy()
    {
        var rule = new RoutingRule
        {
            Name = "6+",
            MinPages = 6,
            TargetPrinterId = Guid.NewGuid(),
        };

        Assert.False(rule.Matches(Job(pages: 5)));
        Assert.True(rule.Matches(Job(pages: 6)));
    }

    [Fact]
    public void Unknown_page_count_does_not_match_a_page_range()
    {
        var rule = new RoutingRule
        {
            Name = "range",
            MinPages = 1,
            MaxPages = 10,
            TargetPrinterId = Guid.NewGuid(),
        };

        Assert.False(rule.Matches(Job(pages: 0)));
    }

    [Fact]
    public void Source_app_regex_is_case_insensitive()
    {
        var target = IppPrinter("target");
        var rule = new RoutingRule
        {
            Name = "WPS",
            SourceAppPattern = "wps",
            TargetPrinterId = target.Id,
        };

        var result = RoutingEngine.Route(
            Job(sourceApp: "WPS Office"), new[] { rule }, new[] { target });

        Assert.True(result.IsRouted);
        Assert.Equal(target.Id, result.Printer!.Id);
    }

    [Fact]
    public void Invalid_regex_does_not_throw_or_match()
    {
        var target = IppPrinter("target");
        var rule = new RoutingRule
        {
            Name = "bad regex",
            DocumentNamePattern = "[",
            TargetPrinterId = target.Id,
        };

        var result = RoutingEngine.Route(Job(), new[] { rule }, new[] { target });

        Assert.False(result.IsRouted);
    }

    [Fact]
    public void Incompatible_rule_target_falls_through_to_default()
    {
        var raw = RawPrinter("raw-ps", DocumentFormat.Ps);
        var fallback = IppPrinter("ipp-default", isDefault: true);

        var rule = new RoutingRule { Name = "raw", TargetPrinterId = raw.Id };

        var result = RoutingEngine.Route(Job(), new[] { rule }, new[] { raw, fallback });

        Assert.True(result.IsRouted);
        Assert.Equal(fallback.Id, result.Printer!.Id);
        Assert.Null(result.Rule);
    }

    [Fact]
    public void Uses_explicit_default_printer_id()
    {
        var explicitDefault = IppPrinter("explicit");
        var flaggedDefault = IppPrinter("flagged", isDefault: true);

        var result = RoutingEngine.Route(
            Job(), Array.Empty<RoutingRule>(), new[] { explicitDefault, flaggedDefault },
            defaultPrinterId: explicitDefault.Id);

        Assert.Equal(explicitDefault.Id, result.Printer!.Id);
    }

    [Fact]
    public void No_rule_and_no_default_is_unrouted()
    {
        var result = RoutingEngine.Route(Job(), Array.Empty<RoutingRule>(), Array.Empty<Printer>());

        Assert.False(result.IsRouted);
        Assert.Null(result.Printer);
    }

    [Fact]
    public void Incompatible_default_is_unrouted()
    {
        var rawPs = RawPrinter("raw-ps", DocumentFormat.Ps, isDefault: true);

        var result = RoutingEngine.Route(Job(), Array.Empty<RoutingRule>(), new[] { rawPs });

        Assert.False(result.IsRouted);
        Assert.Contains("不支持", result.Reason);
    }

    [Fact]
    public void Disabled_rule_is_ignored()
    {
        var target = IppPrinter("target");
        var rule = new RoutingRule
        {
            Name = "disabled",
            Enabled = false,
            TargetPrinterId = target.Id,
        };

        var result = RoutingEngine.Route(Job(), new[] { rule }, new[] { target });

        Assert.False(result.IsRouted);
    }

    [Fact]
    public void Format_compatibility_rules()
    {
        var ippUnknownCaps = new Printer { Protocol = PrinterProtocol.Ipp };
        var ippNoPdf = new Printer
        {
            Protocol = PrinterProtocol.Ipp,
            Capabilities = new PrinterCapabilities
            {
                DocumentFormats = { DocumentFormat.Pwg },
            },
        };
        var rawAuto = RawPrinter("raw-auto", DocumentFormat.Unknown);
        var rawPs = RawPrinter("raw-ps", DocumentFormat.Ps);

        Assert.True(FormatCompatibility.IsCompatible(DocumentFormat.Pdf, ippUnknownCaps));
        Assert.False(FormatCompatibility.IsCompatible(DocumentFormat.Pdf, ippNoPdf));
        Assert.True(FormatCompatibility.IsCompatible(DocumentFormat.Pdf, rawAuto));
        Assert.True(FormatCompatibility.IsCompatible(DocumentFormat.Ps, rawAuto));
        Assert.False(FormatCompatibility.IsCompatible(DocumentFormat.Pdf, rawPs));
        Assert.True(FormatCompatibility.IsCompatible(DocumentFormat.Ps, rawPs));
        Assert.False(FormatCompatibility.IsCompatible(DocumentFormat.Unknown, ippUnknownCaps));
    }
}
