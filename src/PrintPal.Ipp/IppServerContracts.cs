using System.Net;
using PrintPal.Core;

namespace PrintPal.Ipp;

public sealed class IppServerOptions
{
    public string Name { get; set; } = "PrintPal Virtual Printer";

    public string? MakeAndModel { get; set; } = "PrintPal Virtual Printer";

    /// <summary>Host name/IP advertised in printer-uri-supported and job-uri.</summary>
    public string HostName { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8631;

    public string ResourcePath { get; set; } = "/ipp/print";

    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    public PrinterCapabilities Capabilities { get; set; } = PrinterCapabilities.CreateDefault();

    public long MaxDocumentBytes { get; set; } = 500L * 1024 * 1024;

    public string? PrinterUuid { get; set; }

    public string? PrinterLocation { get; set; } = "Local";

    public bool EnableHealthEndpoint { get; set; } = true;

    public string IppUri => $"ipp://{HostName}:{Port}{NormalizePath(ResourcePath)}";

    public string HttpUri => $"http://{HostName}:{Port}{NormalizePath(ResourcePath)}";

    public string BaseHttpUri => $"http://{HostName}:{Port}/";

    public static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/ipp/print" : (path.StartsWith('/') ? path : "/" + path);
}

public sealed class IppJobContext
{
    public required int JobId { get; init; }

    public required ushort Operation { get; init; }

    public required IReadOnlyDictionary<string, IppAttribute> Attributes { get; init; }

    public required IReadOnlyList<IppCollection> Collections { get; init; }

    public required byte[] Document { get; init; }

    public DocumentFormat Format { get; init; }

    public string? DocumentFormatMime { get; init; }

    public string? Compression { get; init; }

    public string? JobName => GetString("job-name");

    public string? UserName => GetString("requesting-user-name");

    public string? GetString(string name) =>
        Attributes.TryGetValue(name, out var attribute) ? attribute.StringValue : null;

    public int GetInt(string name, int fallback = 0) =>
        Attributes.TryGetValue(name, out var attribute) ? attribute.IntValue : fallback;

    public IppCollection? GetCollection(string name) =>
        Collections.FirstOrDefault(collection =>
            string.Equals(collection.Name, name, StringComparison.OrdinalIgnoreCase));

    public PaperSize GetPaperSize()
    {
        var media = GetString("media");
        if (!string.IsNullOrWhiteSpace(media))
        {
            return PaperSize.FromIppKeyword(media);
        }

        var mediaCollection = GetCollection("media-col");
        var mediaSize = mediaCollection?.GetCollection("media-size");
        var width = mediaSize?.GetInt("x-dimension") ?? 0;
        var height = mediaSize?.GetInt("y-dimension") ?? 0;

        return width > 0 && height > 0
            ? PaperSize.FromHundredthMm(width, height)
            : PaperSize.Unknown;
    }

    public ColorMode GetColorMode() =>
        ColorModeExtensions.FromIppKeyword(GetString("print-color-mode"));

    public DuplexMode GetDuplexMode() =>
        DuplexModeExtensions.FromIppKeyword(GetString("sides"));

    public int GetCopies() => Math.Max(1, GetInt("copies", 1));
}

public sealed record IppJobResult(
    ushort StatusCode = IppStatus.SuccessfulOk,
    string? StatusMessage = null,
    int JobState = IppJobState.Completed,
    string? JobStateReasons = "job-completed-successfully",
    int JobId = 0,
    string? JobUri = null)
{
    public bool IsSuccess => IppStatus.IsSuccess(StatusCode);

    public static IppJobResult Success(int jobState = IppJobState.Completed) =>
        new(IppStatus.SuccessfulOk, null, jobState, "job-completed-successfully");

    public static IppJobResult Failure(ushort statusCode, string message) =>
        new(statusCode, message, IppJobState.Aborted, "job-aborted-by-system");
}

public interface IIppJobHandler
{
    Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken);
}
