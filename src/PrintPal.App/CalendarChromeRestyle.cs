using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PrintPal.App;

/// <summary>
/// 默认 CalendarItem 模板不给标题栏 / 翻页按钮挂样式（Style=null，外观全部内联在模板里），
/// 其悬浮色是硬编码系统蓝，外部样式覆盖不了（属性优先级低于模板属性）。
/// 按仓库约定不替换 Calendar / CalendarItem 模板（换模板会静默破坏视图切换），
/// 这里只对三个命名部件做本地 SetValue 套上 Theme.xaml 里跟随主题 token 的模板
/// （本地值优先级最高），部件的 Name 与事件处理不动，视图切换不受影响。
///
/// 另外两处对称性修正（同样本地 SetValue）：
/// - 主题隐式 Button 样式（Theme.xaml ButtonBase）带 Margin=0,0,8,0，会把右箭头拉离卡片右缘
///   （左箭头 Left 对齐不受影响），chrome 按钮外边距统一归零后按实测矩形对齐；
/// - MonthView / YearView 默认 Center 对齐但实测整体偏左约 2px，按与内容区中心的实测差微调外边距。
/// </summary>
public static class CalendarChromeRestyle
{
    /// <summary>chrome 按钮距 CalendarItem 内容区左右缘的目标间距。</summary>
    private const double ChromeInset = 8.0;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CalendarChromeRestyle),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty RestyledProperty = DependencyProperty.RegisterAttached(
        "Restyled", typeof(bool), typeof(CalendarChromeRestyle), new PropertyMetadata(false));

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Calendar cal || e.NewValue is not true)
        {
            return;
        }

        cal.Loaded += (_, _) =>
        {
            Apply(cal);
            cal.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Apply(cal));
        };
        cal.DisplayModeChanged += (_, _) => Apply(cal);
        cal.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                Apply(cal);
            }
        };
        Apply(cal);
    }

    private static void Apply(Calendar cal)
    {
        ApplyTemplate(cal, "PART_HeaderButton", "PrintPalCalendarHeaderTemplate");
        ApplyTemplate(cal, "PART_PreviousButton", "PrintPalCalendarPrevTemplate");
        ApplyTemplate(cal, "PART_NextButton", "PrintPalCalendarNextTemplate");
        NormalizeChromeMargins(cal);
        cal.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AlignChrome(cal));
    }

    private static void ApplyTemplate(Calendar cal, string partName, string templateKey)
    {
        if (FindPart(cal, partName) is not Button part || (bool)part.GetValue(RestyledProperty))
        {
            return;
        }

        if (part.TryFindResource(templateKey) is not ControlTemplate template)
        {
            return;
        }

        part.SetValue(Control.TemplateProperty, template);
        part.SetValue(RestyledProperty, true);
    }

    private static void NormalizeChromeMargins(Calendar cal)
    {
        foreach (var name in new[] { "PART_HeaderButton", "PART_PreviousButton", "PART_NextButton" })
        {
            if (FindPart(cal, name) is Button part && part.Margin != default)
            {
                part.Margin = default;
            }
        }
    }

    private static void AlignChrome(Calendar cal)
    {
        if (cal.ActualWidth < 50 || FindPart(cal, "PART_CalendarItem") is not FrameworkElement item)
        {
            return;
        }

        var inner = GetRect(item, cal);
        AlignEdge(cal, "PART_PreviousButton", inner.Left + ChromeInset, alignRight: false);
        AlignEdge(cal, "PART_NextButton", inner.Right - ChromeInset, alignRight: true);
        var center = (inner.Left + inner.Right) / 2;
        AlignCenter(cal, "PART_MonthView", center);
        AlignCenter(cal, "PART_YearView", center);
    }

    private static void AlignEdge(Calendar cal, string partName, double targetEdge, bool alignRight)
    {
        if (FindPart(cal, partName) is not Button part || part.Visibility != Visibility.Visible)
        {
            return;
        }

        var rect = GetRect(part, cal);
        // Right 对齐时右缘 = 锚点 - Margin.Right：targetEdge - rect.Right 为负表示需要左移（加大右外边距）
        var delta = alignRight ? targetEdge - rect.Right : targetEdge - rect.Left;
        if (Math.Abs(delta) < 0.5)
        {
            return;
        }

        var margin = part.Margin;
        if (alignRight)
        {
            margin.Right -= delta;
        }
        else
        {
            margin.Left += delta;
        }

        part.Margin = margin;
    }

    private static void AlignCenter(Calendar cal, string partName, double targetCenter)
    {
        // 隐藏视图（如月模式下的 YearView）保留着上次的布局矩形，不能按它调整
        if (FindPart(cal, partName) is not FrameworkElement part
            || part.Visibility != Visibility.Visible
            || part.ActualWidth < 20)
        {
            return;
        }

        var rect = GetRect(part, cal);
        var delta = targetCenter - (rect.Left + rect.Width / 2);
        if (Math.Abs(delta) < 0.5)
        {
            return;
        }

        var margin = part.Margin;
        margin.Left += delta;
        margin.Right -= delta;
        part.Margin = margin;
    }

    private static Rect GetRect(FrameworkElement fe, FrameworkElement ancestor)
    {
        return fe.TransformToVisual(ancestor).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
    }

    private static DependencyObject? FindPart(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
            {
                return fe;
            }

            var found = FindPart(child, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
