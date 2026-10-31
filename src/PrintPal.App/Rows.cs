using System.ComponentModel;
using System.Text;
using System.Text.Json;
using PrintPal.Core;

namespace PrintPal.App;

public sealed class BillChipRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public BillChipRow(BillSummary summary, int index)
    {
        Summary = summary;
        IndexText = index.ToString();
    }

    public BillSummary Summary { get; private set; }

    public Guid Id => Summary.Id;

    public string Name => Summary.Name;

    /// <summary>账单序号，按建单顺序稳定编号，切换当前账单不改变（账单切换列表展示用）。</summary>
    public string IndexText { get; }

    /// <summary>是否为当前正在记账的账单（列表中高亮，点击不切换）。</summary>
    public bool IsCurrent => Summary.Status == BillStatus.Active;

    public string AmountText => Money.FormatCents(Summary.AmountCents);

    public string ParkedTimeText => Summary.ParkedAtUtc is { } at ? at.ToLocalTime().ToString("HH:mm") : "";

    public string ChipText => $"{Summary.Name}  {Money.FormatCents(Summary.AmountCents)}";

    public string ToolTip
    {
        get
        {
            if (IsCurrent)
            {
                return $"当前账单 · {AmountText} · {Summary.JobCount} 单";
            }

            var parked = Summary.ParkedAtUtc is { } at ? $" · 挂起 {at.ToLocalTime():HH:mm}" : string.Empty;
            return $"{AmountText} · {Summary.JobCount} 单{parked}\n点击切换到该账单";
        }
    }

    /// <summary>
    /// 原地更新账单汇总并通知绑定刷新。成员不变时（切换当前账单、金额变动等）
    /// 不重建集合：chip 容器不重新生成，高亮平滑移动，溢出区不会闪现一帧。
    /// </summary>
    public void UpdateFrom(BillSummary summary)
    {
        if (Summary.Id != summary.Id)
        {
            return;
        }

        var changed = Summary.Status != summary.Status ||
                      Summary.Name != summary.Name ||
                      Summary.AmountCents != summary.AmountCents ||
                      Summary.JobCount != summary.JobCount ||
                      Summary.ParkedAtUtc != summary.ParkedAtUtc;
        if (!changed)
        {
            return;
        }

        Summary = summary;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public sealed class ClosedBillRow
{
    public ClosedBillRow(Bill bill)
    {
        Bill = bill;
    }

    public Bill Bill { get; }

    public string ClosedDateText => Bill.ClosedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";

    public string ClosedTimeText => Bill.ClosedAtUtc?.ToLocalTime().ToString("HH:mm") ?? "";

    public string BillName => Bill.Name;

    public int JobCount => Bill.JobCount;

    public int TotalPages => Bill.TotalPages;

    public string AmountText => Money.FormatCents(Bill.AmountCents);

    public string Note => Bill.Note ?? "";
}


public sealed class JobRow
{
    private readonly IReadOnlyDictionary<Guid, string> _printerNames;

    public JobRow(PrintJob job, IReadOnlyDictionary<Guid, string> printerNames)
    {
        Job = job;
        _printerNames = printerNames;
    }

    public PrintJob Job { get; }

    public string TimeText => Job.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");

    public string TimeShortText => Job.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");

    public string DocumentName => Job.DocumentName;

    public string UserName => Job.UserName;

    public string PaperText => Job.PaperSize.IsKnown ? Job.PaperSize.Name : "未知";

    public string ColorText => Job.ColorMode.DisplayName();

    public string DuplexText => Job.DuplexMode.DisplayName();

    public int PageCount => Job.PageCount;

    public int Copies => Job.Copies;

    public string SpecText => $"{PaperText} · {ColorText} · {DuplexText}";

    public string UnitPriceText => Money.FormatCents(Job.UnitPriceCents);

    public string AmountText => Money.FormatCents(Job.AmountCents);

    public string TargetText => Job.TargetPrinterId is Guid id && _printerNames.TryGetValue(id, out var name)
        ? name
        : "—";

    public string StatusText => Job.Status switch
    {
        JobStatus.Captured => "已捕获",
        JobStatus.Priced => "待转发",
        JobStatus.NeedsReview => "待确认",
        JobStatus.Unrouted => "未路由",
        JobStatus.Held => "待确认",
        JobStatus.Forwarding => "转发中",
        JobStatus.Succeeded => "已转发",
        JobStatus.Failed => "失败",
        JobStatus.Unknown => "未知",
        JobStatus.Canceled => "已取消",
        _ => Job.Status.ToString(),
    };

    public string DetailsText
    {
        get
        {
            var builder = new StringBuilder();

            builder.AppendLine($"文档：{Job.DocumentName}");
            builder.AppendLine($"用户：{Job.UserName}");
            builder.AppendLine($"时间：{Job.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"纸张：{PaperText}");
            builder.AppendLine($"颜色：{ColorText}");
            builder.AppendLine($"单双面：{DuplexText}");
            builder.AppendLine($"页数：{Job.PageCount} 页/份 × {Job.Copies} 份 = {Job.BillableSides} 面");
            builder.AppendLine($"张数：{Job.TotalSheets} 张（双面 {Job.TotalDuplexSheets} + 单面 {Job.TotalSimplexSheets}）");
            builder.AppendLine($"金额：{Money.FormatCents(Job.AmountCents)}");
            builder.AppendLine($"目标：{TargetText}");
            builder.AppendLine($"状态：{StatusText}" +
                               (string.IsNullOrWhiteSpace(Job.StatusReason) ? "" : $"（{Job.StatusReason}）"));
            builder.AppendLine($"文档格式：{Job.DocumentFormat.DisplayName()}");
            builder.AppendLine($"文档路径：{Job.DocumentPath ?? "—"}");
            builder.AppendLine($"SHA-256：{Job.DocumentSha256 ?? "—"}");

            var breakdown = TryGetBreakdown(Job.PricingJson);
            if (!string.IsNullOrWhiteSpace(breakdown))
            {
                builder.AppendLine();
                builder.AppendLine("计价明细：");
                builder.AppendLine(breakdown);
            }

            return builder.ToString();
        }
    }

    private static string? TryGetBreakdown(string? pricingJson)
    {
        if (string.IsNullOrWhiteSpace(pricingJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PriceQuote>(pricingJson)?.Breakdown;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class PrinterRow
{
    public PrinterRow(Printer printer)
    {
        Printer = printer;
    }

    public Printer Printer { get; }

    public string Name => Printer.Name;

    public string ProtocolText => Printer.Protocol == PrinterProtocol.Ipp ? "IPP" : "Raw 9100";

    public string Address => Printer.Uri;

    public string EnabledText => Printer.Enabled ? "启用" : "停用";

    public string DefaultText => Printer.IsDefault ? "默认" : "";

    public string StatusText => Printer.LastTestOk switch
    {
        true => "在线",
        false => "连接失败",
        null => "未测试",
    };

    public string CapabilitiesText
    {
        get
        {
            var capabilities = Printer.Capabilities;
            if (capabilities is null)
            {
                return "未获取";
            }

            var formats = capabilities.DocumentFormats.Count == 0
                ? "?"
                : string.Join("/", capabilities.DocumentFormats.Select(format => format.DisplayName()));
            var media = capabilities.MediaSizes.Count == 0
                ? "?"
                : string.Join("/", capabilities.MediaSizes.Select(size => size.Name));

            return $"{formats}; {media}";
        }
    }
}

public sealed class RoutingRow
{
    private readonly IReadOnlyDictionary<Guid, string> _printerNames;

    public RoutingRow(RoutingRule rule, IReadOnlyDictionary<Guid, string> printerNames)
    {
        Rule = rule;
        _printerNames = printerNames;
    }

    public RoutingRule Rule { get; }

    public string PriorityText => Rule.Priority.ToString();

    public string Name => Rule.Name;

    public string ConditionsText
    {
        get
        {
            var parts = new List<string>();

            parts.Add(Rule.MatchPaperSizes.Count == 0
                ? "纸张任意"
                : "纸张 " + string.Join("/", Rule.MatchPaperSizes));

            parts.Add(Rule.MatchColorModes.Count == 0
                ? "颜色任意"
                : "颜色 " + string.Join("/", Rule.MatchColorModes.Select(mode => mode.DisplayName())));

            parts.Add(Rule.MatchDuplexModes.Count == 0
                ? "单双面任意"
                : string.Join("/", Rule.MatchDuplexModes.Select(mode => mode.DisplayName())));

            if (Rule.MinPages.HasValue || Rule.MaxPages.HasValue)
            {
                parts.Add($"页数 {Rule.MinPages?.ToString() ?? "0"}–{Rule.MaxPages?.ToString() ?? "∞"}");
            }

            if (!string.IsNullOrWhiteSpace(Rule.SourceAppPattern))
            {
                parts.Add("应用 ~ " + Rule.SourceAppPattern);
            }

            if (!string.IsNullOrWhiteSpace(Rule.DocumentNamePattern))
            {
                parts.Add("文档 ~ " + Rule.DocumentNamePattern);
            }

            return string.Join(" / ", parts);
        }
    }

    public string TargetText => Rule.TargetPrinterId is Guid id && _printerNames.TryGetValue(id, out var name)
        ? name
        : "（未设置）";

    public string EnabledText => Rule.Enabled ? "启用" : "停用";
}

public sealed class PriceRow
{
    public PriceRow(PriceRule rule)
    {
        Rule = rule;
        PaperDisplay = string.IsNullOrWhiteSpace(rule.PaperSize) || rule.PaperSize == "*"
            ? "其他纸张"
            : rule.PaperSize;
        ColorDisplay = rule.ColorMode switch
        {
            ColorMode.Monochrome => "黑白",
            ColorMode.Color => "彩色",
            _ => "任意颜色",
        };
        SimplexYuan = Money.ToYuan(rule.SimplexPriceCents);
        DuplexYuan = Money.ToYuan(rule.DuplexPriceCents);
        Enabled = rule.Enabled;
        PriorityText = rule.Priority.ToString();
    }

    public PriceRule Rule { get; }

    public string PaperDisplay { get; }

    public string ColorDisplay { get; }

    public decimal SimplexYuan { get; set; }

    public decimal DuplexYuan { get; set; }

    public bool Enabled { get; set; }

    public string PriorityText { get; }

    public void ApplyToRule()
    {
        Rule.SimplexPriceCents = Money.FromYuan(SimplexYuan);
        Rule.DuplexPriceCents = Money.FromYuan(DuplexYuan);
        Rule.Enabled = Enabled;
        Rule.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}
