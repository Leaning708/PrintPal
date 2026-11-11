using PrintPal.Core;
using Xunit;

namespace PrintPal.Core.Tests;

public sealed class RoutingTests
{
    private static Printer IppPrinter(
        string name,
        PrinterRoutingCapability? routing = null,
        PrinterCapabilities? capabilities = null) => new()
    {
        Name = name,
        Protocol = PrinterProtocol.Ipp,
        Host = "127.0.0.1",
        Port = 8631,
        Capabilities = capabilities ?? PrinterCapabilities.CreateDefault(name),
        Routing = routing ?? new PrinterRoutingCapability(),
    };

    private static Printer RawPrinter(string name, DocumentFormat preference, PrinterRoutingCapability? routing = null) => new()
    {
        Name = name,
        Protocol = PrinterProtocol.Raw,
        Host = "127.0.0.1",
        Port = 9100,
        FormatPreference = preference,
        Capabilities = PrinterCapabilities.CreateDefault(name),
        Routing = routing ?? new PrinterRoutingCapability(),
    };

    private static PrintJob Job(
        PaperSize? paper = null,
        ColorMode color = ColorMode.Monochrome,
        DuplexMode duplex = DuplexMode.Simplex) => new()
    {
        PaperSize = paper ?? PaperSize.A4,
        ColorMode = color,
        DuplexMode = duplex,
        PageCount = 3,
        DocumentName = "report.docx",
        DocumentFormat = DocumentFormat.Pdf,
    };

    private static PrinterRoutingCapability Routing(
        string[]? papers = null,
        ColorMode[]? colors = null,
        DuplexMode[]? duplex = null) => new()
    {
        PaperSizes = papers?.ToList() ?? new List<string>(),
        ColorModes = colors?.ToList() ?? new List<ColorMode>(),
        DuplexModes = duplex?.ToList() ?? new List<DuplexMode>(),
    };

    // ---------------------------------------------------------------- capability

    [Fact]
    public void Empty_capability_matches_any_job()
    {
        var capability = new PrinterRoutingCapability();

        Assert.True(capability.Supports(Job(PaperSize.A5, ColorMode.Color, DuplexMode.TwoSidedLongEdge)));
        Assert.True(capability.Supports(Job()));
    }

    [Fact]
    public void Capability_is_cartesian_product_of_papers_colors_duplex()
    {
        // 勾了 A4/A5 + 彩色 → 代表 A4/A5 都支持彩色
        var capability = Routing(
            new[] { "A4", "A5" },
            new[] { ColorMode.Color },
            new[] { DuplexMode.Simplex });

        Assert.True(capability.Supports(Job(PaperSize.A4, ColorMode.Color, DuplexMode.Simplex)));
        Assert.True(capability.Supports(Job(PaperSize.A5, ColorMode.Color, DuplexMode.Simplex)));
        Assert.False(capability.Supports(Job(PaperSize.A3, ColorMode.Color, DuplexMode.Simplex)));
        Assert.False(capability.Supports(Job(PaperSize.A4, ColorMode.Monochrome, DuplexMode.Simplex)));
        Assert.False(capability.Supports(Job(PaperSize.A4, ColorMode.Color, DuplexMode.TwoSidedLongEdge)));
    }

    [Fact]
    public void Detect_results_can_seed_a_routing_capability()
    {
        var detected = new PrinterCapabilities
        {
            MediaSizes = { PaperSize.A4, PaperSize.A5 },
            ColorModes = { ColorMode.Monochrome, ColorMode.Color },
            DuplexModes = { DuplexMode.Simplex, DuplexMode.TwoSidedLongEdge },
        };

        var routing = PrinterRoutingCapability.FromDetected(detected);

        Assert.Equal(2, routing.PaperSizes.Count);
        Assert.Equal(2, routing.ColorModes.Count);
        Assert.Equal(2, routing.DuplexModes.Count);
        Assert.True(routing.Supports(Job(PaperSize.A4, ColorMode.Color, DuplexMode.TwoSidedLongEdge)));
        Assert.False(routing.Supports(Job(PaperSize.A3, ColorMode.Color, DuplexMode.Simplex)));
    }

    // ---------------------------------------------------------------- candidates

    [Fact]
    public void Candidates_include_printers_whose_capability_covers_the_job()
    {
        var first = IppPrinter("first");
        var second = IppPrinter("second");

        var result = RoutingEngine.FindCandidates(Job(), new[] { first, second });

        Assert.True(result.IsRouted);
        Assert.Equal(2, result.Printers.Count);
    }

    [Fact]
    public void Printers_without_declared_capability_are_not_candidates()
    {
        var declared = IppPrinter("declared");
        var undeclared = IppPrinter("undeclared", routing: null);
        undeclared.Routing = null;

        var result = RoutingEngine.FindCandidates(Job(), new[] { declared, undeclared });

        Assert.True(result.IsRouted);
        Assert.Single(result.Printers);
        Assert.Equal(declared.Id, result.Printers[0].Id);
    }

    [Fact]
    public void Disabled_printer_is_excluded()
    {
        var target = IppPrinter("target");
        target.Enabled = false;

        var result = RoutingEngine.FindCandidates(Job(), new[] { target });

        Assert.False(result.IsRouted);
        Assert.Contains("无法路由", result.Reason);
    }

    [Fact]
    public void Capability_takes_paper_color_and_duplex_into_account()
    {
        var monoA4 = IppPrinter("mono-a4", Routing(
            new[] { "A4" },
            new[] { ColorMode.Monochrome },
            new[] { DuplexMode.Simplex, DuplexMode.TwoSidedLongEdge }));
        var colorA4 = IppPrinter("color-a4", Routing(
            new[] { "A4" },
            new[] { ColorMode.Monochrome, ColorMode.Color },
            new[] { DuplexMode.Simplex, DuplexMode.TwoSidedLongEdge }));
        var a3 = IppPrinter("a3", Routing(
            new[] { "A3", "A5" },
            new[] { ColorMode.Monochrome, ColorMode.Color },
            new[] { DuplexMode.Simplex }));

        var printers = new[] { monoA4, colorA4, a3 };

        Assert.Equal(2, RoutingEngine.FindCandidates(Job(PaperSize.A4, ColorMode.Monochrome), printers).Printers.Count);
        Assert.Single(RoutingEngine.FindCandidates(Job(PaperSize.A4, ColorMode.Color), printers).Printers);
        Assert.Single(RoutingEngine.FindCandidates(Job(PaperSize.A5, ColorMode.Color), printers).Printers);
        Assert.False(RoutingEngine.FindCandidates(Job(PaperSize.A5, ColorMode.Color, DuplexMode.TwoSidedLongEdge), printers).IsRouted);
        Assert.Single(RoutingEngine.FindCandidates(Job(PaperSize.A3, ColorMode.Color), printers).Printers);
    }

    [Fact]
    public void Incompatible_format_is_excluded()
    {
        var raw = RawPrinter("raw-ps", DocumentFormat.Ps);
        var ipp = IppPrinter("ipp");

        var result = RoutingEngine.FindCandidates(Job(), new[] { raw, ipp });

        Assert.True(result.IsRouted);
        Assert.Single(result.Printers);
        Assert.Equal(ipp.Id, result.Printers[0].Id);
    }

    [Fact]
    public void No_printer_capable_is_unrouted()
    {
        var a3Only = IppPrinter("a3", Routing(new[] { "A3" }));

        var result = RoutingEngine.FindCandidates(Job(PaperSize.A4), new[] { a3Only });

        Assert.False(result.IsRouted);
        Assert.Contains("不支持", result.Reason);
    }

    // ---------------------------------------------------------------- affinity

    private static PrinterUsageSnapshot Usage(
        Dictionary<Guid, int>? billCounts = null,
        Dictionary<Guid, DateTimeOffset>? billLastUsed = null,
        Dictionary<Guid, int>? otherOpen = null,
        Dictionary<Guid, DateTimeOffset>? lastUsed = null) =>
        new(billCounts ?? new(), billLastUsed ?? new(), otherOpen ?? new(), lastUsed ?? new());

    [Fact]
    public void Affinity_prefers_printer_already_used_by_the_bill()
    {
        var first = IppPrinter("first");
        var second = IppPrinter("second");

        var choice = AffinityPolicy.Choose(
            new[] { first, second },
            Usage(billCounts: new() { [second.Id] = 2 },
                  billLastUsed: new() { [second.Id] = DateTimeOffset.UtcNow }),
            affinityEnabled: true);

        Assert.NotNull(choice);
        Assert.Equal(second.Id, choice!.Printer.Id);
        Assert.Equal(RouteSource.Affinity, choice.Source);
    }

    [Fact]
    public void Affinity_prefers_printer_with_more_jobs_in_the_bill()
    {
        var first = IppPrinter("first");
        var second = IppPrinter("second");
        var older = DateTimeOffset.UtcNow.AddMinutes(-10);

        var choice = AffinityPolicy.Choose(
            new[] { first, second },
            Usage(
                billCounts: new() { [first.Id] = 1, [second.Id] = 3 },
                billLastUsed: new() { [first.Id] = older, [second.Id] = older }),
            affinityEnabled: true);

        Assert.Equal(second.Id, choice!.Printer.Id);
    }

    [Fact]
    public void Affinity_is_skipped_when_disabled()
    {
        var first = IppPrinter("first");
        var second = IppPrinter("second");

        var choice = AffinityPolicy.Choose(
            new[] { first, second },
            Usage(billCounts: new() { [second.Id] = 5 }),
            affinityEnabled: false);

        Assert.NotNull(choice);
        Assert.Equal(RouteSource.Balance, choice!.Source);
        Assert.Equal(first.Id, choice.Printer.Id);
    }

    [Fact]
    public void Balance_prefers_printer_without_other_open_bills()
    {
        var busy = IppPrinter("busy");
        var idle = IppPrinter("idle");

        var choice = AffinityPolicy.Choose(
            new[] { busy, idle },
            Usage(otherOpen: new() { [busy.Id] = 2, [idle.Id] = 0 }),
            affinityEnabled: true);

        Assert.Equal(idle.Id, choice!.Printer.Id);
        Assert.Equal(RouteSource.Balance, choice.Source);
    }

    [Fact]
    public void Balance_prefers_least_other_open_bills()
    {
        var more = IppPrinter("more");
        var less = IppPrinter("less");

        var choice = AffinityPolicy.Choose(
            new[] { more, less },
            Usage(otherOpen: new() { [more.Id] = 3, [less.Id] = 1 }),
            affinityEnabled: true);

        Assert.Equal(less.Id, choice!.Printer.Id);
    }

    [Fact]
    public void Balance_tie_breaks_by_least_recently_used()
    {
        var recent = IppPrinter("recent");
        var stale = IppPrinter("stale");

        var choice = AffinityPolicy.Choose(
            new[] { recent, stale },
            Usage(
                otherOpen: new() { [recent.Id] = 0, [stale.Id] = 0 },
                lastUsed: new()
                {
                    [recent.Id] = DateTimeOffset.UtcNow,
                    [stale.Id] = DateTimeOffset.UtcNow.AddHours(-5),
                }),
            affinityEnabled: true);

        Assert.Equal(stale.Id, choice!.Printer.Id);
    }

    [Fact]
    public void No_candidates_yields_no_choice()
    {
        var choice = AffinityPolicy.Choose(Array.Empty<Printer>(), Usage(), affinityEnabled: true);

        Assert.Null(choice);
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
