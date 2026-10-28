using System.Net;

namespace PrintPal.Agent;

public sealed class AgentOptions
{
    public string PrinterName { get; set; } = "PrintPal Virtual Printer";

    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrintPal");

    public int IppPort { get; set; } = 8630;

    public string BindAddress { get; set; } = "127.0.0.1";

    public string AdvertiseHost { get; set; } = "127.0.0.1";

    public string ResourcePath { get; set; } = "/ipp/print";

    public bool AutoForward { get; set; } = true;

    public bool SeedDemoPrinters { get; set; }

    /// <summary>Maximum number of forward attempts (including the first one).</summary>
    public int ForwardMaxAttempts { get; set; } = 4;

    /// <summary>Per-attempt forward timeout. A timeout is treated as Unknown, never auto-retried.</summary>
    public TimeSpan ForwardTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delays between attempts. The last delay is reused if attempts exceed the list.</summary>
    public List<TimeSpan> ForwardRetryDelays { get; set; } = new()
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    };

    public long MaxDocumentBytes { get; set; } = 500L * 1024 * 1024;

    public string DatabasePath => Path.Combine(DataDirectory, "printpal.db");

    public string DocumentDirectory => Path.Combine(DataDirectory, "documents");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public IPAddress ResolveBindAddress()
    {
        if (BindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(BindAddress, out var address)
            ? address
            : throw new ArgumentException($"Invalid bind address: {BindAddress}");
    }
}
