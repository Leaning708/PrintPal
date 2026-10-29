using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    private readonly ObservableCollection<BillingTagRow> _tagRows = new();
    private readonly ObservableCollection<BillingSessionRow> _sessionRows = new();
    private readonly ObservableCollection<JobRow> _historyJobRows = new();
    private readonly List<JobRow> _allJobs = new();
    private readonly DispatcherTimer _timer;

    private List<Printer> _printers = new();
    private List<RoutingRule> _routingRules = new();
    private List<PriceRule> _priceRules = new();
    private List<BillingSession> _closedSessions = new();
    private BillingTag? _currentTag;
    private BillingSession? _currentSession;

    private WinForms.NotifyIcon? _trayIcon;
    private bool _initialized;
    private bool _reloading;
    private bool _balloonShown;
    private bool _suppressAutostart;
    private bool _pricesDirty;
    private bool _syncingTags;
    private bool _applyingMode;
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
        HistoryGrid.ItemsSource = _sessionRows;
        HistoryJobsGrid.ItemsSource = _historyJobRows;
        FullTagTabs.ItemsSource = _tagRows;
        SimpleTagTabs.ItemsSource = _tagRows;

        IppUriText.Text = agent.IppUri;
        DataDirText.Text = agent.Options.DataDirectory;
        AgentStatusText.Text = $"IPP 服务：{agent.IppUri}";
        AutoForwardCheck.IsChecked = agent.Options.AutoForward;

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.1";
        VersionText.Text = $"PrintPal {version}";

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
        };
        LocationChanged += (_, _) => SaveModePosition();
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
                await SetModeAsync(mode, persist: false);
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

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 PrintPal", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("完整模式", null, (_, _) => Dispatcher.Invoke(async () => await SetModeAsync(UiMode.Full)));
        menu.Items.Add("简洁模式", null, (_, _) => Dispatcher.Invoke(async () => await SetModeAsync(UiMode.Simple)));
        menu.Items.Add("极简模式", null, (_, _) => Dispatcher.Invoke(async () => await SetModeAsync(UiMode.Minimal)));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) => OpenFolder(_agent.Options.DataDirectory));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => ((App)Application.Current).ExitApplication()));

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
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
            var billing = await _agent.Database.EnsureActiveBillingContextAsync();
            _currentTag = billing.Tag;
            _currentSession = billing.Session;

            var tagsTask = _agent.Database.GetBillingTagsAsync();
            var jobsTask = _agent.Database.GetSessionJobsAsync(_currentSession.Id);
            var printersTask = _agent.Database.GetPrintersAsync();
            var routingTask = _agent.Database.GetRoutingRulesAsync();
            var pricesTask = _agent.Database.GetPriceRulesAsync();
            var sessionsTask = _agent.Database.GetClosedSessionsAsync(null, null, null, 200);

            await Task.WhenAll(tagsTask, jobsTask, printersTask, routingTask, pricesTask, sessionsTask);

            var tags = await tagsTask;
            var jobs = await jobsTask;
            _printers = (await printersTask).ToList();
            _routingRules = (await routingTask).ToList();
            _priceRules = (await pricesTask).ToList();
            _closedSessions = (await sessionsTask).ToList();

            _tagRows.Clear();
            foreach (var tag in tags)
            {
                _tagRows.Add(new BillingTagRow(tag));
            }

            var printerNames = _printers.ToDictionary(printer => printer.Id, printer => printer.Name);
            _allJobs.Clear();
            _allJobs.AddRange(jobs.Select(job => new JobRow(job, printerNames)));

            _printerRows.Clear();
            foreach (var printer in _printers)
            {
                _printerRows.Add(new PrinterRow(printer));
            }

            _routingRows.Clear();
            foreach (var rule in _routingRules.OrderBy(rule => rule.Priority))
            {
                _routingRows.Add(new RoutingRow(rule, printerNames));
            }

            _priceRows.Clear();
            if (!_pricesDirty)
            {
                foreach (var rule in _priceRules)
                {
                    _priceRows.Add(new PriceRow(rule));
                }
            }

            _sessionRows.Clear();
            foreach (var session in _closedSessions)
            {
                _sessionRows.Add(new BillingSessionRow(session));
            }

            ApplyFilters(selectJobId: selectedJobId);
            SyncTagSelection();
            UpdateBillingChrome();

            StatusBarText.Text = $"最后刷新 {DateTime.Now:HH:mm:ss} · 当前标签 {jobs.Count} 条记录";
            SimpleStatusText.Text = StatusBarText.Text;
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
        if (_currentTag is null)
        {
            return;
        }

        var amount = _allJobs.Sum(row => row.Job.AmountCents);
        var count = _allJobs.Count;
        var amountText = Money.FormatCents(amount);

        FullCurrentTagText.Text = $"当前标签：{_currentTag.Name}";
        FullAmountText.Text = amountText;
        SimpleCurrentTagText.Text = $"当前标签：{_currentTag.Name}";
        SimpleAmountText.Text = amountText;
        SimpleCountText.Text = $"本次 {count} 单";
        MinimalTagNameText.Text = _currentTag.Name;
        MinimalAmountText.Text = amountText;
        MinimalCountText.Text = $"{count} 单";
    }

    private void SyncTagSelection()
    {
        if (_currentTag is null)
        {
            return;
        }

        var row = _tagRows.FirstOrDefault(item => item.Tag.Id == _currentTag.Id);
        if (row is null)
        {
            return;
        }

        _syncingTags = true;
        try
        {
            FullTagTabs.SelectedItem = row;
            SimpleTagTabs.SelectedItem = row;
        }
        finally
        {
            _syncingTags = false;
        }
    }

    private void ApplyFilters(Guid? selectJobId = null)
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

        FilterSummaryText.Text = $"{list.Count} 条";

        if (selectJobId is Guid id)
        {
            var selected = _jobRows.FirstOrDefault(row => row.Job.Id == id);
            if (selected is not null)
            {
                JobsGrid.SelectedItem = selected;
                JobsGrid.ScrollIntoView(selected);
            }
        }

        if (JobsGrid.SelectedItem is null && _jobRows.Count > 0)
        {
            JobsGrid.SelectedIndex = 0;
        }

        if (_jobRows.Count == 0)
        {
            JobDetailsBox.Text = "";
        }
    }

    private void Filters_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            ApplyFilters();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
        {
            ApplyFilters();
        }
    }

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        JobDetailsBox.Text = (JobsGrid.SelectedItem as JobRow)?.DetailsText ?? "";
    }

    private async void TagTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTags || !_initialized || sender is not ListBox list)
        {
            return;
        }

        if (list.SelectedItem is BillingTagRow row)
        {
            await SelectTagAsync(row);
        }
    }


    private async Task SelectTagAsync(BillingTagRow row)
    {
        if (_currentTag?.Id == row.Tag.Id)
        {
            return;
        }

        try
        {
            var session = await _agent.Database.OpenSessionAsync(row.Tag.Id);
            await SaveSettingAsync("activeBillingTagId", row.Tag.Id.ToString("D"));
            await SaveSettingAsync("activeBillingSessionId", session.Id.ToString("D"));
            _currentTag = row.Tag;
            _currentSession = session;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"切换标签失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddTag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tags = await _agent.Database.GetBillingTagsAsync(includeArchived: true);
            var next = tags.Count == 0 ? 1 : tags.Max(tag => tag.SequenceNumber) + 1;
            var tag = new BillingTag
            {
                SequenceNumber = next,
                Name = $"标签{next}",
            };
            await _agent.Database.SaveBillingTagAsync(tag);
            var session = await _agent.Database.OpenSessionAsync(tag.Id);
            await SaveSettingAsync("activeBillingTagId", tag.Id.ToString("D"));
            await SaveSettingAsync("activeBillingSessionId", session.Id.ToString("D"));
            _currentTag = tag;
            _currentSession = session;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"新建标签失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RenameTag_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTag is null)
        {
            return;
        }

        var dialog = new BillingTagEditWindow(_currentTag) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _agent.Database.SaveBillingTagAsync(dialog.EditedTag);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存标签失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTag is null || _currentSession is null)
        {
            return;
        }

        if (_allJobs.Count > 0)
        {
            MessageBox.Show("当前标签已有打印记录，请使用“结账”归档。", "PrintPal",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show($"删除空标签“{_currentTag.Name}”？", "PrintPal",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _agent.Database.CheckoutAndAdvanceAsync(_currentSession.Id, "删除空标签");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除标签失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSession is null || _currentTag is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"结账“{_currentTag.Name}”？\n\n本次 {_allJobs.Count} 单，金额 {FullAmountText.Text}。\n结账后自动归档并切换到下一个标签。",
            "PrintPal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var label = _currentTag.Name;
            var amount = FullAmountText.Text;
            var result = await _agent.Database.CheckoutAndAdvanceAsync(_currentSession.Id, null);
            _currentTag = result.NextTag;
            _currentSession = result.NextSession;
            await ReloadAsync();
            MessageBox.Show($"“{label}”已结账：{amount}。\n当前标签：{_currentTag.Name}",
                "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (CheckoutBlockedException ex)
        {
            var names = string.Join("\n", ex.BlockingJobs.Take(8)
                .Select(job => $"{job.DocumentName}（{JobStatusText(job.Status)}）"));
            MessageBox.Show($"还有未处理的任务，不能结账：\n\n{names}\n\n请先转发完成或删除这些记录。",
                "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"结账失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SwitchNextTag_Click(object sender, RoutedEventArgs e)
    {
        if (_tagRows.Count == 0)
        {
            return;
        }

        var index = _tagRows.ToList().FindIndex(row => row.Tag.Id == _currentTag?.Id);
        var next = _tagRows[(index + 1 + _tagRows.Count) % _tagRows.Count];
        await SelectTagAsync(next);
    }

    private async void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not BillingSessionRow row)
        {
            _historyJobRows.Clear();
            return;
        }

        try
        {
            var jobs = await _agent.Database.GetSessionJobsAsync(row.Session.Id);
            var printerNames = _printers.ToDictionary(printer => printer.Id, printer => printer.Name);
            _historyJobRows.Clear();
            foreach (var job in jobs)
            {
                _historyJobRows.Add(new JobRow(job, printerNames));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取历史任务失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TagTabs_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        RenameTag_Click(sender, e);
        await Task.CompletedTask;
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

    private async void FullMode_Click(object sender, RoutedEventArgs e) => await SetModeAsync(UiMode.Full);

    private async void SimpleMode_Click(object sender, RoutedEventArgs e) => await SetModeAsync(UiMode.Simple);

    private async void MinimalMode_Click(object sender, RoutedEventArgs e) => await SetModeAsync(UiMode.Minimal);

    private async void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingMode || !_initialized || sender is not ComboBox combo || combo.SelectedIndex < 0)
        {
            return;
        }

        var mode = combo.SelectedIndex switch
        {
            1 => UiMode.Simple,
            2 => UiMode.Minimal,
            _ => UiMode.Full,
        };

        await SetModeAsync(mode);
    }

    private void SyncModeCombos()
    {
        var index = _mode switch
        {
            UiMode.Simple => 1,
            UiMode.Minimal => 2,
            _ => 0,
        };

        FullModeCombo.SelectedIndex = index;
        SimpleModeCombo.SelectedIndex = index;
        MinimalModeCombo.SelectedIndex = index;
    }

    private void MinimalTagName_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            RenameTag_Click(sender, e);
        }
    }

    private void CloseToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private async void MinimalHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
        await Task.CompletedTask;
    }

    private async void TopmostToggle_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        await SaveSettingAsync(TopmostKey(_mode), Topmost ? "true" : "false");
        UpdateTopmostButtons();
    }

    private async Task SetModeAsync(UiMode mode, bool persist = true)
    {
        if (_applyingMode)
        {
            return;
        }

        _applyingMode = true;
        try
        {
            SaveModeBounds();
            _mode = mode;
            SyncModeCombos();
            FullLayout.Visibility = mode == UiMode.Full ? Visibility.Visible : Visibility.Collapsed;
            SimpleLayout.Visibility = mode == UiMode.Simple ? Visibility.Visible : Visibility.Collapsed;
            MinimalLayout.Visibility = mode == UiMode.Minimal ? Visibility.Visible : Visibility.Collapsed;

            if (mode == UiMode.Minimal)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#0F172A")!;
                Width = 300;
                Height = 168;
                MinWidth = 300;
                MinHeight = 168;
                await RestoreMinimalPositionAsync();
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = true;
                Background = System.Windows.Media.Brushes.White;
                MinWidth = 680;
                MinHeight = 480;

                if (mode == UiMode.Full)
                {
                    Width = await GetDoubleSettingAsync("fullWidth", 1400);
                    Height = await GetDoubleSettingAsync("fullHeight", 880);
                }
                else
                {
                    Width = await GetDoubleSettingAsync("simpleWidth", 800);
                    Height = await GetDoubleSettingAsync("simpleHeight", 580);
                }

                if (mode == UiMode.Full)
                {
                    Left = await GetDoubleSettingAsync("fullLeft", Left);
                    Top = await GetDoubleSettingAsync("fullTop", Top);
                }
                else
                {
                    Left = await GetDoubleSettingAsync("simpleLeft", Left);
                    Top = await GetDoubleSettingAsync("simpleTop", Top);
                }
            }

            Topmost = await GetBoolSettingAsync(TopmostKey(mode), mode == UiMode.Minimal);
            UpdateTopmostButtons();
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
        if (!double.IsNaN(Left) && !double.IsNaN(Top) && !double.IsNaN(Width) && !double.IsNaN(Height))
        {
            _ = SaveSettingAsync($"{prefix}Left", Left.ToString("0"));
            _ = SaveSettingAsync($"{prefix}Top", Top.ToString("0"));
            _ = SaveSettingAsync($"{prefix}Width", Width.ToString("0"));
            _ = SaveSettingAsync($"{prefix}Height", Height.ToString("0"));
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
        var text = Topmost ? "置顶：开" : "置顶：关";
        FullPinButton.Content = text;
        SimplePinButton.Content = text;
        MinimalPinButton.Content = text;
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
            return double.TryParse(value, out var parsed) ? parsed : fallback;
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



