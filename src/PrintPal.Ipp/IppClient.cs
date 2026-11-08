using System.Net.Http.Headers;
using PrintPal.Core;

namespace PrintPal.Ipp;

public sealed record IppPrintJobRequest(
    Uri PrinterUri,
    string JobName,
    string UserName,
    DocumentFormat DocumentFormat,
    byte[] Document,
    int Copies = 1,
    PaperSize? PaperSize = null,
    ColorMode ColorMode = ColorMode.Monochrome,
    DuplexMode DuplexMode = DuplexMode.Simplex,
    int PrintQuality = 4);

public sealed class IppException : Exception
{
    public IppException(ushort statusCode, string? statusMessage)
        : base($"IPP {IppStatus.GetName(statusCode)}: {statusMessage ?? "no status message"}")
    {
        StatusCode = statusCode;
    }

    public ushort StatusCode { get; }
}

public static class IppUri
{
    public static Uri ToHttp(string ippUri) => ToHttp(new Uri(ippUri, UriKind.Absolute));

    public static Uri ToHttp(Uri ippUri)
    {
        ArgumentNullException.ThrowIfNull(ippUri);

        var scheme = ippUri.Scheme.Equals("ipps", StringComparison.OrdinalIgnoreCase) ||
                     ippUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "http";

        var port = ippUri.Port <= 0 ? 631 : ippUri.Port;
        var path = string.IsNullOrEmpty(ippUri.AbsolutePath) ? "/" : ippUri.AbsolutePath;

        return new Uri($"{scheme}://{ippUri.Host}:{port}{path}{ippUri.Query}");
    }
}

/// <summary>Minimal IPP client used for forwarding and for testing the IPP server.</summary>
public sealed class IppClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private int _requestId;

    public IppClient(HttpClient? httpClient = null, TimeSpan? timeout = null)
    {
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };

            _http = new HttpClient(handler)
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(30),
            };
            _ownsHttpClient = true;
        }
    }

    public async Task<PrinterCapabilities> GetPrinterAttributesAsync(
        Uri printerUri,
        CancellationToken cancellationToken = default)
    {
        var requestId = NextRequestId();
        var builder = new IppMessageBuilder(1, 1, IppOperation.GetPrinterAttributes, requestId);

        builder.BeginOperationAttributes();
        builder.AddString(IppTag.Uri, "printer-uri", printerUri.ToString());

        var attributes = new[]
        {
            "printer-name",
            "printer-make-and-model",
            "printer-state",
            "printer-state-reasons",
            "printer-is-accepting-jobs",
            "document-format-supported",
            "media-supported",
            "print-color-mode-supported",
            "sides-supported",
            "copies-supported",
        };

        for (var i = 0; i < attributes.Length; i++)
        {
            builder.AddString(IppTag.Keyword, i == 0 ? "requested-attributes" : string.Empty, attributes[i]);
        }

        var response = await PostAsync(printerUri, builder.End(), cancellationToken);

        return MapCapabilities(response);
    }

    public async Task<IppJobResult> PrintJobAsync(
        IppPrintJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestId = NextRequestId();
        var builder = BuildJobAttributes(
            new IppMessageBuilder(1, 1, IppOperation.PrintJob, requestId), request, includeOperationDocumentFormat: true);

        var body = AppendDocument(builder.End(), request.Document);
        var response = await PostAsync(request.PrinterUri, body, cancellationToken);

        return MapJobResult(response);
    }

    public async Task<IppJobResult> ValidateJobAsync(
        IppPrintJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var requestId = NextRequestId();
        var builder = BuildJobAttributes(
            new IppMessageBuilder(1, 1, IppOperation.ValidateJob, requestId), request, includeOperationDocumentFormat: true);

        var response = await PostAsync(request.PrinterUri, builder.End(), cancellationToken);
        return MapJobResult(response);
    }

    public async Task<IppJobResult> GetJobAttributesAsync(
        Uri printerUri,
        int jobId,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var requestId = NextRequestId();
        var builder = new IppMessageBuilder(1, 1, IppOperation.GetJobAttributes, requestId);

        builder.BeginOperationAttributes();
        builder.AddString(IppTag.Uri, "printer-uri", printerUri.ToString());
        builder.AddString(IppTag.NameWithoutLanguage, "requesting-user-name", userName);
        builder.AddInt(IppTag.Integer, "job-id", jobId);
        builder.AddString(IppTag.Keyword, "requested-attributes", "job-state");
        builder.AddString(IppTag.Keyword, string.Empty, "job-state-reasons");

        var response = await PostAsync(printerUri, builder.End(), cancellationToken);
        return MapJobResult(response);
    }

    public async Task CancelJobAsync(
        Uri printerUri,
        int jobId,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var requestId = NextRequestId();
        var builder = new IppMessageBuilder(1, 1, IppOperation.CancelJob, requestId);

        builder.BeginOperationAttributes();
        builder.AddString(IppTag.Uri, "printer-uri", printerUri.ToString());
        builder.AddString(IppTag.NameWithoutLanguage, "requesting-user-name", userName);
        builder.AddInt(IppTag.Integer, "job-id", jobId);

        await PostAsync(printerUri, builder.End(), cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private IppMessageBuilder BuildJobAttributes(
        IppMessageBuilder builder,
        IppPrintJobRequest request,
        bool includeOperationDocumentFormat)
    {
        builder.BeginOperationAttributes();
        builder.AddString(IppTag.Uri, "printer-uri", request.PrinterUri.ToString());
        builder.AddString(IppTag.NameWithoutLanguage, "requesting-user-name", request.UserName);
        builder.AddString(IppTag.NameWithoutLanguage, "job-name", request.JobName);

        if (includeOperationDocumentFormat)
        {
            builder.AddString(IppTag.MimeMediaType, "document-format", request.DocumentFormat.ToMimeType());
        }

        builder.BeginGroup(IppTag.JobAttributes);
        builder.AddInt(IppTag.Integer, "copies", request.Copies);

        if (request.PaperSize is not null)
        {
            builder.AddString(IppTag.Keyword, "media", request.PaperSize.ToIppKeyword());
        }

        builder.AddString(IppTag.Keyword, "sides", request.DuplexMode.ToIppKeyword());
        builder.AddString(IppTag.Keyword, "print-color-mode", request.ColorMode.ToIppKeyword());
        builder.AddInt(IppTag.Enum, "print-quality", request.PrintQuality);

        return builder;
    }

    private async Task<IppResponse> PostAsync(
        Uri printerUri,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var httpUri = IppUri.ToHttp(printerUri);

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");

        using var response = await _http.PostAsync(httpUri, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new IppException(0xFFFF, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var ipp = IppResponse.Parse(bytes);

        if (!IppStatus.IsSuccess(ipp.StatusCode))
        {
            throw new IppException(ipp.StatusCode, ipp.StatusMessage);
        }

        return ipp;
    }

    private static byte[] AppendDocument(byte[] header, byte[] document)
    {
        if (document.Length == 0)
        {
            return header;
        }

        var body = new byte[header.Length + document.Length];
        Buffer.BlockCopy(header, 0, body, 0, header.Length);
        Buffer.BlockCopy(document, 0, body, header.Length, document.Length);
        return body;
    }

    private static IppJobResult MapJobResult(IppResponse response) => new(
        response.StatusCode,
        response.StatusMessage,
        response.GetInt("job-state", IppJobState.Completed),
        response.GetString("job-state-reasons"),
        response.GetInt("job-id"),
        response.GetString("job-uri"));

    private static PrinterCapabilities MapCapabilities(IppResponse response)
    {
        var capabilities = new PrinterCapabilities
        {
            Name = response.GetString("printer-name"),
            MakeAndModel = response.GetString("printer-make-and-model"),
        };

        foreach (var value in Values(response, "document-format-supported"))
        {
            var format = DocumentFormatExtensions.FromMimeType(value);
            if (format != DocumentFormat.Unknown && !capabilities.DocumentFormats.Contains(format))
            {
                capabilities.DocumentFormats.Add(format);
            }
        }

        foreach (var value in Values(response, "media-supported"))
        {
            var paper = PaperSize.FromIppKeyword(value);
            if (paper.IsKnown && !capabilities.MediaSizes.Contains(paper))
            {
                capabilities.MediaSizes.Add(paper);
            }
        }

        foreach (var value in Values(response, "print-color-mode-supported"))
        {
            var color = ColorModeExtensions.FromIppKeyword(value);
            if (color != ColorMode.Unknown && !capabilities.ColorModes.Contains(color))
            {
                capabilities.ColorModes.Add(color);
            }
        }

        foreach (var value in Values(response, "sides-supported"))
        {
            var side = DuplexModeExtensions.FromIppKeyword(value);
            if (side != DuplexMode.Unknown && !capabilities.DuplexModes.Contains(side))
            {
                capabilities.DuplexModes.Add(side);
            }
        }

        var copies = response.Get("copies-supported");
        if (copies is not null)
        {
            capabilities.MinCopies = copies.RangeLower;
            capabilities.MaxCopies = copies.RangeUpper;
        }

        return capabilities;
    }

    private static IEnumerable<string> Values(IppResponse response, string attributeName) =>
        response.Get(attributeName)?.StringValues ?? Array.Empty<string>();

    private uint NextRequestId() => (uint)Interlocked.Increment(ref _requestId);
}
