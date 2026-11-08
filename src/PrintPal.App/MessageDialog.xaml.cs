using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PrintPal.App;

public enum MessageTone
{
    Question,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Themed replacement for the stock MessageBox: a rounded white card with the same
/// typography, border and shadow language as the rest of the app.
/// </summary>
public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    public static bool Show(
        Window? owner,
        string title,
        string message,
        string primaryText = "确定",
        string secondaryText = "取消",
        MessageTone tone = MessageTone.Question)
    {
        var dialog = new MessageDialog
        {
            Owner = owner ?? Application.Current?.MainWindow,
            Title = title,
        };

        if (dialog.Owner is null)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primaryText;
        dialog.SecondaryButton.Content = secondaryText;
        dialog.SecondaryButton.Visibility = string.IsNullOrWhiteSpace(secondaryText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        dialog.ApplyTone(tone);

        return dialog.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message) =>
        Show(owner, title, message, "知道了", string.Empty, MessageTone.Info);

    public static void Warn(Window? owner, string title, string message) =>
        Show(owner, title, message, "知道了", string.Empty, MessageTone.Warning);

    public static void Error(Window? owner, string title, string message) =>
        Show(owner, title, message, "知道了", string.Empty, MessageTone.Error);

    private void ApplyTone(MessageTone tone)
    {
        var brushKey = tone switch
        {
            MessageTone.Warning => "WarningBrush",
            MessageTone.Error => "DangerBrush",
            _ => "AccentBrush",
        };

        ToneDot.Background = (System.Windows.Media.Brush)FindResource(brushKey);
    }
}
