using PrintPal.Core.Theming;
using Xunit;

namespace PrintPal.Core.Tests;

public sealed class ThemeTests
{
    [Fact]
    public void Palette_covers_every_required_token()
    {
        var palette = ThemePaletteBuilder.Build(ThemeCustomization.CreateDefault());

        foreach (var token in ThemePaletteBuilder.RequiredTokens)
        {
            Assert.True(palette.ContainsKey(token), $"missing token: {token}");
            Assert.Matches("^#[0-9A-Fa-f]{6}$|^#[0-9A-Fa-f]{8}$", palette[token]);
        }
    }

    [Fact]
    public void Light_and_dark_palettes_differ()
    {
        var light = ThemePaletteBuilder.Build(new ThemeCustomization { IsDark = false });
        var dark = ThemePaletteBuilder.Build(new ThemeCustomization { IsDark = true });

        Assert.NotEqual(light["PageBackgroundBrush"], dark["PageBackgroundBrush"]);
        Assert.NotEqual(light["InkBrush"], dark["InkBrush"]);
        Assert.NotEqual(light["SidebarTop"], dark["SidebarTop"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Body_text_has_readable_contrast_on_page(bool isDark)
    {
        var palette = ThemePaletteBuilder.Build(new ThemeCustomization { IsDark = isDark });

        var inkContrast = ThemePaletteBuilder.ContrastRatio(
            palette["InkBrush"], palette["PageBackgroundBrush"]);
        var secondaryContrast = ThemePaletteBuilder.ContrastRatio(
            palette["InkSecondaryBrush"], palette["CardBrush"]);
        var mutedContrast = ThemePaletteBuilder.ContrastRatio(
            palette["MutedBrush"], palette["CardBrush"]);

        Assert.True(inkContrast >= 7, $"ink/page contrast too low: {inkContrast:0.0}");
        Assert.True(secondaryContrast >= 4.5, $"secondary/card contrast too low: {secondaryContrast:0.0}");
        Assert.True(mutedContrast >= 3, $"muted/card contrast too low: {mutedContrast:0.0}");
    }

    [Theory]
    [InlineData(false, "#2563EB")]
    [InlineData(false, "#059669")]
    [InlineData(false, "#D97706")]
    [InlineData(false, "#7C3AED")]
    [InlineData(true, "#60A5FA")]
    [InlineData(true, "#34D399")]
    public void Preset_accents_keep_primary_readable(bool isDark, string accent)
    {
        var palette = ThemePaletteBuilder.Build(new ThemeCustomization { IsDark = isDark, Accent = accent });

        // 主按钮文字颜色自动取白/黑中对比度更高者：至少 4.5:1
        var contrast = ThemePaletteBuilder.ContrastRatio(
            palette["AccentForegroundBrush"], palette["AccentBrush"]);
        Assert.True(contrast >= 4.5, $"primary button contrast too low for {accent}: {contrast:0.0}");
    }

    [Fact]
    public void Too_light_accent_is_darkened_for_light_theme()
    {
        var normalized = ThemePaletteBuilder.NormalizeAccent("#FFF7A8", isDark: false);
        var contrast = ThemePaletteBuilder.ContrastRatio("#FFFFFF", normalized);

        Assert.True(contrast >= 3, $"accent not normalized enough: {normalized}, contrast {contrast:0.0}");
    }

    [Fact]
    public void Invalid_accent_falls_back_to_default()
    {
        Assert.Equal(ThemePaletteBuilder.DefaultLightAccent,
            ThemePaletteBuilder.NormalizeAccent("not-a-color", isDark: false));
        Assert.Equal(ThemePaletteBuilder.DefaultDarkAccent,
            ThemePaletteBuilder.NormalizeAccent(null, isDark: true));
    }

    [Fact]
    public void Custom_page_color_is_used_when_selected()
    {
        var palette = ThemePaletteBuilder.Build(new ThemeCustomization
        {
            PageStyle = ThemePageStyle.Custom,
            PageColor = "#101820",
        });

        Assert.Equal("#101820", palette["PageBackgroundBrush"]);
    }

    [Fact]
    public void Custom_page_style_without_color_falls_back_to_tint()
    {
        var normalized = new ThemeCustomization
        {
            PageStyle = ThemePageStyle.Custom,
            PageColor = "bad",
        }.Normalized();

        Assert.Equal(ThemePageStyle.Tint, normalized.PageStyle);
    }

    [Fact]
    public void Customization_json_roundtrips()
    {
        var customization = new ThemeCustomization
        {
            IsDark = true,
            Accent = "#34D399",
            PageStyle = ThemePageStyle.Neutral,
            SidebarStyle = ThemeSidebarStyle.Gradient,
            Name = "门店绿",
        };

        var parsed = ThemeCustomization.FromJson(customization.ToJson());

        Assert.NotNull(parsed);
        Assert.True(parsed!.IsDark);
        Assert.Equal("#34D399", parsed.Accent);
        Assert.Equal(ThemePageStyle.Neutral, parsed.PageStyle);
        Assert.Equal(ThemeSidebarStyle.Gradient, parsed.SidebarStyle);
        Assert.Equal("门店绿", parsed.Name);
    }

    [Fact]
    public void Invalid_json_returns_null()
    {
        Assert.Null(ThemeCustomization.FromJson("{not json"));
        Assert.Null(ThemeCustomization.FromJson(""));
    }

    [Fact]
    public void Semantic_colors_stay_stable_across_themes()
    {
        var light = ThemePaletteBuilder.Build(new ThemeCustomization { Accent = "#D97706" });
        var dark = ThemePaletteBuilder.Build(new ThemeCustomization { IsDark = true, Accent = "#34D399" });

        Assert.Equal("#0E9F6E", light["SuccessBrush"]);
        Assert.Equal("#0E9F6E", dark["SuccessBrush"]);
        Assert.Equal("#DC2626", light["DangerBrush"]);
        Assert.Equal("#D97706", light["WarningBrush"]);
    }

    [Fact]
    public void Advanced_overrides_apply_to_palette()
    {
        var customization = new ThemeCustomization
        {
            Overrides = new Dictionary<string, string>
            {
                ["PageBackgroundBrush"] = "#101820",
                ["InkBrush"] = "#F0F0F0",
                ["AccentForegroundBrush"] = "#000000",
            },
        };

        var palette = ThemePaletteBuilder.Build(customization);

        Assert.Equal("#101820", palette["PageBackgroundBrush"]);
        Assert.Equal("#F0F0F0", palette["InkBrush"]);
        Assert.Equal("#000000", palette["AccentForegroundBrush"]);
    }

    [Fact]
    public void Unknown_or_invalid_overrides_are_dropped()
    {
        var normalized = new ThemeCustomization
        {
            Overrides = new Dictionary<string, string>
            {
                ["NotAToken"] = "#FFFFFF",
                ["PageBackgroundBrush"] = "oops",
            },
        }.Normalized();

        Assert.Null(normalized.Overrides);

        var palette = ThemePaletteBuilder.Build(new ThemeCustomization
        {
            Overrides = new Dictionary<string, string> { ["InkBrush"] = "#123456" },
        });
        Assert.Equal("#123456", palette["InkBrush"]);
    }

    [Fact]
    public void Overrides_survive_json_roundtrip()
    {
        var customization = new ThemeCustomization
        {
            Accent = "#059669",
            Overrides = new Dictionary<string, string>
            {
                ["InkBrush"] = "#222222",
                ["MutedBrush"] = "#666666",
            },
        };

        var parsed = ThemeCustomization.FromJson(customization.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal("#222222", parsed!.Overrides!["InkBrush"]);
        Assert.Equal("#666666", parsed.Overrides["MutedBrush"]);
        Assert.Equal(ThemeCustomization.CurrentVersion, parsed.Version);
    }

    [Fact]
    public void Normalized_assigns_stable_id_when_missing()
    {
        var first = new ThemeCustomization().Normalized();
        var second = first.Normalized();

        Assert.False(string.IsNullOrWhiteSpace(first.Id));
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void Theme_library_adds_unique_named_themes()
    {
        var library = new ThemeLibrary();
        var seed = new ThemeCustomization { IsDark = true, Accent = "#34D399" };

        var first = library.Add(seed, null);
        var second = library.Add(seed, "门店绿");

        Assert.Equal(2, library.Themes.Count);
        Assert.NotEqual(first, second);
        Assert.Equal("自定义主题 1", library.Themes[0].Name);
        Assert.Equal("门店绿", library.Themes[1].Name);
        Assert.Equal(second, library.SelectedId);
    }

    [Fact]
    public void Theme_library_update_and_remove()
    {
        var library = new ThemeLibrary();
        var id = library.Add(new ThemeCustomization(), "A");

        var updated = library.Find(id)!.Normalized();
        updated.Name = "B";
        updated.Accent = "#DB2777";

        Assert.True(library.Update(updated));
        Assert.Equal("B", library.Find(id)!.Name);
        Assert.Equal("#DB2777", library.Find(id)!.Accent);

        Assert.True(library.Remove(id));
        Assert.Null(library.Find(id));
        Assert.Null(library.SelectedId);
    }

    [Fact]
    public void Theme_library_json_roundtrips_and_survives_invalid_data()
    {
        var library = new ThemeLibrary();
        library.Add(new ThemeCustomization { Accent = "#0891B2" }, "青");
        library.Add(new ThemeCustomization { IsDark = true }, "深色版");

        var parsed = ThemeLibrary.FromJson(library.ToJson());

        Assert.Equal(2, parsed.Themes.Count);
        Assert.Equal(library.SelectedId, parsed.SelectedId);
        Assert.Equal(ThemeLibrary.FromJson("not json").Themes.Count, 0);
    }

    [Fact]
    public void Legacy_customization_migrates_into_library()
    {
        var legacy = new ThemeCustomization { Accent = "#D97706", Name = "自定义" };
        var library = ThemeLibrary.FromLegacy(legacy);

        Assert.Single(library.Themes);
        Assert.Equal(library.Themes[0].Id, library.SelectedId);
        Assert.Equal("自定义", library.Themes[0].Name);
    }
}
