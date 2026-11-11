using System.Globalization;

namespace PrintPal.Core.Theming;

/// <summary>
/// 由"种子色"派生出完整主题调色板（方案 A）。
/// 产物是 token → #RRGGBB 映射，App 侧直接替换 Theme.xaml 里的同名 Brush。
/// 纯逻辑，不依赖 WPF，便于单测。
/// </summary>
public static class ThemePaletteBuilder
{
    public const string DefaultLightAccent = "#2563EB";
    public const string DefaultDarkAccent = "#60A5FA";

    /// <summary>派生结果必须覆盖的全部 token。</summary>
    public static IReadOnlyList<string> RequiredTokens { get; } = new[]
    {
        "AccentBrush", "AccentDarkBrush", "AccentSoftBrush", "AccentForegroundBrush",
        "PageBackgroundBrush", "CardBrush", "LineBrush", "LineStrongBrush",
        "InkBrush", "InkSecondaryBrush", "MutedBrush", "SoftMutedBrush", "HoverFillBrush",
        "SidebarTop", "SidebarBottom", "SidebarCardBrush", "SidebarMutedBrush", "SidebarNavTextBrush",
        "SidebarNavHoverBrush", "SidebarNavActiveBrush", "SidebarSectionBrush", "SidebarCountBrush",
        "SidebarCountTextBrush", "SidebarTextBrush", "SidebarIndicatorBrush",
        "InsetBackgroundBrush", "InsetBorderBrush", "SubtleButtonBackgroundBrush", "GhostHoverBrush",
        "TooltipBackgroundBrush", "TooltipForegroundBrush", "TooltipBorderBrush",
        "PillNeutralBackgroundBrush", "PillNeutralTextBrush", "OverlayBrush", "ChartToggleBackgroundBrush",
        "DangerBorderBrush", "WarningBorderBrush", "HoverOverlayBrush",
        "MinimalBackgroundBrush", "MinimalTopBarBrush", "MinimalTextBrush", "MinimalMutedBrush",
        "MinimalChipBrush", "MinimalChipBorderBrush", "MinimalChipTextBrush", "MinimalChipActiveBrush",
        "MinimalButtonBackgroundBrush", "MinimalButtonForegroundBrush",
        "MinimalNoticeBackgroundBrush", "MinimalNoticeBorderBrush", "MinimalNoticeForegroundBrush",
        "SuccessBrush", "SuccessSoftBrush", "WarningBrush", "WarningSoftBrush", "DangerBrush", "DangerSoftBrush",
        "ScrollThumbBrush", "ScrollThumbHoverBrush", "ScrollThumbPressedBrush",
    };

    public static IReadOnlyDictionary<string, string> Build(ThemeCustomization customization)
    {
        ArgumentNullException.ThrowIfNull(customization);

        var c = customization.Normalized();
        var accent = ParseOrThrow(c.Accent);

        var white = Rgb.FromHex("#FFFFFF");
        var black = Rgb.FromHex("#000000");
        var ink = Rgb.FromHex(c.IsDark ? "#E6EDF7" : "#0F172A");
        var inkSecondary = Rgb.FromHex(c.IsDark ? "#C3CFE0" : "#334155");
        var muted = Rgb.FromHex(c.IsDark ? "#8FA0B8" : "#64748B");
        var softMuted = Rgb.FromHex(c.IsDark ? "#6B7C94" : "#94A3B8");

        var pageBase = Rgb.FromHex(c.IsDark ? "#0B1220" : "#F3F6FB");
        var page = c.PageStyle switch
        {
            ThemePageStyle.Neutral => pageBase,
            ThemePageStyle.Custom => ParseOrThrow(c.PageColor!),
            _ => Rgb.Blend(pageBase, accent, 0.06),
        };

        var card = c.IsDark ? Rgb.Blend(page, white, 0.06) : white;
        var line = Rgb.Blend(page, c.IsDark ? white : accent, c.IsDark ? 0.10 : 0.10);
        var lineStrong = Rgb.Blend(page, c.IsDark ? white : accent, c.IsDark ? 0.18 : 0.18);
        var hoverFill = Rgb.Blend(page, accent, c.IsDark ? 0.12 : 0.06);

        var accentDark = c.IsDark ? Rgb.Blend(accent, white, 0.20) : Rgb.Blend(accent, black, 0.15);
        var accentSoft = c.IsDark ? Rgb.Blend(page, accent, 0.30) : Rgb.Blend(white, accent, 0.12);

        var sidebar = BuildSidebar(c, page, accent, ink, muted, card, accentDark);
        var inset = Rgb.Blend(page, accentSoft, 0.50);
        var subtle = Rgb.Blend(page, line, 0.45);
        var pill = Rgb.Blend(page, accentSoft, 0.30);
        var pillText = Rgb.Blend(inkSecondary, accent, 0.25);

        var success = Rgb.FromHex("#0E9F6E");
        var warning = Rgb.FromHex("#D97706");
        var danger = Rgb.FromHex("#DC2626");

        var minimalBackground = c.IsDark ? Rgb.FromHex("#0B1322") : card;
        var minimalText = c.IsDark ? white : ink;
        var minimalMuted = c.IsDark ? Rgb.FromHex("#94A3B8") : muted;

        var palette = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AccentBrush"] = accent.Hex,
            ["AccentDarkBrush"] = accentDark.Hex,
            ["AccentSoftBrush"] = accentSoft.Hex,
            ["AccentForegroundBrush"] = (Contrast(white, accent) >= Contrast(black, accent) ? white : black).Hex,
            ["PageBackgroundBrush"] = page.Hex,
            ["CardBrush"] = card.Hex,
            ["LineBrush"] = line.Hex,
            ["LineStrongBrush"] = lineStrong.Hex,
            ["InkBrush"] = ink.Hex,
            ["InkSecondaryBrush"] = inkSecondary.Hex,
            ["MutedBrush"] = muted.Hex,
            ["SoftMutedBrush"] = softMuted.Hex,
            ["HoverFillBrush"] = hoverFill.Hex,

            ["SidebarTop"] = sidebar.Top.Hex,
            ["SidebarBottom"] = sidebar.Bottom.Hex,
            ["SidebarCardBrush"] = sidebar.Card.Hex,
            ["SidebarMutedBrush"] = sidebar.Muted.Hex,
            ["SidebarNavTextBrush"] = sidebar.NavText.Hex,
            ["SidebarNavHoverBrush"] = sidebar.NavHover.Hex,
            ["SidebarNavActiveBrush"] = sidebar.NavActive.Hex,
            ["SidebarSectionBrush"] = sidebar.Section.Hex,
            ["SidebarCountBrush"] = sidebar.Count.Hex,
            ["SidebarCountTextBrush"] = sidebar.CountText.Hex,
            ["SidebarTextBrush"] = sidebar.Text.Hex,
            ["SidebarIndicatorBrush"] = sidebar.Indicator.Hex,

            ["InsetBackgroundBrush"] = inset.Hex,
            ["InsetBorderBrush"] = line.Hex,
            ["SubtleButtonBackgroundBrush"] = subtle.Hex,
            ["GhostHoverBrush"] = Rgb.Blend(page, accent, 0.12).Hex,
            ["TooltipBackgroundBrush"] = (c.IsDark ? Rgb.FromHex("#1E293B") : ink).Hex,
            ["TooltipForegroundBrush"] = white.Hex,
            ["TooltipBorderBrush"] = Rgb.FromHex("#334155").Hex,
            ["PillNeutralBackgroundBrush"] = pill.Hex,
            ["PillNeutralTextBrush"] = pillText.Hex,
            ["OverlayBrush"] = c.IsDark ? "#B80B1220" : "#B8FFFFFF",
            ["ChartToggleBackgroundBrush"] = subtle.Hex,
            ["DangerBorderBrush"] = c.IsDark ? "#7F1D1D" : "#F6C9C9",
            ["WarningBorderBrush"] = c.IsDark ? "#78350F" : "#F3E0C6",
            ["HoverOverlayBrush"] = (c.IsDark ? white : ink).Hex,

            ["MinimalBackgroundBrush"] = minimalBackground.Hex,
            ["MinimalTopBarBrush"] = accent.Hex,
            ["MinimalTextBrush"] = minimalText.Hex,
            ["MinimalMutedBrush"] = minimalMuted.Hex,
            ["MinimalChipBrush"] = Rgb.Blend(minimalBackground, accent, c.IsDark ? 0.18 : 0.10).Hex,
            ["MinimalChipBorderBrush"] = Rgb.Blend(minimalBackground, accent, 0.25).Hex,
            ["MinimalChipTextBrush"] = c.IsDark ? "#CBD5E1" : inkSecondary.Hex,
            ["MinimalChipActiveBrush"] = (c.IsDark ? Rgb.Blend(accent, black, 0.25) : accent).Hex,
            ["MinimalButtonBackgroundBrush"] = Rgb.Blend(minimalBackground, accent, c.IsDark ? 0.14 : 0.08).Hex,
            ["MinimalButtonForegroundBrush"] = minimalText.Hex,
            ["MinimalNoticeBackgroundBrush"] = "#F59E0B",
            ["MinimalNoticeBorderBrush"] = "#FBBF24",
            ["MinimalNoticeForegroundBrush"] = "#1F2937",

            ["SuccessBrush"] = success.Hex,
            ["SuccessSoftBrush"] = Rgb.Blend(card, success, c.IsDark ? 0.25 : 0.12).Hex,
            ["WarningBrush"] = warning.Hex,
            ["WarningSoftBrush"] = Rgb.Blend(card, warning, c.IsDark ? 0.25 : 0.12).Hex,
            ["DangerBrush"] = danger.Hex,
            ["DangerSoftBrush"] = Rgb.Blend(card, danger, c.IsDark ? 0.25 : 0.10).Hex,

            ["ScrollThumbBrush"] = Rgb.Blend(card, muted, 0.45).Hex,
            ["ScrollThumbHoverBrush"] = Rgb.Blend(card, muted, 0.65).Hex,
            ["ScrollThumbPressedBrush"] = Rgb.Blend(card, muted, 0.85).Hex,
        };

        // 方案 B：高级模式下逐 token 覆盖（key/HEX 已在 Normalized 里校验过）
        if (c.Overrides is not null)
        {
            foreach (var (token, hex) in c.Overrides)
            {
                var normalizedHex = NormalizeOptionalHex(hex);
                if (normalizedHex is not null && palette.ContainsKey(token))
                {
                    palette[token] = normalizedHex;
                }
            }
        }

        return palette;
    }

    private static SidebarColors BuildSidebar(
        ThemeCustomization c,
        Rgb page,
        Rgb accent,
        Rgb ink,
        Rgb muted,
        Rgb card,
        Rgb accentDark)
    {
        var white = Rgb.FromHex("#FFFFFF");
        Rgb top;
        Rgb bottom;

        if (c.IsDark)
        {
            (top, bottom) = c.SidebarStyle switch
            {
                ThemeSidebarStyle.Gradient => (Rgb.Blend(page, accent, 0.28), page),
                ThemeSidebarStyle.Neutral => (page, Rgb.Blend(page, ink, 0.04)),
                _ => (Rgb.Blend(page, accent, 0.12), page),
            };

            return new SidebarColors(
                Top: top,
                Bottom: bottom,
                Card: Rgb.Blend(page, accent, 0.15),
                Muted: muted,
                NavText: Rgb.Blend(ink, accent, 0.10),
                NavHover: Rgb.Blend(page, white, 0.08),
                NavActive: accent,
                Section: Rgb.Blend(muted, accent, 0.15),
                Count: Rgb.Blend(page, accent, 0.25),
                CountText: Rgb.Blend(accent, white, 0.35),
                Text: ink,
                Indicator: white);
        }
        else
        {
            (top, bottom) = c.SidebarStyle switch
            {
                ThemeSidebarStyle.Gradient => (Rgb.Blend(white, accent, 0.20), Rgb.Blend(white, accent, 0.42)),
                ThemeSidebarStyle.Neutral => (Rgb.Blend(page, ink, 0.02), Rgb.Blend(page, ink, 0.06)),
                _ => (Rgb.Blend(page, accent, 0.10), Rgb.Blend(page, accent, 0.22)),
            };

            return new SidebarColors(
                Top: top,
                Bottom: bottom,
                Card: card,
                Muted: Rgb.Blend(muted, accent, 0.25),
                NavText: Rgb.Blend(ink, accent, 0.35),
                NavHover: Rgb.Blend(page, accent, 0.16),
                NavActive: accent,
                Section: Rgb.Blend(muted, accent, 0.20),
                Count: Rgb.Blend(page, accent, 0.18),
                CountText: accentDark,
                Text: ink,
                Indicator: white);
        }
    }

    private sealed record SidebarColors(
        Rgb Top,
        Rgb Bottom,
        Rgb Card,
        Rgb Muted,
        Rgb NavText,
        Rgb NavHover,
        Rgb NavActive,
        Rgb Section,
        Rgb Count,
        Rgb CountText,
        Rgb Text,
        Rgb Indicator);

    // ---------------------------------------------------------------- public helpers

    /// <summary>校验并规范化主色；过亮/过暗时自动修正，保证按钮文字可读。</summary>
    public static string NormalizeAccent(string? hex, bool isDark)
    {
        var accent = TryParse(hex) ?? Rgb.FromHex(isDark ? DefaultDarkAccent : DefaultLightAccent);
        var black = Rgb.FromHex("#000000");
        var white = Rgb.FromHex("#FFFFFF");

        if (isDark)
        {
            // 深色页面需要足够亮的主色：太暗就逐步提亮（最多 8 步）。
            for (var i = 0; i < 8 && accent.RelativeLuminance < 0.18; i++)
            {
                accent = Rgb.Blend(accent, white, 0.12);
            }

            // 也不要过亮，避免和大面积深色背景对比刺眼。
            for (var i = 0; i < 8 && accent.RelativeLuminance > 0.62; i++)
            {
                accent = Rgb.Blend(accent, black, 0.12);
            }
        }
        else
        {
            // 浅色页面上的主按钮需要足够深：逐步加深到与白色有 3:1 对比。
            for (var i = 0; i < 12 && Contrast(white, accent) < 3.0; i++)
            {
                accent = Rgb.Blend(accent, black, 0.10);
            }

            // 太暗（接近黑）也难看，稍微提亮一点。
            if (Contrast(white, accent) < 1.2)
            {
                accent = Rgb.Blend(accent, white, 0.25);
            }
        }

        return accent.Hex;
    }

    /// <summary>校验可选 HEX；非法返回 null。</summary>
    public static string? NormalizeOptionalHex(string? hex) => TryParse(hex)?.Hex;

    /// <summary>WCAG 对比度，用于自检配色可读性。</summary>
    public static double ContrastRatio(string foregroundHex, string backgroundHex)
    {
        var a = ParseOrThrow(foregroundHex).RelativeLuminance;
        var b = ParseOrThrow(backgroundHex).RelativeLuminance;
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static Rgb ParseOrThrow(string hex) => TryParse(hex) ?? throw new ArgumentException($"非法颜色值：{hex}", nameof(hex));

    private static Rgb? TryParse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var text = hex.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length == 8)
        {
            // 忽略 alpha（token 里只有 Overlay 需要 alpha，直接按字面返回）
            text = text[2..];
        }

        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return new Rgb(
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
    }

    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb FromHex(string hex) => TryParse(hex) ?? throw new ArgumentException($"非法颜色值：{hex}", nameof(hex));

        public string Hex => $"#{R:X2}{G:X2}{B:X2}";

        public static Rgb Blend(Rgb from, Rgb to, double amount)
        {
            amount = Math.Clamp(amount, 0, 1);
            return new Rgb(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        public double RelativeLuminance
        {
            get
            {
                static double Channel(byte value)
                {
                    var c = value / 255.0;
                    return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                }

                return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
            }
        }
    }
    private static double Contrast(Rgb a, Rgb b)
    {
        var la = a.RelativeLuminance;
        var lb = b.RelativeLuminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}