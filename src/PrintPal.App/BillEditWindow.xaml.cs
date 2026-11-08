using System.Windows;
using PrintPal.Core;

namespace PrintPal.App;

public partial class BillEditWindow : Window
{
    public BillEditWindow(Bill bill)
    {
        InitializeComponent();
        Bill = bill;
        NameBox.Text = bill.Name;
        NoteBox.Text = bill.Note ?? "";
        NameBox.SelectAll();
        NameBox.Focus();
    }

    public Bill Bill { get; }

    public string BillName { get; private set; } = string.Empty;

    public string? Note { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("请输入账单名称。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BillName = name;
        Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        DialogResult = true;
    }
}
