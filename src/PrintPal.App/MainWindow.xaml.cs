using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintPal.Agent;
using PrintPal.App.Themes;
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
    private readonly ObservableCollection<BillChipRow> _openBillChips = new();
    private readonly ObservableCollection<ClosedBillRow> _closedBillRows = new();
    private readonly ObservableCollection<JobRow> _historyJobRows = new();
    private readonly ObservableCollection<FlowRow> _flowRows = new();
    private readonly ObservableCollection<DailyStatRow> _dailyStatRows = new();
    private readonly ObservableCollection<DimensionStatRow> _dimensionStatRows = new();
    private readonly ObservableCollection<HourBarRow> _hourBarRows = new();
    private IReadOnlyList<PrintFlowEntry> _allFlows = Array.Empty<PrintFlowEntry>();
    private IReadOnlyDictionary<Guid, string> _jobFlowNos = new Dictionary<Guid, string>();
    private string? _currentBillFlowNo;
    private IReadOnlyList<DailyPrintStat> _dailyStats = Array.Empty<DailyPrintStat>();
    private bool _suppressStatsRange;
    private StatsChartMode _chartMode = StatsChartMode.Trend;
    private readonly List<(System.Windows.Point Point, DailyPrintStat Stat, double Value)> _chartPoints = new();
    private Border? _chartHoverBorder;
    private TextBlock? _chartHoverText;
    private System.Windows.Shapes.Line? _chartGuideLine;

    private enum StatsChartMode
    {
        Trend,
        Hour,
    }
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
    private double _detailsWidth = DetailsWidthDefault;
    private bool _printDetailsShown;
    private bool _historyDetailsShown;
    private bool _restoreModePosition;
    private DispatcherTimer? _boundsSaveTimer;
    private UiMode _mode = UiMode.Full;

    // 历史结账「账单明细」宽度（拖拽后写入 Settings.historyDetailsWidth）
    private double _historyDetailsWidth = HistoryDetailsWidthDefault;

    // 打印流水分页
    private int _flowPage = 1;
    private readonly List<FlowRow> _flowFilteredRows = new();

    // 主题
    private readonly List<Button> _themeButtons = new();
    private string _themeKey = ThemeManager.DefaultKey;

    // 极简模式打印通知（滚动 marquee）
    private DateTimeOffset _lastSeenJobCreatedAt = DateTimeOffset.MinValue;
    private DispatcherTimer? _minimalNoticeTimer;
    private double? _minimalHeightBeforeNotice;
    private Storyboard? _minimalNoticeStoryboard;

    private const double DetailsWidthDefault = 360;
    private const double DetailsWidthMin = 260;
    private const double DetailsWidthMax = 760;

    private const double HistoryDetailsWidthDefault = 460;
    private const double HistoryDetailsWidthMin = 320;
    private const double HistoryDetailsWidthMax = 900;

    private int _flowPageSize = 50;

    private enum UiMode
    {
        Full,
        Compact,
        Minimal,
    }

    public MainWindow(IPrintPalBackend agent)
    {
        InitializeComponent();

        _agent = agent;

        CashierGrid.ItemsSource = _jobRows;
        CompactCashierGrid.ItemsSource = _jobRows;
        PrintersGrid.ItemsSource = _printerRows;
        RoutingGrid.ItemsSource = _routingRows;
        PricesGrid.ItemsSource = _priceRows;
        HistoryGrid.ItemsSource = _closedBillRows;
        HistoryCashierGrid.ItemsSource = _historyJobRows;
        FlowsGrid.ItemsSource = _flowRows;
        DailyStatsGrid.ItemsSource = _dailyStatRows;
        DimensionStatsGrid.ItemsSource = _dimensionStatRows;
        HourBars.ItemsSource = _hourBarRows;
        FullParkedBills.ItemsSource = _openBillChips;
        CompactParkedBills.ItemsSource = _openBillChips;
        MinimalParkedBills.ItemsSource = _openBillChips;
        FullParkedPopupList.ItemsSource = _openBillChips;
        CompactParkedPopupList.ItemsSource = _openBillChips;
        MinimalParkedPopupList.ItemsSource = _openBillChips;

        UpdateChartMode();

        _chipStrips.Add(new ChipOverflowStrip(
            FullParkedPanel, FullParkedChipHost, FullParkedBills, FullParkedOverflowButton, FullParkedPopup,
            (DataTemplate)FindResource("ParkedBillChipTemplate")));
        _chipStrips.Add(new ChipOverflowStrip(
            CompactParkedPanel, CompactParkedChipHost, CompactParkedBills, CompactParkedOverflowButton, CompactParkedPopup,
            (DataTemplate)FindResource("ParkedBillChipTemplate")));
        _chipStrips.Add(new ChipOverflowStrip(
            MinimalParkedPanel, MinimalParkedChipHost, MinimalParkedBills, MinimalParkedOverflowButton, MinimalParkedPopup,
            (DataTemplate)FindResource("SmallParkedBillChipTemplate")));
        foreach (var strip in _chipStrips)
        {
            strip.Host.LayoutUpdated += OnChipHostLayoutUpdated;
        }

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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await ReloadAsync();

        // 窗口移动/缩放后防抖保存边界：任意时刻退出（含强杀）都能恢复最新位置
        LocationChanged += (_, _) => ScheduleBoundsSave();
        SizeChanged += (_, _) => ScheduleBoundsSave();

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
            // 打印详情宽度在首次 Reload（会触发布局）之前就位，避免打开时先闪一下默认宽度。
            _detailsWidth = Math.Clamp(
                await GetDoubleSettingAsync("detailsWidth", DetailsWidthDefault),
                DetailsWidthMin,
                DetailsWidthMax);

            // 历史账单明细宽度同理
            _historyDetailsWidth = Math.Clamp(
                await GetDoubleSettingAsync("historyDetailsWidth", HistoryDetailsWidthDefault),
                HistoryDetailsWidthMin,
                HistoryDetailsWidthMax);

            // 主题：先应用再 Reload，避免首帧闪一下默认配色
            _themeKey = await _agent.Database.GetSettingAsync("uiTheme") ?? ThemeManager.DefaultKey;
            Console.WriteLine($"THEME setting loaded: [{_themeKey}]");
            ThemeManager.Apply(ThemeManager.Find(_themeKey));
            BuildThemeButtons();

            await ReloadAsync();
            UpdateQueueStatus();

            var savedMode = await _agent.Database.GetSettingAsync("uiMode");
            // 兼容更名前的持久化值（Simple → Compact）
            if (savedMode == "Simple")
            {
                savedMode = "Compact";
            }
            if (Enum.TryParse<UiMode>(savedMode, out var mode))
            {
                await SetModeAsync(mode, persist: false, force: true);
            }

            UpdatePrintDetailsPanel();

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
        menu.Items.Add(CreateTrayItem("紧凑模式", () => _ = SetModeAsync(UiMode.Compact)));
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

        if (NavList.SelectedIndex == 2)
        {
            _ = LoadFlowsAsync();
        }
        else if (NavList.SelectedIndex == 3)
        {
            _ = LoadStatsAsync();
        }
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
        FlowsPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        StatsPage.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PrintersPage.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        RoutingPage.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        PricesPage.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;

        (PageTitleText.Text, PageSubtitleText.Text) = index switch
        {
            1 => ("历史结账", "按账单归档的结账记录"),
            2 => ("打印流水", "全部打印操作，可追溯到账单"),
            3 => ("统计", "营业额、账单数与多维分布"),
            4 => ("打印机", "维护 IPP 与 Raw 9100 目标"),
            5 => ("路由规则", "按纸张、颜色、单双面自动分流"),
            6 => ("价格表", "按张计价，保存后立即生效"),
            7 => ("设置", "服务、数据与启动项"),
            _ => ("收银台", "账单收银与打印转发"),
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

        if (CompactMaximizeGlyph is not null)
        {
            CompactMaximizeGlyph.Text = glyph;
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
            var selectedJobId = (ActiveCashierGrid.SelectedItem as JobRow)?.Job.Id;
            var selectedBillId = (HistoryGrid.SelectedItem as ClosedBillRow)?.Bill.Id;
            var selectedPrinterId = (PrintersGrid.SelectedItem as PrinterRow)?.Printer.Id;
            var selectedRoutingId = (RoutingGrid.SelectedItem as RoutingRow)?.Rule.Id;
            var jobsScroll = GetScrollViewer(CashierGrid);
            var compactJobsScroll = GetScrollViewer(CompactCashierGrid);
            var historyScroll = GetScrollViewer(HistoryGrid);
            var printersScroll = GetScrollViewer(PrintersGrid);
            var routingScroll = GetScrollViewer(RoutingGrid);
            var pricesScroll = GetScrollViewer(PricesGrid);
            var jobsOffset = jobsScroll?.VerticalOffset ?? 0;
            var jobsHOffset = jobsScroll?.HorizontalOffset ?? 0;
            var compactJobsOffset = compactJobsScroll?.VerticalOffset ?? 0;
            var compactJobsHOffset = compactJobsScroll?.HorizontalOffset ?? 0;
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
            var jobFlowTask = _agent.Database.GetJobFlowNumbersAsync();
            var billFlowTask = _agent.Database.GetBillFlowNoAsync(bill.Id);

            await Task.WhenAll(jobsTask, openBillsTask, printersTask, routingTask, pricesTask, closedBillsTask,
                jobFlowTask, billFlowTask);

            var jobs = await jobsTask;
            _openBills = (await openBillsTask).ToList();
            _printers = (await printersTask).ToList();
            _routingRules = (await routingTask).ToList();
            _priceRules = (await pricesTask).ToList();
            _closedBills = (await closedBillsTask).ToList();
            _jobFlowNos = await jobFlowTask;
            _currentBillFlowNo = await billFlowTask;

            SyncOpenBillChips();

            var printerNames = _printers.ToDictionary(printer => printer.Id, printer => printer.Name);
            _allJobs.Clear();
            foreach (var job in jobs.OrderByDescending(job => job.CreatedAtUtc))
            {
                _allJobs.Add(new JobRow(job, printerNames)
                {
                    FlowNo = _jobFlowNos.TryGetValue(job.Id, out var flowNo) ? flowNo : "",
                });
            }

            DetectNewJobsForMinimalNotice();

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
            UpdateBillFlowNoButton();
            UpdateDetailsFlowNo();

            if (FlowsPage.Visibility == Visibility.Visible)
            {
                await LoadFlowsAsync((FlowsGrid.SelectedItem as FlowRow)?.Flow.Id);
            }

            StatusBarText.Text = $"最后刷新 {DateTime.Now:HH:mm:ss} · 当前账单 {jobs.Count} 条记录";

            _ = Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    RestoreScrollOffset(CashierGrid, jobsOffset, jobsHOffset);
                    RestoreScrollOffset(CompactCashierGrid, compactJobsOffset, compactJobsHOffset);
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

    // ------------------------------------------------------------------ print flows

    private async Task LoadFlowsAsync(Guid? selectId = null)
    {
        try
        {
            // 旧版服务/安装包写入的任务没有流水，刷新前先补齐，保证「每条任务都有流水」。
            await _agent.Database.EnsureFlowsAsync();
            _allFlows = await _agent.Database.GetFlowsAsync(limit: 1000);
            ApplyFlowFilter(selectId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"load flows failed: {ex.Message}");
            StatusBarText.Text = $"流水加载失败：{ex.Message}";
        }
    }

    private void ApplyFlowFilter(Guid? selectId = null)
    {
        if (FlowSearchBox is null)
        {
            return;
        }

        var search = FlowSearchBox.Text.Trim();
        _flowFilteredRows.Clear();
        _flowFilteredRows.AddRange(_allFlows
            // 流水页只展示文件（打印任务）流水；账单流水仍保留在库里用于追溯与统计。
            .Where(entry => entry.Flow.Kind == PrintFlowKind.Job)
            .Where(entry => search.Length == 0
                || entry.Flow.FlowNo.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (entry.Flow.BillNameSnapshot ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                || (entry.Flow.DocumentNameSnapshot ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                || (entry.Flow.TargetPrinterSnapshot ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new FlowRow(entry)));

        var totalPages = Math.Max(1, (int)Math.Ceiling(_flowFilteredRows.Count / (double)_flowPageSize));
        _flowPage = Math.Clamp(_flowPage, 1, totalPages);

        // 追溯时如果目标不在当前页，自动跳到它所在页
        if (selectId is Guid targetId)
        {
            var index = _flowFilteredRows.FindIndex(row => row.Flow.Id == targetId);
            if (index >= 0)
            {
                _flowPage = index / _flowPageSize + 1;
            }
        }

        var pageRows = _flowFilteredRows
            .Skip((_flowPage - 1) * _flowPageSize)
            .Take(_flowPageSize)
            .ToList();

        _flowRows.Clear();
        foreach (var row in pageRows)
        {
            _flowRows.Add(row);
        }

        FlowCountText.Text = $"共 {_flowFilteredRows.Count} 条";
        FlowPageText.Text = $"第 {_flowPage} / {totalPages} 页";
        FlowPrevPageButton.IsEnabled = _flowPage > 1;
        FlowNextPageButton.IsEnabled = _flowPage < totalPages;
        BuildFlowPageNumbers(totalPages);

        if (selectId is Guid selectedId)
        {
            var selected = _flowRows.FirstOrDefault(row => row.Flow.Id == selectedId);
            if (selected is not null)
            {
                FlowsGrid.SelectedItem = selected;
                FlowsGrid.ScrollIntoView(selected);
            }
        }
    }

    private void FlowPageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || FlowPageSizeCombo is null)
        {
            return;
        }

        _flowPageSize = FlowPageSizeCombo.SelectedIndex switch
        {
            0 => 20,
            2 => 100,
            _ => 50,
        };
        _flowPage = 1;
        ApplyFlowFilter();
    }

    private void BuildFlowPageNumbers(int totalPages)
    {
        FlowPageNumbersPanel.Children.Clear();
        if (totalPages <= 1)
        {
            return;
        }

        foreach (var page in BuildPageList(_flowPage, totalPages))
        {
            if (page is null)
            {
                FlowPageNumbersPanel.Children.Add(new TextBlock
                {
                    Text = "…",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0),
                    Foreground = (System.Windows.Media.Brush)FindResource("SoftMutedBrush"),
                });
                continue;
            }

            var button = new Button
            {
                Content = page.Value.ToString(),
                Tag = page.Value,
                MinWidth = 28,
                Height = 26,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = $"第 {page.Value} 页",
            };

            if (page.Value == _flowPage)
            {
                button.Background = (System.Windows.Media.Brush)FindResource("AccentBrush");
                button.Foreground = System.Windows.Media.Brushes.White;
                button.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentDarkBrush");
                button.FontWeight = FontWeights.SemiBold;
            }

            button.Click += FlowPageNumber_Click;
            FlowPageNumbersPanel.Children.Add(button);
        }
    }

    private static List<int?> BuildPageList(int current, int total)
    {
        var pages = new List<int?>();
        if (total <= 7)
        {
            for (var i = 1; i <= total; i++)
            {
                pages.Add(i);
            }

            return pages;
        }

        pages.Add(1);
        var start = Math.Max(2, current - 1);
        var end = Math.Min(total - 1, current + 1);
        if (start > 2)
        {
            pages.Add(null);
        }

        for (var i = start; i <= end; i++)
        {
            pages.Add(i);
        }

        if (end < total - 1)
        {
            pages.Add(null);
        }

        pages.Add(total);
        return pages;
    }

    private void FlowPageNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int page })
        {
            _flowPage = page;
            ApplyFlowFilter();
        }
    }

    private void FlowPageJumpBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            JumpToFlowPage();
            e.Handled = true;
        }
    }

    private void FlowPageJump_Click(object sender, RoutedEventArgs e) => JumpToFlowPage();

    private void JumpToFlowPage()
    {
        if (!int.TryParse(FlowPageJumpBox.Text.Trim(), out var page))
        {
            return;
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(_flowFilteredRows.Count / (double)_flowPageSize));
        _flowPage = Math.Clamp(page, 1, totalPages);
        FlowPageJumpBox.Text = "";
        ApplyFlowFilter();
    }

    private void FlowPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_flowPage > 1)
        {
            _flowPage--;
            ApplyFlowFilter();
        }
    }

    private void FlowNextPage_Click(object sender, RoutedEventArgs e)
    {
        _flowPage++;
        ApplyFlowFilter();
    }

    private void FlowSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            _flowPage = 1;
            ApplyFlowFilter();
        }
    }

    private async void FlowsRefresh_Click(object sender, RoutedEventArgs e) => await LoadFlowsAsync();

    private async void FlowsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FlowsGrid.SelectedItem is FlowRow row)
        {
            await TraceFlowAsync(row);
        }
    }

    private async void FlowTrace_Click(object sender, RoutedEventArgs e)
    {
        if (FlowsGrid.SelectedItem is FlowRow row)
        {
            await TraceFlowAsync(row);
        }
        else
        {
            StatusBarText.Text = "请先选择一条流水";
        }
    }

    private void FlowCopyNo_Click(object sender, RoutedEventArgs e)
    {
        if (FlowsGrid.SelectedItem is not FlowRow row)
        {
            StatusBarText.Text = "请先选择一条流水";
            return;
        }

        try
        {
            Clipboard.SetText(row.FlowNo);
            StatusBarText.Text = $"已复制流水号 {row.FlowNo}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"copy flow no failed: {ex.Message}");
        }
    }

    private async void FlowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (FlowsGrid.SelectedItem is not FlowRow row)
        {
            StatusBarText.Text = "请先选择一条流水";
            return;
        }

        var confirmed = MessageDialog.Show(
            this,
            "删除流水",
            $"删除流水 {row.FlowNo}？\n\n只删除流水记录，账单和打印任务不受影响。",
            "删除",
            "取消",
            MessageTone.Question);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _agent.Database.DeleteFlowAsync(row.Flow.Id);
            await LoadFlowsAsync();
            StatusBarText.Text = $"已删除流水 {row.FlowNo}";
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, "删除流水失败", ex.Message);
        }
    }

    /// <summary>从流水追溯到账单：优先按当前账单/历史账单跳转；账单已删除时展示快照。</summary>
    private async Task TraceFlowAsync(FlowRow row)
    {
        var flow = row.Flow;

        Bill? bill = flow.BillId is Guid billId ? await _agent.Database.GetBillAsync(billId) : null;

        if (bill is null && flow.JobId is Guid jobId)
        {
            var job = await _agent.Database.GetJobAsync(jobId);
            if (job?.BillId is Guid jobBillId)
            {
                bill = await _agent.Database.GetBillAsync(jobBillId);
            }
        }

        if (bill is null)
        {
            MessageDialog.Info(
                this,
                "账单已删除",
                $"流水号：{flow.FlowNo}\n" +
                $"账单快照：{flow.BillNameSnapshot ?? "—"}\n" +
                $"金额：{row.AmountText}\n" +
                $"状态：{row.StatusText}\n\n" +
                "该账单已不存在，仅保留流水快照，无法跳转。");
            return;
        }

        if (bill.Status == BillStatus.Closed)
        {
            await NavigateToClosedBillAsync(bill.Id, flow.JobId);
            return;
        }

        var jobExists = flow.JobId is Guid candidateJobId
                        && await _agent.Database.GetJobAsync(candidateJobId) is not null;

        try
        {
            if (bill.Status == BillStatus.Parked)
            {
                await _agent.Database.ActivateBillAsync(bill.Id);
            }

            NavList.SelectedIndex = 0;
            await ReloadAsync();

            if (jobExists && flow.JobId is Guid targetJobId)
            {
                ApplyFilters(selectJobId: targetJobId, scrollToSelection: true);
            }

            StatusBarText.Text = $"已定位到账单「{bill.Name}」";
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, "无法定位账单", ex.Message);
        }
    }

    /// <summary>跳到历史结账并选中账单；有任务 Id 时同时选中该任务（高亮 + 滚动到可见）。</summary>
    private async Task NavigateToClosedBillAsync(Guid billId, Guid? jobId)
    {
        NavList.SelectedIndex = 1;
        HistoryRangeCombo.SelectedIndex = 0;

        if (HistorySearchBox.Text.Length > 0)
        {
            HistorySearchBox.Text = "";
        }

        ApplyHistoryFilter(billId);

        // 程序化选中不会触发「打开明细」逻辑，这里显式打开账单明细。
        if (HistoryGrid.SelectedItem is ClosedBillRow selectedBillRow)
        {
            _historyDetailsClosed = false;
            await LoadHistoryJobsAsync(selectedBillRow);
            UpdateHistoryDetailsPanel();
        }

        if (jobId is Guid targetJobId)
        {
            // 账单明细里的任务列表是异步加载的，等它出现后再选中。
            for (var attempt = 0; attempt < 40 && _historyJobRows.All(row => row.Job.Id != targetJobId); attempt++)
            {
                await Task.Delay(50);
            }

            var row = _historyJobRows.FirstOrDefault(item => item.Job.Id == targetJobId);
            if (row is not null)
            {
                HistoryCashierGrid.SelectedItem = row;
                HistoryCashierGrid.ScrollIntoView(row);
                StatusBarText.Text = "已定位到历史结账中的账单与打印任务";
                return;
            }
        }

        StatusBarText.Text = "已定位到历史结账中的账单";
    }

    // ------------------------------------------------------------------ statistics

    private async Task LoadStatsAsync()
    {
        // 本地 SQLite 通常很快；超过 200ms 才显示加载遮罩，避免闪烁。
        var loadTask = LoadStatsCoreAsync();
        var delay = Task.Delay(200);
        var first = await Task.WhenAny(loadTask, delay);
        if (first == delay)
        {
            StatsLoadingOverlay.Visibility = Visibility.Visible;
            try
            {
                await loadTask;
            }
            finally
            {
                StatsLoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            await loadTask;
        }
    }

    private async Task LoadStatsCoreAsync()
    {
        try
        {
            var rangeIndex = StatsRangeCombo?.SelectedIndex ?? 1;
            var (from, to) = ComputeStatsRange(rangeIndex);

            var dailyTask = _agent.Database.GetDailyStatsAsync(from, to);
            var paperTask = _agent.Database.GetDimensionStatsAsync("paper", from, to);
            var colorTask = _agent.Database.GetDimensionStatsAsync("color", from, to);
            var duplexTask = _agent.Database.GetDimensionStatsAsync("duplex", from, to);
            var printerTask = _agent.Database.GetDimensionStatsAsync("printer", from, to);
            var hourTask = _agent.Database.GetHourStatsAsync(from, to);
            var openBillsTask = _agent.Database.GetOpenBillsAsync();

            await Task.WhenAll(dailyTask, paperTask, colorTask, duplexTask, printerTask, hourTask, openBillsTask);

            var daily = await dailyTask;
            var dimensions = (await paperTask)
                .Concat(await colorTask)
                .Concat(await duplexTask)
                .Concat(await printerTask)
                .ToList();
            var hours = await hourTask;
            var openBills = await openBillsTask;

            _dailyStatRows.Clear();
            foreach (var stat in daily.OrderByDescending(item => item.Date))
            {
                _dailyStatRows.Add(new DailyStatRow(stat));
            }

            _dimensionStatRows.Clear();
            foreach (var stat in dimensions)
            {
                _dimensionStatRows.Add(new DimensionStatRow(stat));
            }

            var maxJobs = hours.Count > 0 ? hours.Max(item => item.JobCount) : 0;
            _hourBarRows.Clear();
            foreach (var stat in hours)
            {
                _hourBarRows.Add(new HourBarRow(stat, maxJobs));
            }

            var revenue = daily.Sum(item => item.RevenueCents);
            var billCount = daily.Sum(item => item.BillCount);
            var jobCount = daily.Sum(item => item.JobCount);
            var pageCount = daily.Sum(item => item.PageCount);
            var sheetCount = daily.Sum(item => item.SheetCount);

            StatsRevenueText.Text = Money.FormatCents(revenue);
            StatsBillsText.Text = billCount.ToString();
            StatsJobsText.Text = jobCount.ToString();
            StatsPagesText.Text = pageCount.ToString();
            StatsSheetsText.Text = sheetCount.ToString();
            StatsAvgText.Text = billCount > 0 ? Money.FormatCents(revenue / billCount) : Money.FormatCents(0);

            var pendingAmount = openBills.Sum(item => item.AmountCents);
            StatsRangeHint.Text =
                $"{from:yyyy-MM-dd} ~ {to.AddDays(-1):yyyy-MM-dd} · 待结 {openBills.Count} 张 " +
                $"{Money.FormatCents(pendingAmount)}（未计入营业额）";

            _dailyStats = daily;
            _ = Dispatcher.BeginInvoke(new Action(DrawStatsChart), DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            StatsRangeHint.Text = $"统计加载失败：{ex.Message}";
        }
    }

    private (DateTimeOffset From, DateTimeOffset To) ComputeStatsRange(int rangeIndex)
    {
        var today = DateTime.Today;
        var offset = TimeZoneInfo.Local.GetUtcOffset(today);

        if (rangeIndex == 4)
        {
            var start = (StatsStartPicker?.SelectedDate ?? today.AddDays(-6)).Date;
            var end = (StatsEndPicker?.SelectedDate ?? today).Date;
            if (end < start)
            {
                (start, end) = (end, start);
            }

            if ((end - start).TotalDays > 366)
            {
                end = start.AddDays(366);
            }

            return (new DateTimeOffset(start, offset), new DateTimeOffset(end.AddDays(1), offset));
        }

        var to = new DateTimeOffset(today.AddDays(1), offset);
        var from = rangeIndex switch
        {
            0 => new DateTimeOffset(today, offset),
            2 => new DateTimeOffset(today.AddDays(-29), offset),
            3 => new DateTimeOffset(2000, 1, 1, 0, 0, 0, offset),
            _ => new DateTimeOffset(today.AddDays(-6), offset),
        };

        return (from, to);
    }

    private void StatsRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StatsCustomRangePanel is null)
        {
            return;
        }

        var custom = StatsRangeCombo.SelectedIndex == 4;
        StatsCustomRangePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

        if (custom)
        {
            // 首次切到自定义时给一个默认区间（近 7 天）；程序化赋值不触发自动弹出结束选择。
            _suppressStatsRange = true;
            try
            {
                if (StatsStartPicker.SelectedDate is null)
                {
                    StatsStartPicker.SelectedDate = DateTime.Today.AddDays(-6);
                }

                if (StatsEndPicker.SelectedDate is null)
                {
                    StatsEndPicker.SelectedDate = DateTime.Today;
                }
            }
            finally
            {
                _suppressStatsRange = false;
            }
        }

        if (_initialized && StatsPage.Visibility == Visibility.Visible)
        {
            _ = LoadStatsAsync();
        }
    }

    private void StatsStartPicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStatsRange || StatsStartPicker.SelectedDate is not DateTime start)
        {
            return;
        }

        _suppressStatsRange = true;
        try
        {
            if (StatsEndPicker.SelectedDate is not DateTime end || end < start)
            {
                StatsEndPicker.SelectedDate = start;
            }

            // 选完开始日期自动弹出结束日期选择，少点一次。
            StatsEndPicker.IsDropDownOpen = true;
        }
        finally
        {
            _suppressStatsRange = false;
        }

        // 只改了开始日期时先不刷新：等用户选完结束日期再更新统计。
        StatsRangeHint.Text = "已选择开始日期，请选择结束日期后更新统计";
    }

    private void StatsEndPicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStatsRange
            || StatsStartPicker.SelectedDate is not DateTime start
            || StatsEndPicker.SelectedDate is not DateTime end)
        {
            return;
        }

        if (end < start)
        {
            _suppressStatsRange = true;
            try
            {
                StatsEndPicker.SelectedDate = start;
            }
            finally
            {
                _suppressStatsRange = false;
            }

            return;
        }

        if ((end - start).TotalDays > 366)
        {
            _suppressStatsRange = true;
            try
            {
                StatsEndPicker.SelectedDate = start.AddDays(366);
            }
            finally
            {
                _suppressStatsRange = false;
            }

            StatusBarText.Text = "自定义时间段最长一年，已自动截断到 366 天";
            return;
        }

        if (_initialized && StatsPage.Visibility == Visibility.Visible)
        {
            _ = LoadStatsAsync();
        }
    }

    private void StatsMetric_Changed(object sender, SelectionChangedEventArgs e) => DrawStatsChart();

    private void StatsChartMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        _chartMode = tag == "hour" ? StatsChartMode.Hour : StatsChartMode.Trend;
        UpdateChartMode();
    }

    private void UpdateChartMode()
    {
        if (StatsChartHost is null)
        {
            return;
        }

        var trend = _chartMode == StatsChartMode.Trend;
        StatsChartHost.Visibility = trend ? Visibility.Visible : Visibility.Collapsed;
        HourBars.Visibility = trend ? Visibility.Collapsed : Visibility.Visible;
        StatsMetricCombo.Visibility = trend ? Visibility.Visible : Visibility.Collapsed;

        var accentSoft = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
        var accentDark = (System.Windows.Media.Brush)FindResource("AccentDarkBrush");
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var transparent = System.Windows.Media.Brushes.Transparent;

        StatsChartTrendButton.Background = trend ? accentSoft : transparent;
        StatsChartTrendButton.Foreground = trend ? accentDark : muted;
        StatsChartHourButton.Background = trend ? transparent : accentSoft;
        StatsChartHourButton.Foreground = trend ? muted : accentDark;

        if (trend)
        {
            DrawStatsChart();
        }
    }

    private void StatsChartHost_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_chartMode != StatsChartMode.Trend
            || _chartPoints.Count == 0
            || _chartHoverBorder is null
            || _chartHoverText is null
            || _chartGuideLine is null)
        {
            return;
        }

        var position = e.GetPosition(StatsChartHost);
        var nearest = _chartPoints[0];
        var bestDistance = double.MaxValue;
        foreach (var candidate in _chartPoints)
        {
            var distance = Math.Abs(candidate.Point.X - position.X);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                nearest = candidate;
            }
        }

        _chartHoverText.Text =
            $"{nearest.Stat.Date:yyyy-MM-dd} · {FormatChartValue(nearest.Value, StatsMetricCombo?.SelectedIndex ?? 0)}";
        _chartHoverBorder.Visibility = Visibility.Visible;
        _chartGuideLine.Visibility = Visibility.Visible;
        _chartGuideLine.X1 = nearest.Point.X;
        _chartGuideLine.X2 = nearest.Point.X;
        _chartGuideLine.Y1 = 12;
        _chartGuideLine.Y2 = Math.Max(12, StatsChartHost.ActualHeight - 24);

        _chartHoverBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = _chartHoverBorder.DesiredSize.Width;
        var left = Math.Min(Math.Max(0, nearest.Point.X + 10), Math.Max(0, StatsChartHost.ActualWidth - width - 4));
        Canvas.SetLeft(_chartHoverBorder, left);
        Canvas.SetTop(_chartHoverBorder, Math.Max(0, nearest.Point.Y - 34));
    }

    private void StatsChartHost_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_chartHoverBorder is not null)
        {
            _chartHoverBorder.Visibility = Visibility.Collapsed;
        }

        if (_chartGuideLine is not null)
        {
            _chartGuideLine.Visibility = Visibility.Collapsed;
        }
    }

    private void StatsChartHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawStatsChart();

    /// <summary>用 Canvas 画每日趋势折线（无第三方图表库）：网格 + 折线 + 数据点 + 日期标签。</summary>
    private void DrawStatsChart()
    {
        if (StatsChartCanvas is null || StatsChartHost is null)
        {
            return;
        }

        StatsChartCanvas.Children.Clear();
        _chartPoints.Clear();
        _chartHoverBorder = null;
        _chartHoverText = null;
        _chartGuideLine = null;

        var width = StatsChartHost.ActualWidth;
        var height = StatsChartHost.ActualHeight;
        var series = BuildChartSeries(_dailyStats);

        StatsChartEmptyText.Visibility = series.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (series.Count == 0 || width < 80 || height < 80)
        {
            return;
        }

        var metric = StatsMetricCombo?.SelectedIndex ?? 0;
        var values = series.Select(item => MetricValue(item, metric)).ToList();

        var max = Math.Max(1.0, values.Max());
        const double left = 10;
        const double right = 10;
        const double top = 12;
        const double bottom = 24;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;

        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var lineBrush = (System.Windows.Media.Brush)FindResource("LineBrush");
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        var soft = (System.Windows.Media.Brush)FindResource("SoftMutedBrush");

        for (var i = 0; i <= 2; i++)
        {
            var y = top + plotHeight * i / 2.0;
            StatsChartCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = left,
                Y1 = y,
                X2 = left + plotWidth,
                Y2 = y,
                Stroke = lineBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });

            var label = new TextBlock
            {
                Text = FormatChartValue(max * (2 - i) / 2.0, metric),
                FontSize = 9.5,
                Foreground = soft,
            };
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y - 12);
            StatsChartCanvas.Children.Add(label);
        }

        var points = new PointCollection();
        for (var i = 0; i < series.Count; i++)
        {
            var x = series.Count == 1 ? left + plotWidth / 2 : left + plotWidth * i / (series.Count - 1);
            var y = top + plotHeight * (1 - values[i] / max);
            points.Add(new System.Windows.Point(x, y));
        }

        StatsChartCanvas.Children.Add(new System.Windows.Shapes.Polyline
        {
            Points = points,
            Stroke = accent,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        for (var i = 0; i < series.Count; i++)
        {
            var point = points[i];
            _chartPoints.Add((point, series[i], values[i]));

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = accent,
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 1,
                ToolTip = $"{series[i].Date:yyyy-MM-dd} · {FormatChartValue(values[i], metric)}",
            };
            Canvas.SetLeft(dot, point.X - 3);
            Canvas.SetTop(dot, point.Y - 3);
            StatsChartCanvas.Children.Add(dot);
        }

        var step = Math.Max(1, (int)Math.Ceiling(series.Count / 6.0));
        for (var i = 0; i < series.Count; i += step)
        {
            var label = new TextBlock
            {
                Text = series[i].Date.ToString("MM-dd"),
                FontSize = 9.5,
                Foreground = muted,
            };
            Canvas.SetLeft(label, Math.Max(0, Math.Min(width - 34, points[i].X - 16)));
            Canvas.SetTop(label, top + plotHeight + 6);
            StatsChartCanvas.Children.Add(label);
        }

        // 悬停辅助：竖向参考线 + 跟随鼠标的数值气泡（MouseMove 里更新）。
        _chartGuideLine = new System.Windows.Shapes.Line
        {
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        StatsChartCanvas.Children.Add(_chartGuideLine);

        _chartHoverText = new TextBlock
        {
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.White,
        };
        _chartHoverBorder = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("InkBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Child = _chartHoverText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        StatsChartCanvas.Children.Add(_chartHoverBorder);
    }

    private static string FormatChartValue(double value, int metric) => metric == 0
        ? "¥" + value.ToString("0.##")
        : value.ToString("0");

    /// <summary>把稀疏的每日数据补成连续日期（中间没有数据的天按 0 画），跨度太大时保持稀疏。</summary>
    private static List<DailyPrintStat> BuildChartSeries(IReadOnlyList<DailyPrintStat> stats)
    {
        if (stats.Count == 0)
        {
            return new List<DailyPrintStat>();
        }

        var ordered = stats.OrderBy(item => item.Date).ToList();
        var first = ordered[0].Date;
        var last = ordered[^1].Date;
        var span = last.DayNumber - first.DayNumber;
        if (span <= 0 || span > 400)
        {
            return ordered;
        }

        var byDate = ordered.ToDictionary(item => item.Date);
        var series = new List<DailyPrintStat>(span + 1);
        for (var i = 0; i <= span; i++)
        {
            var date = DateOnly.FromDayNumber(first.DayNumber + i);
            series.Add(byDate.TryGetValue(date, out var stat)
                ? stat
                : new DailyPrintStat(date, 0, 0, 0, 0, 0, 0));
        }

        return series;
    }

    private static double MetricValue(DailyPrintStat item, int metric) => metric switch
    {
        1 => item.BillCount,
        2 => item.JobCount,
        3 => item.PageCount,
        4 => item.SheetCount,
        _ => item.RevenueCents / 100.0,
    };

    private async void StatsRefresh_Click(object sender, RoutedEventArgs e) => await LoadStatsAsync();

    private void StatsExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出统计",
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                FileName = $"PrintPal-stats-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.AppendLine("日期,星期,营业额(元),账单数,任务数,页数,张数");
            foreach (var row in _dailyStatRows)
            {
                builder.AppendLine(
                    $"{row.DateText},{row.WeekText},{Money.ToYuan(row.Stat.RevenueCents):0.00}," +
                    $"{row.BillCount},{row.JobCount},{row.PageCount},{row.SheetCount}");
            }

            builder.AppendLine();
            builder.AppendLine("维度,取值,任务数,张数,金额(元)");
            foreach (var row in _dimensionStatRows)
            {
                builder.AppendLine(
                    $"{row.DimensionText},{row.KeyText},{row.JobCount},{row.SheetCount}," +
                    $"{Money.ToYuan(row.Stat.AmountCents):0.00}");
            }

            builder.AppendLine();
            builder.AppendLine("时段,任务数,金额(元)");
            foreach (var row in _hourBarRows)
            {
                builder.AppendLine($"{row.HourText}:00,{row.Stat.JobCount},{Money.ToYuan(row.Stat.AmountCents):0.00}");
            }

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            StatusBarText.Text = $"统计已导出：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, "导出失败", ex.Message);
        }
    }

    private void UpdateBillingChrome()    {
        var amount = _allJobs.Sum(row => row.Job.AmountCents);
        var count = _allJobs.Count;
        var amountText = Money.FormatCents(amount);
        var name = _currentBill?.Name ?? "未开单";

        FullBillNameText.Text = name;
        FullAmountText.Text = amountText;
        CashierNavCountText.Text = count.ToString();

        CompactCurrentTagText.Text = name;
        CompactAmountText.Text = amountText;

        MinimalBillNameText.Text = name;
        MinimalAmountText.Text = amountText;

        // 账单切换列表：只要有未结账单就显示（当前账单也在列表里高亮）
        var billListVisible = _openBillChips.Count > 0;
        FullParkedCaptionText.Visibility = billListVisible ? Visibility.Visible : Visibility.Collapsed;
        FullParkedPanel.Visibility = billListVisible ? Visibility.Visible : Visibility.Collapsed;
        CompactParkedPanel.Visibility = billListVisible ? Visibility.Visible : Visibility.Collapsed;
        MinimalParkedPanel.Visibility = billListVisible ? Visibility.Visible : Visibility.Collapsed;

        var hasJobs = count > 0;
        FullParkButton.IsEnabled = hasJobs;
        FullCheckoutButton.IsEnabled = hasJobs;
        CompactParkButton.IsEnabled = hasJobs;
        CompactCheckoutButton.IsEnabled = hasJobs;
        MinimalParkButton.IsEnabled = hasJobs;
        MinimalCheckoutButton.IsEnabled = hasJobs;
    }

    // ------------------------------------------------------------------ history filter / details

    /// <summary>
    /// 账单切换列表每 2 秒刷新一次。列表 = 当前账单 + 挂起账单，按建单顺序稳定排序：
    /// 切换当前账单只是移动高亮，不改变任何一张的位置。
    /// 成员不变时原地更新行（容器不重建，避免溢出区闪现一帧）；
    /// 有增删时才重建，且新容器由 ItemContainerStyle 默认隐藏，等溢出计算后再显示。
    /// </summary>
    private void SyncOpenBillChips()
    {
        var open = _openBills.OrderBy(item => item.SequenceNumber).ToList();

        var sameMembers = open.Count == _openBillChips.Count;
        if (sameMembers)
        {
            for (var i = 0; i < open.Count; i++)
            {
                if (_openBillChips[i].Summary.Id != open[i].Id)
                {
                    sameMembers = false;
                    break;
                }
            }
        }

        if (sameMembers)
        {
            for (var i = 0; i < open.Count; i++)
            {
                _openBillChips[i].UpdateFrom(open[i]);
            }
        }
        else
        {
            _openBillChips.Clear();
            for (var i = 0; i < open.Count; i++)
            {
                _openBillChips.Add(new BillChipRow(open[i], i + 1));
            }

            if (_openBillChips.Count == 0)
            {
                CloseBillPopups();
            }
        }

        UpdateAllStripOverflows();
    }

    // ------------------------------------------------------------------ parked chips overflow (+N)

    /// <summary>一处「未结账单 chip 列表 + 溢出折叠」的元素引用与状态（完整/紧凑/极简各一份）。</summary>
    private sealed class ChipOverflowStrip
    {
        public ChipOverflowStrip(FrameworkElement panel, Grid host, ItemsControl items, Button overflowButton, Popup popup, DataTemplate chipTemplate)
        {
            Panel = panel;
            Host = host;
            Items = items;
            OverflowButton = overflowButton;
            Popup = popup;
            ChipTemplate = chipTemplate;
        }

        public FrameworkElement Panel { get; }
        public Grid Host { get; }
        public ItemsControl Items { get; }
        public Button OverflowButton { get; }
        public Popup Popup { get; }
        public DataTemplate ChipTemplate { get; }

        public int Fit { get; set; }
        public double LastHostWidth { get; set; } = -1;
        public int LastTotal { get; set; } = -1;
    }

    private readonly List<ChipOverflowStrip> _chipStrips = new();

    /// <summary>
    /// 方案 B：chip 一行放满，放不下的折叠进「+N」浮层。
    /// 布局宿主的 LayoutUpdated 里同步计算并一次性应用（无延迟修正、无多轮重排）。
    /// </summary>
    private void UpdateAllStripOverflows()
    {
        foreach (var strip in _chipStrips)
        {
            UpdateStripOverflow(strip);
        }
    }

    private void UpdateStripOverflow(ChipOverflowStrip strip)
    {
        if (strip.Panel.Visibility != Visibility.Visible || strip.Host.ActualWidth <= 0)
        {
            return;
        }

        // chip 宽度用脱离可视树的原型测量：刚重建的容器直接 Measure 拿不到真实宽度。
        // 原型与真实排版存在 ±1px 级误差，由 ClipToBounds 吸收（不再做多轮修正避免闪烁）。
        var total = _openBillChips.Count;
        var widths = new double[total];
        for (var i = 0; i < total; i++)
        {
            widths[i] = MeasureChipWidth(_openBillChips[i], strip.ChipTemplate) + 6; // chip 右侧间距
        }

        double Available(double reserved) => strip.Host.ActualWidth - 20 - reserved; // ItemsControl 左距 10 + 右侧留白 10

        int CountFit(double reserved)
        {
            var used = 0d;
            var count = 0;
            for (var i = 0; i < total; i++)
            {
                if (used + widths[i] > Available(reserved))
                {
                    break;
                }

                used += widths[i];
                count++;
            }

            return count;
        }

        var fit = CountFit(0);
        if (fit < total)
        {
            strip.OverflowButton.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            // chip 自带 6px 右距（已计入 widths），+N 与 chip 的间距 = 该右距，与其他 chip 间一致
            fit = Math.Max(CountFit(strip.OverflowButton.DesiredSize.Width), 1);
        }

        ApplyOverflowFit(strip, fit, total);
        strip.LastHostWidth = strip.Host.ActualWidth;
        strip.LastTotal = total;
    }

    private void ApplyOverflowFit(ChipOverflowStrip strip, int fit, int total)
    {
        strip.Fit = fit;
        for (var i = 0; i < total; i++)
        {
            if (strip.Items.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter container)
            {
                var target = i < fit ? Visibility.Visible : Visibility.Collapsed;
                if (container.Visibility != target)
                {
                    container.Visibility = target;
                }
            }
        }

        var overflow = fit < total;
        var buttonVisibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (strip.OverflowButton.Visibility != buttonVisibility)
        {
            strip.OverflowButton.Visibility = buttonVisibility;
        }

        var content = $"+{total - fit}";
        if (strip.OverflowButton.Content is not string current || current != content)
        {
            strip.OverflowButton.Content = content;
        }
    }

    private void ParkedOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var strip = _chipStrips.FirstOrDefault(item => item.OverflowButton == button);
        if (strip is null)
        {
            return;
        }

        if (!strip.Popup.IsOpen)
        {
            // 浮层右缘对齐账单列表右缘（宽度按 MinWidth 估）
            strip.Popup.HorizontalOffset = Math.Max(0, strip.Panel.ActualWidth - 260);
        }

        strip.Popup.IsOpen = !strip.Popup.IsOpen;
    }

    private void CloseBillPopups()
    {
        foreach (var strip in _chipStrips)
        {
            strip.Popup.IsOpen = false;
        }
    }

    /// <summary>
    /// 布局宿主每次布局后同步重算溢出折叠：宽度或账单数变化时一次性按最终宽度应用，
    /// 无延迟修正、无多轮重排（消除调整窗口时的震动与任务表滚动条闪现）。
    /// 自身可见性变更触发的布局会再次进入这里，宽度未变时直接跳过（幂等收敛）。
    /// </summary>
    private void OnChipHostLayoutUpdated(object? sender, EventArgs e)
    {
        foreach (var strip in _chipStrips)
        {
            if (strip.Panel.Visibility != Visibility.Visible || strip.Host.ActualWidth <= 0)
            {
                continue;
            }

            var width = strip.Host.ActualWidth;
            var total = _openBillChips.Count;
            if (width == strip.LastHostWidth && total == strip.LastTotal)
            {
                continue; // 宽度/数量未变，跳过（自身可见性变更触发的布局会到这里）
            }

            UpdateStripOverflow(strip);
        }
    }

    /// <summary>用 chip 模板实例化一个离屏原型来测量宽度，不受当前布局状态影响。</summary>
    private double MeasureChipWidth(BillChipRow row, DataTemplate chipTemplate)
    {
        var proxy = new ContentPresenter
        {
            Content = row,
            ContentTemplate = chipTemplate,
        };
        proxy.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        return proxy.DesiredSize.Width;
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
        HistoryDetailsSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        HistoryDetailsSpacerColumn.Width = new GridLength(visible ? 14 : 0);
        HistoryDetailsColumn.MinWidth = visible ? HistoryDetailsWidthMin : 0;
        HistoryDetailsColumn.MaxWidth = visible ? HistoryDetailsWidthMax : 0;
        // 用像素宽度而不是星号：拖拽后的宽度在切换账单/刷新时保持不变（持久化到 Settings.historyDetailsWidth）。
        // 只在「显示/隐藏切换」时设置列宽，避免刷新/拖拽把宽度弹回。
        if (!visible)
        {
            if (_historyDetailsShown)
            {
                HistoryDetailsColumn.Width = new GridLength(0);
                _historyDetailsShown = false;
            }

            return;
        }

        if (!_historyDetailsShown)
        {
            HistoryDetailsColumn.Width = new GridLength(_historyDetailsWidth);
            _historyDetailsShown = true;
        }

        if (!visible)
        {
            HistorySummaryText.Text = "选择左侧账单查看其中的打印记录";
        }
    }

    private void HistoryDetailsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var width = HistoryDetailsColumn.ActualWidth;
        if (width >= HistoryDetailsWidthMin && width <= HistoryDetailsWidthMax)
        {
            _historyDetailsWidth = width;
        }
    }

    private void HistoryDetailsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = HistoryDetailsColumn.ActualWidth;
        if (width < HistoryDetailsWidthMin || width > HistoryDetailsWidthMax)
        {
            return;
        }

        _historyDetailsWidth = Math.Clamp(width, HistoryDetailsWidthMin, HistoryDetailsWidthMax);
        _ = SaveSettingAsync("historyDetailsWidth", _historyDetailsWidth.ToString("0"));
    }

    private async Task LoadHistoryJobsAsync(ClosedBillRow row)
    {
        var keepOffset = _historyBillId == row.Bill.Id;
        var scroll = GetScrollViewer(HistoryCashierGrid);
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
                new Action(() => RestoreScrollOffset(HistoryCashierGrid, offset, hOffset)),
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
                CashierGrid.SelectedItem = selected;
                CompactCashierGrid.SelectedItem = selected;
                if (scrollToSelection)
                {
                    CashierGrid.ScrollIntoView(selected);
                }
            }
        }

        if (_jobRows.Count == 0)
        {
            PrintDetailsContent.DataContext = null;
        }

        UpdatePrintDetailsPanel();
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

    private void CashierGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PrintDetailsContent.DataContext = CashierGrid.SelectedItem as JobRow;
        UpdateDetailsFlowNo();
        UpdatePrintDetailsPanel();
    }

    private void UpdateDetailsFlowNo()
    {
        if (DetailsFlowNoRow is null)
        {
            return;
        }

        var flowNo = (CashierGrid.SelectedItem as JobRow)?.FlowNo ?? "";
        DetailsFlowNoText.Text = flowNo;
        DetailsFlowNoRow.Visibility = string.IsNullOrEmpty(flowNo) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateBillFlowNoButton()
    {
        if (CopyBillFlowNoButton is null)
        {
            return;
        }

        CopyBillFlowNoButton.Visibility = string.IsNullOrEmpty(_currentBillFlowNo)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CopyDetailsFlowNo_Click(object sender, RoutedEventArgs e)
    {
        var flowNo = (CashierGrid.SelectedItem as JobRow)?.FlowNo;
        if (string.IsNullOrEmpty(flowNo))
        {
            return;
        }

        try
        {
            Clipboard.SetText(flowNo);
            StatusBarText.Text = $"已复制流水号 {flowNo}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"copy job flow no failed: {ex.Message}");
        }
    }

    private void CopyBillFlowNo_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentBillFlowNo))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_currentBillFlowNo);
            StatusBarText.Text = $"已复制账单流水号 {_currentBillFlowNo}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"copy bill flow no failed: {ex.Message}");
        }
    }

    private void CopyPrintDetails_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = (CashierGrid.SelectedItem as JobRow)?.DetailsText;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Copy details failed: {ex.Message}");
        }
    }

    private void UpdatePrintDetailsPanel()
    {
        var visible = CashierGrid.SelectedItem is JobRow && !_detailsClosed;

        PrintDetailsCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PrintDetailsSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PrintDetailsSpacerColumn.Width = new GridLength(visible ? 14 : 0);
        PrintDetailsColumn.MinWidth = visible ? DetailsWidthMin : 0;
        PrintDetailsColumn.MaxWidth = visible ? DetailsWidthMax : 0;
        // 用像素宽度而不是星号：拖拽后的宽度在切换任务/刷新时保持不变（持久化到 Settings.detailsWidth）。
        // 只在「显示/隐藏切换」时设置列宽：1 秒自动刷新不再触碰宽度，
        // 避免拖拽中或刚拖完就被弹回默认值。
        if (!visible)
        {
            if (_printDetailsShown)
            {
                PrintDetailsColumn.Width = new GridLength(0);
                _printDetailsShown = false;
            }

            return;
        }

        if (!_printDetailsShown)
        {
            PrintDetailsColumn.Width = new GridLength(_detailsWidth);
            _printDetailsShown = true;
        }
    }

    private void PrintDetailsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var width = PrintDetailsColumn.ActualWidth;
        if (width >= DetailsWidthMin && width <= DetailsWidthMax)
        {
            _detailsWidth = width;
        }
    }

    private void PrintDetailsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = PrintDetailsColumn.ActualWidth;
        if (width < DetailsWidthMin || width > DetailsWidthMax)
        {
            return;
        }

        _detailsWidth = Math.Clamp(width, DetailsWidthMin, DetailsWidthMax);
        _ = SaveSettingAsync("detailsWidth", _detailsWidth.ToString("0"));
    }

    private void CopyDetailLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DetailLine line } || string.IsNullOrEmpty(line.CopyValue))
        {
            return;
        }

        try
        {
            Clipboard.SetText(line.CopyValue);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Copy detail line failed: {ex.Message}");
        }
    }

    private void ClosePrintDetails_Click(object sender, RoutedEventArgs e)
    {
        _detailsClosed = true;
        UpdatePrintDetailsPanel();
    }

    /// <summary>当前生效的任务列表：紧凑模式用紧凑列表，其余用完整列表。</summary>
    private DataGrid ActiveCashierGrid => _mode == UiMode.Compact ? CompactCashierGrid : CashierGrid;

    private JobRow? SelectedJobRow => ActiveCashierGrid.SelectedItem as JobRow;

    /// <summary>右键任务行先选中该行，再弹出与打印详情一致的操作菜单。</summary>
    private void JobGrid_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid
            && ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is DataGridRow row
            && !row.IsSelected)
        {
            grid.SelectedItem = row.Item;
        }
    }

    // 点任务行把关闭的打印详情面板带回来；点列表空白处（含表头排序）自动关闭打印详情面板。
    private void CashierGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var onRow = false;
        for (var d = source; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridRow)
            {
                onRow = true;
                break;
            }

            if (d is DataGridColumnHeader)
            {
                return; // 表头点击是排序操作，不改变打印详情面板状态
            }
        }

        if (onRow)
        {
            if (_detailsClosed)
            {
                _detailsClosed = false;
                UpdatePrintDetailsPanel();
            }
        }
        else if (!_detailsClosed)
        {
            _detailsClosed = true;
            UpdatePrintDetailsPanel();
        }

        if (!onRow)
        {
            // 点空白处同时让搜索框失焦，占位文字重现、光标消失
            System.Windows.Input.Keyboard.ClearFocus();
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

    /// <summary>点击账单 chip：切换当前显示的账单（旧账单自动挂起，列表位置不变）。</summary>
    private async void SwitchBill_Click(object sender, RoutedEventArgs e)
    {

        if (sender is not Button { DataContext: BillChipRow chip })
        {
            return;
        }

        CloseBillPopups();

        if (chip.IsCurrent)
        {
            return;
        }

        try
        {
            var bill = await _agent.Database.ActivateBillAsync(chip.Id);
            await ReloadAsync();
            StatusBarText.Text = $"已切换到「{bill.Name}」";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"切换失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BillName_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 双击改名：阻止冒泡到窗口拖拽，避免改名时窗口跟着动。
            e.Handled = true;
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
        if (SelectedJobRow is not JobRow row)
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

    // 点账单行打开/恢复明细面板；点列表空白处（含表头排序）自动关闭明细面板。
    private void HistoryGrid_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var onRow = false;
        for (var d = source; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridRow)
            {
                onRow = true;
                break;
            }

            if (d is DataGridColumnHeader)
            {
                return; // 表头点击是排序操作，不改变明细面板状态
            }
        }

        if (onRow)
        {
            if (_historyDetailsClosed)
            {
                _historyDetailsClosed = false;
                UpdateHistoryDetailsPanel();

                if (HistoryGrid.SelectedItem is ClosedBillRow row)
                {
                    _ = LoadHistoryJobsAsync(row);
                }
            }
        }
        else if (!_historyDetailsClosed)
        {
            _historyDetailsClosed = true;
            UpdateHistoryDetailsPanel();
        }

        if (!onRow)
        {
            // 点空白处同时让历史搜索框失焦，占位文字重现、光标消失
            System.Windows.Input.Keyboard.ClearFocus();
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

    private async void CompactMode_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != UiMode.Compact)
        {
            await SetModeAsync(UiMode.Compact);
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
        SetModeBar(FullModeFullButton, FullModeCompactButton, FullModeMinimalButton);
        SetModeBar(CompactModeFullButton, CompactModeCompactButton, CompactModeMinimalButton);
        SetMinimalModeBar(MinimalModeFullButton, MinimalModeCompactButton, MinimalModeMinimalButton);
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

    // 极简模式的模式选择器：与完整/紧凑模式同一套思路——激活项用主题强调色，
    // 非激活项透明（露出外层 MinimalChipBrush 容器），颜色全部来自主题 token，
    // 不再硬编码深蓝/深灰，浅色主题下也不会出现突兀的深色按钮。
    private void SetMinimalModeBar(params Button[] buttons)
    {
        var activeBackground = (System.Windows.Media.Brush)FindResource("MinimalChipActiveBrush");
        var inactiveForeground = (System.Windows.Media.Brush)FindResource("MinimalMutedBrush");
        foreach (var button in buttons)
        {
            var active = ModeFromButton(button) == _mode;
            button.Background = active ? activeBackground : System.Windows.Media.Brushes.Transparent;
            button.Foreground = active ? System.Windows.Media.Brushes.White : inactiveForeground;
        }
    }

    private static UiMode ModeFromButton(Button button) => button.Tag switch
    {
        "Compact" => UiMode.Compact,
        "Minimal" => UiMode.Minimal,
        _ => UiMode.Full,
    };

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void MinimalHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 双击/快速点击时鼠标可能已抬起，忽略即可。
        }
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
        // 先保存当前模式的位置/大小（必须在 _applyingMode 置位之前，否则保存会被跳过）
        SaveModeBounds();

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
            _mode = mode;
            UpdateModeSwitchButtons();
            FullLayout.Visibility = mode == UiMode.Full ? Visibility.Visible : Visibility.Collapsed;
            CompactLayout.Visibility = mode == UiMode.Compact ? Visibility.Visible : Visibility.Collapsed;
            MinimalLayout.Visibility = mode == UiMode.Minimal ? Visibility.Visible : Visibility.Collapsed;

            if (mode != UiMode.Minimal)
            {
                HideMinimalPrintNotice();
            }

            if (mode == UiMode.Minimal)
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = false;
                Background = (System.Windows.Media.Brush)FindResource("MinimalBackgroundBrush");
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
                MinWidth = mode == UiMode.Full ? 1200 : 700;
                MinHeight = mode == UiMode.Full ? 620 : 420;
                ConfigureWindowChrome(mode == UiMode.Full ? 78 : 58, 6);

                var prefix = mode == UiMode.Full ? "full" : "compact";
                var savedBounds = ReadSavedBounds(prefix);
                if (savedBounds is not null)
                {
                    // 恢复该模式上次的位置与大小（侧车文件，跨重启持久）
                    Width = savedBounds.Width;
                    Height = savedBounds.Height;
                    Left = savedBounds.Left;
                    Top = savedBounds.Top;
                    _restoreModePosition = true;
                }
                else
                {
                    // 没有记录（首次使用）→ 默认尺寸
                    Width = mode == UiMode.Full ? 1440 : 776;
                    Height = mode == UiMode.Full ? 900 : 436;
                }
            }

            var workArea = SystemParameters.WorkArea;
            var maxWidth = Math.Max(MinWidth, workArea.Width - 24);
            var maxHeight = Math.Max(MinHeight, workArea.Height - 24);
            Width = Math.Min(Math.Max(Width, MinWidth), maxWidth);
            Height = Math.Min(Math.Max(Height, MinHeight), maxHeight);
            if (_restoreModePosition)
            {
                // 兜底：窗口至少保留在虚拟屏幕内（显示器变更/分辨率调整时防止整窗消失）
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
                Left = Math.Min(Math.Max(Left, vsLeft), Math.Max(vsLeft, vsRight - Width));
                Top = Math.Min(Math.Max(Top, vsTop), Math.Max(vsTop, vsBottom - Height));
            }
            else if (mode != UiMode.Minimal)
            {
                // 切换到完整/紧凑模式时居中于主屏工作区；极简模式保持右下角记忆位置。
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }
            _restoreModePosition = false;
            UpdateLayout();

            // 兜底：从极简模式（316×186）切回完整/紧凑模式，或从侧车恢复尺寸时，
            // 窗口内容可能仍停留在旧尺寸，右侧/底部露出未绘制区域（表现为大黑边）。
            // 在 Loaded 优先级再强制重排一次，确保根 Grid 撑满窗口。
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                WindowRoot.InvalidateMeasure();
                WindowRoot.InvalidateArrange();
                UpdateLayout();
            }), DispatcherPriority.Loaded);

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

    /// <summary>窗口移动/缩放后防抖保存边界（500ms 静止后落库），任意时刻退出都能恢复最新位置。</summary>
    private void ScheduleBoundsSave()
    {
        if (!_initialized)
        {
            return;
        }

        _boundsSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _boundsSaveTimer.Stop();
        _boundsSaveTimer.Tick += BoundsSaveTimer_Tick;
        _boundsSaveTimer.Start();
    }

    private void BoundsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _boundsSaveTimer?.Stop();
        SaveModeBounds();
    }

    /// <summary>窗口边界侧车文件：同步写盘，避开数据库 WAL/异步写入的时序问题。</summary>
    private static string BoundsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintPal", "window-bounds.json");

    private WindowBounds? ReadSavedBounds(string prefix)
    {
        try
        {
            var path = BoundsFilePath;
            if (!File.Exists(path))
            {
                return null;
            }

            var node = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            if (node.TryGetProperty(prefix, out var entry) &&
                entry.TryGetProperty("left", out var left) &&
                entry.TryGetProperty("top", out var top) &&
                entry.TryGetProperty("width", out var width) &&
                entry.TryGetProperty("height", out var height))
            {
                return new WindowBounds(left.GetDouble(), top.GetDouble(), width.GetDouble(), height.GetDouble());
            }
        }
        catch
        {
            // 侧车文件损坏时忽略，走默认尺寸/居中
        }

        return null;
    }

    private void WriteSavedBounds(string prefix, double left, double top, double width, double height)
    {
        try
        {
            Dictionary<string, Dictionary<string, double>>? root = null;
            if (File.Exists(BoundsFilePath))
            {
                try
                {
                    root = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(BoundsFilePath));
                }
                catch
                {
                    root = null;
                }
            }

            root ??= new Dictionary<string, Dictionary<string, double>>();
            root[prefix] = new Dictionary<string, double>
            {
                ["left"] = left,
                ["top"] = top,
                ["width"] = width,
                ["height"] = height,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(BoundsFilePath)!);
            File.WriteAllText(BoundsFilePath, System.Text.Json.JsonSerializer.Serialize(root, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 侧车写入失败不影响主流程（数据库路径仍在）
        }
    }

    private sealed record WindowBounds(double Left, double Top, double Width, double Height);

    /// <summary>在 UI 线程捕获当前模式的窗口边界（最大化时取还原边界；返回 null 表示无需保存）。</summary>
    private (UiMode Mode, double L, double T, double W, double H)? CaptureModeBounds()
    {
        if (!_initialized || _applyingMode)
        {
            return null;
        }

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

        if (double.IsNaN(left) || double.IsNaN(top) || double.IsNaN(width) || double.IsNaN(height) ||
            width <= 0 || height <= 0)
        {
            return null;
        }

        return (_mode, left, top, width, height);
    }

    /// <summary>把边界写入侧车文件与数据库（后台线程执行，避免锁竞争阻塞 UI）。</summary>
    private void PersistModeBounds((UiMode Mode, double L, double T, double W, double H) bounds)
    {
        var prefix = bounds.Mode switch
        {
            UiMode.Full => "full",
            UiMode.Compact => "compact",
            _ => "minimal",
        };
        WriteSavedBounds(prefix, bounds.L, bounds.T, bounds.W, bounds.H);

        // 兼容：极简位置仍写数据库（RestoreMinimalPositionAsync 的回退源）
        if (bounds.Mode == UiMode.Minimal)
        {
            _ = SaveSettingAsync("minimalLeft", bounds.L.ToString("0"));
            _ = SaveSettingAsync("minimalTop", bounds.T.ToString("0"));
        }
    }

    private void SaveModeBounds()
    {
        if (CaptureModeBounds() is not { } bounds)
        {
            return;
        }

        // 边界落盘放到后台线程：数据库/文件 I/O 不阻塞 UI 线程
        _ = Task.Run(() => PersistModeBounds(bounds));
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
        // 优先读侧车文件（含位置与大小）；回退到数据库的 minimalLeft/Top
        var saved = ReadSavedBounds("minimal");
        if (saved is not null)
        {
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            ClampToWorkArea();
            return;
        }

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

    // ------------------------------------------------------------------ theme

    private void BuildThemeButtons()
    {
        if (ThemeButtonsPanel is null || DarkThemeButtonsPanel is null)
        {
            return;
        }

        ThemeButtonsPanel.Children.Clear();
        DarkThemeButtonsPanel.Children.Clear();
        _themeButtons.Clear();

        foreach (var theme in ThemeManager.Themes)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(theme.Colors["AccentBrush"]),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = theme.Name,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(label);

            var button = new Button
            {
                Content = content,
                Tag = theme.Key,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 6, 12, 6),
                ToolTip = $"切换到「{theme.Name}」主题",
            };
            button.Click += Theme_Click;
            (theme.IsDark ? DarkThemeButtonsPanel : ThemeButtonsPanel).Children.Add(button);
            _themeButtons.Add(button);
        }

        UpdateThemeButtons();
    }

    private void UpdateThemeButtons()
    {
        foreach (var button in _themeButtons)
        {
            var selected = string.Equals(button.Tag as string, _themeKey, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = (System.Windows.Media.Brush)FindResource(selected ? "AccentBrush" : "LineStrongBrush");
            button.BorderThickness = new Thickness(selected ? 1.5 : 1);
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private async void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
        {
            return;
        }

        _themeKey = key;
        ThemeManager.Apply(ThemeManager.Find(key));
        UpdateThemeButtons();
        UpdateModeSwitchButtons();

        // 完整模式窗口背景用的是 SidebarBrush，主题切换后同步刷新
        if (_mode == UiMode.Full)
        {
            Background = (System.Windows.Media.Brush)FindResource("SidebarBrush");
        }
        else if (_mode == UiMode.Minimal)
        {
            Background = (System.Windows.Media.Brush)FindResource("MinimalBackgroundBrush");
        }

        await SaveSettingAsync("uiTheme", key);
    }

    // ------------------------------------------------------------------ minimal print notice

    private void DetectNewJobsForMinimalNotice()
    {
        if (_allJobs.Count == 0)
        {
            return;
        }

        var newest = _allJobs.Max(row => row.Job.CreatedAtUtc);
        if (_lastSeenJobCreatedAt == DateTimeOffset.MinValue)
        {
            _lastSeenJobCreatedAt = newest;
            return;
        }

        var newJobs = _allJobs
            .Where(row => row.Job.CreatedAtUtc > _lastSeenJobCreatedAt)
            .OrderBy(row => row.Job.CreatedAtUtc)
            .ToList();

        _lastSeenJobCreatedAt = newest;

        if (_mode == UiMode.Minimal && newJobs.Count > 0)
        {
            ShowMinimalPrintNotice(newJobs);
        }
    }

    private void ShowMinimalPrintNotice(IReadOnlyList<JobRow> jobs)
    {
        if (MinimalPrintNotice is null)
        {
            return;
        }

        var parts = jobs.Take(3).Select(row =>
            $"{row.DocumentName} → {row.TargetText} · {row.AmountText} · {row.StatusText}");
        var text = string.Join("     ｜     ", parts);
        if (jobs.Count > 3)
        {
            text += $"     ｜     等 {jobs.Count} 个文件";
        }

        MinimalPrintNoticeText.Text = text;
        MinimalPrintNotice.Visibility = Visibility.Visible;

        // 极简窗口默认 186 高，通知条会占 42px（34 高 + 8 间距）；临时加高，隐藏时恢复
        if (_minimalHeightBeforeNotice is null)
        {
            _minimalHeightBeforeNotice = Height;
            Height += 42;
        }

        StartMinimalNoticeMarquee();

        _minimalNoticeTimer?.Stop();
        _minimalNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _minimalNoticeTimer.Tick += (_, _) => HideMinimalPrintNotice();
        _minimalNoticeTimer.Start();
    }

    private void StartMinimalNoticeMarquee()
    {
        _minimalNoticeStoryboard?.Stop();
        _minimalNoticeStoryboard = null;

        // 等布局完成后再量文本宽度，否则首帧宽度为 0，滚动距离算错。
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MinimalPrintNotice.Visibility != Visibility.Visible)
            {
                return;
            }

            MinimalPrintNoticeTransform.BeginAnimation(TranslateTransform.XProperty, null);

            // StackPanel 给 TextBlock 无限宽度，Measure(infinity) 得到的就是文本自然宽度；
            // 不能用 ActualWidth/DesiredSize 在 Grid 直接测量时的值（会被可视区宽度裁切）。
            MinimalPrintNoticeText.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var textWidth = MinimalPrintNoticeText.DesiredSize.Width;
            var hostWidth = MinimalPrintNoticeViewport.ActualWidth;
            if (hostWidth <= 0)
            {
                hostWidth = Math.Max(120, Width - 80);
            }

            MinimalPrintNoticeTransform.X = 0;
            if (textWidth <= hostWidth)
            {
                return;
            }

            // 来回反复滚动：0 → -(textWidth - hostWidth) → 0，循环往复。
            // 终点正好让最后一个字贴住可视区右边缘，不会滚过头。
            var distance = textWidth - hostWidth;
            var animation = new DoubleAnimation
            {
                From = 0,
                To = -distance,
                Duration = TimeSpan.FromSeconds(Math.Max(3, distance / 45)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            MinimalPrintNoticeTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }), DispatcherPriority.Loaded);
    }

    private void HideMinimalPrintNotice()
    {
        _minimalNoticeTimer?.Stop();
        _minimalNoticeTimer = null;
        _minimalNoticeStoryboard?.Stop();
        _minimalNoticeStoryboard = null;
        MinimalPrintNoticeTransform.BeginAnimation(TranslateTransform.XProperty, null);

        if (MinimalPrintNotice is not null)
        {
            MinimalPrintNotice.Visibility = Visibility.Collapsed;
        }

        if (_minimalHeightBeforeNotice is double height)
        {
            Height = height;
            _minimalHeightBeforeNotice = null;
        }
    }

    private void UpdateTopmostButtons()
    {
        const string pinGlyph = "\uE718";
        const string pinnedGlyph = "\uE840";

        foreach (var button in new[] { FullPinButton, CompactPinButton, MinimalPinButton })
        {
            button.Content = Topmost ? pinnedGlyph : pinGlyph;
            button.ToolTip = Topmost ? "取消置顶" : "置顶";
        }
    }

    private static string TopmostKey(UiMode mode) => mode switch
    {
        UiMode.Full => "topmost.full",
        UiMode.Compact => "topmost.compact",
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
        if (SelectedJobRow is not JobRow row)
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
        if (SelectedJobRow is not JobRow row)
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
        if (SelectedJobRow is not JobRow row || string.IsNullOrWhiteSpace(row.Job.DocumentPath))
        {
            MessageBox.Show("该任务没有文档路径。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!File.Exists(row.Job.DocumentPath))
        {
            MessageBox.Show("文档已不存在。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 用系统默认程序直接打开文档
        Process.Start(new ProcessStartInfo(row.Job.DocumentPath)
        {
            UseShellExecute = true,
        });
    }

    private void OpenDocFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJobRow is not JobRow row || string.IsNullOrWhiteSpace(row.Job.DocumentPath))
        {
            MessageBox.Show("该任务没有文档路径。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folder = Path.GetDirectoryName(row.Job.DocumentPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show("文档所在目录已不存在。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (File.Exists(row.Job.DocumentPath))
        {
            // 打开所在目录并定位选中该文件，方便确认是哪一份
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Job.DocumentPath}\"")
            {
                UseShellExecute = true,
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo(folder)
            {
                UseShellExecute = true,
            });
        }
    }

    private async void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJobRow is not JobRow row)
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

    // 双击打印机行直接进入编辑；表头/空白处双击不动作。
    private void PrintersGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(PrintersGrid, source) is DataGridRow)
        {
            EditPrinter_Click(sender, e);
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

    // 双击路由规则行直接进入编辑；表头/空白处双击不动作。
    private void RoutingGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(RoutingGrid, source) is DataGridRow)
        {
            EditRouting_Click(sender, e);
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
            await SavePriceRowsAsync();
            await ReloadAsync();
            MessageBox.Show("价格已保存。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存价格失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>把表格里的行内修改落库（提交正在编辑的单元格、逐条写回规则）。</summary>
    private async Task SavePriceRowsAsync()
    {
        PricesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        foreach (var row in _priceRows)
        {
            row.ApplyToRule();
            await _agent.Database.SavePriceRuleAsync(row.Rule);
        }

        _pricesDirty = false;
    }

    /// <summary>双击价格列保持行内编辑；双击其他列打开价格编辑窗口。</summary>
    private async void PricesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        DataGridCell? cell = null;
        for (var d = source; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridCell hit)
            {
                cell = hit;
                break;
            }

            if (d is DataGridColumnHeader)
            {
                return; // 表头双击不动作
            }
        }

        if (cell is null)
        {
            return; // 行间空白
        }

        if (cell.Column.Header is string header && (header.Contains("单面价") || header.Contains("双面价")))
        {
            return; // 价格列保留双击行内编辑
        }

        if (PricesGrid.SelectedItem is not PriceRow priceRow)
        {
            return;
        }

        try
        {
            await SavePriceRowsAsync();

            var dialog = new PriceEditWindow(priceRow.Rule) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                await _agent.Database.SavePriceRuleAsync(dialog.Rule);
                await ReloadAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开价格编辑失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void CheckQueue_Click(object sender, RoutedEventArgs e) => _ = CheckQueueStatusAsync();

    private bool _checkingQueue;

    /// <summary>点击「检查队列」：先展示「检查中」过渡状态，再在后台查询本地打印队列。</summary>
    private async Task CheckQueueStatusAsync()
    {
        if (_checkingQueue)
        {
            return;
        }

        _checkingQueue = true;
        CheckQueueButton.IsEnabled = false;
        QueueStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        QueueStatusText.Text = "队列状态：检查中 ...";

        try
        {
            // 让「检查中」先渲染出来，再做可能较慢的本地队列查询
            await Task.Delay(150);

            var result = await Task.Run(FindQueueStatus);
            QueueStatusText.Foreground = (System.Windows.Media.Brush)FindResource(result.success ? "SuccessBrush" : "WarningBrush");
            QueueStatusText.Text = result.text;
        }
        finally
        {
            _checkingQueue = false;
            CheckQueueButton.IsEnabled = true;
        }
    }

    /// <summary>查询本地打印队列状态（可在后台线程调用），返回展示文案与是否正常。</summary>
    private static (string text, bool success) FindQueueStatus()
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(QueueName);

            if (queue.IsOffline)
            {
                return ("队列状态：已安装，但当前离线（确认 PrintPal 正在运行）", false);
            }

            var status = queue.QueueStatus == PrintQueueStatus.None ? "正常" : queue.QueueStatus.ToString();
            return ($"队列状态：已安装（{status}）", true);
        }
        catch (Exception ex)
        {
            return ($"队列状态：未安装或不可用（{ex.GetType().Name}）— 请运行安装脚本", false);
        }
    }

    private void UpdateQueueStatus()
    {
        var (text, success) = FindQueueStatus();
        QueueStatusText.Foreground = (System.Windows.Media.Brush)FindResource(success ? "SuccessBrush" : "WarningBrush");
        QueueStatusText.Text = text;
    }

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



