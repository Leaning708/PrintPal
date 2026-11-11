using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PrintPal.App.Themes;
using PrintPal.Core.Theming;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PrintPal.App;

/// <summary>
/// 新建/编辑自定义配色：简单模式（种子色派生）+ 高级模式（逐 token 编辑、导入/导出）。
/// 预览方式：直接把派生主题应用到全局（打开时记住原主题，取消/关闭则还原）。
/// </summary>
public partial class CustomThemeWindow : Window
{
    private static readonly string[] AccentPresets =
    {
        "#2563EB", "#059669", "#D97706", "#7C3AED", "#DC2626",
        "#0891B2", "#DB2777", "#475569", "#0EA5E9", "#65A30D",
    };

    /// <summary>高级模式分组：显示名 + (token, 中文标签)。</summary>
    private static readonly (string Group, (string Token, string Label)[] Rows)[] TokenGroups =
    {
        ("主色与按钮", new[]
        {
            ("AccentBrush", "主色"),
            ("AccentDarkBrush", "深色强调"),
            ("AccentSoftBrush", "浅强调底色"),
            ("AccentForegroundBrush", "主按钮文字"),
        }),
        ("文字", new[]
        {
            ("InkBrush", "主文字"),
            ("InkSecondaryBrush", "次要文字"),
            ("MutedBrush", "弱化文字"),
            ("SoftMutedBrush", "更弱文字"),
        }),
        ("背景与线条", new[]
        {
            ("PageBackgroundBrush", "页面底色"),
            ("CardBrush", "卡片"),
            ("HoverFillBrush", "悬停填充"),
            ("LineBrush", "线条"),
            ("LineStrongBrush", "强线条"),
        }),
        ("侧栏", new[]
        {
            ("SidebarTop", "侧栏渐变起"),
            ("SidebarBottom", "侧栏渐变止"),
            ("SidebarCardBrush", "侧栏卡片"),
            ("SidebarMutedBrush", "侧栏弱化文字"),
            ("SidebarNavTextBrush", "侧栏导航文字"),
            ("SidebarNavHoverBrush", "侧栏悬停"),
            ("SidebarNavActiveBrush", "侧栏选中"),
            ("SidebarSectionBrush", "侧栏分组标题"),
            ("SidebarCountBrush", "侧栏计数底"),
            ("SidebarCountTextBrush", "侧栏计数文字"),
            ("SidebarTextBrush", "侧栏正文"),
            ("SidebarIndicatorBrush", "侧栏指示条"),
        }),
        ("组件", new[]
        {
            ("InsetBackgroundBrush", "内嵌块底"),
            ("InsetBorderBrush", "内嵌块边框"),
            ("SubtleButtonBackgroundBrush", "次要按钮底"),
            ("GhostHoverBrush", "幽灵按钮悬停"),
            ("ChartToggleBackgroundBrush", "统计切换底"),
            ("PillNeutralBackgroundBrush", "胶囊底"),
            ("PillNeutralTextBrush", "胶囊文字"),
            ("TooltipBackgroundBrush", "提示底"),
            ("TooltipForegroundBrush", "提示文字"),
            ("TooltipBorderBrush", "提示边框"),
            ("OverlayBrush", "遮罩"),
            ("HoverOverlayBrush", "悬停遮罩"),
            ("DangerBorderBrush", "危险边框"),
            ("WarningBorderBrush", "警告边框"),
        }),
        ("语义色", new[]
        {
            ("SuccessBrush", "成功"),
            ("SuccessSoftBrush", "成功浅底"),
            ("WarningBrush", "警告"),
            ("WarningSoftBrush", "警告浅底"),
            ("DangerBrush", "危险"),
            ("DangerSoftBrush", "危险浅底"),
        }),
        ("极简模式", new[]
        {
            ("MinimalBackgroundBrush", "极简背景"),
            ("MinimalTopBarBrush", "极简顶栏"),
            ("MinimalTextBrush", "极简主文字"),
            ("MinimalMutedBrush", "极简弱化文字"),
            ("MinimalChipBrush", "极简胶囊底"),
            ("MinimalChipBorderBrush", "极简胶囊边框"),
            ("MinimalChipTextBrush", "极简胶囊文字"),
            ("MinimalChipActiveBrush", "极简胶囊选中"),
            ("MinimalButtonBackgroundBrush", "极简按钮底"),
            ("MinimalButtonForegroundBrush", "极简按钮文字"),
            ("MinimalNoticeBackgroundBrush", "极简通知底"),
            ("MinimalNoticeBorderBrush", "极简通知边框"),
            ("MinimalNoticeForegroundBrush", "极简通知文字"),
        }),
        ("滚动条", new[]
        {
            ("ScrollThumbBrush", "滑块"),
            ("ScrollThumbHoverBrush", "滑块悬停"),
            ("ScrollThumbPressedBrush", "滑块按下"),
        }),
    };

    private readonly ThemeDefinition _previous;
    private readonly Dictionary<string, TextBox> _tokenBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _tokenSwatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _tokenResetButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private string _themeId = "";
    private string _themeName = "";

    private bool _suppressEvents;
    private bool _saved;
    private bool _suppressInitialApply;

    public CustomThemeWindow(
        ThemeCustomization initial,
        ThemeDefinition previous,
        bool isNew = false,
        string? defaultName = null)
    {
        InitializeComponent();

        _previous = previous;

        var model = (initial ?? ThemeCustomization.CreateDefault()).Normalized();
        if (!string.IsNullOrWhiteSpace(defaultName))
        {
            model.Name = defaultName.Trim();
        }

        DialogTitleText.Text = isNew ? "新建自定义主题" : "编辑自定义主题";
        DeleteThemeButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        _suppressInitialApply = isNew;

        BuildAccentSwatches();
        BuildAdvancedRows();
        LoadFromModel(model);
    }

    public ThemeCustomization Customization { get; private set; } = ThemeCustomization.CreateDefault();

    /// <summary>用户点了「删除此主题」。</summary>
    public bool DeleteRequested { get; private set; }

    // ------------------------------------------------------------ build ui

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BuildAccentSwatches()
    {
        foreach (var hex in AccentPresets)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Margin = new Thickness(0, 0, 8, 8),
                BorderBrush = (Brush)FindResource("LineStrongBrush"),
                BorderThickness = new Thickness(1),
                Tag = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hex,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _suppressEvents = true;
                AccentBox.Text = (string)swatch.Tag;
                _suppressEvents = false;
                ApplyPreview();
            };
            AccentSwatchPanel.Children.Add(swatch);
        }
    }

    private void BuildAdvancedRows()
    {
        foreach (var (group, rows) in TokenGroups)
        {
            var header = new TextBlock
            {
                Text = group,
                Style = (Style)FindResource("SectionTitle"),
                Margin = new Thickness(0, group == "主色与按钮" ? 0 : 14, 0, 8),
            };
            AdvancedRowsHost.Children.Add(header);

            var card = new Border
            {
                Style = (Style)FindResource("CardBorder"),
                Padding = new Thickness(16, 10, 16, 10),
            };
            var stack = new StackPanel();
            card.Child = stack;

            foreach (var (token, label) in rows)
            {
                stack.Children.Add(BuildTokenRow(token, label));
            }

            AdvancedRowsHost.Children.Add(card);
        }
    }

    private Grid BuildTokenRow(string token, string label)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var swatchButton = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 6, 0),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "点击选择颜色",
            Tag = token,
        };
        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            Background = System.Windows.Media.Brushes.Transparent,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(swatchButton, token + "_swatch");
        swatchButton.Content = swatch;
        swatchButton.Click += (_, _) => PickColor(token);
        Grid.SetColumn(swatchButton, 1);
        row.Children.Add(swatchButton);

        var box = new TextBox
        {
            Width = 96,
            Tag = token,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, token);
        box.TextChanged += TokenBox_TextChanged;
        Grid.SetColumn(box, 2);
        row.Children.Add(box);

        var reset = new Button
        {
            Content = "\uE7A7",
            Style = (Style)FindResource("IconButton"),
            Width = 26,
            Height = 26,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "恢复为种子派生",
            Tag = token,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(reset, token + "_reset");
        reset.Click += (_, _) => ResetToken(token);
        Grid.SetColumn(reset, 3);
        row.Children.Add(reset);

        _tokenBoxes[token] = box;
        _tokenSwatches[token] = swatch;
        _tokenResetButtons[token] = reset;
        return row;
    }

    // ------------------------------------------------------------ model <-> ui

    private void LoadFromModel(ThemeCustomization model)
    {
        _suppressEvents = true;

        var normalized = model.Normalized();
        _themeId = normalized.Id;
        _themeName = normalized.Name;

        _overrides.Clear();
        if (normalized.Overrides is not null)
        {
            foreach (var (token, hex) in normalized.Overrides)
            {
                _overrides[token] = hex;
            }
        }

        ThemeNameBox.Text = normalized.Name;
        LightRadio.IsChecked = !normalized.IsDark;
        DarkRadio.IsChecked = normalized.IsDark;

        AccentBox.Text = normalized.Accent;
        PageTintRadio.IsChecked = normalized.PageStyle == ThemePageStyle.Tint;
        PageNeutralRadio.IsChecked = normalized.PageStyle == ThemePageStyle.Neutral;
        PageCustomRadio.IsChecked = normalized.PageStyle == ThemePageStyle.Custom;
        PageColorBox.Text = normalized.PageColor ?? "#F3F6FB";

        SidebarTintRadio.IsChecked = normalized.SidebarStyle == ThemeSidebarStyle.Tint;
        SidebarGradientRadio.IsChecked = normalized.SidebarStyle == ThemeSidebarStyle.Gradient;
        SidebarNeutralRadio.IsChecked = normalized.SidebarStyle == ThemeSidebarStyle.Neutral;

        _suppressEvents = false;

        UpdatePageColorBoxState();
        SyncSimpleTextBoxes();

        if (_suppressInitialApply)
        {
            // 新建主题：只回填预览数据，不直接改 App 配色（避免"只点新建就变深"）。
            _suppressInitialApply = false;
            RefreshWithoutApplying();
        }
        else
        {
            ApplyPreview();
        }
    }

    /// <summary>
    /// 只把派生结果回填到对话框（行值/对比度提示），不真正应用主题。
    /// 打开对话框时"没做任何改动"不应改变整个 App 的配色。
    /// </summary>
    private void RefreshWithoutApplying()
    {
        var model = BuildFromUi();
        if (model is null)
        {
            return;
        }

        Customization = model;
        _lastPalette = ThemePaletteBuilder.Build(model);
        RefreshTokenRows(_lastPalette);
        SyncSimpleTextBoxes();
        UpdateStatus(_lastPalette);
    }

    private ThemeCustomization? BuildFromUi()
    {
        var accent = ThemePaletteBuilder.NormalizeOptionalHex(AccentBox.Text);
        if (accent is null)
        {
            ShowError("主色不是合法的 HEX 颜色（例如 #2563EB）。");
            return null;
        }

        var pageStyle = PageCustomRadio.IsChecked == true
            ? ThemePageStyle.Custom
            : PageNeutralRadio.IsChecked == true
                ? ThemePageStyle.Neutral
                : ThemePageStyle.Tint;

        string? pageColor = null;
        if (pageStyle == ThemePageStyle.Custom)
        {
            pageColor = ThemePaletteBuilder.NormalizeOptionalHex(PageColorBox.Text);
            if (pageColor is null)
            {
                ShowError("页面底色的 HEX 不合法。");
                return null;
            }
        }

        var sidebarStyle = SidebarGradientRadio.IsChecked == true
            ? ThemeSidebarStyle.Gradient
            : SidebarNeutralRadio.IsChecked == true
                ? ThemeSidebarStyle.Neutral
                : ThemeSidebarStyle.Tint;

        var name = ThemeNameBox.Text.Trim();
        if (name.Length == 0)
        {
            name = _themeName.Length > 0 ? _themeName : "自定义";
        }

        ClearError();
        return new ThemeCustomization
        {
            Id = _themeId,
            Name = name,
            IsDark = DarkRadio.IsChecked == true,
            Accent = accent,
            PageStyle = pageStyle,
            PageColor = pageColor,
            SidebarStyle = sidebarStyle,
            Overrides = _overrides.Count > 0
                ? new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase)
                : null,
        }.Normalized();
    }

    private IReadOnlyDictionary<string, string>? _lastPalette;

    private void ApplyPreview()
    {
        if (_suppressEvents)
        {
            return;
        }

        var model = BuildFromUi();
        if (model is null)
        {
            return;
        }

        Customization = model;
        ThemeManager.Apply(ThemeManager.BuildCustom(model));

        _lastPalette = ThemePaletteBuilder.Build(model);
        RefreshTokenRows(_lastPalette);
        SyncSimpleTextBoxes();
        UpdateStatus(_lastPalette);
    }

    private void RefreshTokenRows(IReadOnlyDictionary<string, string> palette)
    {
        foreach (var (token, box) in _tokenBoxes)
        {
            if (!palette.TryGetValue(token, out var hex))
            {
                continue;
            }

            if (!box.IsKeyboardFocusWithin)
            {
                // 程序回填不能触发 TextChanged，否则刚清掉的 override 会被立刻加回来，
                // 表现为「还原」按钮点完还在、可以连点。
                var wasSuppressed = _suppressEvents;
                _suppressEvents = true;
                try
                {
                    box.Text = hex;
                }
                finally
                {
                    _suppressEvents = wasSuppressed;
                }
            }

            box.BorderBrush = (Brush)FindResource("LineStrongBrush");
            if (_tokenSwatches.TryGetValue(token, out var swatch))
            {
                swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }

            if (_tokenResetButtons.TryGetValue(token, out var reset))
            {
                reset.Visibility = _overrides.ContainsKey(token) ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }

    private void SyncSimpleTextBoxes()
    {
        var suppress = _suppressEvents;
        _suppressEvents = true;
        try
        {
            InkColorBox.Text = _overrides.GetValueOrDefault("InkBrush", "");
            InkSecondaryColorBox.Text = _overrides.GetValueOrDefault("InkSecondaryBrush", "");
            MutedColorBox.Text = _overrides.GetValueOrDefault("MutedBrush", "");
        }
        finally
        {
            _suppressEvents = suppress;
        }
    }

    private void UpdateStatus(IReadOnlyDictionary<string, string> palette)
    {
        OverrideCountText.Text = $"已覆盖 {_overrides.Count} 项";

        var issues = new List<string>();
        void Check(string token, string background, double min, string label)
        {
            if (palette.TryGetValue(token, out var fg) && palette.TryGetValue(background, out var bg))
            {
                var contrast = ThemePaletteBuilder.ContrastRatio(fg, bg);
                if (contrast < min)
                {
                    issues.Add($"{label} {contrast:0.0}:1");
                }
            }
        }

        Check("InkBrush", "PageBackgroundBrush", 4.5, "主文字/页面");
        Check("InkSecondaryBrush", "CardBrush", 4.5, "次要文字/卡片");
        Check("MutedBrush", "CardBrush", 3.0, "弱化文字/卡片");
        Check("AccentForegroundBrush", "AccentBrush", 3.0, "主按钮文字/主色");

        if (issues.Count == 0)
        {
            ContrastWarningText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ContrastWarningText.Text = "对比度偏低：" + string.Join("、", issues) + "（可读性会下降，建议调整）";
            ContrastWarningText.Visibility = Visibility.Visible;
        }
    }

    private void UpdatePageColorBoxState()
    {
        if (PageColorBox is not null)
        {
            PageColorBox.IsEnabled = PageCustomRadio.IsChecked == true;
        }
    }

    // ------------------------------------------------------------ token editing

    private void TokenBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string token } box || _suppressEvents || !IsLoaded)
        {
            return;
        }

        ApplyTokenText(token, box.Text, box);
    }

    private void TextToken_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string token } box || _suppressEvents || !IsLoaded)
        {
            return;
        }

        ApplyTokenText(token, box.Text, box);
    }

    private void ApplyTokenText(string token, string text, TextBox box)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            _overrides.Remove(token);
            box.BorderBrush = (Brush)FindResource("LineStrongBrush");
            ApplyPreview();
            return;
        }

        var hex = ThemePaletteBuilder.NormalizeOptionalHex(trimmed);
        if (hex is null)
        {
            box.BorderBrush = (Brush)FindResource("DangerBrush");
            return;
        }

        _overrides[token] = hex;
        box.BorderBrush = (Brush)FindResource("LineStrongBrush");
        ApplyPreview();
    }

    private void ResetToken(string token)
    {
        if (_overrides.Remove(token))
        {
            ApplyPreview();
        }
    }

    private void ClearOverrides_Click(object sender, RoutedEventArgs e)
    {
        _overrides.Clear();
        ApplyPreview();
    }

    private void PickColor(string token)
    {
        if (!IsLoaded)
        {
            return;
        }

        var current = _lastPalette is not null && _lastPalette.TryGetValue(token, out var hex)
            ? hex
            : "#FFFFFF";

        var picked = ColorPickerWindow.Pick(this, current);
        if (picked is null)
        {
            return;
        }

        _overrides[token] = picked;

        if (_tokenBoxes.TryGetValue(token, out var box))
        {
            _suppressEvents = true;
            box.Text = picked;
            _suppressEvents = false;
        }

        ApplyPreview();
    }

    // ------------------------------------------------------------ simple inputs

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void AnySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded)
        {
            return;
        }

        UpdatePageColorBoxState();
        ApplyPreview();
    }

    private void AccentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded)
        {
            return;
        }

        ApplyPreview();
    }

    private void PageColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents || !IsLoaded)
        {
            return;
        }

        ApplyPreview();
    }

    // ------------------------------------------------------------ tabs / files

    private void SimpleTab_Click(object sender, RoutedEventArgs e) => SwitchTab(advanced: false);

    private void AdvancedTab_Click(object sender, RoutedEventArgs e) => SwitchTab(advanced: true);

    private void SwitchTab(bool advanced)
    {
        SimplePanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;

        SimpleTabButton.Style = (Style)FindResource(advanced ? "SecondaryButton" : "AccentSoftButton");
        AdvancedTabButton.Style = (Style)FindResource(advanced ? "AccentSoftButton" : "SecondaryButton");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入配色",
            Filter = "PrintPal 配色 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ThemeCustomization? parsed;
        try
        {
            parsed = ThemeCustomization.FromJson(File.ReadAllText(dialog.FileName));
        }
        catch (IOException ex)
        {
            MessageBox.Show($"读取文件失败：{ex.Message}", "导入配色", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (parsed is null)
        {
            MessageBox.Show("文件不是有效的 PrintPal 配色 JSON。", "导入配色",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 导入只覆盖颜色/明暗/名称，不改变当前正在编辑的主题 id（避免意外覆盖别的主题）。
        var imported = parsed.Normalized();
        imported.Id = _themeId;
        if (string.IsNullOrWhiteSpace(imported.Name) || imported.Name == "自定义")
        {
            imported.Name = _themeName;
        }

        LoadFromModel(imported);
        SwitchTab(advanced: false);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var model = BuildFromUi();
        if (model is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出配色",
            Filter = "PrintPal 配色 (*.json)|*.json",
            FileName = "printpal-theme.json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, model.Normalized().ToJson());
        }
        catch (IOException ex)
        {
            MessageBox.Show($"保存文件失败：{ex.Message}", "导出配色", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show("配色已导出，可在其他机器上「导入配色」使用。", "导出配色",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ------------------------------------------------------------ save / cancel

    private void DeleteTheme_Click(object sender, RoutedEventArgs e)
    {
        var name = ThemeNameBox.Text.Trim();
        var confirm = MessageBox.Show(
            $"删除自定义主题「{(name.Length > 0 ? name : _themeName)}」？此操作不可撤销。",
            "删除主题",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        DeleteRequested = true;
        _saved = true;
        DialogResult = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var model = BuildFromUi();
        if (model is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ThemeNameBox.Text))
        {
            ShowError("请填写主题名称。");
            ThemeNameBox.Focus();
            return;
        }

        Customization = model;
        _saved = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_saved)
        {
            // 取消/直接关闭：还原打开前的主题，避免"预览残留"。
            ThemeManager.Apply(_previous);
        }

        base.OnClosing(e);
    }
}
