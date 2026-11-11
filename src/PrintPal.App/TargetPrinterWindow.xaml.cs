using System.Windows;
using PrintPal.Core;

namespace PrintPal.App;

/// <summary>
/// 人工选择目标：只列出打印能力"能处理该任务"的打印机（候选集），
/// 不能选规则不支持的机器。候选为空时禁止转发并提示补规则。
/// </summary>
public partial class TargetPrinterWindow : Window
{
    public TargetPrinterWindow(
        PrintJob job,
        IReadOnlyList<Printer> printers)
    {
        InitializeComponent();

        var candidates = RoutingEngine.FindCandidates(job, printers);
        var eligible = candidates.Printers
            .OrderBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        PrinterCombo.ItemsSource = eligible;
        PrinterCombo.IsEnabled = eligible.Count > 0;
        PrinterCombo.SelectedIndex = eligible.Count > 0 ? 0 : -1;

        HintText.Text = eligible.Count > 0
            ? "只列出能力声明支持该任务的打印机；能力不匹配的机器不会出现。"
            : $"{candidates.Reason}。请先到「打印机」页编辑目标打印机、补充打印能力，再进行人工转发。";

        OkButton.IsEnabled = eligible.Count > 0;
    }

    public Printer? SelectedPrinter => PrinterCombo.SelectedItem as Printer;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPrinter is null)
        {
            MessageBox.Show("请选择打印机。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
