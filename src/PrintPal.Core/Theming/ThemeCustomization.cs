using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintPal.Core.Theming;

/// <summary>页面底色风格：跟随主色微染 / 纯中性 / 自定义。</summary>
public enum ThemePageStyle
{
    Tint = 0,
    Neutral = 1,
    Custom = 2,
}

/// <summary>侧栏风格：浅染 / 渐变 / 中性。</summary>
public enum ThemeSidebarStyle
{
    Tint = 0,
    Gradient = 1,
    Neutral = 2,
}

/// <summary>
/// 自定义主题参数（方案 A：种子色派生）。
/// 只保存"种子"，完整配色由 <see cref="ThemePaletteBuilder"/> 派生，保证可读性与一致性。
/// </summary>
public sealed class ThemeCustomization
{
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Version { get; set; } = CurrentVersion;

    /// <summary>自定义主题的唯一 id（多个自定义主题靠它区分；旧数据为空时补发）。</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "自定义";

    public bool IsDark { get; set; }

    /// <summary>品牌主色（#RRGGBB）。</summary>
    public string Accent { get; set; } = "#2563EB";

    public ThemePageStyle PageStyle { get; set; } = ThemePageStyle.Tint;

    /// <summary>PageStyle=Custom 时使用的页面底色（#RRGGBB）。</summary>
    public string? PageColor { get; set; }

    public ThemeSidebarStyle SidebarStyle { get; set; } = ThemeSidebarStyle.Tint;

    /// <summary>方案 B：高级模式下对单个 token 的覆盖（token → #RRGGBB）；null/空 = 全部按种子派生。</summary>
    public Dictionary<string, string>? Overrides { get; set; }

    public static ThemeCustomization CreateDefault(bool isDark = false) => new()
    {
        IsDark = isDark,
        Accent = isDark ? "#60A5FA" : "#2563EB",
    };

    /// <summary>把非法/缺失字段收敛回安全值（HEX 校验、页码风格范围、版本）。</summary>
    public ThemeCustomization Normalized()
    {
        var normalized = new ThemeCustomization
        {
            Version = CurrentVersion,
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim(),
            Name = string.IsNullOrWhiteSpace(Name) ? "自定义" : Name.Trim(),
            IsDark = IsDark,
            Accent = ThemePaletteBuilder.NormalizeAccent(Accent, IsDark),
            PageStyle = Enum.IsDefined(PageStyle) ? PageStyle : ThemePageStyle.Tint,
            PageColor = ThemePaletteBuilder.NormalizeOptionalHex(PageColor),
            SidebarStyle = Enum.IsDefined(SidebarStyle) ? SidebarStyle : ThemeSidebarStyle.Tint,
        };

        if (normalized.PageStyle == ThemePageStyle.Custom && normalized.PageColor is null)
        {
            normalized.PageStyle = ThemePageStyle.Tint;
        }

        if (Overrides is not null)
        {
            var allowed = new HashSet<string>(
                ThemePaletteBuilder.RequiredTokens.Append("AccentForegroundBrush"),
                StringComparer.OrdinalIgnoreCase);
            var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (token, value) in Overrides)
            {
                if (!allowed.Contains(token))
                {
                    continue;
                }

                var hex = ThemePaletteBuilder.NormalizeOptionalHex(value);
                if (hex is not null)
                {
                    cleaned[token] = hex;
                }
            }

            normalized.Overrides = cleaned.Count > 0 ? cleaned : null;
        }

        return normalized;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析失败返回 null（调用方回退到内置主题），不抛异常。</summary>
    public static ThemeCustomization? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ThemeCustomization>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
