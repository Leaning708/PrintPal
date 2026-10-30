using System.Windows;
using PrintPal.Core;

namespace PrintPal.App;

public partial class RoutingEditWindow : Window
{
    private readonly RoutingRule _rule;

    public RoutingEditWindow(RoutingRule? rule, IReadOnlyList<Printer> printers)
    {
        InitializeComponent();

        MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 32);

        _rule = rule ?? new RoutingRule();

        TargetCombo.ItemsSource = printers;

        if (rule is null)
        {
            NameBox.Text = "新路由规则";
            PriorityBox.Text = "100";
            TargetCombo.SelectedIndex = printers.Count > 0 ? 0 : -1;
            EnabledCheck.IsChecked = true;
            return;
        }

        NameBox.Text = rule.Name;
        PriorityBox.Text = rule.Priority.ToString();
        PaperBox.Text = string.Join(",", rule.MatchPaperSizes);
        MonoCheck.IsChecked = rule.MatchColorModes.Contains(ColorMode.Monochrome);
        ColorCheck.IsChecked = rule.MatchColorModes.Contains(ColorMode.Color);
        SimplexCheck.IsChecked = rule.MatchDuplexModes.Contains(DuplexMode.Simplex);
        LongCheck.IsChecked = rule.MatchDuplexModes.Contains(DuplexMode.TwoSidedLongEdge);
        ShortCheck.IsChecked = rule.MatchDuplexModes.Contains(DuplexMode.TwoSidedShortEdge);
        MinPagesBox.Text = rule.MinPages?.ToString() ?? "";
        MaxPagesBox.Text = rule.MaxPages?.ToString() ?? "";
        SourceAppBox.Text = rule.SourceAppPattern ?? "";
        DocNameBox.Text = rule.DocumentNamePattern ?? "";
        EnabledCheck.IsChecked = rule.Enabled;

        TargetCombo.SelectedItem = printers.FirstOrDefault(printer => printer.Id == rule.TargetPrinterId);
    }

    public RoutingRule Rule => _rule;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = TargetCombo.SelectedItem as Printer;
            if (target is null)
            {
                MessageBox.Show("请选择目标打印机。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                MessageBox.Show("请输入规则名称。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var priority = ParseInt(PriorityBox.Text, "优先级", 100);
            var minPages = ParseNullableInt(MinPagesBox.Text, "最少页数");
            var maxPages = ParseNullableInt(MaxPagesBox.Text, "最多页数");

            if (minPages.HasValue && maxPages.HasValue && minPages > maxPages)
            {
                MessageBox.Show("最少页数不能大于最多页数。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var paperSizes = ParsePaperSizes(PaperBox.Text);

            var colors = new List<ColorMode>();
            if (MonoCheck.IsChecked == true) colors.Add(ColorMode.Monochrome);
            if (ColorCheck.IsChecked == true) colors.Add(ColorMode.Color);

            var duplexModes = new List<DuplexMode>();
            if (SimplexCheck.IsChecked == true) duplexModes.Add(DuplexMode.Simplex);
            if (LongCheck.IsChecked == true) duplexModes.Add(DuplexMode.TwoSidedLongEdge);
            if (ShortCheck.IsChecked == true) duplexModes.Add(DuplexMode.TwoSidedShortEdge);

            _rule.Name = NameBox.Text.Trim();
            _rule.Priority = priority;
            _rule.TargetPrinterId = target.Id;
            _rule.MatchPaperSizes = paperSizes;
            _rule.MatchColorModes = colors;
            _rule.MatchDuplexModes = duplexModes;
            _rule.MinPages = minPages;
            _rule.MaxPages = maxPages;
            _rule.SourceAppPattern = string.IsNullOrWhiteSpace(SourceAppBox.Text) ? null : SourceAppBox.Text.Trim();
            _rule.DocumentNamePattern = string.IsNullOrWhiteSpace(DocNameBox.Text) ? null : DocNameBox.Text.Trim();
            _rule.Enabled = EnabledCheck.IsChecked == true;
            _rule.UpdatedAtUtc = DateTimeOffset.UtcNow;

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static int ParseInt(string text, string field, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), out var value)
            ? value
            : throw new ArgumentException($"{field}必须是整数。");
    }

    private static int? ParseNullableInt(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), out var value)
            ? value
            : throw new ArgumentException($"{field}必须是整数。");
    }

    private static List<string> ParsePaperSizes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        var values = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0);

        var result = new List<string>();
        foreach (var value in values)
        {
            var paper = PaperSize.FromName(value);
            var normalized = paper.IsKnown ? paper.Name : value.ToUpperInvariant();
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
