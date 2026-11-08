using System.Windows;
using System.Windows.Media;
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
    public const string DefaultKey = "blue";

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
        Make("blue", "默认蓝", false, new()
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
        Make("emerald", "墨绿", false, new()
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
        Make("amber", "暖橙", false, new()
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
        Make("violet", "紫罗兰", false, new()
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
        Make("graphite", "石墨", false, new()
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
            ["AccentBrush"] = "#60A5FA", ["AccentDarkBrush"] = "#3B82F6", ["AccentSoftBrush"] = "#1E3A5F",
            ["PageBackgroundBrush"] = "#0B1220", ["CardBrush"] = "#131C2E", ["HoverFillBrush"] = "#182338",
            ["LineBrush"] = "#1F2A3D", ["LineStrongBrush"] = "#2A3850",
            ["InkBrush"] = "#E6EDF7", ["InkSecondaryBrush"] = "#C3CFE0", ["MutedBrush"] = "#8FA0B8", ["SoftMutedBrush"] = "#6B7C94",
            ["SidebarTop"] = "#0F1A2E", ["SidebarBottom"] = "#0A1220", ["SidebarCardBrush"] = "#16233A", ["SidebarMutedBrush"] = "#8FA0B8",
            ["SidebarNavTextBrush"] = "#A9BBD6", ["SidebarNavHoverBrush"] = "#1B2A44", ["SidebarNavActiveBrush"] = "#2563EB",
            ["SidebarSectionBrush"] = "#6B7C94", ["SidebarCountBrush"] = "#1E3A5F", ["SidebarCountTextBrush"] = "#93C5FD",
            ["SidebarTextBrush"] = "#E6EDF7", ["MinimalTopBarBrush"] = "#3B82F6", ["MinimalChipActiveBrush"] = "#2563EB",
        }),
        Make("dark-emerald", "深绿", true, new()
        {
            ["AccentBrush"] = "#34D399", ["AccentDarkBrush"] = "#10B981", ["AccentSoftBrush"] = "#0F3D2E",
            ["PageBackgroundBrush"] = "#0A1410", ["CardBrush"] = "#10201A", ["HoverFillBrush"] = "#14261F",
            ["LineBrush"] = "#1B332A", ["LineStrongBrush"] = "#24463A",
            ["InkBrush"] = "#E3F2EA", ["InkSecondaryBrush"] = "#B9D6C8", ["MutedBrush"] = "#86A99A", ["SoftMutedBrush"] = "#5F7D70",
            ["SidebarTop"] = "#0C1F18", ["SidebarBottom"] = "#08140F", ["SidebarCardBrush"] = "#123026", ["SidebarMutedBrush"] = "#86A99A",
            ["SidebarNavTextBrush"] = "#A9CDBC", ["SidebarNavHoverBrush"] = "#17362B", ["SidebarNavActiveBrush"] = "#059669",
            ["SidebarSectionBrush"] = "#6E8F80", ["SidebarCountBrush"] = "#0F3D2E", ["SidebarCountTextBrush"] = "#6EE7B7",
            ["SidebarTextBrush"] = "#E3F2EA", ["MinimalTopBarBrush"] = "#059669", ["MinimalChipActiveBrush"] = "#059669",
        }),
        Make("dark-amber", "深橙", true, new()
        {
            ["AccentBrush"] = "#FBBF24", ["AccentDarkBrush"] = "#F59E0B", ["AccentSoftBrush"] = "#3D2E12",
            ["PageBackgroundBrush"] = "#14100A", ["CardBrush"] = "#1E1810", ["HoverFillBrush"] = "#241D12",
            ["LineBrush"] = "#332A1B", ["LineStrongBrush"] = "#463A26",
            ["InkBrush"] = "#F5EDE0", ["InkSecondaryBrush"] = "#D9C9AE", ["MutedBrush"] = "#A99A82", ["SoftMutedBrush"] = "#7D7261",
            ["SidebarTop"] = "#1F1810", ["SidebarBottom"] = "#140F09", ["SidebarCardBrush"] = "#2A2115", ["SidebarMutedBrush"] = "#A99A82",
            ["SidebarNavTextBrush"] = "#D9C9AE", ["SidebarNavHoverBrush"] = "#2E2416", ["SidebarNavActiveBrush"] = "#D97706",
            ["SidebarSectionBrush"] = "#8A7A62", ["SidebarCountBrush"] = "#3D2E12", ["SidebarCountTextBrush"] = "#FCD34D",
            ["SidebarTextBrush"] = "#F5EDE0", ["MinimalTopBarBrush"] = "#D97706", ["MinimalChipActiveBrush"] = "#D97706",
        }),
        Make("dark-violet", "深紫", true, new()
        {
            ["AccentBrush"] = "#A78BFA", ["AccentDarkBrush"] = "#8B5CF6", ["AccentSoftBrush"] = "#2E2350",
            ["PageBackgroundBrush"] = "#0F0B18", ["CardBrush"] = "#171226", ["HoverFillBrush"] = "#1C1630",
            ["LineBrush"] = "#2A2140", ["LineStrongBrush"] = "#3A2E56",
            ["InkBrush"] = "#EDE8F8", ["InkSecondaryBrush"] = "#CFC4E8", ["MutedBrush"] = "#9C90B5", ["SoftMutedBrush"] = "#6F6488",
            ["SidebarTop"] = "#171128", ["SidebarBottom"] = "#0F0A1B", ["SidebarCardBrush"] = "#221A38", ["SidebarMutedBrush"] = "#9C90B5",
            ["SidebarNavTextBrush"] = "#C9BDE6", ["SidebarNavHoverBrush"] = "#251C40", ["SidebarNavActiveBrush"] = "#7C3AED",
            ["SidebarSectionBrush"] = "#7E7099", ["SidebarCountBrush"] = "#2E2350", ["SidebarCountTextBrush"] = "#C4B5FD",
            ["SidebarTextBrush"] = "#EDE8F8", ["MinimalTopBarBrush"] = "#7C3AED", ["MinimalChipActiveBrush"] = "#7C3AED",
        }),
        Make("dark-graphite", "深灰", true, new()
        {
            ["AccentBrush"] = "#94A3B8", ["AccentDarkBrush"] = "#64748B", ["AccentSoftBrush"] = "#1F2937",
            ["PageBackgroundBrush"] = "#0B0F14", ["CardBrush"] = "#141A21", ["HoverFillBrush"] = "#1A222B",
            ["LineBrush"] = "#232C36", ["LineStrongBrush"] = "#2F3A46",
            ["InkBrush"] = "#E6EBF1", ["InkSecondaryBrush"] = "#C3CDD8", ["MutedBrush"] = "#8A97A6", ["SoftMutedBrush"] = "#64707E",
            ["SidebarTop"] = "#121820", ["SidebarBottom"] = "#0B0F14", ["SidebarCardBrush"] = "#1B232D", ["SidebarMutedBrush"] = "#8A97A6",
            ["SidebarNavTextBrush"] = "#B9C4D0", ["SidebarNavHoverBrush"] = "#1E2731", ["SidebarNavActiveBrush"] = "#475569",
            ["SidebarSectionBrush"] = "#6B7785", ["SidebarCountBrush"] = "#1F2937", ["SidebarCountTextBrush"] = "#CBD5E1",
            ["SidebarTextBrush"] = "#E6EBF1", ["MinimalTopBarBrush"] = "#475569", ["MinimalChipActiveBrush"] = "#475569",
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

        return new ThemeDefinition(key, name, isDark, colors);
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
}
