using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PrintPal.Core;

namespace PrintPal.Ipp;

/// <summary>
/// A self-contained IPP printer server. It advertises a Windows-compatible
/// capability set, accepts Print-Job and Create-Job/Send-Document, optionally
/// decompresses gzip documents, and hands the job to an <see cref="IIppJobHandler"/>.
/// </summary>
public sealed class IppServer : IAsyncDisposable
{
    private readonly IppServerOptions _options;
    private readonly IIppJobHandler _handler;
    private readonly ILogger<IppServer>? _logger;
    private readonly ConcurrentDictionary<int, PendingJob> _pendingJobs = new();
    private WebApplication? _app;
    private int _nextJobId;

    public IppServer(IppServerOptions options, IIppJobHandler handler, ILogger<IppServer>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _logger = logger;

        _options.ResourcePath = IppServerOptions.NormalizePath(_options.ResourcePath);
        _options.Capabilities.Name ??= _options.Name;
    }

    public IppServerOptions Options => _options;

    public string IppUri => _options.IppUri;

    public string HttpUri => _options.HttpUri;

    public bool IsRunning => _app is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            throw new InvalidOperationException("The IPP server is already running.");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(_options.BindAddress, _options.Port);
            options.Limits.MaxRequestBodySize = _options.MaxDocumentBytes + 1024 * 1024;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
        });

        var app = builder.Build();

        if (_options.EnableHealthEndpoint)
        {
            app.MapGet("/health", () => Results.Json(new
            {
                status = "ok",
                name = _options.Name,
                ippUri = _options.IppUri,
                httpUri = _options.HttpUri,
                pendingJobs = _pendingJobs.Count,
                time = DateTimeOffset.Now,
            }));
        }

        app.MapPost(_options.ResourcePath, HandleIppAsync);
        app.MapGet(_options.ResourcePath, (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        });

        await app.StartAsync(cancellationToken);
        _app = app;

        _logger?.LogInformation("IPP server '{Name}' listening on {HttpUri} ({IppUri})",
            _options.Name, _options.HttpUri, _options.IppUri);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var app = _app;
        if (app is null)
        {
            return;
        }

        _app = null;

        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync();
            _pendingJobs.Clear();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task HandleIppAsync(HttpContext context)
    {
        byte[] body;

        using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = buffer.ToArray();
        }

        if (body.Length > _options.MaxDocumentBytes + 1024 * 1024)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        IppRequest request;

        try
        {
            request = IppRequest.Parse(body);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not parse IPP request ({Bytes} bytes)", body.Length);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        byte[] response;

        try
        {
            response = await DispatchAsync(request, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "IPP operation {Operation} failed", IppOperation.GetName(request.OperationId));
            response = BuildError(request, IppStatus.ServerErrorInternalError, ex.Message);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/ipp";
        context.Response.ContentLength = response.Length;
        context.Response.Headers["Server"] = "PrintPal-IPP";

        try
        {
            await context.Response.Body.WriteAsync(response, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // The client disconnected; the job result is already recorded.
        }
    }

    private async Task<byte[]> DispatchAsync(IppRequest request, CancellationToken cancellationToken)
    {
        switch (request.OperationId)
        {
            case IppOperation.GetPrinterAttributes:
                return IppPrinterAttributes.Build(request, _options);

            case IppOperation.ValidateJob:
                return BuildOk(request);

            case IppOperation.CreateJob:
                return HandleCreateJob(request);

            case IppOperation.SendDocument:
                return await HandleSendDocumentAsync(request, cancellationToken);

            case IppOperation.PrintJob:
                return await HandlePrintJobAsync(request, cancellationToken);

            case IppOperation.GetJobAttributes:
                return BuildJobResponse(
                    request,
                    request.GetInt("job-id"),
                    IppJobState.Completed,
                    "job-completed-successfully");

            case IppOperation.CancelJob:
                return BuildOk(request);

            default:
                return BuildError(
                    request,
                    IppStatus.ServerErrorOperationNotSupported,
                    $"Operation {IppOperation.GetName(request.OperationId)} is not supported.");
        }
    }

    private byte[] HandleCreateJob(IppRequest request)
    {
        var jobId = Interlocked.Increment(ref _nextJobId);
        _pendingJobs[jobId] = new PendingJob(request, DateTimeOffset.UtcNow);

        _logger?.LogDebug("Create-Job id={JobId} name={JobName}", jobId, request.GetString("job-name"));

        return BuildJobResponse(request, jobId, IppJobState.Pending, "job-incoming");
    }

    private async Task<byte[]> HandleSendDocumentAsync(IppRequest request, CancellationToken cancellationToken)
    {
        var jobId = request.GetInt("job-id");
        if (jobId <= 0)
        {
            jobId = Interlocked.Increment(ref _nextJobId);
        }

        _pendingJobs.TryRemove(jobId, out var pending);

        var lastDocument = request.Get("last-document");
        var isLast = lastDocument is null ||
                     lastDocument.Values.Count == 0 ||
                     lastDocument.Values[0].Length == 0 ||
                     lastDocument.Values[0][0] != 0;

        if (!isLast)
        {
            return BuildError(
                request,
                IppStatus.ServerErrorMultipleDocumentJobsNotSupported,
                "Multiple documents per job are not supported.");
        }

        var attributes = MergeAttributes(pending?.Request.Attributes, request.Attributes);
        var collections = pending?.Request.Collections ?? request.Collections;
        var compression = request.GetString("compression") ?? pending?.Request.GetString("compression");
        var formatMime = request.GetString("document-format") ?? pending?.Request.GetString("document-format");

        var decompressed = Decompress(request.Document.ToArray(), compression, out var decompressionError);
        if (decompressionError is not null)
        {
            return BuildError(request, IppStatus.ClientErrorCompressionError, decompressionError);
        }

        if (decompressed.LongLength > _options.MaxDocumentBytes)
        {
            return BuildError(request, IppStatus.ClientErrorRequestEntityTooLarge, "Document is too large.");
        }

        var context = CreateJobContext(
            jobId, request.OperationId, attributes, collections, decompressed, formatMime, compression);

        IppJobResult result;
        try
        {
            result = await _handler.HandleJobAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Job handler failed for job {JobId}", jobId);
            return BuildError(request, IppStatus.ServerErrorInternalError, ex.Message);
        }

        if (!result.IsSuccess)
        {
            return BuildError(request, result.StatusCode, result.StatusMessage ?? "Job failed.");
        }

        return BuildJobResponse(request, jobId, result.JobState, result.JobStateReasons ?? "job-completed-successfully");
    }

    private async Task<byte[]> HandlePrintJobAsync(IppRequest request, CancellationToken cancellationToken)
    {
        var jobId = Interlocked.Increment(ref _nextJobId);
        var attributes = ToDictionary(request.Attributes);
        var collections = request.Collections;
        var compression = request.GetString("compression");
        var formatMime = request.GetString("document-format");

        var decompressed = Decompress(request.Document.ToArray(), compression, out var decompressionError);
        if (decompressionError is not null)
        {
            return BuildError(request, IppStatus.ClientErrorCompressionError, decompressionError);
        }

        if (decompressed.LongLength > _options.MaxDocumentBytes)
        {
            return BuildError(request, IppStatus.ClientErrorRequestEntityTooLarge, "Document is too large.");
        }

        var context = CreateJobContext(
            jobId, request.OperationId, attributes, collections, decompressed, formatMime, compression);

        IppJobResult result;
        try
        {
            result = await _handler.HandleJobAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Job handler failed for job {JobId}", jobId);
            return BuildError(request, IppStatus.ServerErrorInternalError, ex.Message);
        }

        if (!result.IsSuccess)
        {
            return BuildError(request, result.StatusCode, result.StatusMessage ?? "Job failed.");
        }

        return BuildJobResponse(request, jobId, result.JobState, result.JobStateReasons ?? "job-completed-successfully");
    }

    private IppJobContext CreateJobContext(
        int jobId,
        ushort operation,
        IReadOnlyDictionary<string, IppAttribute> attributes,
        IReadOnlyList<IppCollection> collections,
        byte[] document,
        string? formatMime,
        string? compression)
    {
        var format = DocumentFormatExtensions.FromMimeType(formatMime);
        if (format == DocumentFormat.Unknown)
        {
            format = Sniff(document);
        }

        return new IppJobContext
        {
            JobId = jobId,
            Operation = operation,
            Attributes = attributes,
            Collections = collections,
            Document = document,
            Format = format,
            DocumentFormatMime = formatMime,
            Compression = compression,
        };
    }

    private static Dictionary<string, IppAttribute> MergeAttributes(
        IReadOnlyList<IppAttribute>? baseAttributes,
        IReadOnlyList<IppAttribute> requestAttributes)
    {
        var merged = new Dictionary<string, IppAttribute>(StringComparer.OrdinalIgnoreCase);

        if (baseAttributes is not null)
        {
            foreach (var attribute in baseAttributes)
            {
                merged[attribute.Name] = attribute;
            }
        }

        foreach (var attribute in requestAttributes)
        {
            merged[attribute.Name] = attribute;
        }

        return merged;
    }

    private static Dictionary<string, IppAttribute> ToDictionary(IEnumerable<IppAttribute> attributes)
    {
        var result = new Dictionary<string, IppAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in attributes)
        {
            result[attribute.Name] = attribute;
        }

        return result;
    }

    private static byte[] Decompress(byte[] document, string? compression, out string? error)
    {
        error = null;

        var isGzip = string.Equals(compression, "gzip", StringComparison.OrdinalIgnoreCase) ||
                     (document.Length > 2 && document[0] == 0x1F && document[1] == 0x8B);

        if (!isGzip)
        {
            return document;
        }

        try
        {
            using var input = new MemoryStream(document);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex)
        {
            error = "gzip decompression failed: " + ex.Message;
            return Array.Empty<byte>();
        }
    }

    private static DocumentFormat Sniff(ReadOnlySpan<byte> document)
    {
        if (document.Length == 0)
        {
            return DocumentFormat.Unknown;
        }

        var head = document[..Math.Min(document.Length, 1024)];

        if (head.StartsWith("%PDF-"u8)) return DocumentFormat.Pdf;
        if (head.StartsWith("PwgRaster"u8)) return DocumentFormat.Pwg;
        if (head.StartsWith("%!PS"u8)) return DocumentFormat.Ps;
        if (head.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 })) return DocumentFormat.Oxps;

        return DocumentFormat.Unknown;
    }

    private byte[] BuildJobResponse(IppRequest request, int jobId, int jobState, string reasons)
    {
        var builder = new IppMessageBuilder(
            request.VersionMajor,
            request.VersionMinor,
            IppStatus.SuccessfulOk,
            request.RequestId);

        builder.BeginOperationAttributes();
        builder.BeginGroup(IppTag.JobAttributes);
        builder.AddString(IppTag.Uri, "job-uri", $"{_options.IppUri}/jobs/{jobId}");
        builder.AddInt(IppTag.Integer, "job-id", jobId);
        builder.AddInt(IppTag.Enum, "job-state", jobState);
        builder.AddString(IppTag.Keyword, "job-state-reasons", reasons);

        return builder.End();
    }

    private static byte[] BuildOk(IppRequest request)
    {
        var builder = new IppMessageBuilder(
            request.VersionMajor,
            request.VersionMinor,
            IppStatus.SuccessfulOk,
            request.RequestId);

        builder.BeginOperationAttributes();
        return builder.End();
    }

    private static byte[] BuildError(IppRequest request, ushort status, string? message)
    {
        var builder = new IppMessageBuilder(
            request.VersionMajor,
            request.VersionMinor,
            status,
            request.RequestId);

        builder.BeginOperationAttributes();
        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.AddString(IppTag.TextWithoutLanguage, "status-message", message);
        }

        return builder.End();
    }

    private sealed record PendingJob(IppRequest Request, DateTimeOffset CreatedAt);
}
