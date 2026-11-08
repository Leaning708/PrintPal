using System.Text.RegularExpressions;

namespace PrintPal.Core;

public sealed class RoutingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public int Priority { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Canonical paper names; empty or "*" means any.</summary>
    public List<string> MatchPaperSizes { get; set; } = new();

    /// <summary>Empty means any colour mode.</summary>
    public List<ColorMode> MatchColorModes { get; set; } = new();

    /// <summary>Empty means any duplex mode.</summary>
    public List<DuplexMode> MatchDuplexModes { get; set; } = new();

    /// <summary>Inclusive minimum per-copy page count; null means no lower bound.</summary>
    public int? MinPages { get; set; }

    /// <summary>Inclusive maximum per-copy page count; null means no upper bound.</summary>
    public int? MaxPages { get; set; }

    /// <summary>Optional case-insensitive regular expression over SourceApp.</summary>
    public string? SourceAppPattern { get; set; }

    /// <summary>Optional case-insensitive regular expression over DocumentName.</summary>
    public string? DocumentNamePattern { get; set; }

    public Guid? TargetPrinterId { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool Matches(PrintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!Enabled)
        {
            return false;
        }

        if (MatchPaperSizes.Count > 0 &&
            !MatchPaperSizes.Any(size =>
                size == "*" ||
                string.Equals(size, job.PaperSize.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (MatchColorModes.Count > 0 && !MatchColorModes.Contains(job.ColorMode))
        {
            return false;
        }

        if (MatchDuplexModes.Count > 0 && !MatchDuplexModes.Contains(job.DuplexMode))
        {
            return false;
        }

        // A page range cannot be evaluated when the page count is unknown.
        if (MinPages.HasValue && (job.PageCount <= 0 || job.PageCount < MinPages.Value))
        {
            return false;
        }

        if (MaxPages.HasValue && (job.PageCount <= 0 || job.PageCount > MaxPages.Value))
        {
            return false;
        }

        if (!MatchesPattern(job.SourceApp, SourceAppPattern))
        {
            return false;
        }

        if (!MatchesPattern(job.DocumentName, DocumentNamePattern))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesPattern(string? value, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(
                value,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

public sealed record RoutingResult(Printer? Printer, RoutingRule? Rule, string Reason)
{
    public bool IsRouted => Printer is not null;

    public static RoutingResult Unrouted(string reason) => new(null, null, reason);
}

public static class FormatCompatibility
{
    public static bool IsCompatible(DocumentFormat format, Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        if (format == DocumentFormat.Unknown)
        {
            return false;
        }

        if (printer.Protocol == PrinterProtocol.Ipp)
        {
            // Unknown capabilities: assume compatible; "Test connection" fills them in.
            return printer.Capabilities is null || printer.Capabilities.SupportsFormat(format);
        }

        return printer.FormatPreference switch
        {
            DocumentFormat.Unknown => format is DocumentFormat.Pdf or DocumentFormat.Ps or DocumentFormat.Pwg,
            _ => printer.FormatPreference == format,
        };
    }
}

public static class RoutingEngine
{
    public static RoutingResult Route(
        PrintJob job,
        IReadOnlyList<RoutingRule> rules,
        IReadOnlyList<Printer> printers,
        Guid? defaultPrinterId = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(printers);

        foreach (var rule in rules
                     .Where(rule => rule.Enabled)
                     .OrderBy(rule => rule.Priority)
                     .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!rule.Matches(job) || rule.TargetPrinterId is not Guid targetId)
            {
                continue;
            }

            var target = printers.FirstOrDefault(printer => printer.Id == targetId && printer.Enabled);
            if (target is null || !FormatCompatibility.IsCompatible(job.DocumentFormat, target))
            {
                continue;
            }

            return new RoutingResult(target, rule, $"命中规则“{rule.Name}”");
        }

        var fallback = defaultPrinterId is Guid id
            ? printers.FirstOrDefault(printer => printer.Id == id && printer.Enabled)
            : printers.FirstOrDefault(printer => printer.Enabled && printer.IsDefault);

        if (fallback is null)
        {
            return RoutingResult.Unrouted("没有匹配的路由规则，也没有默认打印机");
        }

        if (!FormatCompatibility.IsCompatible(job.DocumentFormat, fallback))
        {
            return RoutingResult.Unrouted($"默认打印机“{fallback.Name}”不支持 {job.DocumentFormat.DisplayName()}");
        }

        return new RoutingResult(fallback, null, "使用默认打印机");
    }
}
