using System.Windows;
using System.Windows.Media;
using PrintPal.Core.Theming;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PrintPal.App.Themes;

/// <summary>
/// 一套界面主题：Key / 名称 / 是否深色 / 颜色 token 表。
/// token 名与 Theme.xaml 里的 Brush 资源键一一对应（SidebarTop/SidebarBottom 用于渐变）。
/// </summary>
public sealed record ThemeDefinition(string Key, string Name, bool IsDark, IReadOnlyDictionary<string, Color> Colors);

/// <summary>
/// 运行时主题切换：直接修改 Theme.xaml 里共享 Brush 的 Color（XAML 用 DynamicResource 引用，
/// 替换资源后全局刷新）。浅色/深色各 5 套，共 10 套。
/// </summary>
public static class ThemeManager
{
    public const string DefaultKey = "light-blue";

    /// <summary>自定义配色的设置键（uiTheme 里存这个值，具体参数在 uiThemeCustom）。</summary>
    public const string CustomKey = "custom";

    /// <summary>由种子色派生出一套完整主题（方案 A）。</summary>
    public static ThemeDefinition BuildCustom(ThemeCustomization customization)
    {
        ArgumentNullException.ThrowIfNull(customization);

        var normalized = customization.Normalized();
        var palette = ThemePaletteBuilder.Build(normalized);
        var colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        foreach (var (token, hex) in palette)
        {
            colors[token] = Hex(hex);
        }

        return new ThemeDefinition(CustomKey, normalized.Name, normalized.IsDark, colors);
    }

    private static readonly Dictionary<string, string> LightBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AccentBrush"] = "#2563EB",
        ["AccentDarkBrush"] = "#1D4ED8",
        ["AccentSoftBrush"] = "#EAF1FF",
        ["PageBackgroundBrush"] = "#F3F6FB",
        ["CardBrush"] = "#FFFFFF",
        ["LineBrush"] = "#E6EBF3",
        ["LineStrongBrush"] = "#D7DEEA",
        ["InkBrush"] = "#0F172A",
        ["InkSecondaryBrush"] = "#334155",
        ["MutedBrush"] = "#64748B",
        ["SoftMutedBrush"] = "#94A3B8",
        ["HoverFillBrush"] = "#F5F8FD",
        ["SidebarTop"] = "#EAF0FB",
        ["SidebarBottom"] = "#D8E3F5",
        ["SidebarCardBrush"] = "#FFFFFF",
        ["SidebarMutedBrush"] = "#5B7BB5",
        ["SidebarNavTextBrush"] = "#33507F",
        ["SidebarNavHoverBrush"] = "#E1EAF8",
        ["SidebarNavActiveBrush"] = "#2563EB",
        ["SidebarSectionBrush"] = "#7C93BF",
        ["SidebarCountBrush"] = "#D5E2F8",
        ["SidebarCountTextBrush"] = "#1D4ED8",
        ["SidebarTextBrush"] = "#1E3A8A",
        ["SidebarIndicatorBrush"] = "#FFFFFF",
        ["InsetBackgroundBrush"] = "#F8FAFC",
        ["InsetBorderBrush"] = "#EDF1F7",
        ["SubtleButtonBackgroundBrush"] = "#F6F8FC",
        ["GhostHoverBrush"] = "#EEF4FF",
        ["TooltipBackgroundBrush"] = "#0F172A",
        ["TooltipForegroundBrush"] = "#FFFFFF",
        ["TooltipBorderBrush"] = "#334155",
        ["PillNeutralBackgroundBrush"] = "#F1F5F9",
        ["PillNeutralTextBrush"] = "#64748B",
        ["OverlayBrush"] = "#B8FFFFFF",
        ["ChartToggleBackgroundBrush"] = "#F6F8FC",
        ["DangerBorderBrush"] = "#F6C9C9",
        ["WarningBorderBrush"] = "#F3E0C6",
        ["HoverOverlayBrush"] = "#0F172A",
        ["MinimalBackgroundBrush"] = "#FFFFFF",
        ["MinimalTopBarBrush"] = "#2563EB",
        ["MinimalTextBrush"] = "#0F172A",
        ["MinimalMutedBrush"] = "#64748B",
        ["MinimalChipBrush"] = "#F1F5F9",
        ["MinimalChipBorderBrush"] = "#E2E8F0",
        ["MinimalChipTextBrush"] = "#475569",
        ["MinimalChipActiveBrush"] = "#2563EB",
        ["MinimalButtonBackgroundBrush"] = "#F1F5F9",
        ["MinimalButtonForegroundBrush"] = "#0F172A",
        ["MinimalNoticeBackgroundBrush"] = "#F59E0B",
        ["MinimalNoticeBorderBrush"] = "#FBBF24",
        ["MinimalNoticeForegroundBrush"] = "#1F2937",
    };

    private static readonly Dictionary<string, string> DarkBase = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AccentBrush"] = "#60A5FA",
        ["AccentDarkBrush"] = "#3B82F6",
        ["AccentSoftBrush"] = "#1E3A5F",
        ["PageBackgroundBrush"] = "#0B1220",
        ["CardBrush"] = "#131C2E",
        ["LineBrush"] = "#1F2A3D",
        ["LineStrongBrush"] = "#2A3850",
        ["InkBrush"] = "#E6EDF7",
        ["InkSecondaryBrush"] = "#C3CFE0",
        ["MutedBrush"] = "#8FA0B8",
        ["SoftMutedBrush"] = "#6B7C94",
        ["HoverFillBrush"] = "#182338",
        ["SidebarTop"] = "#0F1A2E",
        ["SidebarBottom"] = "#0A1220",
        ["SidebarCardBrush"] = "#16233A",
        ["SidebarMutedBrush"] = "#8FA0B8",
        ["SidebarNavTextBrush"] = "#A9BBD6",
        ["SidebarNavHoverBrush"] = "#1B2A44",
        ["SidebarNavActiveBrush"] = "#2563EB",
        ["SidebarSectionBrush"] = "#6B7C94",
        ["SidebarCountBrush"] = "#1E3A5F",
        ["SidebarCountTextBrush"] = "#93C5FD",
        ["SidebarTextBrush"] = "#E6EDF7",
        ["SidebarIndicatorBrush"] = "#FFFFFF",
        ["InsetBackgroundBrush"] = "#16223A",
        ["InsetBorderBrush"] = "#22304A",
        ["SubtleButtonBackgroundBrush"] = "#1A2740",
        ["GhostHoverBrush"] = "#1E2E4A",
        ["TooltipBackgroundBrush"] = "#1E293B",
        ["TooltipForegroundBrush"] = "#E2E8F0",
        ["TooltipBorderBrush"] = "#334155",
        ["PillNeutralBackgroundBrush"] = "#1E2A40",
        ["PillNeutralTextBrush"] = "#A9BBD6",
        ["OverlayBrush"] = "#B80B1220",
        ["ChartToggleBackgroundBrush"] = "#1A2740",
        ["DangerBorderBrush"] = "#7F1D1D",
        ["WarningBorderBrush"] = "#78350F",
        ["HoverOverlayBrush"] = "#FFFFFF",
        ["MinimalBackgroundBrush"] = "#0B1322",
        ["MinimalTopBarBrush"] = "#3B82F6",
        ["MinimalTextBrush"] = "#FFFFFF",
        ["MinimalMutedBrush"] = "#94A3B8",
        ["MinimalChipBrush"] = "#1E293B",
        ["MinimalChipBorderBrush"] = "#334155",
        ["MinimalChipTextBrush"] = "#CBD5E1",
        ["MinimalChipActiveBrush"] = "#2563EB",
        ["MinimalButtonBackgroundBrush"] = "#1E293B",
        ["MinimalButtonForegroundBrush"] = "#E2E8F0",
        ["MinimalNoticeBackgroundBrush"] = "#F59E0B",
        ["MinimalNoticeBorderBrush"] = "#FBBF24",
        ["MinimalNoticeForegroundBrush"] = "#1F2937",
    };

    public static IReadOnlyList<ThemeDefinition> Themes { get; } = new[]
    {
        // ---------------- 浅色 5 套 ----------------
        Make("light-blue", "浅蓝", false, new()
        {
            ["AccentBrush"] = "#2563EB", ["AccentDarkBrush"] = "#1D4ED8", ["AccentSoftBrush"] = "#EAF1FF",
            ["PageBackgroundBrush"] = "#F3F6FB", ["HoverFillBrush"] = "#F5F8FD",
            ["LineBrush"] = "#E6EBF3", ["LineStrongBrush"] = "#D7DEEA",
            ["InkBrush"] = "#0F172A", ["InkSecondaryBrush"] = "#334155", ["MutedBrush"] = "#64748B", ["SoftMutedBrush"] = "#94A3B8",
            ["SidebarTop"] = "#EAF0FB", ["SidebarBottom"] = "#D8E3F5", ["SidebarCardBrush"] = "#FFFFFF", ["SidebarMutedBrush"] = "#5B7BB5",
            ["SidebarNavTextBrush"] = "#33507F", ["SidebarNavHoverBrush"] = "#E1EAF8", ["SidebarNavActiveBrush"] = "#2563EB",
            ["SidebarSectionBrush"] = "#7C93BF", ["SidebarCountBrush"] = "#D5E2F8", ["SidebarCountTextBrush"] = "#1D4ED8",
            ["SidebarTextBrush"] = "#1E3A8A", ["MinimalTopBarBrush"] = "#2563EB", ["MinimalChipActiveBrush"] = "#2563EB",
        }),
        Make("light-green", "浅绿", false, new()
        {
            ["AccentBrush"] = "#059669", ["AccentDarkBrush"] = "#047857", ["AccentSoftBrush"] = "#E7F7F0",
            ["PageBackgroundBrush"] = "#F2F8F5", ["HoverFillBrush"] = "#F3FAF7",
            ["LineBrush"] = "#E1EEE8", ["LineStrongBrush"] = "#CFE3DA",
            ["InkBrush"] = "#0B1F17", ["InkSecondaryBrush"] = "#2F4A3E", ["MutedBrush"] = "#5B7A6C", ["SoftMutedBrush"] = "#8FA99C",
            ["SidebarTop"] = "#E6F5EE", ["SidebarBottom"] = "#D0EBDD", ["SidebarCardBrush"] = "#FFFFFF", ["SidebarMutedBrush"] = "#4A8A70",
            ["SidebarNavTextBrush"] = "#2F6B55", ["SidebarNavHoverBrush"] = "#D9F0E4", ["SidebarNavActiveBrush"] = "#059669",
            ["SidebarSectionBrush"] = "#6FA88F", ["SidebarCountBrush"] = "#CFEBDD", ["SidebarCountTextBrush"] = "#047857",
            ["SidebarTextBrush"] = "#064E3B", ["MinimalTopBarBrush"] = "#059669", ["MinimalChipActiveBrush"] = "#059669",
        }),
        Make("light-orange", "浅橙", false, new()
        {
            ["AccentBrush"] = "#D97706", ["AccentDarkBrush"] = "#B45309", ["AccentSoftBrush"] = "#FEF3E2",
            ["PageBackgroundBrush"] = "#FBF6EF", ["HoverFillBrush"] = "#FDF8F1",
            ["LineBrush"] = "#F0E4D2", ["LineStrongBrush"] = "#E4D3BB",
            ["InkBrush"] = "#241A0E", ["InkSecondaryBrush"] = "#4A3A26", ["MutedBrush"] = "#7A6A52", ["SoftMutedBrush"] = "#A99A82",
            ["SidebarTop"] = "#FBF1E1", ["SidebarBottom"] = "#F2E0C5", ["SidebarCardBrush"] = "#FFFFFF", ["SidebarMutedBrush"] = "#9A7A3E",
            ["SidebarNavTextBrush"] = "#7A5A24", ["SidebarNavHoverBrush"] = "#F6E8CE", ["SidebarNavActiveBrush"] = "#D97706",
            ["SidebarSectionBrush"] = "#B08A4A", ["SidebarCountBrush"] = "#F3E2C4", ["SidebarCountTextBrush"] = "#B45309",
            ["SidebarTextBrush"] = "#7C2D12", ["MinimalTopBarBrush"] = "#D97706", ["MinimalChipActiveBrush"] = "#D97706",
        }),
        Make("light-purple", "浅紫", false, new()
        {
            ["AccentBrush"] = "#7C3AED", ["AccentDarkBrush"] = "#6D28D9", ["AccentSoftBrush"] = "#F1EBFF",
            ["PageBackgroundBrush"] = "#F7F4FC", ["HoverFillBrush"] = "#F9F6FE",
            ["LineBrush"] = "#EAE2F6", ["LineStrongBrush"] = "#DCCFF0",
            ["InkBrush"] = "#1B1230", ["InkSecondaryBrush"] = "#3D3157", ["MutedBrush"] = "#6B5F86", ["SoftMutedBrush"] = "#9C90B5",
            ["SidebarTop"] = "#F1ECFA", ["SidebarBottom"] = "#E2D7F4", ["SidebarCardBrush"] = "#FFFFFF", ["SidebarMutedBrush"] = "#7A66A8",
            ["SidebarNavTextBrush"] = "#5B4A85", ["SidebarNavHoverBrush"] = "#E8DFF7", ["SidebarNavActiveBrush"] = "#7C3AED",
            ["SidebarSectionBrush"] = "#9A86C4", ["SidebarCountBrush"] = "#E6DCF6", ["SidebarCountTextBrush"] = "#6D28D9",
            ["SidebarTextBrush"] = "#4C1D95", ["MinimalTopBarBrush"] = "#7C3AED", ["MinimalChipActiveBrush"] = "#7C3AED",
        }),
        Make("light-gray", "浅灰", false, new()
        {
            ["AccentBrush"] = "#475569", ["AccentDarkBrush"] = "#334155", ["AccentSoftBrush"] = "#EEF1F5",
            ["PageBackgroundBrush"] = "#F4F6F8", ["HoverFillBrush"] = "#F7F9FA",
            ["LineBrush"] = "#E3E8ED", ["LineStrongBrush"] = "#D3DAE1",
            ["InkBrush"] = "#0F172A", ["InkSecondaryBrush"] = "#334155", ["MutedBrush"] = "#64748B", ["SoftMutedBrush"] = "#94A3B8",
            ["SidebarTop"] = "#EEF1F5", ["SidebarBottom"] = "#DDE3EA", ["SidebarCardBrush"] = "#FFFFFF", ["SidebarMutedBrush"] = "#6B7A8C",
            ["SidebarNavTextBrush"] = "#4A5A6A", ["SidebarNavHoverBrush"] = "#E3E9EF", ["SidebarNavActiveBrush"] = "#475569",
            ["SidebarSectionBrush"] = "#8A99A8", ["SidebarCountBrush"] = "#E2E8EF", ["SidebarCountTextBrush"] = "#334155",
            ["SidebarTextBrush"] = "#1F2937", ["MinimalTopBarBrush"] = "#475569", ["MinimalChipActiveBrush"] = "#475569",
        }),

        // ---------------- 深色 5 套 ----------------
        Make("dark-blue", "深蓝", true, new()
        {
            ["AccentBrush"] = "#60A5FA", ["AccentDarkBrush"] = "#3B82F6", ["AccentSoftBrush"] = "#1E2A3F",
            ["PageBackgroundBrush"] = "#0F1219", ["CardBrush"] = "#171C26", ["HoverFillBrush"] = "#1D2430",
            ["LineBrush"] = "#222A38", ["LineStrongBrush"] = "#2E3849",
            ["InkBrush"] = "#E7EAF0", ["InkSecondaryBrush"] = "#C4CAD6", ["MutedBrush"] = "#8D95A5", ["SoftMutedBrush"] = "#667082",
            ["SidebarTop"] = "#141922", ["SidebarBottom"] = "#0F131B", ["SidebarCardBrush"] = "#1C222E", ["SidebarMutedBrush"] = "#8D95A5",
            ["SidebarNavTextBrush"] = "#B6BDCB", ["SidebarNavHoverBrush"] = "#1F2634", ["SidebarNavActiveBrush"] = "#3B82F6",
            ["SidebarSectionBrush"] = "#6E7687", ["SidebarCountBrush"] = "#232E45", ["SidebarCountTextBrush"] = "#93C5FD",
            ["SidebarTextBrush"] = "#E7EAF0", ["MinimalTopBarBrush"] = "#3B82F6", ["MinimalChipActiveBrush"] = "#2563EB",
        }),
        Make("dark-green", "深绿", true, new()
        {
            ["AccentBrush"] = "#34D399", ["AccentDarkBrush"] = "#10B981", ["AccentSoftBrush"] = "#182B25",
            ["PageBackgroundBrush"] = "#0E1310", ["CardBrush"] = "#161C18", ["HoverFillBrush"] = "#1C2420",
            ["LineBrush"] = "#212B24", ["LineStrongBrush"] = "#2C3830",
            ["InkBrush"] = "#E6EDE9", ["InkSecondaryBrush"] = "#C2CFC8", ["MutedBrush"] = "#8A9C92", ["SoftMutedBrush"] = "#64746B",
            ["SidebarTop"] = "#121814", ["SidebarBottom"] = "#0D120F", ["SidebarCardBrush"] = "#1B241E", ["SidebarMutedBrush"] = "#8A9C92",
            ["SidebarNavTextBrush"] = "#B7C4BD", ["SidebarNavHoverBrush"] = "#1E2822", ["SidebarNavActiveBrush"] = "#10B981",
            ["SidebarSectionBrush"] = "#6E7F76", ["SidebarCountBrush"] = "#1C2F27", ["SidebarCountTextBrush"] = "#6EE7B7",
            ["SidebarTextBrush"] = "#E6EDE9", ["MinimalTopBarBrush"] = "#10B981", ["MinimalChipActiveBrush"] = "#059669",
        }),
        Make("dark-amber", "琥珀", true, new()
        {
            ["AccentBrush"] = "#FBBF24", ["AccentDarkBrush"] = "#F59E0B", ["AccentSoftBrush"] = "#2E2A20",
            ["PageBackgroundBrush"] = "#121110", ["CardBrush"] = "#1B1917", ["HoverFillBrush"] = "#211F1C",
            ["LineBrush"] = "#2A2723", ["LineStrongBrush"] = "#38342E",
            ["InkBrush"] = "#EFEAE2", ["InkSecondaryBrush"] = "#D0C9BC", ["MutedBrush"] = "#A29B8C", ["SoftMutedBrush"] = "#767061",
            ["SidebarTop"] = "#1A1815", ["SidebarBottom"] = "#12100E", ["SidebarCardBrush"] = "#242019", ["SidebarMutedBrush"] = "#A29B8C",
            ["SidebarNavTextBrush"] = "#D3CCC0", ["SidebarNavHoverBrush"] = "#262320", ["SidebarNavActiveBrush"] = "#D97706",
            ["SidebarSectionBrush"] = "#7E7869", ["SidebarCountBrush"] = "#34302A", ["SidebarCountTextBrush"] = "#FCD34D",
            ["SidebarTextBrush"] = "#EFEAE2", ["MinimalTopBarBrush"] = "#D97706", ["MinimalChipActiveBrush"] = "#B45309",
        }),
        Make("dark-purple", "深紫", true, new()
        {
            ["AccentBrush"] = "#A78BFA", ["AccentDarkBrush"] = "#8B5CF6", ["AccentSoftBrush"] = "#262230",
            ["PageBackgroundBrush"] = "#121017", ["CardBrush"] = "#191622", ["HoverFillBrush"] = "#1F1C2A",
            ["LineBrush"] = "#242030", ["LineStrongBrush"] = "#302B40",
            ["InkBrush"] = "#EAE8F2", ["InkSecondaryBrush"] = "#C9C5D8", ["MutedBrush"] = "#9490A6", ["SoftMutedBrush"] = "#6B687C",
            ["SidebarTop"] = "#17141F", ["SidebarBottom"] = "#100E16", ["SidebarCardBrush"] = "#201C2C", ["SidebarMutedBrush"] = "#9490A6",
            ["SidebarNavTextBrush"] = "#C6C2D6", ["SidebarNavHoverBrush"] = "#242030", ["SidebarNavActiveBrush"] = "#8B5CF6",
            ["SidebarSectionBrush"] = "#75708A", ["SidebarCountBrush"] = "#292537", ["SidebarCountTextBrush"] = "#C4B5FD",
            ["SidebarTextBrush"] = "#EAE8F2", ["MinimalTopBarBrush"] = "#8B5CF6", ["MinimalChipActiveBrush"] = "#7C3AED",
        }),
        Make("dark-charcoal", "炭黑", true, new()
        {
            ["AccentBrush"] = "#94A3B8", ["AccentDarkBrush"] = "#64748B", ["AccentSoftBrush"] = "#212630",
            ["PageBackgroundBrush"] = "#101214", ["CardBrush"] = "#17191C", ["HoverFillBrush"] = "#1D2024",
            ["LineBrush"] = "#23262B", ["LineStrongBrush"] = "#2F333A",
            ["InkBrush"] = "#E9EAEC", ["InkSecondaryBrush"] = "#C6C9CE", ["MutedBrush"] = "#8F949C", ["SoftMutedBrush"] = "#666B74",
            ["SidebarTop"] = "#15171A", ["SidebarBottom"] = "#0F1113", ["SidebarCardBrush"] = "#1D2024", ["SidebarMutedBrush"] = "#8F949C",
            ["SidebarNavTextBrush"] = "#BAC0C8", ["SidebarNavHoverBrush"] = "#1F2329", ["SidebarNavActiveBrush"] = "#64748B",
            ["SidebarSectionBrush"] = "#71767F", ["SidebarCountBrush"] = "#22262B", ["SidebarCountTextBrush"] = "#CBD5E1",
            ["SidebarTextBrush"] = "#E9EAEC", ["MinimalTopBarBrush"] = "#475569", ["MinimalChipActiveBrush"] = "#475569",
        }),
    };

    public static ThemeDefinition Find(string? key)
    {
        return Themes.FirstOrDefault(theme => string.Equals(theme.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? Themes[0];
    }

    public static void Apply(ThemeDefinition theme)
    {
        foreach (var (key, color) in theme.Colors)
        {
            if (key is "SidebarTop" or "SidebarBottom")
            {
                continue;
            }

            SetBrush(key, color);
        }

        SetGradient("SidebarBrush", theme.Colors["SidebarTop"], theme.Colors["SidebarBottom"]);
        ApplyFillBrushes(theme);
    }

    /// <summary>默认界面字体（与 Theme.xaml 里的 UiFontFamily 初始值保持一致）。</summary>
    public const string DefaultUiFontFamily = "Microsoft YaHei UI";

    /// <summary>
    /// 切换界面字体：替换定义 UiFontFamily 的资源字典条目，并给现有窗口设本地字体，
    /// 保证所有 DynamicResource 引用与继承链立即生效。传 null/空串表示恢复默认字体。
    /// </summary>
    public static void ApplyUiFontFamily(string? familyName)
    {
        var name = string.IsNullOrWhiteSpace(familyName) ? DefaultUiFontFamily : familyName;
        if (Application.Current is not { } app)
        {
            return;
        }

        var family = new System.Windows.Media.FontFamily(name);

        // 关键：UiFontFamily 定义在合并字典（Theme.xaml）里，必须改字典本身才会通知
        // DynamicResource 重新解析；只往 Application.Resources 根上覆盖是收不到通知的。
        var replaced = false;
        foreach (var dictionary in app.Resources.MergedDictionaries)
        {
            if (dictionary.Contains("UiFontFamily"))
            {
                dictionary["UiFontFamily"] = family;
                replaced = true;
            }
        }

        if (!replaced)
        {
            app.Resources["UiFontFamily"] = family;
        }

        // 兜底：给已打开的窗口设本地字体，导航等没有显式字体样式、靠继承的文字立刻跟随。
        foreach (Window window in app.Windows)
        {
            window.FontFamily = family;
        }
    }

    private static ThemeDefinition Make(string key, string name, bool isDark, Dictionary<string, string> overrides)
    {
        var colors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        foreach (var (token, hex) in isDark ? DarkBase : LightBase)
        {
            colors[token] = Hex(hex);
        }

        foreach (var (token, hex) in overrides)
        {
            colors[token] = Hex(hex);
        }

        // 滚动条滑块跟随每套主题的色调：由该主题的 MutedBrush 与 CardBrush 混合得到，
        // 而不是所有浅色主题共用一个蓝灰、所有深色主题共用一个深灰。
        // 悬停/拖动再逐级加深，保证三态在同一色相上递进。
        colors["ScrollThumbBrush"] = Blend(colors["CardBrush"], colors["MutedBrush"], 0.45);
        colors["ScrollThumbHoverBrush"] = Blend(colors["CardBrush"], colors["MutedBrush"], 0.65);
        colors["ScrollThumbPressedBrush"] = Blend(colors["CardBrush"], colors["MutedBrush"], 0.85);

        // 中性面同样跟着主题派生：彩色主题里不再出现固定冷灰块（指标卡/次要按钮/chip）。
        colors["InsetBackgroundBrush"] = Blend(colors["PageBackgroundBrush"], colors["AccentSoftBrush"], 0.55);
        colors["InsetBorderBrush"] = Blend(colors["LineBrush"], colors["AccentBrush"], 0.15);
        colors["SubtleButtonBackgroundBrush"] = Blend(colors["PageBackgroundBrush"], colors["LineBrush"], 0.45);
        colors["ChartToggleBackgroundBrush"] = colors["SubtleButtonBackgroundBrush"];
        colors["PillNeutralBackgroundBrush"] = Blend(colors["PageBackgroundBrush"], colors["AccentSoftBrush"], 0.30);
        colors["PillNeutralTextBrush"] = Blend(colors["InkSecondaryBrush"], colors["AccentBrush"], 0.25);

        // 极简模式（深色卡片）内的 chip/按钮跟随 MinimalTopBar，避免纯灰。
        colors["MinimalChipBrush"] = Blend(colors["MinimalBackgroundBrush"], colors["MinimalTopBarBrush"], 0.14);
        colors["MinimalChipBorderBrush"] = Blend(colors["MinimalBackgroundBrush"], colors["MinimalTopBarBrush"], 0.24);
        colors["MinimalButtonBackgroundBrush"] = Blend(colors["MinimalBackgroundBrush"], colors["MinimalTopBarBrush"], 0.16);

        // 语义状态色（状态胶囊/提示）：浅色主题沿用浅底深字，深色主题改为暗底亮字。
        // 软底由 CardBrush 与语义色混合派生，自动与每套主题的底色协调。
        colors["SuccessBrush"] = isDark ? Hex("#34D399") : Hex("#0E9F6E");
        colors["SuccessSoftBrush"] = Blend(colors["CardBrush"], colors["SuccessBrush"], isDark ? 0.14 : 0.12);
        colors["WarningBrush"] = isDark ? Hex("#FCC84D") : Hex("#D97706");
        colors["WarningSoftBrush"] = Blend(colors["CardBrush"], colors["WarningBrush"], isDark ? 0.14 : 0.12);
        colors["DangerBrush"] = isDark ? Hex("#F87171") : Hex("#DC2626");
        colors["DangerSoftBrush"] = Blend(colors["CardBrush"], colors["DangerBrush"], isDark ? 0.14 : 0.15);

        // 主按钮文字：优先白字（彩色按钮的惯例，浅色主题黑字发闷），只有主色过亮
        // （白字对比 < 2.8:1，如深色主题的浅琥珀/浅蓝主色）时才退回黑字保证可读。
        colors["AccentForegroundBrush"] = PickForegroundOn(colors["AccentBrush"]);

        return new ThemeDefinition(key, name, isDark, colors);
    }

    /// <summary>背景上的主文字颜色：优先白字（对比 ≥ 2.8:1），过亮的背景退回黑字。</summary>
    private static Color PickForegroundOn(Color background) =>
        ContrastRatio(background, Colors.White) >= 2.8 ? Colors.White : Colors.Black;

    /// <summary>
    /// 填充类强调元素（主按钮 / 当前账单 chip）的底色与文字：浅色主题用主色 + 自动对比文字；
    /// 深色主题改用侧边栏选中态同源的深一档主色 + 侧边栏文字色，避免浅色主色大面积填充刺眼。
    /// </summary>
    private static void ApplyFillBrushes(ThemeDefinition theme)
    {
        if (Application.Current is null || !theme.Colors.TryGetValue("AccentBrush", out var accent))
        {
            return;
        }

        var fill = theme.IsDark && theme.Colors.TryGetValue("SidebarNavActiveBrush", out var navActive)
            ? navActive
            : accent;
        SetBrush("AccentFillBrush", fill);

        Color foreground;
        if (theme.IsDark)
        {
            foreground = theme.Colors.TryGetValue("SidebarTextBrush", out var sidebarText)
                ? sidebarText
                : Colors.White;
        }
        else
        {
            foreground = PickForegroundOn(fill);
        }

        SetBrush("AccentFillForegroundBrush", foreground);
    }

    /// <summary>按比例把 <paramref name="to"/> 混入 <paramref name="from"/>（0 = 全 from，1 = 全 to）。</summary>
    private static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(from.A + (to.A - from.A) * amount),
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current?.Resources[key] is not SolidColorBrush brush)
        {
            return;
        }

        if (brush.IsFrozen)
        {
            Application.Current.Resources[key] = new SolidColorBrush(color);
            return;
        }

        brush.Color = color;
    }

    private static void SetGradient(string key, Color top, Color bottom)
    {
        if (Application.Current?.Resources[key] is not LinearGradientBrush brush)
        {
            return;
        }

        if (brush.IsFrozen)
        {
            Application.Current.Resources[key] = new LinearGradientBrush(top, bottom, 90);
            return;
        }

        if (brush.GradientStops.Count >= 2)
        {
            brush.GradientStops[0].Color = top;
            brush.GradientStops[1].Color = bottom;
        }
    }

    private static Color Hex(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value);
    }
    private static double ContrastRatio(Color a, Color b)
    {
        static double Luminance(Color c)
        {
            static double Channel(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }}
