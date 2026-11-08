namespace PrintPal.Core;

public sealed class Printer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public PrinterProtocol Protocol { get; set; } = PrinterProtocol.Ipp;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8631;

    public string ResourcePath { get; set; } = "/ipp/print";

    /// <summary>DocumentFormat.Unknown means "auto / prefer the job format".</summary>
    public DocumentFormat FormatPreference { get; set; } = DocumentFormat.Unknown;

    public bool Enabled { get; set; } = true;

    public bool IsDefault { get; set; }

    public PrinterCapabilities? Capabilities { get; set; }

    public DateTimeOffset? LastTestedAtUtc { get; set; }

    public bool? LastTestOk { get; set; }

    public string? Notes { get; set; }

    public string Uri => Protocol == PrinterProtocol.Ipp
        ? $"ipp://{Host}:{Port}{NormalizePath(ResourcePath)}"
        : $"raw://{Host}:{Port}";

    public override string ToString() => $"{Name} ({Uri})";

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/ipp/print" : (path.StartsWith('/') ? path : "/" + path);
}

public sealed class PrinterCapabilities
{
    public string? Name { get; set; }

    public string? MakeAndModel { get; set; }

    public List<DocumentFormat> DocumentFormats { get; set; } = new();

    public List<PaperSize> MediaSizes { get; set; } = new();

    public List<ColorMode> ColorModes { get; set; } = new();

    public List<DuplexMode> DuplexModes { get; set; } = new();

    public int MinCopies { get; set; } = 1;

    public int MaxCopies { get; set; } = 999;

    public bool SupportsFormat(DocumentFormat format) =>
        DocumentFormats.Count == 0 || DocumentFormats.Contains(format);

    public static PrinterCapabilities CreateDefault(string? name = null) => new()
    {
        Name = name,
        MakeAndModel = "PrintPal Virtual Printer",
        DocumentFormats = new List<DocumentFormat> { DocumentFormat.Pdf, DocumentFormat.Pwg },
        MediaSizes = PaperSize.Standards.ToList(),
        ColorModes = new List<ColorMode> { ColorMode.Monochrome, ColorMode.Color },
        DuplexModes = new List<DuplexMode>
        {
            DuplexMode.Simplex,
            DuplexMode.TwoSidedLongEdge,
            DuplexMode.TwoSidedShortEdge,
        },
        MinCopies = 1,
        MaxCopies = 999,
    };
}
