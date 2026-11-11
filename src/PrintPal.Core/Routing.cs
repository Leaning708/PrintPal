namespace PrintPal.Core;

/// <summary>路由候选集：能力声明能处理该任务的打印机（按名称排序）。</summary>
public sealed record RoutingCandidates(IReadOnlyList<Printer> Printers, string Reason)
{
    public bool IsRouted => Printers.Count > 0;

    public static RoutingCandidates None(string reason) => new(Array.Empty<Printer>(), reason);
}

public enum RouteSource
{
    None = 0,
    /// <summary>同一账单沿用已用过的打印机。</summary>
    Affinity = 1,
    /// <summary>跨账单均衡（其它未结账单占用最少 / 最久未使用）。</summary>
    Balance = 2,
}

public sealed record RouteChoice(Printer Printer, string Reason, RouteSource Source);

/// <summary>路由决策需要的用量快照（由存储层一次性查出）。</summary>
public sealed record PrinterUsageSnapshot(
    IReadOnlyDictionary<Guid, int> BillJobCounts,
    IReadOnlyDictionary<Guid, DateTimeOffset> BillLastUsed,
    IReadOnlyDictionary<Guid, int> OtherOpenBillCounts,
    IReadOnlyDictionary<Guid, DateTimeOffset> LastUsed)
{
    public static PrinterUsageSnapshot Empty { get; } = new(
        new Dictionary<Guid, int>(),
        new Dictionary<Guid, DateTimeOffset>(),
        new Dictionary<Guid, int>(),
        new Dictionary<Guid, DateTimeOffset>());
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
    /// <summary>
    /// 找出所有"能力声明能处理该任务"的打印机：
    /// 启用 + 文档格式兼容 + 已声明路由能力（纸张/颜色/单双面全部覆盖）。
    /// </summary>
    public static RoutingCandidates FindCandidates(
        PrintJob job,
        IReadOnlyList<Printer> printers)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(printers);

        var matched = new List<Printer>();
        var reasons = new List<string>();

        foreach (var printer in printers
                     .Where(printer => printer.Enabled)
                     .OrderBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!FormatCompatibility.IsCompatible(job.DocumentFormat, printer))
            {
                reasons.Add($"“{printer.Name}”不支持 {job.DocumentFormat.DisplayName()}");
                continue;
            }

            if (printer.Routing is null)
            {
                reasons.Add($"“{printer.Name}”未声明路由能力");
                continue;
            }

            if (!printer.Routing.Supports(job))
            {
                reasons.Add($"“{printer.Name}”不支持 {job.PaperSize.Name}/{job.ColorMode.DisplayName()}/{job.DuplexMode.DisplayName()}");
                continue;
            }

            matched.Add(printer);
        }

        if (matched.Count == 0)
        {
            var disabled = printers.Count(printer => !printer.Enabled);
            var detail = reasons.Count > 0
                ? string.Join("；", reasons.Distinct(StringComparer.Ordinal))
                : disabled > 0
                    ? "没有启用的打印机"
                    : "没有打印机声明能处理该任务（纸张/颜色/单双面）";
            return RoutingCandidates.None($"无法路由：{detail}");
        }

        return new RoutingCandidates(
            matched,
            $"候选打印机：{string.Join("、", matched.Select(printer => printer.Name))}");
    }
}

public static class AffinityPolicy
{
    /// <summary>
    /// 在候选集内挑一台：
    /// 1) 同单优先：本账单已用过的打印机（任务数多、最近使用优先）；
    /// 2) 跨账单均衡：其它未结账单占用最少（0 优先），并列时最久未使用，再按名称。
    /// </summary>
    public static RouteChoice? Choose(
        IReadOnlyList<Printer> candidates,
        PrinterUsageSnapshot usage,
        bool affinityEnabled)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(usage);

        if (candidates.Count == 0)
        {
            return null;
        }

        if (affinityEnabled)
        {
            var preferred = candidates
                .Where(printer => usage.BillJobCounts.TryGetValue(printer.Id, out var count) && count > 0)
                .OrderByDescending(printer => usage.BillJobCounts[printer.Id])
                .ThenByDescending(printer =>
                    usage.BillLastUsed.TryGetValue(printer.Id, out var lastUsed) ? lastUsed : DateTimeOffset.MinValue)
                .ThenBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (preferred is not null)
            {
                return new RouteChoice(
                    preferred,
                    $"同一账单沿用“{preferred.Name}”",
                    RouteSource.Affinity);
            }
        }

        var balanced = candidates
            .OrderBy(printer => usage.OtherOpenBillCounts.TryGetValue(printer.Id, out var count) ? count : 0)
            .ThenBy(printer =>
                usage.LastUsed.TryGetValue(printer.Id, out var lastUsed) ? lastUsed : DateTimeOffset.MinValue)
            .ThenBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase)
            .First();

        var otherBills = usage.OtherOpenBillCounts.TryGetValue(balanced.Id, out var used) ? used : 0;
        var reason = otherBills == 0
            ? $"“{balanced.Name}”当前没有其它未结账单，跨账单均衡分配"
            : $"“{balanced.Name}”其它未结账单最少（{otherBills} 个）";

        return new RouteChoice(balanced, reason, RouteSource.Balance);
    }
}
