using System.Windows;
using PrintPal.Core;

namespace PrintPal.App;

public partial class BillingTagEditWindow : Window
{
    public BillingTagEditWindow(BillingTag tag)
    {
        InitializeComponent();
        EditedTag = tag;
        NameBox.Text = tag.Name;
        NoteBox.Text = tag.Note ?? "";
        NameBox.SelectAll();
        NameBox.Focus();
    }

    public BillingTag EditedTag { get; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("请输入标签名称。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EditedTag.Name = name;
        EditedTag.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();
        DialogResult = true;
    }
}

