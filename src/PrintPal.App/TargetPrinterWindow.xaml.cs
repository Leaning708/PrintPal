using System.Windows;
using PrintPal.Core;

namespace PrintPal.App;

public partial class TargetPrinterWindow : Window
{
    public TargetPrinterWindow(IReadOnlyList<Printer> printers)
    {
        InitializeComponent();

        PrinterCombo.ItemsSource = printers;
        PrinterCombo.SelectedItem = printers.FirstOrDefault(printer => printer.IsDefault) ??
                                   printers.FirstOrDefault();
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
}
