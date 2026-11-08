using System.Linq;
using System.Windows;

namespace PrintPal.App;

/// <summary>
/// Themed drop-in replacement for <see cref="System.Windows.MessageBox"/>.
///
/// It intentionally lives in the <c>PrintPal.App</c> namespace so that every
/// unqualified <c>MessageBox.Show(...)</c> call in this assembly binds to this type
/// instead of the stock WPF dialog, and therefore renders <see cref="MessageDialog"/>.
/// </summary>
internal static class MessageBox
{
    public static MessageBoxResult Show(string messageBoxText) =>
        Show(messageBoxText, "PrintPal", MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption) =>
        Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) =>
        Show(messageBoxText, caption, button, MessageBoxImage.None);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var tone = icon switch
        {
            MessageBoxImage.Error => MessageTone.Error,
            MessageBoxImage.Warning => MessageTone.Warning,
            MessageBoxImage.Question => MessageTone.Question,
            _ => MessageTone.Info,
        };

        var title = string.IsNullOrWhiteSpace(caption) || caption == "PrintPal"
            ? DefaultTitle(tone)
            : caption;

        var owner = ResolveOwner();

        if (button == MessageBoxButton.OK)
        {
            MessageDialog.Show(owner, title, messageBoxText, "知道了", string.Empty, tone);
            return MessageBoxResult.OK;
        }

        var confirmed = MessageDialog.Show(owner, title, messageBoxText, "确定", "取消", tone);
        return confirmed ? MessageBoxResult.Yes : MessageBoxResult.No;
    }

    private static string DefaultTitle(MessageTone tone) => tone switch
    {
        MessageTone.Error => "操作失败",
        MessageTone.Warning => "请注意",
        MessageTone.Question => "请确认",
        _ => "提示",
    };

    private static Window? ResolveOwner()
    {
        var application = Application.Current;
        if (application is null)
        {
            return null;
        }

        return application.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? application.MainWindow;
    }
}
