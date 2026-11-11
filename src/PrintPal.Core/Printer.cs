namespace PrintPal.Core;

public sealed class Printer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public PrinterProtocol Protocol { get; set; } = PrinterProtocol.Ipp;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8631;

    public string ResourcePath { get; set; } = "/ipp/print";

    /// <summary>DocumentFormat.Unknown means "auto / prefer the job format".</summary>
    public DocumentFormat FormatPreference { get; set; } = DocumentFormat.Unknown;

    public bool Enabled { get; set; } = true;

    public bool IsDefault { get; set; }

    public PrinterCapabilities? Capabilities { get; set; }

    /// <summary>
    /// 路由能力声明（人工配置）：本机声明能处理哪些纸张 / 颜色 / 单双面。
    /// 语义是"笛卡尔积"——勾了支持的纸张 × 支持的颜色 × 支持的单双面。
    /// null = 未声明能力，不参与自动路由（打印机页会提示去配置）。
    /// </summary>
    public PrinterRoutingCapability? Routing { get; set; }

    public DateTimeOffset? LastTestedAtUtc { get; set; }

    public bool? LastTestOk { get; set; }

    /// <summary>最后一次连接成功的时间（失败时保留，用于显示“上次成功”）。</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; set; }

    public string? Notes { get; set; }

    public string Uri => Protocol == PrinterProtocol.Ipp
        ? $"ipp://{Host}:{Port}{NormalizePath(ResourcePath)}"
        : $"raw://{Host}:{Port}";

    public override string ToString() => $"{Name} ({Uri})";

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/ipp/print" : (path.StartsWith('/') ? path : "/" + path);
}

public sealed class PrinterCapabilities
{
    public string? Name { get; set; }

    public string? MakeAndModel { get; set; }

    public List<DocumentFormat> DocumentFormats { get; set; } = new();

    public List<PaperSize> MediaSizes { get; set; } = new();

    public List<ColorMode> ColorModes { get; set; } = new();

    public List<DuplexMode> DuplexModes { get; set; } = new();

    public int MinCopies { get; set; } = 1;

    public int MaxCopies { get; set; } = 999;

    public bool SupportsFormat(DocumentFormat format) =>
        DocumentFormats.Count == 0 || DocumentFormats.Contains(format);

    public static PrinterCapabilities CreateDefault(string? name = null) => new()
    {
        Name = name,
        MakeAndModel = "PrintPal Virtual Printer",
        DocumentFormats = new List<DocumentFormat> { DocumentFormat.Pdf, DocumentFormat.Pwg },
        MediaSizes = PaperSize.Standards.ToList(),
        ColorModes = new List<ColorMode> { ColorMode.Monochrome, ColorMode.Color },
        DuplexModes = new List<DuplexMode>
        {
            DuplexMode.Simplex,
            DuplexMode.TwoSidedLongEdge,
            DuplexMode.TwoSidedShortEdge,
        },
        MinCopies = 1,
        MaxCopies = 999,
    };
}
/// <summary>
/// 打印机路由能力声明（人工配置，产品模型）：
/// 纸张集合 × 颜色集合 × 单双面集合；空集合表示"任意"。
/// 例如勾选 A4/A5 + 彩色 → 代表 A4/A5 都支持彩色，不再精确到单个组合。
/// </summary>
public sealed class PrinterRoutingCapability
{
    /// <summary>支持的纸张名（Canonical）；空 = 任意纸张。</summary>
    public List<string> PaperSizes { get; set; } = new();

    /// <summary>支持的颜色；空 = 任意颜色。</summary>
    public List<ColorMode> ColorModes { get; set; } = new();

    /// <summary>支持的单双面；空 = 任意单双面。</summary>
    public List<DuplexMode> DuplexModes { get; set; } = new();

    public bool SupportsPaper(PaperSize paper) =>
        PaperSizes.Count == 0 ||
        PaperSizes.Any(size => string.Equals(size, paper.Name, StringComparison.OrdinalIgnoreCase));

    public bool SupportsColor(ColorMode color) =>
        ColorModes.Count == 0 || ColorModes.Contains(color);

    public bool SupportsDuplex(DuplexMode duplex) =>
        DuplexModes.Count == 0 ||
        (duplex.IsDuplex()
            ? DuplexModes.Any(mode => mode.IsDuplex())
            : DuplexModes.Contains(DuplexMode.Simplex));

    public bool Supports(PrintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return SupportsPaper(job.PaperSize) && SupportsColor(job.ColorMode) && SupportsDuplex(job.DuplexMode);
    }

    /// <summary>只声明可处理全部标准纸张/颜色/单双面（新打印机默认）。</summary>
    public static PrinterRoutingCapability Any() => new();

    /// <summary>按"已检测能力"推导一份初始声明（测试连接后可用于预填）。</summary>
    public static PrinterRoutingCapability FromDetected(PrinterCapabilities? capabilities)
    {
        var routing = new PrinterRoutingCapability();
        if (capabilities is null)
        {
            return routing;
        }

        if (capabilities.MediaSizes.Count > 0)
        {
            routing.PaperSizes = capabilities.MediaSizes
                .Select(size => size.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (capabilities.ColorModes.Count > 0)
        {
            routing.ColorModes = capabilities.ColorModes
                .Where(mode => mode is ColorMode.Monochrome or ColorMode.Color)
                .Distinct()
                .ToList();
        }

        if (capabilities.DuplexModes.Count > 0)
        {
            routing.DuplexModes = capabilities.DuplexModes
                .Select(mode => mode.IsDuplex() ? DuplexMode.TwoSidedLongEdge : DuplexMode.Simplex)
                .Distinct()
                .ToList();
        }

        return routing;
    }

    /// <summary>人类可读摘要，如「A4/A5 · 黑白/彩色 · 单面/双面」。</summary>
    public string Describe()
    {
        var papers = PaperSizes.Count == 0 ? "任意纸张" : string.Join("/", PaperSizes);
        var colors = ColorModes.Count == 0
            ? "任意颜色"
            : string.Join("/", ColorModes.Distinct().Select(mode => mode.DisplayName()));
        var duplex = DuplexModes.Count == 0
            ? "任意单双面"
            : string.Join("/", new[]
            {
                DuplexModes.Contains(DuplexMode.Simplex) ? "单面" : null,
                DuplexModes.Any(mode => mode.IsDuplex()) ? "双面" : null,
            }.Where(part => part is not null));

        return $"{papers} · {colors} · {duplex}";
    }
}
/// <summary>打印机健康状态（列表「状态」列）。</summary>
public enum PrinterStatusKind
{
    Disabled = 0,
    Untested = 1,
    Online = 2,
    Failed = 3,
}

/// <summary>打印机状态的展示文案；纯逻辑，便于单测。</summary>
public sealed record PrinterStatus(
    PrinterStatusKind Kind,
    string PillText,
    string? DetailText,
    string TooltipText)
{
    /// <summary>短时间：2026-11-11 14:05（本地时区）。</summary>
    public static string FormatShort(DateTimeOffset? time) =>
        time is null ? "" : time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>完整时间：2026-11-11 14:05:22（本地时区）。</summary>
    public static string FormatFull(DateTimeOffset? time) =>
        time is null ? "" : time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>测试结果文案：成功带时间；失败带上次成功时间（无则只显示失败）。</summary>
    public static string FormatTestResult(bool ok, DateTimeOffset? testedAt, DateTimeOffset? lastSuccessAt) =>
        ok
            ? $"连接成功 · {FormatFull(testedAt)}"
            : lastSuccessAt is null
                ? "连接失败"
                : $"连接失败 · 上次成功 {FormatShort(lastSuccessAt)}";

    public static PrinterStatus Describe(Printer printer)
    {
        ArgumentNullException.ThrowIfNull(printer);

        if (!printer.Enabled)
        {
            return new PrinterStatus(PrinterStatusKind.Disabled, "已停用", null, "打印机已停用，不参与自动检测");
        }

        if (printer.LastTestOk == true)
        {
            var detail = FormatShort(printer.LastTestedAtUtc);
            return new PrinterStatus(
                PrinterStatusKind.Online,
                "在线",
                string.IsNullOrEmpty(detail) ? null : detail,
                $"最近检测 {FormatFull(printer.LastTestedAtUtc)}");
        }

        if (printer.LastTestOk == false)
        {
            var detail = printer.LastSuccessAtUtc is null
                ? null
                : $"上次成功 {FormatShort(printer.LastSuccessAtUtc)}";
            var tooltip = printer.LastSuccessAtUtc is null
                ? $"最近检测 {FormatFull(printer.LastTestedAtUtc)} · 从未成功"
                : $"最近检测 {FormatFull(printer.LastTestedAtUtc)} · 上次成功 {FormatFull(printer.LastSuccessAtUtc)}";
            return new PrinterStatus(PrinterStatusKind.Failed, "连接失败", detail, tooltip);
        }

        return new PrinterStatus(PrinterStatusKind.Untested, "未检测", null, "尚未检测");
    }
}

/// <summary>打印能力校验（纸张/颜色/单双面都必须至少选一种）。</summary>
public static class PrinterCapabilityValidation
{
    public static IReadOnlyList<string> Validate(PrinterRoutingCapability? capability)
    {
        var errors = new List<string>();
        if (capability is null)
        {
            errors.Add("请先配置打印能力。");
            return errors;
        }

        if (capability.PaperSizes.Count == 0)
        {
            errors.Add("请至少勾选一种纸张（A3 / A4 / A5）。");
        }

        if (capability.ColorModes.Count == 0)
        {
            errors.Add("请至少勾选一种颜色（黑白 / 彩色）。");
        }

        if (capability.DuplexModes.Count == 0)
        {
            errors.Add("请至少勾选一种单双面（单面 / 双面）。");
        }

        return errors;
    }
}