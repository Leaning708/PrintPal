using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
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

public sealed class ClosedBillRow{
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

    /// <summary>文件流水号（由 MainWindow 在刷新时从 PrintFlows 填充）。</summary>
    public string FlowNo { get; set; } = "";

    public string TimeText => Job.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss");

    public string TimeShortText => Job.CreatedAtUtc.ToLocalTime().ToString("HH:mm");

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

    // ---- structured details (right-hand 打印详情 panel) -------------------

    public string TimeFullText => Job.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string MetaLineText => $"{Job.UserName} · {TimeFullText}";

    public string PagesText => $"{Job.PageCount} 页/份 × {Job.Copies} 份 = {Job.BillableSides} 面";

    public string SheetsText
    {
        get
        {
            var parts = new List<string>();
            if (Job.TotalDuplexSheets > 0) { parts.Add($"双面 {Job.TotalDuplexSheets}"); }
            if (Job.TotalSimplexSheets > 0) { parts.Add($"单面 {Job.TotalSimplexSheets}"); }

            return parts.Count == 0
                ? $"{Job.TotalSheets} 张"
                : $"{Job.TotalSheets} 张（{string.Join(" + ", parts)}）";
        }
    }

    public string SimplexPriceText => $"{Money.FormatCents(Job.SimplexPriceCents)} / 张";

    public string DuplexPriceText => $"{Money.FormatCents(Job.DuplexPriceCents)} / 张";

    public string FormatText => Job.DocumentFormat.DisplayName();

    public string FileNameText => string.IsNullOrWhiteSpace(Job.DocumentPath)
        ? "—"
        : System.IO.Path.GetFileName(Job.DocumentPath);

    public string DirectoryText => string.IsNullOrWhiteSpace(Job.DocumentPath)
        ? "—"
        : System.IO.Path.GetDirectoryName(Job.DocumentPath) ?? Job.DocumentPath!;

    public string FullPathText => string.IsNullOrWhiteSpace(Job.DocumentPath) ? "—" : Job.DocumentPath!;

    public string FileSizeText => Job.DocumentSize > 0
        ? (Job.DocumentSize >= 1024 * 1024
            ? $"{Job.DocumentSize / 1024.0 / 1024.0:0.#} MB"
            : $"{Job.DocumentSize / 1024.0:0.#} KB")
        : "—";

    public string ShaFullText => string.IsNullOrWhiteSpace(Job.DocumentSha256) ? "—" : Job.DocumentSha256!;

    public string ShaShortText => string.IsNullOrWhiteSpace(Job.DocumentSha256)
        ? "—"
        : (Job.DocumentSha256!.Length > 20 ? Job.DocumentSha256.Substring(0, 16) + "…" : Job.DocumentSha256);

    public string StatusReasonText => Job.StatusReason ?? "";

    public bool HasStatusReason => !string.IsNullOrWhiteSpace(Job.StatusReason);

    public string ForwardedText => Job.ForwardedAtUtc is { } forwarded
        ? forwarded.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "—";

    public string BreakdownText => TryGetBreakdown(Job.PricingJson) ?? "";

    public IReadOnlyList<DetailLine> PriceLines => new[]
    {
        new DetailLine("单面价", SimplexPriceText),
        new DetailLine("双面价", DuplexPriceText),
    };

    public IReadOnlyList<DetailLine> SpecLines => new[]
    {
        new DetailLine("纸张", PaperText),
        new DetailLine("颜色", ColorText),
        new DetailLine("单双面", DuplexText),
        new DetailLine("页数", PagesText),
        new DetailLine("张数", SheetsText),
    };

    public IReadOnlyList<DetailLine> ForwardLines => new[]
    {
        new DetailLine("目标打印机", TargetText),
        new DetailLine("转发时间", ForwardedText),
    };

    public IReadOnlyList<DetailLine> DocLines => new[]
    {
        new DetailLine("格式", FormatText),
        new DetailLine("大小", FileSizeText),
        new DetailLine("文件名", FileNameText, FileNameText, ellipsis: true),
        new DetailLine("目录", DirectoryText, DirectoryText, copyValue: FullPathText),
        new DetailLine("SHA-256", ShaShortText, ShaFullText, mono: true),
    };

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

/// <summary>一行「标签 + 值」，用于打印详情面板的分组信息。</summary>
public sealed class DetailLine
{
    private static readonly System.Windows.Media.FontFamily MonoFont = new("Consolas");

    private static System.Windows.Media.FontFamily UiFont =>
        System.Windows.Application.Current?.TryFindResource("UiFontFamily") as System.Windows.Media.FontFamily
        ?? new System.Windows.Media.FontFamily(Themes.ThemeManager.DefaultUiFontFamily);

    public DetailLine(string label, string value, string? toolTip = null, bool mono = false, bool ellipsis = false, string? copyValue = null)
    {
        Label = label;
        Value = value;
        ToolTip = toolTip;
        Mono = mono;
        Ellipsis = ellipsis;
        CopyValue = copyValue;
    }

    public string Label { get; }

    public string Value { get; }

    public string? ToolTip { get; }

    public bool Mono { get; }

    public bool Ellipsis { get; }

    /// <summary>非空时在该行右侧显示「复制」按钮，复制这个完整值。</summary>
    public string? CopyValue { get; }

    public System.Windows.Visibility CopyVisibility =>
        CopyValue is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public System.Windows.Media.FontFamily FontFamily => Mono ? MonoFont : UiFont;
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
                : string.Join("/", Rule.MatchDuplexModes.Select(mode => mode.DisplayName()).Distinct()));

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

/// <summary>打印流水行。</summary>
public sealed class FlowRow
{
    public FlowRow(PrintFlowEntry entry)
    {
        Entry = entry;
    }

    public PrintFlowEntry Entry { get; }

    public PrintFlow Flow => Entry.Flow;

    public bool IsBill => Flow.Kind == PrintFlowKind.Bill;

    public string FlowNo => Flow.FlowNo;

    public string KindText => IsBill ? "账单" : "文件";

    public string TimeText => Flow.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public string BillText => Entry.BillDisplayName;

    /// <summary>账单已被删除时的标记（空字符串表示正常）。</summary>
    public string BillMissingText => !Entry.BillExists && Flow.BillId is not null ? "已删除" : "";

    public bool HasWarning => BillMissingText.Length > 0;

    public string DocumentText => IsBill ? "—" : Flow.DocumentNameSnapshot ?? "—";

    public string SpecText => Flow.SpecSnapshot ?? "";

    public string AmountText => Money.FormatCents(Entry.AmountCents);

    public string StatusText => FlowTexts.StatusText(Entry.StatusText);

    public string TargetText => Flow.TargetPrinterSnapshot ?? "—";

    public string ToolTip =>
        $"流水号：{FlowNo}\n类型：{KindText}\n时间：{TimeText}\n" +
        $"账单：{BillText}{(HasWarning ? "（已删除）" : "")}\n" +
        (IsBill ? "" : $"文档：{DocumentText}\n规格：{SpecText}\n目标：{TargetText}\n") +
        $"金额：{AmountText}\n状态：{StatusText}";
}

internal static class FlowTexts
{
    public static string StatusText(string raw) => raw switch
    {
        "Succeeded" => "已转发",
        "Failed" => "失败",
        "Unknown" => "未知",
        "NeedsReview" => "待确认",
        "Held" => "待确认",
        "Priced" => "待转发",
        "Unrouted" => "未路由",
        "Forwarding" => "转发中",
        "Captured" => "已捕获",
        "Canceled" => "已取消",
        "Closed" => "已结账",
        "Active" => "当前",
        "Parked" => "已挂起",
        _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw,
    };

    public static string ColorText(string raw)
    {
        if (Enum.TryParse<ColorMode>(raw, ignoreCase: true, out var mode))
        {
            return mode.DisplayName();
        }

        return string.IsNullOrWhiteSpace(raw) ? "—" : raw;
    }

    public static string DuplexText(string raw)
    {
        if (Enum.TryParse<DuplexMode>(raw, ignoreCase: true, out var mode))
        {
            return mode.DisplayName();
        }

        return string.IsNullOrWhiteSpace(raw) ? "—" : raw;
    }
}

/// <summary>统计页：每日明细。</summary>
public sealed class DailyStatRow
{
    public DailyStatRow(DailyPrintStat stat)
    {
        Stat = stat;
    }

    public DailyPrintStat Stat { get; }

    public string DateText => Stat.Date.ToString("yyyy-MM-dd");

    public string WeekText => Stat.Date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    public string RevenueText => Money.FormatCents(Stat.RevenueCents);

    public int BillCount => Stat.BillCount;

    public int JobCount => Stat.JobCount;

    public int PageCount => Stat.PageCount;

    public int SheetCount => Stat.SheetCount;
}

/// <summary>统计页：维度统计行（纸张/颜色/单双面/目标打印机）。</summary>
public sealed class DimensionStatRow
{
    public DimensionStatRow(FlowDimensionStat stat)
    {
        Stat = stat;
    }

    public FlowDimensionStat Stat { get; }

    public string DimensionText => Stat.Dimension switch
    {
        "paper" => "纸张",
        "color" => "颜色",
        "duplex" => "单双面",
        "printer" => "目标打印机",
        _ => Stat.Dimension,
    };

    public string KeyText => Stat.Dimension switch
    {
        "color" => FlowTexts.ColorText(Stat.Key),
        "duplex" => FlowTexts.DuplexText(Stat.Key),
        _ => Stat.Key,
    };

    public int JobCount => Stat.JobCount;

    public int PageCount => Stat.PageCount;

    public int SheetCount => Stat.SheetCount;

    public string AmountText => Money.FormatCents(Stat.AmountCents);
}

/// <summary>统计页：时段分布柱状图（0–23 点）。</summary>
public sealed class HourBarRow
{
    public HourBarRow(HourPrintStat stat, int maxCount)
    {
        Stat = stat;
        var safeMax = Math.Max(1, maxCount);
        BarHeight = stat.JobCount == 0 ? 3 : 8 + 72.0 * stat.JobCount / safeMax;
    }

    public HourPrintStat Stat { get; }

    public double BarHeight { get; }

    public string HourText => Stat.Hour.ToString("00");

    public string CountText => Stat.JobCount.ToString();

    public string ToolTip => $"{Stat.Hour:00}:00 · {Stat.JobCount} 单 · {Money.FormatCents(Stat.AmountCents)}";
}
