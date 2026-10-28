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

    public PrinterEditWindow(Printer? printer)
    {
        InitializeComponent();

        _existing = printer;
        _printer = printer ?? new Printer();

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
            DefaultCheck.IsChecked = printer.IsDefault;
            NotesBox.Text = printer.Notes ?? "";
        }
    }

    public Printer Printer => _printer;

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortBox is null)
        {
            return;
        }

        if (ProtocolCombo.SelectedIndex == 1)
        {
            if (PortBox.Text == "8631" || string.IsNullOrWhiteSpace(PortBox.Text))
            {
                PortBox.Text = "9100";
            }

            PathBox.IsEnabled = false;
        }
        else
        {
            if (PortBox.Text == "9100" || string.IsNullOrWhiteSpace(PortBox.Text))
            {
                PortBox.Text = "8631";
            }

            PathBox.IsEnabled = true;
        }
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
                TestResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                TestResultText.Text =
                    $"连接成功：{capabilities.Name ?? "(未命名)"}\n" +
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
                TestResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
                TestResultText.Text = $"TCP 连接成功：{candidate.Host}:{candidate.Port}";
            }
        }
        catch (Exception ex)
        {
            TestResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResultText.Text = $"连接失败：{ex.GetType().Name}: {ex.Message}";
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

            _printer.Name = candidate.Name;
            _printer.Protocol = candidate.Protocol;
            _printer.Host = candidate.Host;
            _printer.Port = candidate.Port;
            _printer.ResourcePath = candidate.ResourcePath;
            _printer.FormatPreference = candidate.FormatPreference;
            _printer.Enabled = candidate.Enabled;
            _printer.IsDefault = candidate.IsDefault;
            _printer.Notes = candidate.Notes;

            if (_existing is null)
            {
                _printer.Capabilities = PrinterCapabilities.CreateDefault(candidate.Name);
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private Printer BuildCandidate()
    {
        var protocol = ProtocolCombo.SelectedIndex == 1 ? PrinterProtocol.Raw : PrinterProtocol.Ipp;

        var format = FormatCombo.SelectedIndex switch
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
            IsDefault = DefaultCheck.IsChecked == true,
            Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
            Capabilities = _existing?.Capabilities ?? PrinterCapabilities.CreateDefault(NameBox.Text.Trim()),
            LastTestedAtUtc = _existing?.LastTestedAtUtc,
            LastTestOk = _existing?.LastTestOk,
        };
    }
}
