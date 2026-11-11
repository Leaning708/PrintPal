using System.Windows;
using PrintPal.Core;

namespace PrintPal.App;

/// <summary>编辑单条价格（纸张 + 颜色为唯一标识，只读展示）。</summary>
public partial class PriceEditWindow : Window
{
    private readonly PriceRule _rule;

    public PriceEditWindow(PriceRule rule)
    {
        InitializeComponent();

        MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 32);

        _rule = rule;

        PaperText.Text = string.IsNullOrWhiteSpace(rule.PaperSize) || rule.PaperSize == "*"
            ? "其他纸张"
            : rule.PaperSize;
        ColorText.Text = rule.ColorMode switch
        {
            ColorMode.Monochrome => "黑白",
            ColorMode.Color => "彩色",
            _ => "任意颜色",
        };
        SimplexBox.Text = Money.ToYuan(rule.SimplexPriceCents).ToString("0.##");
        DuplexBox.Text = Money.ToYuan(rule.DuplexPriceCents).ToString("0.##");
    }

    public PriceRule Rule => _rule;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(SimplexBox.Text.Trim(), out var simplexYuan) || simplexYuan < 0)
        {
            MessageBox.Show("请输入有效的单面价（非负数字，单位元）。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!decimal.TryParse(DuplexBox.Text.Trim(), out var duplexYuan) || duplexYuan < 0)
        {
            MessageBox.Show("请输入有效的双面价（非负数字，单位元）。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _rule.SimplexPriceCents = Money.FromYuan(simplexYuan);
        _rule.DuplexPriceCents = Money.FromYuan(duplexYuan);

        DialogResult = true;
    }
}
