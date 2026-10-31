using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintPal.Agent;
using PrintPal.Core;
using PrintPal.Ipp;
using PrintPal.Storage;
using WinForms = System.Windows.Forms;

namespace PrintPal.App;

public partial class MainWindow : Window
{
    private const string QueueName = "PrintPal Virtual Printer";

    private readonly IPrintPalBackend _agent;
    private readonly ObservableCollection<JobRow> _jobRows = new();
    private readonly ObservableCollection<PrinterRow> _printerRows = new();
    private readonly ObservableCollection<RoutingRow> _routingRows = new();
    private readonly ObservableCollection<PriceRow> _priceRows = new();
    private readonly ObservableCollection<BillChipRow> _parkedBills = new();
    private readonly ObservableCollection<ClosedBillRow> _closedBillRows = new();
    private readonly ObservableCollection<JobRow> _historyJobRows = new();
    private readonly List<JobRow> _allJobs = new();
    private readonly DispatcherTimer _timer;

    private List<Printer> _printers = new();
    private List<RoutingRule> _routingRules = new();
    private List<PriceRule> _priceRules = new();
    private List<Bill> _closedBills = new();
    private List<BillSummary> _openBills = new();
    private Bill? _currentBill;
    private Guid? _historyBillId;
    private bool _historyDetailsClosed = true;
    private bool _suppressHistorySelection;

    private WinForms.NotifyIcon? _trayIcon;
    private bool _initialized;
    private bool _reloading;
    private bool _balloonShown;
    private bool _suppressAutostart;
    private bool _pricesDirty;
    private bool _applyingMode;
    private bool _detailsClosed;
    private UiMode _mode = UiMode.Full;

    private enum UiMode
    {
        Full,
        Simple,
        Minimal,
    }

    public MainWindow(IPrintPalBackend agent)
    {
        InitializeComponent();

        _agent = agent;

        JobsGrid.ItemsSource = _jobRows;
        SimpleJobsGrid.ItemsSource = _jobRows;
        PrintersGrid.ItemsSource = _printerRows;
        RoutingGrid.ItemsSource = _routingRows;
        PricesGrid.ItemsSource = _priceRows;
        HistoryGrid.ItemsSource = _closedBillRows;
        HistoryJobsGrid.ItemsSource = _historyJobRows;
        FullParkedBills.ItemsSource = _parkedBills;
        SimpleParkedBills.ItemsSource = _parkedBills;
        MinimalParkedBills.ItemsSource = _parkedBills;

        IppUriText.Text = agent.IppUri;
        DataDirText.Text = agent.Options.DataDirectory;
        AgentStatusText.Text = "打印服务运行中";
        AgentUriText.Text = agent.IppUri;
        AutoForwardCheck.IsChecked = agent.Options.AutoForward;

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.1";
        VersionText.Text = $"PrintPal {version}";
        StatusVersionText.Text = $"PrintPal {version}";

        _suppressAutostart = true;
        AutostartCheck.IsChecked = AutostartService.IsEnabled();
        _suppressAutostart = false;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await ReloadAsync();

        CreateTrayIcon();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }

            UpdateMaximizeGlyph();
        };
        LocationChanged += (_, _) => SaveModePosition();

        ApplyPage(0);


    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            await ReloadAsync();
            UpdateQueueStatus();

            var savedMode = await _agent.Database.GetSettingAsync("uiMode");
            if (Enum.TryParse<UiMode>(savedMode, out var mode))
            {
                await SetModeAsync(mode, persist: false, force: true);
            }

            _timer.Start();
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"初始化失败：{ex.Message}";
        }
    }

    public void HideToTray()
    {
        Hide();

        if (!_balloonShown && _trayIcon is not null)
        {
            _balloonShown = true;
            _trayIcon.ShowBalloonTip(2500, "PrintPal 仍在运行", "关闭窗口后仍会继续捕获打印任务；右键托盘图标可退出。",
                WinForms.ToolTipIcon.Info);
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = (Environment.ProcessPath is { } processPath
                ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
                : null) ?? System.Drawing.SystemIcons.Application,
            Text = "PrintPal",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip
        {
            BackColor = TrayMenuRenderer.Surface,
            ForeColor = TrayMenuRenderer.TextColor,
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular,
                System.Drawing.GraphicsUnit.Point, 134),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            MinimumSize = new System.Drawing.Size(196, 0),
            Padding = new WinForms.Padding(6, 8, 6, 8),
            Renderer = new TrayMenuRenderer(),
            DropShadowEnabled = true,
        };

        menu.Items.Add(CreateTrayItem("打开 PrintPal", () => ShowFromTray(), bold: true));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateTrayItem("完整模式", () => _ = SetModeAsync(UiMode.Full)));
        menu.Items.Add(CreateTrayItem("简洁模式", () => _ = SetModeAsync(UiMode.Simple)));
        menu.Items.Add(CreateTrayItem("极简模式", () => _ = SetModeAsync(UiMode.Minimal)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateTrayItem("打开数据目录", () => OpenFolder(_agent.Options.DataDirectory)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(CreateTrayItem("退出", () => ((App)Application.Current).ExitApplication()));

        // Round the popup to match the app's card language (Windows 10 does not do it itself).
        // The shape has to be applied after WinForms has sized the popup, hence BeginInvoke.
        menu.Opened += (_, _) => menu.BeginInvoke(new Action(() =>
        {
            try
            {
                // Items only auto-size to their text, which would leave the hover highlight
                // short of the menu edge. Stretch every row to the full menu width first.
                var innerWidth = menu.ClientSize.Width - menu.Padding.Horizontal;
                foreach (WinForms.ToolStripItem item in menu.Items)
                {
                    item.AutoSize = false;
                    item.Width = Math.Max(0, innerWidth);
                }

                menu.PerformLayout();

                var rounded = CreateRoundRectRgn(0, 0, menu.Width + 1, menu.Height + 1, 10, 10);
                if (rounded != IntPtr.Zero && SetWindowRgn(menu.Handle, rounded, true) == 0)
                {
                    DeleteObject(rounded);
                }
            }
            catch
            {
                // Cosmetics only; never let them break the tray menu.
            }
        }));

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private WinForms.ToolStripMenuItem CreateTrayItem(string text, Action action, bool bold = false)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            Padding = new WinForms.Padding(12, 7, 12, 7),
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9F,
                bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular,
                System.Drawing.GraphicsUnit.Point, 134),
        };

        item.Click += (_, _) => Dispatcher.Invoke(action);
        return item;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageTitleText is null || JobsPage is null || NavList.SelectedIndex < 0)
        {
            return;
        }

        ApplyPage(NavList.SelectedIndex);
    }

    private void ApplyPage(int index)
    {
        if (JobsPage is null)
        {
            return;
        }

        BillingStrip.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        JobsPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PrintersPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        RoutingPage.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PricesPage.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;

        (PageTitleText.Text, PageSubtitleText.Text) = index switch
        {
            1 => ("历史结账", "按账单归档的结账记录"),
            2 => ("打印机", "维护 IPP 与 Raw 9100 目标"),
            3 => ("路由规则", "按纸张、颜色、单双面自动分流"),
            4 => ("价格表", "按张计价，保存后立即生效"),
            5 => ("设置", "服务、数据与启动项"),
            _ => ("打印记录", "实时台账与转发状态"),
        };

    }

    private void MainSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border && border.ActualWidth > 0 && border.ActualHeight > 0)
        {
            // 外层 DWM 圆角 8 px − 内缩 6 px = 同心 2 px，保证深色边框四边等宽。
            border.Clip = new RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                2,
                2);
        }
    }

    // 卡片内的表格行/空状态遮罩是不透明矩形，会把 CardBorder 的圆角盖掉，按圆角裁剪卡片内容。
    private void ClipCardCorners(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border && border.ActualWidth > 0 && border.ActualHeight > 0)
        {
            var radius = border.CornerRadius.TopLeft;
            border.Clip = new RectangleGeometry(
                new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                radius,
                radius);
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        var glyph = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        if (FullMaximizeGlyph is not null)
        {
            FullMaximizeGlyph.Text = glyph;
        }

        if (SimpleMaximizeGlyph is not null)
        {
            SimpleMaximizeGlyph.Text = glyph;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
        UpdateQueueStatus();
    }

    private async Task ReloadAsync()
    {
        if (_reloading)
        {
            return;
        }

        _reloading = true;

        try
        {
            var selectedJobId = (JobsGrid.SelectedItem as JobRow)?.Job.Id;
            var selectedBillId = (HistoryGrid.SelectedItem as ClosedBillRow)?.Bill.Id;
            var selectedPrinterId = (PrintersGrid.SelectedItem as PrinterRow)?.Printer.Id;
            var selectedRoutingId = (RoutingGrid.SelectedItem as RoutingRow)?.Rule.Id;
            var jobsScroll = GetScrollViewer(JobsGrid);
            var simpleJobsScroll = GetScrollViewer(SimpleJobsGrid);
            var historyScroll = GetScrollViewer(HistoryGrid);
            var printersScroll = GetScrollViewer(PrintersGrid);
            var routingScroll = GetScrollViewer(RoutingGrid);
            var pricesScroll = GetScrollViewer(PricesGrid);
            var jobsOffset = jobsScroll?.VerticalOffset ?? 0;
            var jobsHOffset = jobsScroll?.HorizontalOffset ?? 0;
            var simpleJobsOffset = simpleJobsScroll?.VerticalOffset ?? 0;
            var simpleJobsHOffset = simpleJobsScroll?.HorizontalOffset ?? 0;
            var historyOffset = historyScroll?.VerticalOffset ?? 0;
            var historyHOffset = historyScroll?.HorizontalOffset ?? 0;
            var printersOffset = printersScroll?.VerticalOffset ?? 0;
            var printersHOffset = printersScroll?.HorizontalOffset ?? 0;
            var routingOffset = routingScroll?.VerticalOffset ?? 0;
            var routingHOffset = routingScroll?.HorizontalOffset ?? 0;
            var pricesOffset = pricesScroll?.VerticalOffset ?? 0;
            var pricesHOffset = pricesScroll?.HorizontalOffset ?? 0;
            var bill = await _agent.Database.EnsureActiveBillAsync();
            _currentBill = bill;

            var (from, to) = GetHistoryRange();
            var jobsTask = _agent.Database.GetBillJobsAsync(bill.Id);
            var openBillsTask = _agent.Database.GetOpenBillsAsync();
            var printersTask = _agent.Database.GetPrintersAsync();
            var routingTask = _agent.Database.GetRoutingRulesAsync();
            var pricesTask = _agent.Database.GetPriceRulesAsync();
            var closedBillsTask = _agent.Database.GetClosedBillsAsync(from, to);

            await Task.WhenAll(jobsTask, openBillsTask, printersTask, routingTask, pricesTask, closedBillsTask);

            var jobs = await jobsTask;
            _openBills = (await openBillsTask).ToList();
            _printers = (await printersTask).ToList();
            _routingRules = (await routingTask).ToList();
            _priceRules = (await pricesTask).ToList();
            _closedBills = (await closedBillsTask).ToList();

            SyncParkedBills();

            var printerNames = _printers.ToDictionary(printer => printer.Id, printer => printer.Name);
            _allJobs.Clear();
            _allJobs.AddRange(jobs
                .OrderByDescending(job => job.CreatedAtUtc)
                .Select(job => new JobRow(job, printerNames)));

            _printerRows.Clear();
            foreach (var printer in _printers)
            {
                _printerRows.Add(new PrinterRow(printer));
            }

            if (selectedPrinterId is Guid printerId)
            {
                PrintersGrid.SelectedItem = _printerRows.FirstOrDefault(row => row.Printer.Id == printerId);
            }

            _routingRows.Clear();
            foreach (var rule in _routingRules.OrderBy(rule => rule.Priority))
            {
                _routingRows.Add(new RoutingRow(rule, printerNames));
            }

            if (selectedRoutingId is Guid routingId)
            {
                RoutingGrid.SelectedItem = _routingRows.FirstOrDefault(row => row.Rule.Id == routingId);
            }

            if (!_pricesDirty)
            {
                _priceRows.Clear();
                foreach (var rule in _priceRules)
                {
                    _priceRows.Add(new PriceRow(rule));
                }
            }

            ApplyHistoryFilter(selectedBillId);

            ApplyFilters(selectJobId: selectedJobId);
            UpdateBillingChrome();

            StatusBarText.Text = $"最后刷新 {DateTime.Now:HH:mm:ss} · 当前账单 {jobs.Count} 条记录";

            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    RestoreScrollOffset(JobsGrid, jobsOffset, jobsHOffset);
                    RestoreScrollOffset(SimpleJobsGrid, simpleJobsOffset, simpleJobsHOffset);
                    RestoreScrollOffset(HistoryGrid, historyOffset, historyHOffset);
                    RestoreScrollOffset(PrintersGrid, printersOffset, printersHOffset);
                    RestoreScrollOffset(RoutingGrid, routingOffset, routingHOffset);
                    RestoreScrollOffset(PricesGrid, pricesOffset, pricesHOffset);
                }),
                DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"刷新失败：{ex.Message}";
            Console.WriteLine($"UI reload failed: {ex}");
        }
        finally
        {
            _reloading = false;
        }
    }

    private void UpdateBillingChrome()
    {
        var amount = _allJobs.Sum(row => row.Job.AmountCents);
        var count = _allJobs.Count;
        var amountText = Money.FormatCents(amount);
        var parked = _parkedBills.Count;
        var name = _currentBill?.Name ?? "未开单";

        FullBillNameText.Text = name;
        FullAmountText.Text = amountText;
        JobsNavCountText.Text = count.ToString();

        SimpleCurrentTagText.Text = name;
        SimpleAmountText.Text = amountText;

        MinimalBillNameText.Text = name;
        MinimalAmountText.Text = amountText;

        FullParkedCaptionText.Text = parked == 0 ? "暂无挂起账单" : $"已挂起 {parked}";
        SimpleParkedCaptionText.Text = $"已挂起 {parked}";
        SimpleParkedPanel.Visibility = parked == 0 ? Visibility.Collapsed : Visibility.Visible;
        MinimalParkedPanel.Visibility = parked == 0 ? Visibility.Collapsed : Visibility.Visible;
        MinimalParkedCaptionText.Text = $"挂起 {parked}";

        var hasJobs = count > 0;
        FullParkButton.IsEnabled = hasJobs;
        FullCheckoutButton.IsEnabled = hasJobs;
        SimpleParkButton.IsEnabled = hasJobs;
        SimpleCheckoutButton.IsEnabled = hasJobs;
        MinimalParkButton.IsEnabled = hasJobs;
        MinimalCheckoutButton.IsEnabled = hasJobs;
    }

    // ------------------------------------------------------------------ history filter / details

    /// <summary>
    /// 挂起账单 chip 每 2 秒刷新一次；没有变化时不重建集合，
    /// 否则容器会被反复断开，点击（包括 UI 自动化）会落到已断开的元素上。
    /// </summary>
    private void SyncParkedBills()
    {
        var parked = _openBills.Where(item => item.Status == BillStatus.Parked).ToList();

        var unchanged = parked.Count == _parkedBills.Count;
        if (unchanged)
        {
            for (var i = 0; i < parked.Count; i++)
            {
                var current = _parkedBills[i].Summary;
                var next = parked[i];
                if (current.Id != next.Id ||
                    current.Name != next.Name ||
                    current.AmountCents != next.AmountCents ||
                    current.JobCount != next.JobCount ||
                    current.ParkedAtUtc != next.ParkedAtUtc)
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            return;
        }

        _parkedBills.Clear();
        foreach (var summary in parked)
        {
            _parkedBills.Add(new BillChipRow(summary));
        }
    }

    private (DateTimeOffset? From, DateTimeOffset? To) GetHistoryRange()
    {
        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        return HistoryRangeCombo.SelectedIndex switch
        {
            1 => (todayStart, null),
            2 => (todayStart.AddDays(-6), null),
            3 => (todayStart.AddDays(-29), null),
            _ => (null, null),
        };
    }

    private void ApplyHistoryFilter(Guid? selectBillId)
    {
        var search = HistorySearchBox.Text.Trim();
        var filtered = _closedBills
            .Where(bill => MatchesHistorySearch(bill, search))
            .ToList();

        _suppressHistorySelection = true;
        try
        {
            _closedBillRows.Clear();
            foreach (var bill in filtered)
            {
                _closedBillRows.Add(new ClosedBillRow(bill));
            }

            if (selectBillId is Guid id)
            {
                var row = _closedBillRows.FirstOrDefault(item => item.Bill.Id == id);
                if (row is not null)
                {
                    HistoryGrid.SelectedItem = row;
                }
            }
        }
        finally
        {
            _suppressHistorySelection = false;
        }

        HistoryCountText.Text = filtered.Count >= 200
            ? $"共 {filtered.Count} 条（仅显示最近 200 条）"
            : $"共 {filtered.Count} 条";

        UpdateHistoryDetailsPanel();
    }

    private static bool MatchesHistorySearch(Bill bill, string search)
    {
        if (search.Length == 0)
        {
            return true;
        }

        return bill.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (bill.Note?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void UpdateHistoryDetailsPanel()
    {
        var visible = HistoryGrid.SelectedItem is ClosedBillRow && !_historyDetailsClosed;

        HistoryDetailsCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HistoryDetailsSpacerColumn.Width = new GridLength(visible ? 14 : 0);
        HistoryDetailsColumn.MinWidth = visible ? 520 : 0;
        HistoryDetailsColumn.MaxWidth = visible ? 660 : 0;
        HistoryDetailsColumn.Width = visible ? new GridLength(0.42, GridUnitType.Star) : new GridLength(0);

        if (!visible)
        {
            HistorySummaryText.Text = "选择左侧账单查看其中的打印记录";
        }
    }

    private async Task LoadHistoryJobsAsync(ClosedBillRow row)
    {
        var keepOffset = _historyBillId == row.Bill.Id;
        var scroll = GetScrollViewer(HistoryJobsGrid);
        var offset = keepOffset ? scroll?.VerticalOffset ?? 0 : 0;
        var hOffset = keepOffset ? scroll?.HorizontalOffset ?? 0 : 0;
        _historyBillId = row.Bill.Id;

        HistorySummaryText.Text = $"{row.BillName} · {row.JobCount} 单 · {row.TotalPages} 页 · {row.AmountText}";

        try
        {
            var jobs = await _agent.Database.GetBillJobsAsync(row.Bill.Id);
            var printerNames = _printers.ToDictionary(printer => printer.Id, printer => printer.Name);
            _historyJobRows.Clear();
            foreach (var job in jobs.OrderByDescending(job => job.CreatedAtUtc))
            {
                _historyJobRows.Add(new JobRow(job, printerNames));
            }

            _ = Dispatcher.BeginInvoke(
                new Action(() => RestoreScrollOffset(HistoryJobsGrid, offset, hOffset)),
                DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取历史任务失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyFilters(Guid? selectJobId = null, bool scrollToSelection = false)
    {
        var filtered = _allJobs.AsEnumerable();

        var statusText = (StatusCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部状态";
        filtered = statusText switch
        {
            "已转发" => filtered.Where(row => row.Job.Status == JobStatus.Succeeded),
            "失败" => filtered.Where(row => row.Job.Status == JobStatus.Failed),
            "待转发" => filtered.Where(row => row.Job.Status == JobStatus.Priced),
            "待确认" => filtered.Where(row => row.Job.Status is JobStatus.NeedsReview or JobStatus.Held),
            "未路由" => filtered.Where(row => row.Job.Status == JobStatus.Unrouted),
            "未知" => filtered.Where(row => row.Job.Status == JobStatus.Unknown),
            _ => filtered,
        };

        var search = SearchBox.Text.Trim();
        if (search.Length > 0)
        {
            filtered = filtered.Where(row =>
                row.DocumentName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                row.UserName.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();

        _jobRows.Clear();
        foreach (var row in list)
        {
            _jobRows.Add(row);
        }

        FilterSummaryText.Text = $"共 {list.Count} 条";

        if (selectJobId is Guid id)
        {
            var selected = _jobRows.FirstOrDefault(row => row.Job.Id == id);
            if (selected is not null)
            {
                JobsGrid.SelectedItem = selected;
                if (scrollToSelection)
                {
                    JobsGrid.ScrollIntoView(selected);
                }
            }
        }

        if (_jobRows.Count == 0)
        {
            JobDetailsBox.Text = "";
        }

        UpdateJobDetailsPanel();
    }

    private void Filters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            ApplyFilters(scrollToSelection: true);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            ApplyFilters(scrollToSelection: true);
        }
    }

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        JobDetailsBox.Text = (JobsGrid.SelectedItem as JobRow)?.DetailsText ?? "";
        UpdateJobDetailsPanel();
    }

    private void UpdateJobDetailsPanel()
    {
        var visible = JobsGrid.SelectedItem is JobRow && !_detailsClosed;

        JobDetailsCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        JobDetailsSpacerColumn.Width = new GridLength(visible ? 14 : 0);
        JobDetailsColumn.MinWidth = visible ? 310 : 0;
        JobDetailsColumn.MaxWidth = visible ? 450 : 0;
        JobDetailsColumn.Width = visible ? new GridLength(0.26, GridUnitType.Star) : new GridLength(0);
    }

    private void CloseJobDetails_Click(object sender, RoutedEventArgs e)
    {
        _detailsClosed = true;
        UpdateJobDetailsPanel();
    }

    // Clicking a job row brings the closed details panel back.
    private void JobsGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_detailsClosed || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(JobsGrid, source) is DataGridRow)
        {
            _detailsClosed = false;
            UpdateJobDetailsPanel();
        }
    }

    private async void ParkBill_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var parkedName = _currentBill?.Name ?? "";
            var next = await _agent.Database.ParkActiveBillAsync();
            await ReloadAsync();
            StatusBarText.Text = $"「{parkedName}」已挂起 · 当前账单：{next.Name}";
        }
        catch (NoBillJobsException)
        {
            MessageBox.Show("当前账单还没有打印记录，不能挂起。", "PrintPal",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"挂起失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ResumeBill_Click(object sender, RoutedEventArgs e)
    {

        if (sender is not Button { DataContext: BillChipRow chip })
        {
            return;
        }

        try
        {
            var bill = await _agent.Database.ActivateBillAsync(chip.Id);
            await ReloadAsync();
            StatusBarText.Text = $"已取回「{bill.Name}」";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"取回失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BillName_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _ = RenameCurrentBillAsync();
        }
    }

    private async Task RenameCurrentBillAsync()
    {
        if (_currentBill is null)
        {
            return;
        }

        var dialog = new BillEditWindow(_currentBill) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _agent.Database.UpdateBillAsync(_currentBill.Id, dialog.BillName, dialog.Note);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存账单失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBill is null || _allJobs.Count == 0)
        {
            return;
        }

        var confirmed = MessageDialog.Show(
            this,
            "确认结账",
            $"结账「{_currentBill.Name}」？\n\n本次 {_allJobs.Count} 单，金额 {FullAmountText.Text}。\n结账后自动归档，并取回最近挂起的账单。",
            "结账",
            "取消",
            MessageTone.Question);
        if (!confirmed)
        {
            return;
        }

        try
        {
            var label = _currentBill.Name;
            var amount = FullAmountText.Text;
            var result = await _agent.Database.CheckoutActiveBillAsync(null);
            await ReloadAsync();
            MessageDialog.Info(this, "已结账",
                $"「{label}」已结账：{amount}。\n当前账单：{result.NextBill.Name}");
        }
        catch (CheckoutBlockedException ex)
        {
            var names = string.Join("\n", ex.BlockingJobs.Take(8)
                .Select(job => $"{job.DocumentName}（{JobStatusText(job.Status)}）"));
            MessageDialog.Warn(this, "无法结账",
                $"还有未处理的任务，不能结账：\n\n{names}\n\n请先转发完成或删除这些记录。");
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, "结账失败", ex.Message);
        }
    }

    private async void MoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not JobRow row)
        {
            MessageBox.Show("请先选择一条打印记录。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.Job.Status == JobStatus.Forwarding)
        {
            MessageBox.Show("任务正在转发，稍后再移动。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var bills = await _agent.Database.GetOpenBillsAsync();
            if (bills.Count <= 1)
            {
                var confirm = MessageBox.Show(
                    "现在只有一张未结账账单。新建一张账单并把这条记录移过去？",
                    "PrintPal", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                var moved = await _agent.Database.MoveJobAsync(row.Job.Id, null, createNewActive: true);
                await ReloadAsync();
                StatusBarText.Text = $"已移到「{moved.BillNameSnapshot}」";
                return;
            }

            var dialog = new MoveJobWindow(row.DocumentName, bills, _currentBill?.Id) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var target = await _agent.Database.MoveJobAsync(row.Job.Id, dialog.TargetBillId, dialog.CreateNew);
            await ReloadAsync();
            StatusBarText.Text = $"已移到「{target.BillNameSnapshot}」";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"移动失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        await ReloadAsync();
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var selected = (HistoryGrid.SelectedItem as ClosedBillRow)?.Bill.Id;
        ApplyHistoryFilter(selected);
    }

    private async void HistoryRefresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void CloseHistoryDetails_Click(object sender, RoutedEventArgs e)
    {
        _historyDetailsClosed = true;
        UpdateHistoryDetailsPanel();
    }

    private async void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistorySelection)
        {
            return;
        }

        if (HistoryGrid.SelectedItem is not ClosedBillRow row)
        {
            _historyBillId = null;
            _historyJobRows.Clear();
            UpdateHistoryDetailsPanel();
            return;
        }

        _historyDetailsClosed = false;
        await LoadHistoryJobsAsync(row);
        UpdateHistoryDetailsPanel();
    }

    private void HistoryGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_historyDetailsClosed || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(HistoryGrid, source) is not DataGridRow)
        {
            return;
        }

        _historyDetailsClosed = false;
        UpdateHistoryDetailsPanel();

        if (HistoryGrid.SelectedItem is ClosedBillRow row)
        {
            _ = LoadHistoryJobsAsync(row);
        }
    }

    private static string JobStatusText(JobStatus status) => status switch
    {
        JobStatus.Succeeded => "已转发",
        JobStatus.Failed => "失败",
        JobStatus.Unknown => "未知",
        JobStatus.NeedsReview => "待确认",
        JobStatus.Unrouted => "未路由",
        JobStatus.Held => "待确认",
        JobStatus.Priced => "待转发",
        JobStatus.Captured => "捕获中",
        _ => status.ToString(),
    };

    // Re-selecting the mode that is already active is a no-op: it must not re-apply
    // the layout or re-center the window. Only an actual switch does that.
    private async void FullMode_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != UiMode.Full)
        {
            await SetModeAsync(UiMode.Full);
        }
    }

    private async void SimpleMode_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != UiMode.Simple)
        {
            await SetModeAsync(UiMode.Simple);
        }
    }

    private async void MinimalMode_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != UiMode.Minimal)
        {
            await SetModeAsync(UiMode.Minimal);
        }
    }

    private void UpdateModeSwitchButtons()
    {
        SetModeBar(FullModeFullButton, FullModeSimpleButton, FullModeMinimalButton);
        SetModeBar(SimpleModeFullButton, SimpleModeSimpleButton, SimpleModeMinimalButton);
        SetDarkModeBar(MinimalModeFullButton, MinimalModeSimpleButton, MinimalModeMinimalButton);
    }

    private void SetModeBar(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            var active = ModeFromButton(button) == _mode;
            button.Background = active ? (System.Windows.Media.Brush)FindResource("AccentSoftBrush") : System.Windows.Media.Brushes.Transparent;
            button.Foreground = active ? (System.Windows.Media.Brush)FindResource("AccentDarkBrush") : (System.Windows.Media.Brush)FindResource("MutedBrush");
        }
    }

    private void SetDarkModeBar(params Button[] buttons)
    {
        foreach (var button in buttons)
        {
            var active = ModeFromButton(button) == _mode;
            button.Background = new System.Windows.Media.SolidColorBrush(
                active
                    ? System.Windows.Media.Color.FromRgb(0x1D, 0x4E, 0xD8)
                    : System.Windows.Media.Color.FromRgb(0x1E, 0x29, 0x3B));
            button.Foreground = active
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
        }
    }

    private static UiMode ModeFromButton(Button button) => button.Tag switch
    {
        "Simple" => UiMode.Simple,
        "Minimal" => UiMode.Minimal,
        _ => UiMode.Full,
    };

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private async void MinimalHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
        await Task.CompletedTask;
    }

    // Buttons and tag chips swallow their own mouse input, so this only fires on blank caption area.
    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void TopmostToggle_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        await SaveSettingAsync(TopmostKey(_mode), Topmost ? "true" : "false");
        UpdateTopmostButtons();
    }

    private async Task SetModeAsync(UiMode mode, bool persist = true, bool force = false)
    {
        if (_applyingMode)
        {
            return;
        }

        // Switching to the mode that is already active (button bar, tray menu) must not
        // re-run layout/centering. Startup restore passes force:true to apply it anyway.
        if (!force && mode == _mode)
        {
            return;
        }

        _applyingMode = true;
        try
        {
            SaveModeBounds();
            _mode = mode;
            UpdateModeSwitchButtons();
            FullLayout.Visibility = mode == UiMode.Full ? Visibility.Visible : Visibility.Collapsed;
            SimpleLayout.Visibility = mode == UiMode.Simple ? Visibility.Visible : Visibility.Collapsed;
            MinimalLayout.Visibility = mode == UiMode.Minimal ? Visibility.Visible : Visibility.Collapsed;

            if (mode == UiMode.Minimal)
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = false;
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x13, 0x22));
                MinWidth = 316;
                MinHeight = 186;
                Width = 316;
                Height = 186;
                ConfigureWindowChrome(0, 0);
                UpdateLayout();
                await RestoreMinimalPositionAsync();
            }
            else
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = true;
                Background = mode == UiMode.Full
                    ? (System.Windows.Media.Brush)FindResource("SidebarBrush")
                    : (System.Windows.Media.Brush)FindResource("PageBackgroundBrush");
                MinWidth = mode == UiMode.Full ? 1200 : 760;
                MinHeight = mode == UiMode.Full ? 620 : 500;
                ConfigureWindowChrome(mode == UiMode.Full ? 78 : 58, 6);

                if (mode == UiMode.Full)
                {
                    Width = await GetDoubleSettingAsync("fullWidth", 1440);
                    Height = await GetDoubleSettingAsync("fullHeight", 900);
                }
                else
                {
                    Width = await GetDoubleSettingAsync("simpleWidth", 940);
                    Height = await GetDoubleSettingAsync("simpleHeight", 640);
                }
            }

            var workArea = SystemParameters.WorkArea;
            var maxWidth = Math.Max(MinWidth, workArea.Width - 24);
            var maxHeight = Math.Max(MinHeight, workArea.Height - 24);
            Width = Math.Min(Math.Max(Width, MinWidth), maxWidth);
            Height = Math.Min(Math.Max(Height, MinHeight), maxHeight);
            if (mode != UiMode.Minimal)
            {
                // 切换到完整/简洁模式时居中于主屏工作区；极简模式保持右下角记忆位置。
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }
            UpdateLayout();

            Topmost = await GetBoolSettingAsync(TopmostKey(mode), mode == UiMode.Minimal);
            UpdateTopmostButtons();
            UpdateMaximizeGlyph();
            if (persist)
            {
                await SaveSettingAsync("uiMode", mode.ToString());
            }
        }
        finally
        {
            _applyingMode = false;
        }
    }

    private void ConfigureWindowChrome(double captionHeight, double resizeBorder)
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is null)
        {
            return;
        }

        chrome.CaptionHeight = captionHeight;
        chrome.ResizeBorderThickness = new Thickness(resizeBorder);
        chrome.UseAeroCaptionButtons = false;
    }

    private void SaveModeBounds()
    {
        if (!_initialized || _applyingMode)
        {
            return;
        }

        if (_mode == UiMode.Minimal)
        {
            _ = SaveSettingAsync("minimalLeft", Left.ToString("0"));
            _ = SaveSettingAsync("minimalTop", Top.ToString("0"));
            return;
        }

        var prefix = _mode == UiMode.Full ? "full" : "simple";
        var left = Left;
        var top = Top;
        var width = Width;
        var height = Height;

        if (WindowState != WindowState.Normal)
        {
            var restore = RestoreBounds;
            if (restore.Width > 0 && restore.Height > 0)
            {
                left = restore.Left;
                top = restore.Top;
                width = restore.Width;
                height = restore.Height;
            }
        }

        if (!double.IsNaN(left) && !double.IsNaN(top) &&
            !double.IsNaN(width) && !double.IsNaN(height) &&
            width > 0 && height > 0)
        {
            _ = SaveSettingAsync($"{prefix}Left", left.ToString("0"));
            _ = SaveSettingAsync($"{prefix}Top", top.ToString("0"));
            _ = SaveSettingAsync($"{prefix}Width", width.ToString("0"));
            _ = SaveSettingAsync($"{prefix}Height", height.ToString("0"));
        }
    }

    private void SaveModePosition()
    {
        if (_applyingMode || _mode != UiMode.Minimal)
        {
            return;
        }

        _ = SaveSettingAsync("minimalLeft", Left.ToString("0"));
        _ = SaveSettingAsync("minimalTop", Top.ToString("0"));
    }

    private async Task RestoreMinimalPositionAsync()
    {
        var left = await _agent.Database.GetSettingAsync("minimalLeft");
        var top = await _agent.Database.GetSettingAsync("minimalTop");
        if (double.TryParse(left, out var savedLeft) && double.TryParse(top, out var savedTop) &&
            savedLeft > -1000 && savedTop > -1000)
        {
            Left = savedLeft;
            Top = savedTop;
            ClampToWorkArea();
            return;
        }

        PlaceMinimalBottomRight();
    }

    private void ClampToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
    }

    private void PlaceMinimalBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }

    private void UpdateTopmostButtons()
    {
        const string pinGlyph = "\uE718";
        const string pinnedGlyph = "\uE840";

        foreach (var button in new[] { FullPinButton, SimplePinButton, MinimalPinButton })
        {
            button.Content = Topmost ? pinnedGlyph : pinGlyph;
            button.ToolTip = Topmost ? "取消置顶" : "置顶";
        }
    }

    private static string TopmostKey(UiMode mode) => mode switch
    {
        UiMode.Full => "topmost.full",
        UiMode.Simple => "topmost.simple",
        _ => "topmost.minimal",
    };

    private async Task<bool> GetBoolSettingAsync(string key, bool fallback)
    {
        try
        {
            var value = await _agent.Database.GetSettingAsync(key);
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private async Task<double> GetDoubleSettingAsync(string key, double fallback)
    {
        try
        {
            var value = await _agent.Database.GetSettingAsync(key);
            return double.TryParse(value, out var parsed) && !double.IsNaN(parsed) && !double.IsInfinity(parsed)
                ? parsed
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private async Task SaveSettingAsync(string key, string value)
    {
        try
        {
            await _agent.Database.SetSettingAsync(key, value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"save setting {key} failed: {ex.Message}");
        }
    }

    private async void RetryJob_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not JobRow row)
        {
            MessageBox.Show("请选择任务。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.Job.Status == JobStatus.Succeeded)
        {
            MessageBox.Show("该任务已经转发成功。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.Job.Status == JobStatus.NeedsReview)
        {
            MessageBox.Show("待确认任务还没有有效计价，不能转发。", "PrintPal", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (row.Job.TargetPrinterId is null)
        {
            MessageBox.Show("该任务没有目标打印机，请使用“选择目标转发”。", "PrintPal",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _agent.EnqueueForwardAsync(row.Job.Id);
            StatusBarText.Text = $"已加入转发队列：{row.DocumentName}";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"转发失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ChooseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not JobRow row)
        {
            MessageBox.Show("请选择任务。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.Job.Status == JobStatus.Succeeded)
        {
            MessageBox.Show("该任务已经转发成功。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.Job.Status == JobStatus.NeedsReview)
        {
            MessageBox.Show("待确认任务还没有有效计价，不能转发。", "PrintPal", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_printers.Count == 0)
        {
            MessageBox.Show("请先在“打印机”页添加打印机。", "PrintPal", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new TargetPrinterWindow(_printers) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPrinter is null)
        {
            return;
        }

        try
        {
            await _agent.EnqueueForwardAsync(row.Job.Id, dialog.SelectedPrinter.Id);
            StatusBarText.Text = $"已加入转发队列：{row.DocumentName} → {dialog.SelectedPrinter.Name}";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"转发失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenDoc_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not JobRow row || string.IsNullOrWhiteSpace(row.Job.DocumentPath))
        {
            MessageBox.Show("该任务没有文档路径。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(row.Job.DocumentPath))
        {
            MessageBox.Show("文档已不存在。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Job.DocumentPath}\"")
        {
            UseShellExecute = true,
        });
    }

    private async void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not JobRow row)
        {
            return;
        }

        if (MessageBox.Show($"删除记录“{row.DocumentName}”？\n（只删除台账，不删除已转发的文档）", "PrintPal",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await _agent.Database.MarkJobDeletedAsync(row.Job.Id);
        await ReloadAsync();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_jobRows.Count == 0)
        {
            MessageBox.Show("当前没有可导出的记录。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 PrintPal 打印记录",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"PrintPal-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("时间,文档,用户,纸张,颜色,单双面,页数,份数,单价(元),金额(元),目标打印机,状态,原因");

        foreach (var row in _jobRows)
        {
            builder.AppendLine(string.Join(",",
                Escape(row.TimeText),
                Escape(row.DocumentName),
                Escape(row.UserName),
                Escape(row.PaperText),
                Escape(row.ColorText),
                Escape(row.DuplexText),
                row.PageCount.ToString(),
                row.Copies.ToString(),
                Money.ToYuan(row.Job.UnitPriceCents).ToString("0.00"),
                Money.ToYuan(row.Job.AmountCents).ToString("0.00"),
                Escape(row.TargetText),
                Escape(row.StatusText),
                Escape(row.Job.StatusReason ?? "")));
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
        MessageBox.Show($"已导出到：\n{dialog.FileName}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ------------------------------------------------------------------ printers

    private Printer? SelectedPrinter => (PrintersGrid.SelectedItem as PrinterRow)?.Printer;

    private async void AddPrinter_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PrinterEditWindow(null) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _agent.Database.SavePrinterAsync(dialog.Printer);
            await ReloadAsync();
        }
    }

    private async void EditPrinter_Click(object sender, RoutedEventArgs e)
    {
        var printer = SelectedPrinter;
        if (printer is null)
        {
            MessageBox.Show("请选择打印机。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PrinterEditWindow(printer) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _agent.Database.SavePrinterAsync(dialog.Printer);
            await ReloadAsync();
        }
    }

    private async void DeletePrinter_Click(object sender, RoutedEventArgs e)
    {
        var printer = SelectedPrinter;
        if (printer is null)
        {
            return;
        }

        if (MessageBox.Show($"删除打印机“{printer.Name}”？", "PrintPal",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await _agent.Database.DeletePrinterAsync(printer.Id);
        await ReloadAsync();
    }

    private async void TestPrinter_Click(object sender, RoutedEventArgs e)
    {
        var printer = SelectedPrinter;
        if (printer is null)
        {
            MessageBox.Show("请选择打印机。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (printer.Protocol == PrinterProtocol.Ipp)
            {
                using var client = new IppClient();
                var capabilities = await client.GetPrinterAttributesAsync(new Uri(printer.Uri));
                printer.Capabilities = capabilities;
                printer.LastTestOk = true;
                printer.LastTestedAtUtc = DateTimeOffset.UtcNow;
                await _agent.Database.SavePrinterAsync(printer);

                MessageBox.Show(
                    $"连接成功：{capabilities.Name ?? printer.Name}\n" +
                    $"格式：{string.Join(", ", capabilities.DocumentFormats.Select(format => format.DisplayName()))}\n" +
                    $"纸张：{string.Join(", ", capabilities.MediaSizes.Select(size => size.Name))}\n" +
                    $"颜色：{string.Join(", ", capabilities.ColorModes)}\n" +
                    $"双面：{string.Join(", ", capabilities.DuplexModes)}",
                    "PrintPal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(printer.Host, printer.Port, timeout.Token);
                printer.LastTestOk = true;
                printer.LastTestedAtUtc = DateTimeOffset.UtcNow;
                await _agent.Database.SavePrinterAsync(printer);
                MessageBox.Show($"TCP 连接成功：{printer.Host}:{printer.Port}", "PrintPal",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            printer.LastTestOk = false;
            printer.LastTestedAtUtc = DateTimeOffset.UtcNow;
            await _agent.Database.SavePrinterAsync(printer);
            MessageBox.Show($"连接失败：{ex.GetType().Name}: {ex.Message}", "PrintPal",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ReloadAsync();
        }
    }

    private async void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedPrinter;
        if (selected is null)
        {
            return;
        }

        foreach (var printer in _printers)
        {
            printer.IsDefault = printer.Id == selected.Id;
            await _agent.Database.SavePrinterAsync(printer);
        }

        await ReloadAsync();
    }

    // ------------------------------------------------------------------ routing

    private RoutingRule? SelectedRouting => (RoutingGrid.SelectedItem as RoutingRow)?.Rule;

    private async void AddRouting_Click(object sender, RoutedEventArgs e)
    {
        if (_printers.Count == 0)
        {
            MessageBox.Show("请先添加打印机。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new RoutingEditWindow(null, _printers) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _agent.Database.SaveRoutingRuleAsync(dialog.Rule);
            await ReloadAsync();
        }
    }

    private async void EditRouting_Click(object sender, RoutedEventArgs e)
    {
        var rule = SelectedRouting;
        if (rule is null)
        {
            MessageBox.Show("请选择路由规则。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new RoutingEditWindow(rule, _printers) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await _agent.Database.SaveRoutingRuleAsync(dialog.Rule);
            await ReloadAsync();
        }
    }

    private async void DeleteRouting_Click(object sender, RoutedEventArgs e)
    {
        var rule = SelectedRouting;
        if (rule is null)
        {
            return;
        }

        if (MessageBox.Show($"删除路由规则“{rule.Name}”？", "PrintPal",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await _agent.Database.DeleteRoutingRuleAsync(rule.Id);
        await ReloadAsync();
    }

    // ------------------------------------------------------------------ prices

    private void PricesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        _pricesDirty = true;
    }

    private void PricesGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // WPF's default Undo can make the DataGrid look like it lost every price.
        // Price editing is explicit-save only; never let Ctrl+Z/Y mutate the table.
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0 &&
            e.Key is System.Windows.Input.Key.Z or System.Windows.Input.Key.Y)
        {
            e.Handled = true;
            StatusBarText.Text = "价格表不支持撤销/重做；修改后请点击“保存价格”。";
        }
    }

    private async void SavePrices_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PricesGrid.CommitEdit(DataGridEditingUnit.Row, true);

            foreach (var row in _priceRows)
            {
                row.ApplyToRule();
                await _agent.Database.SavePriceRuleAsync(row.Rule);
            }

            _pricesDirty = false;
            await ReloadAsync();
            MessageBox.Show("价格已保存。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存价格失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ settings

    private async void AutoForward_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        var enabled = AutoForwardCheck.IsChecked == true;
        _agent.Options.AutoForward = enabled;
        await _agent.Database.SetSettingAsync("autoForward", enabled ? "true" : "false");
        StatusBarText.Text = enabled ? "已开启自动转发" : "已关闭自动转发";
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressAutostart)
        {
            return;
        }

        try
        {
            AutostartService.SetEnabled(AutostartCheck.IsChecked == true);
            StatusBarText.Text = AutostartCheck.IsChecked == true ? "已设置开机启动" : "已取消开机启动";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置开机启动失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
            _suppressAutostart = true;
            AutostartCheck.IsChecked = AutostartService.IsEnabled();
            _suppressAutostart = false;
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(_agent.Options.DataDirectory);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(_agent.Options.DataDirectory, "logs"));

    private void CopyInstall_Click(object sender, RoutedEventArgs e)
    {
        var command = $"Add-Printer -IppURL {_agent.IppUri}";
        Clipboard.SetText(command);
        MessageBox.Show($"已复制安装命令：\n\n{command}\n\n请在管理员 PowerShell 中执行；Win10 需先运行：\n" +
                        "Add-PrinterDriver -Name 'Microsoft IPP Class Driver'",
            "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CheckQueue_Click(object sender, RoutedEventArgs e) => UpdateQueueStatus();

    private void InstallHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "安装虚拟打印机队列（需要管理员权限）：\n\n" +
            "1. 保持 PrintPal 运行；\n" +
            "2. 以管理员身份打开 PowerShell；\n" +
            "3. Win10 先执行：\n" +
            "   Add-PrinterDriver -Name 'Microsoft IPP Class Driver'\n\n" +
            "4. 执行：\n" +
            $"   Add-Printer -IppURL {_agent.IppUri}\n\n" +
            "或运行仓库 scripts\\Install-PrintPalQueue.ps1。\n" +
            "安装后可用 Get-Printer 检查队列状态。",
            "PrintPal 安装队列",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void UpdateQueueStatus()
    {
        QueueStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        QueueStatusText.Text = "队列状态：检查中 ...";

        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(QueueName);

            if (queue.IsOffline)
            {
                QueueStatusText.Text = "队列状态：已安装，但当前离线（确认 PrintPal 正在运行）";
            }
            else
            {
                QueueStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                var status = queue.QueueStatus == PrintQueueStatus.None ? "正常" : queue.QueueStatus.ToString();
                QueueStatusText.Text = $"队列状态：已安装（{status}）";
            }
        }
        catch (Exception ex)
        {
            QueueStatusText.Text = $"队列状态：未安装或不可用（{ex.GetType().Name}）— 请运行安装脚本";
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static ScrollViewer? GetScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
        {
            return viewer;
        }

        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            var nested = GetScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void RestoreScrollOffset(DependencyObject grid, double verticalOffset, double horizontalOffset = 0)
    {
        var viewer = GetScrollViewer(grid);
        if (viewer is null)
        {
            return;
        }

        if (verticalOffset > 0)
        {
            viewer.ScrollToVerticalOffset(verticalOffset);
        }

        if (horizontalOffset > 0)
        {
            viewer.ScrollToHorizontalOffset(horizontalOffset);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowMessageHook);
        TryEnableRoundedCorners(handle);
    }

    private static void TryEnableRoundedCorners(IntPtr hwnd)
    {
        try
        {
            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2.
            // Windows 10 ignores the attribute; Windows 11 applies it.
            const int CornerPreference = 33;
            var preference = 2;
            DwmSetWindowAttribute(hwnd, CornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;

        if (message == WmGetMinMaxInfo)
        {
            ApplyMaximizeWorkArea(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static void ApplyMaximizeWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        const int MonitorDefaultToNearest = 2;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = monitorInfo.WorkArea;
        var full = monitorInfo.Monitor;

        // WindowChrome adds an invisible sizing frame around a borderless window.
        // Align the maximized window to the monitor work area instead of covering the taskbar.
        minMax.MaxPosition.X = work.Left - full.Left;
        minMax.MaxPosition.Y = work.Top - full.Top;
        minMax.MaxSize.X = work.Right - work.Left;
        minMax.MaxSize.Y = work.Bottom - work.Top;
        minMax.MaxTrackSize.X = full.Right - full.Left;
        minMax.MaxTrackSize.Y = full.Bottom - full.Top;

        Marshal.StructureToPtr(minMax, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveModeBounds();

        if (!((App)Application.Current).IsExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        base.OnClosed(e);
    }
}

/// <summary>
/// Draws the tray context menu with the same flat, light surface + blue accent language
/// as the WPF UI instead of the stock grey WinForms look.
/// </summary>
internal sealed class TrayMenuRenderer : WinForms.ToolStripRenderer
{
    private const int TextPaddingLeft = 14;

    internal static readonly System.Drawing.Color Surface = System.Drawing.Color.White;
    internal static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0xEA, 0xF1, 0xFF);
    internal static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0xE6, 0xEB, 0xF3);
    internal static readonly System.Drawing.Color TextColor = System.Drawing.Color.FromArgb(0x33, 0x41, 0x55);
    internal static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(0x1D, 0x4E, 0xD8);
    internal static readonly System.Drawing.Color Disabled = System.Drawing.Color.FromArgb(0x94, 0xA3, 0xB8);

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Surface);
    }

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        using var pen = new System.Drawing.Pen(Border);
        var bounds = new System.Drawing.Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = RoundedPath(bounds, 9);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        var graphics = e.Graphics;
        var bounds = new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.Item.Size);

        if (!e.Item.Selected || !e.Item.Enabled)
        {
            using var background = new System.Drawing.SolidBrush(Surface);
            graphics.FillRectangle(background, bounds);
            return;
        }

        // Cover the whole item so the highlight reaches the menu edge like a native menu.
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var highlight = new System.Drawing.Rectangle(
            1,
            1,
            Math.Max(0, bounds.Width - 2),
            Math.Max(0, bounds.Height - 2));
        using var path = RoundedPath(highlight, 7);
        using var fill = new System.Drawing.SolidBrush(Hover);
        graphics.FillPath(fill, path);
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        // Draw the label ourselves, vertically centred over the full item height;
        // the default renderer uses its own padded text rectangle, which made the
        // text look top-aligned in these taller items.
        var color = !e.Item.Enabled ? Disabled : e.Item.Selected ? Accent : TextColor;
        var textBounds = new System.Drawing.Rectangle(
            TextPaddingLeft,
            0,
            Math.Max(0, e.Item.Width - TextPaddingLeft - 12),
            e.Item.Height);
        var flags = WinForms.TextFormatFlags.Left
            | WinForms.TextFormatFlags.VerticalCenter
            | WinForms.TextFormatFlags.SingleLine
            | WinForms.TextFormatFlags.NoPrefix
            | WinForms.TextFormatFlags.EndEllipsis;

        WinForms.TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font, textBounds, color, flags);
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new System.Drawing.Pen(Border);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e)
    {
        // Items have no icons; keep the whole menu surface flat white.
    }

    internal static System.Drawing.Drawing2D.GraphicsPath RoundedPath(System.Drawing.Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}



