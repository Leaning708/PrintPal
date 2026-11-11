using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;

namespace PrintPal.App;

/// <summary>
/// 应用内风格的颜色选择器（代替 WinForms ColorDialog）：
/// 饱和度/明度方块 + 色相条 + HEX 输入 + 常用色，不显示系统标题栏。
/// </summary>
public partial class ColorPickerWindow : Window
{
    private static readonly string[] Presets =
    {
        "#2563EB", "#059669", "#D97706", "#7C3AED", "#DC2626",
        "#0891B2", "#DB2777", "#475569", "#0EA5E9", "#65A30D",
        "#FFFFFF", "#F3F6FB", "#E6EBF3", "#94A3B8", "#64748B",
        "#334155", "#0F172A", "#000000", "#FEF3E2", "#E7F7F0",
    };

    private double _hue;
    private double _saturation = 1;
    private double _value = 1;
    private bool _suppressHex;
    private bool _draggingSv;
    private bool _draggingHue;

    public ColorPickerWindow(string initialHex)
    {
        InitializeComponent();

        if (TryParseHex(initialHex, out var color))
        {
            SetFromColor(color);
        }

        BuildPresets();
        UpdateVisuals();
    }

    public string ResultHex { get; private set; } = "#FFFFFF";

    /// <summary>打开取色器；取消返回 null。</summary>
    public static string? Pick(Window owner, string initialHex)
    {
        var window = new ColorPickerWindow(initialHex) { Owner = owner };
        return window.ShowDialog() == true ? window.ResultHex : null;
    }

    // ------------------------------------------------------------ ui

    private void BuildPresets()
    {
        foreach (var hex in Presets)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = (Brush)FindResource("LineStrongBrush"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Tag = hex,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(swatch, "preset_" + hex);
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                if (TryParseHex((string)swatch.Tag, out var picked))
                {
                    SetFromColor(picked);
                    UpdateVisuals();
                }
            };
            PresetPanel.Children.Add(swatch);
        }
    }

    private void UpdateVisuals()
    {
        _hue = (_hue % 360 + 360) % 360;

        HueRect.Fill = new SolidColorBrush(HsvToRgb(_hue, 1, 1));

        Canvas.SetLeft(SvCursor, _saturation * SvCanvas.Width - SvCursor.Width / 2);
        Canvas.SetTop(SvCursor, (1 - _value) * SvCanvas.Height - SvCursor.Height / 2);
        Canvas.SetLeft(HueCursor, _hue / 360 * HueCanvas.Width - HueCursor.Width / 2);

        var color = HsvToRgb(_hue, _saturation, _value);
        PreviewSwatch.Background = new SolidColorBrush(color);

        _suppressHex = true;
        HexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _suppressHex = false;

        ResultHex = HexBox.Text;
    }

    private void SetFromColor(Color color)
    {
        var (h, s, v) = RgbToHsv(color);
        _hue = h;
        _saturation = s;
        _value = v;
    }

    // ------------------------------------------------------------ mouse

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        SvCanvas.CaptureMouse();
        UpdateSv(e.GetPosition(SvCanvas));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSv)
        {
            UpdateSv(e.GetPosition(SvCanvas));
        }
    }

    private void Sv_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false;
        SvCanvas.ReleaseMouseCapture();
    }

    private void UpdateSv(Point point)
    {
        _saturation = Math.Clamp(point.X / SvCanvas.Width, 0, 1);
        _value = 1 - Math.Clamp(point.Y / SvCanvas.Height, 0, 1);
        UpdateVisuals();
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        HueCanvas.CaptureMouse();
        UpdateHue(e.GetPosition(HueCanvas));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingHue)
        {
            UpdateHue(e.GetPosition(HueCanvas));
        }
    }

    private void Hue_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = false;
        HueCanvas.ReleaseMouseCapture();
    }

    private void UpdateHue(Point point)
    {
        _hue = Math.Clamp(point.X / HueCanvas.Width, 0, 1) * 360;
        UpdateVisuals();
    }

    // ------------------------------------------------------------ hex

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressHex)
        {
            return;
        }

        if (TryParseHex(HexBox.Text, out var color))
        {
            SetFromColor(color);
            UpdateVisuals();
        }
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ color math

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue;
        if (delta < 0.00001)
        {
            hue = 0;
        }
        else if (max == r)
        {
            hue = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            hue = 60 * ((b - r) / delta + 2);
        }
        else
        {
            hue = 60 * ((r - g) / delta + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    // ------------------------------------------------------------ window

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultHex = $"#{((SolidColorBrush)PreviewSwatch.Background).Color.R:X2}" +
                    $"{((SolidColorBrush)PreviewSwatch.Background).Color.G:X2}" +
                    $"{((SolidColorBrush)PreviewSwatch.Background).Color.B:X2}";
        DialogResult = true;
    }
}
