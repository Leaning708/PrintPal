using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using PrintPal.Agent;

namespace PrintPal.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private IPrintPalBackend? _agent;
    private StreamWriter? _logWriter;

    public IPrintPalBackend Agent => _agent ?? throw new InvalidOperationException("Agent is not running.");

    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startup = StartupOptions.Parse(e.Args);

        _instanceMutex = new Mutex(true, @"Local\PrintPal.App.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PrintPal 已在运行。", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Directory.CreateDirectory(startup.Agent.DataDirectory);
        var logDirectory = Path.Combine(startup.Agent.DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        var logStream = new FileStream(
            Path.Combine(logDirectory, "app.log"),
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        _logWriter = new StreamWriter(logStream)
        {
            AutoFlush = true,
        };
        Console.SetOut(_logWriter);
        Console.SetError(_logWriter);

        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PrintPal starting, args={string.Join(' ', e.Args)}");

        if (await IsServiceHealthyAsync(startup.Agent.IppPort))
        {
            Console.WriteLine("PrintPal Windows service is running; using service mode.");
            var serviceData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PrintPal");
            var serviceOptions = new AgentOptions
            {
                DataDirectory = serviceData,
                IppPort = startup.Agent.IppPort,
                BindAddress = startup.Agent.BindAddress,
                AdvertiseHost = startup.Agent.AdvertiseHost,
                ResourcePath = startup.Agent.ResourcePath,
                AutoForward = startup.Agent.AutoForward,
                SeedDemoPrinters = false,
            };
            _agent = new ServiceAgentProxy(serviceOptions);
        }
        else
        {
            _agent = new PrintPalAgent(startup.Agent);
        }

        try
        {
            await _agent.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AGENT START FAILED: {ex}");
            MessageBox.Show(
                $"PrintPal 后台服务启动失败：{ex.Message}\n\n" +
                "常见原因：8630 端口被占用，或数据目录不可写。",
                "PrintPal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            if (!startup.AutoForwardSpecified)
            {
                var autoForward = await _agent.Database.GetSettingAsync("autoForward");
                if (bool.TryParse(autoForward, out var enabled))
                {
                    _agent.Options.AutoForward = enabled;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load autoForward: {ex.Message}");
        }

        var window = new MainWindow(_agent);
        MainWindow = window;

        try
        {
            await window.InitializeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UI INIT FAILED: {ex}");
            MessageBox.Show($"界面初始化失败：{ex.Message}", "PrintPal", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (startup.StartHidden)
        {
            window.HideToTray();
        }
        else
        {
            window.Show();
            window.Activate();
        }
    }

    private static async Task<bool> IsServiceHealthyAsync(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/health");
            return json.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void ExitApplication()
    {
        IsExiting = true;

        if (MainWindow is MainWindow window)
        {
            window.Close();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _agent?.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AGENT STOP FAILED: {ex}");
        }

        try
        {
            _logWriter?.Flush();
            _logWriter?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        base.OnExit(e);
    }

    private sealed class StartupOptions
    {
        public AgentOptions Agent { get; } = new();

        public bool StartHidden { get; private set; }

        public bool AutoForwardSpecified { get; private set; }

        public static StartupOptions Parse(string[] args)
        {
            var options = new StartupOptions();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--background":
                        options.StartHidden = true;
                        break;

                    case "--seed-demo":
                        options.Agent.SeedDemoPrinters = true;
                        break;

                    case "--no-auto-forward":
                        options.Agent.AutoForward = false;
                        options.AutoForwardSpecified = true;
                        break;

                    case "--data" when i + 1 < args.Length:
                        options.Agent.DataDirectory = Path.GetFullPath(args[++i]);
                        break;

                    case "--port" when i + 1 < args.Length:
                        options.Agent.IppPort = int.Parse(args[++i]);
                        break;

                    case "--bind" when i + 1 < args.Length:
                        options.Agent.BindAddress = args[++i];
                        break;

                    case "--host" when i + 1 < args.Length:
                        options.Agent.AdvertiseHost = args[++i];
                        break;
                }
            }

            return options;
        }
    }
}
