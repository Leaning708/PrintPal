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
    private readonly List<JobRow> _allJobs = new();
    private readonly DispatcherTimer _timer;

    private List<Printer> _printers = new();
    private List<RoutingRule> _routingRules = new();
    private List<PriceRule> _priceRules = new();

    private WinForms.NotifyIcon? _trayIcon;
    private bool _initialized;
    private bool _reloading;
    private bool _balloonShown;
    private bool _suppressAutostart;
    private bool _pricesDirty;

    public MainWindow(IPrintPalBackend agent)
    {
        InitializeComponent();

        _agent = agent;

        JobsGrid.ItemsSource = _jobRows;
        PrintersGrid.ItemsSource = _printerRows;
        RoutingGrid.ItemsSource = _routingRows;
        PricesGrid.ItemsSource = _priceRows;

        IppUriText.Text = agent.IppUri;
        DataDirText.Text = agent.Options.DataDirectory;
        AgentStatusText.Text = $"IPP 服务：{agent.IppUri}";
        AutoForwardCheck.IsChecked = agent.Options.AutoForward;

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.3.0";
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
            Text = "PrintPal 打印助手",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 PrintPal", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
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
        Topmost = true;
        Topmost = false;
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

            var jobsTask = _agent.Database.GetJobsAsync(500);
            var printersTask = _agent.Database.GetPrintersAsync();
            var routingTask = _agent.Database.GetRoutingRulesAsync();
            var pricesTask = _agent.Database.GetPriceRulesAsync();

            await Task.WhenAll(jobsTask, printersTask, routingTask, pricesTask);

            var jobs = await jobsTask;
            _printers = (await printersTask).ToList();
            _routingRules = (await routingTask).ToList();
            _priceRules = (await pricesTask).ToList();

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

            ApplyFilters(selectJobId: selectedJobId);

            var now = DateTimeOffset.Now;
            var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            var weekStart = todayStart.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

            var today = await _agent.Database.GetSummaryAsync(todayStart, now.AddMinutes(1));
            var week = await _agent.Database.GetSummaryAsync(weekStart, now.AddMinutes(1));
            var month = await _agent.Database.GetSummaryAsync(monthStart, now.AddMinutes(1));

            TodayText.Text = FormatSummary(today);
            WeekText.Text = FormatSummary(week);
            MonthText.Text = FormatSummary(month);

            StatusBarText.Text = $"最后刷新 {DateTime.Now:HH:mm:ss} · 共 {jobs.Count} 条记录";
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

    private static string FormatSummary(JobSummary summary) =>
        $"{summary.JobCount} 单 · {Money.FormatCents(summary.AmountCents)}";

    private void ApplyFilters(Guid? selectJobId = null)
    {
        var filtered = _allJobs.AsEnumerable();

        var now = DateTimeOffset.Now;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);

        filtered = DateRangeCombo.SelectedIndex switch
        {
            1 => filtered.Where(row => row.Job.CreatedAtUtc >= todayStart),
            2 => filtered.Where(row => row.Job.CreatedAtUtc >= todayStart.AddDays(-(((int)now.DayOfWeek + 6) % 7))),
            3 => filtered.Where(row => row.Job.CreatedAtUtc >= new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset)),
            _ => filtered,
        };

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

        var total = list.Sum(row => row.Job.AmountCents);
        FilterSummaryText.Text = $"{list.Count} 条 · {Money.FormatCents(total)}";

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

