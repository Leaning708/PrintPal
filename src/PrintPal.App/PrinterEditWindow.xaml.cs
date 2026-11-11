using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using PrintPal.Core;
using PrintPal.Ipp;

namespace PrintPal.App;

public partial class PrinterEditWindow : Window
{
    private readonly Printer? _existing;
    private readonly Printer _printer;
    private PrinterCapabilities? _detected;
    private bool _suppressCapabilityChecks;

    public PrinterEditWindow(Printer? printer)
    {
        InitializeComponent();

        MaxHeight = Math.Max(560, SystemParameters.WorkArea.Height - 32);

        _existing = printer;
        _printer = printer ?? new Printer();
        _detected = printer?.Capabilities;

        ProtocolCombo.ItemsSource = new[] { "IPP", "Raw 9100" };
        FormatCombo.ItemsSource = new[] { "Auto（跟随任务）", "PDF", "PWG Raster", "PostScript" };

        if (printer is null)
        {
            NameBox.Text = "Fake Printer";
            ProtocolCombo.SelectedIndex = 0;
            HostBox.Text = "127.0.0.1";
            PortBox.Text = "8631";
            PathBox.Text = "/ipp/print";
            FormatCombo.SelectedIndex = 0;
            EnabledCheck.IsChecked = true;
        }
        else
        {
            NameBox.Text = printer.Name;
            ProtocolCombo.SelectedIndex = printer.Protocol == PrinterProtocol.Ipp ? 0 : 1;
            HostBox.Text = printer.Host;
            PortBox.Text = printer.Port.ToString();
            PathBox.Text = printer.ResourcePath;
            FormatCombo.SelectedIndex = printer.FormatPreference switch
            {
                DocumentFormat.Pdf => 1,
                DocumentFormat.Pwg => 2,
                DocumentFormat.Ps => 3,
                _ => 0,
            };
            EnabledCheck.IsChecked = printer.Enabled;
            NotesBox.Text = printer.Notes ?? "";
        }

        LoadCapability(printer?.Routing);
        UpdateDetectedText();
        UpdateProtocolUi();
        RestoreTestResult();
    }

    public Printer Printer => _printer;

    /// <summary>重新打开窗口时展示上一次的测试结果（成功带时间，失败带上次成功时间）。</summary>
    private void RestoreTestResult()
    {
        if (_printer.LastTestOk is not bool ok)
        {
            return;
        }

        TestResultText.Foreground = ok
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.Firebrick;
        TestResultText.Text = PrinterStatus.FormatTestResult(
            ok,
            _printer.LastTestedAtUtc,
            _printer.LastSuccessAtUtc);
    }
    private bool IsIpp => ProtocolCombo.SelectedIndex != 1;

    /// <summary>IPP 时文档格式固定"自动协商"并置灰；资源路径只对 IPP 显示。</summary>
    private void UpdateProtocolUi()
    {
        var isIpp = IsIpp;
        FormatCombo.IsEnabled = !isIpp;
        if (!isIpp)
        {
            FormatCombo.SelectedIndex = 0;
        }

        ResourcePathPanel.Visibility = isIpp ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>null → 默认勾选 A3/A4/A5 + 黑白/彩色 + 单双面（新打印机乐观缺省）。</summary>
    private void LoadCapability(PrinterRoutingCapability? routing)
    {
        var capability = routing ?? new PrinterRoutingCapability();

        _suppressCapabilityChecks = true;

        if (capability.PaperSizes.Count == 0)
        {
            A3Check.IsChecked = true;
            A4Check.IsChecked = true;
            A5Check.IsChecked = true;
        }
        else
        {
            A3Check.IsChecked = capability.PaperSizes.Contains("A3", StringComparer.OrdinalIgnoreCase);
            A4Check.IsChecked = capability.PaperSizes.Contains("A4", StringComparer.OrdinalIgnoreCase);
            A5Check.IsChecked = capability.PaperSizes.Contains("A5", StringComparer.OrdinalIgnoreCase);
        }

        var colors = capability.ColorModes;
        MonoCheck.IsChecked = colors.Count == 0 || colors.Contains(ColorMode.Monochrome);
        ColorCheck.IsChecked = colors.Count == 0 || colors.Contains(ColorMode.Color);

        var duplex = capability.DuplexModes;
        SimplexCheck.IsChecked = duplex.Count == 0 || duplex.Contains(DuplexMode.Simplex);
        DuplexCheck.IsChecked = duplex.Count == 0 || duplex.Any(mode => mode.IsDuplex());

        _suppressCapabilityChecks = false;
    }

    private void UpdateDetectedText()
    {
        if (_detected is null)
        {
            DetectedText.Text = "尚未检测。可先用「测试连接」读取，检测成功后会自动填充打印能力。";
            return;
        }

        var formats = _detected.DocumentFormats.Count == 0
            ? "未知"
            : string.Join("/", _detected.DocumentFormats.Select(format => format.DisplayName()).Distinct());
        var papers = _detected.MediaSizes.Count == 0
            ? "未知"
            : string.Join("/", _detected.MediaSizes.Select(size => size.Name));
        var colors = _detected.ColorModes.Count == 0
            ? "未知"
            : string.Join("/", _detected.ColorModes.Select(mode => mode.DisplayName()).Distinct());
        var duplex = _detected.DuplexModes.Count == 0
            ? "未知"
            : string.Join("/", _detected.DuplexModes.Select(mode => mode.DisplayName()).Distinct());

        // 每类独占一行
        DetectedText.Text =
            $"格式：{formats}\n颜色：{colors}\n纸张：{papers}\n单双面：{duplex}";
    }

    private void FillFromDetected_Click(object sender, RoutedEventArgs e)
    {
        if (_detected is null)
        {
            MessageBox.Show("还没有检测结果，请先点「测试连接」。", "PrintPal", MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        LoadCapability(PrinterRoutingCapability.FromDetected(_detected));
    }

    private PrinterRoutingCapability BuildCapability()
    {
        var capability = new PrinterRoutingCapability();

        if (A3Check.IsChecked == true) capability.PaperSizes.Add("A3");
        if (A4Check.IsChecked == true) capability.PaperSizes.Add("A4");
        if (A5Check.IsChecked == true) capability.PaperSizes.Add("A5");

        if (MonoCheck.IsChecked == true) capability.ColorModes.Add(ColorMode.Monochrome);
        if (ColorCheck.IsChecked == true) capability.ColorModes.Add(ColorMode.Color);

        if (SimplexCheck.IsChecked == true) capability.DuplexModes.Add(DuplexMode.Simplex);
        if (DuplexCheck.IsChecked == true) capability.DuplexModes.Add(DuplexMode.TwoSidedLongEdge);

        return capability;
    }

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortBox is null || ResourcePathPanel is null)
        {
            return;
        }

        if (ProtocolCombo.SelectedIndex == 1)
        {
            if (PortBox.Text == "8631" || string.IsNullOrWhiteSpace(PortBox.Text))
            {
                PortBox.Text = "9100";
            }
        }
        else
        {
            if (PortBox.Text == "9100" || string.IsNullOrWhiteSpace(PortBox.Text))
            {
                PortBox.Text = "8631";
            }
        }

        UpdateProtocolUi();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Foreground = System.Windows.Media.Brushes.DimGray;
        TestResultText.Text = "正在连接 ...";

        try
        {
            var candidate = BuildCandidate();

            if (candidate.Protocol == PrinterProtocol.Ipp)
            {
                using var client = new IppClient();
                var capabilities = await client.GetPrinterAttributesAsync(new Uri(candidate.Uri));
                _detected = capabilities;
                UpdateDetectedText();
                LoadCapability(PrinterRoutingCapability.FromDetected(capabilities));

                var testedAt = DateTimeOffset.UtcNow;
                _printer.LastTestedAtUtc = testedAt;
                _printer.LastTestOk = true;
                _printer.LastSuccessAtUtc = testedAt;

                TestResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                TestResultText.Text =
                    $"{PrinterStatus.FormatTestResult(true, testedAt, testedAt)}\n" +
                    $"名称：{capabilities.Name ?? candidate.Name}\n" +
                    $"格式：{string.Join(", ", capabilities.DocumentFormats.Select(format => format.DisplayName()))}\n" +
                    $"纸张：{string.Join(", ", capabilities.MediaSizes.Select(size => size.Name))}\n" +
                    $"颜色：{string.Join(", ", capabilities.ColorModes)}\n" +
                    $"双面：{string.Join(", ", capabilities.DuplexModes)}";
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(candidate.Host, candidate.Port, timeout.Token);

                var testedAt = DateTimeOffset.UtcNow;
                _printer.LastTestedAtUtc = testedAt;
                _printer.LastTestOk = true;
                _printer.LastSuccessAtUtc = testedAt;

                TestResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                TestResultText.Text =
                    $"{PrinterStatus.FormatTestResult(true, testedAt, testedAt)}\n" +
                    $"TCP {candidate.Host}:{candidate.Port}";
            }
        }
        catch (Exception ex)
        {
            var testedAt = DateTimeOffset.UtcNow;
            _printer.LastTestedAtUtc = testedAt;
            _printer.LastTestOk = false;

            TestResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResultText.Text =
                $"{PrinterStatus.FormatTestResult(false, testedAt, _printer.LastSuccessAtUtc)}\n" +
                $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var candidate = BuildCandidate();

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                MessageBox.Show("请输入打印机名称。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(candidate.Host))
            {
                MessageBox.Show("请输入主机/IP。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var capability = BuildCapability();
            var errors = PrinterCapabilityValidation.Validate(capability);
            if (errors.Count > 0)
            {
                MessageBox.Show(string.Join("\n", errors), "PrintPal", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _printer.Name = candidate.Name;
            _printer.Protocol = candidate.Protocol;
            _printer.Host = candidate.Host;
            _printer.Port = candidate.Port;
            _printer.ResourcePath = candidate.ResourcePath;
            _printer.FormatPreference = candidate.FormatPreference;
            _printer.Enabled = candidate.Enabled;
            _printer.Notes = candidate.Notes;
            _printer.Routing = capability;

            if (_existing is null)
            {
                _printer.Capabilities = _detected ?? PrinterCapabilities.CreateDefault(candidate.Name);
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private Printer BuildCandidate()
    {
        var isIpp = IsIpp;
        var protocol = isIpp ? PrinterProtocol.Ipp : PrinterProtocol.Raw;

        // IPP 由协议自动协商文档格式；Raw 9100 才需要声明格式偏好。
        var format = isIpp
            ? DocumentFormat.Unknown
            : FormatCombo.SelectedIndex switch
            {
                1 => DocumentFormat.Pdf,
                2 => DocumentFormat.Pwg,
                3 => DocumentFormat.Ps,
                _ => DocumentFormat.Unknown,
            };

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("端口必须是 1–65535 的整数。");
        }

        return new Printer
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = NameBox.Text.Trim(),
            Protocol = protocol,
            Host = HostBox.Text.Trim(),
            Port = port,
            ResourcePath = string.IsNullOrWhiteSpace(PathBox.Text) ? "/ipp/print" : PathBox.Text.Trim(),
            FormatPreference = format,
            Enabled = EnabledCheck.IsChecked == true,
            Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
            Capabilities = _detected ?? _existing?.Capabilities ?? PrinterCapabilities.CreateDefault(NameBox.Text.Trim()),
            LastTestedAtUtc = _printer.LastTestedAtUtc ?? _existing?.LastTestedAtUtc,
            LastTestOk = _printer.LastTestOk ?? _existing?.LastTestOk,
            LastSuccessAtUtc = _printer.LastSuccessAtUtc ?? _existing?.LastSuccessAtUtc,
        };
    }
}
