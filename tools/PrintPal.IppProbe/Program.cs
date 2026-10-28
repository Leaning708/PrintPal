using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintPal.IppProbe.Capture;
using PrintPal.IppProbe.Ipp;

namespace PrintPal.IppProbe;

/// <summary>
/// Diagnostic IPP virtual-printer probe for the P0 feasibility gate.
///
/// It deliberately does not print anything. It accepts the job, saves the rendered
/// document exactly as Windows sent it, logs every IPP attribute, and counts pages
/// from the document. This answers the only questions that matter before building
/// PrintPal: what does the Windows IPP Class Driver actually send, and can we read
/// page count / copies / sides / media / colour from it?
/// </summary>
internal static class Program
{
    private static readonly object LogLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly Dictionary<int, DateTimeOffset> Jobs = new();
    private static readonly ConcurrentDictionary<int, IppRequest> PendingJobs = new();

    private static string _captureDir = Path.Combine(AppContext.BaseDirectory, "capture");
    private static string _httpUrl = "http://127.0.0.1:8630/ipp/print";
    private static string _ippUri = "ipp://127.0.0.1:8630/ipp/print";
    private static int _nextJobId;

    public static async Task<int> Main(string[] args)
    {
        var port = 8630;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i]);
                    break;
                case "--capture" when i + 1 < args.Length:
                    _captureDir = Path.GetFullPath(args[++i]);
                    break;
            }
        }

        Directory.CreateDirectory(_captureDir);
        _httpUrl = $"http://127.0.0.1:{port}/ipp/print";
        _ippUri = $"ipp://127.0.0.1:{port}/ipp/print";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
        });

        var app = builder.Build();

        // Log every non-IPP HTTP request. If the WSD/IPP port monitor uses a
        // different path (WS-Print/SOAP) for the actual job, this is how we find out.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;

            if (!path.Equals("/health", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/ipp/print", StringComparison.OrdinalIgnoreCase))
            {
                Log($"HTTP {context.Request.Method} {path} " +
                    $"ct={context.Request.ContentType ?? "-"} len={context.Request.ContentLength?.ToString() ?? "-"} " +
                    $"soap={context.Request.Headers["SOAPAction"].ToString()}" +
                    $"userAgent={context.Request.Headers["User-Agent"].ToString()}");

                if (context.Request.ContentLength is > 0 and <= 1_048_576)
                {
                    context.Request.EnableBuffering();
                    using var reader = new StreamReader(
                        context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                    var text = await reader.ReadToEndAsync();
                    context.Request.Body.Position = 0;
                    Log($"  BODY[{text.Length}]: {text[..Math.Min(600, text.Length)]}");
                }
            }

            await next();
        });

        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            url = _httpUrl,
            ippUri = _ippUri,
            capture = _captureDir,
            jobs = Jobs.Count,
            time = DateTimeOffset.Now,
        }));

        app.MapPost("/ipp/print", HandleIppAsync);
        app.MapGet("/ipp/print", (HttpContext context) =>
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        });

        Console.WriteLine($"PrintPal IPP probe listening on {_httpUrl}");
        Console.WriteLine($"IPP URI advertised to Windows: {_ippUri}");
        Console.WriteLine($"Capture directory: {_captureDir}");
        Console.WriteLine("Add the queue, then print from Notepad/Word/WPS. Ctrl+C to stop.");

        await app.RunAsync();
        return 0;
    }

    private static async Task HandleIppAsync(HttpContext context)
    {
        byte[] body;

        using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = buffer.ToArray();
        }

        if (body.Length < 8)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        IppRequest request;

        try
        {
            request = IppRequest.Parse(body);
        }
        catch (Exception ex)
        {
            Log($"PARSE FAILED bytes={body.Length}: {ex.GetType().Name}: {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        LogRequest(request);

        byte[] response;

        try
        {
            response = request.OperationId switch
            {
                0x0002 => HandlePrintJob(request),
                0x0004 => BuildOk(request),
                0x0005 => HandleCreateJob(request),
                0x0006 => HandleSendDocument(request),
                0x0008 => HandleCancelJob(request),
                0x0009 => HandleGetJob(request),
                0x000B => BuildPrinterAttributes(request),
                _ => BuildError(request, 0x0501, "operation not supported"),
            };
        }
        catch (Exception ex)
        {
            Log($"OPERATION {request.OperationNameText} FAILED: {ex.GetType().Name}: {ex.Message}");
            response = BuildError(request, 0x0500, ex.Message);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/ipp";
        context.Response.ContentLength = response.Length;
        context.Response.Headers["Server"] = "PrintPal-IppProbe";

        try
        {
            await context.Response.Body.WriteAsync(response, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Client went away; the job (if any) is already saved.
        }
    }

    // ------------------------------------------------------------------ operations

    private static byte[] HandlePrintJob(IppRequest request)
    {
        var jobId = Interlocked.Increment(ref _nextJobId);
        Jobs[jobId] = DateTimeOffset.Now;

        var saved = SaveDocument(request, request, jobId, "print-job");
        LogJobSummary(request, request, jobId, saved);

        // completed = 9. The probe has already durably saved everything it needs.
        return BuildJobResponse(request, jobId, 9, "job-completed-successfully");
    }

    private static byte[] HandleCreateJob(IppRequest request)
    {
        var jobId = Interlocked.Increment(ref _nextJobId);
        Jobs[jobId] = DateTimeOffset.Now;

        // Windows uses Create-Job + Send-Document. The job attributes (copies,
        // sides, media-col, colour) arrive here; the document arrives later.
        PendingJobs[jobId] = request;

        LogJobSummary(request, request, jobId, null);

        // pending = 3; the document arrives in Send-Document.
        return BuildJobResponse(request, jobId, 3, "job-incoming");
    }

    private static byte[] HandleSendDocument(IppRequest request)
    {
        var jobId = request.GetInt("job-id", 0);
        if (jobId <= 0)
        {
            jobId = Interlocked.Increment(ref _nextJobId);
        }

        Jobs[jobId] = DateTimeOffset.Now;

        PendingJobs.TryRemove(jobId, out var jobContext);
        jobContext ??= request;

        var saved = SaveDocument(request, jobContext, jobId, "send-document");
        LogJobSummary(jobContext, request, jobId, saved);

        var last = request.Get("last-document");
        var isLast = last is null || last.Values.Count == 0 || last.Values[0].Length == 0 || last.Values[0][0] != 0;

        return BuildJobResponse(request, jobId, isLast ? 9 : 5, isLast ? "job-completed-successfully" : "job-in-progress");
    }

    private static byte[] HandleGetJob(IppRequest request)
    {
        var jobId = request.GetInt("job-id", 0);
        return BuildJobResponse(request, jobId, 9, "job-completed-successfully");
    }

    private static byte[] HandleCancelJob(IppRequest request) => BuildOk(request);

    // ------------------------------------------------------------------ responses

    private static byte[] BuildPrinterAttributes(IppRequest request)
    {
        var builder = new IppResponseBuilder(request.VersionMajor, request.VersionMinor, 0x0000, request.RequestId);
        BeginOperationAttributes(builder);

        builder.BeginGroup(IppTag.PrinterAttributes);

        AddStrings(builder, IppTag.Uri, "printer-uri-supported", _ippUri, _httpUrl);
        AddStrings(builder, IppTag.Keyword, "uri-authentication-supported", "none", "none");
        AddStrings(builder, IppTag.Keyword, "uri-security-supported", "none", "none");

        builder.AddString(IppTag.NameWithoutLanguage, "printer-name", "PrintPal Virtual Printer");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-info", "PrintPal Virtual Printer (IPP probe)");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-location", "Local");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-make-and-model", "PrintPal Virtual Printer");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-state-message", "Ready");

        builder.AddInt(IppTag.Enum, "printer-state", 3);
        builder.AddString(IppTag.Keyword, "printer-state-reasons", "none");
        builder.AddBool(IppTag.Boolean, "printer-is-accepting-jobs", true);
        builder.AddInt(IppTag.Integer, "queued-job-count", 0);

        builder.AddString(IppTag.Keyword, "ipp-versions-supported", "1.1");
        builder.AddString(IppTag.Keyword, "", "2.0");

        AddInts(builder, IppTag.Enum, "operations-supported",
            0x0002, // Print-Job
            0x0004, // Validate-Job
            0x0005, // Create-Job
            0x0006, // Send-Document
            0x0008, // Cancel-Job
            0x0009, // Get-Job-Attributes
            0x000B);// Get-Printer-Attributes

        builder.AddString(IppTag.Charset, "charset-configured", "utf-8");
        builder.AddString(IppTag.Charset, "charset-supported", "utf-8");
        builder.AddString(IppTag.NaturalLanguage, "natural-language-configured", "en");
        builder.AddString(IppTag.NaturalLanguage, "generated-natural-language-supported", "en");
        builder.AddString(IppTag.NaturalLanguage, "", "zh-cn");

        builder.AddString(IppTag.MimeMediaType, "document-format-default", "application/pdf");
        builder.AddString(IppTag.MimeMediaType, "document-format-preferred", "application/pdf");
        AddStrings(builder, IppTag.MimeMediaType, "document-format-supported",
            "application/pdf",
            "image/pwg-raster",
            "application/octet-stream");

        builder.AddString(IppTag.Keyword, "media-default", "iso_a4_210x297mm");
        AddStrings(builder, IppTag.Keyword, "media-supported",
            "iso_a4_210x297mm",
            "iso_a3_297x420mm",
            "iso_a5_148x210mm",
            "na_letter_8.5x11in",
            "na_legal_8.5x14in");
        AddStrings(builder, IppTag.Keyword, "media-ready",
            "iso_a4_210x297mm",
            "iso_a3_297x420mm",
            "iso_a5_148x210mm",
            "na_letter_8.5x11in",
            "na_legal_8.5x14in");
        AddStrings(builder, IppTag.Keyword, "media-type-supported", "stationery");
        AddStrings(builder, IppTag.Keyword, "media-source-supported", "auto", "main", "tray-1");
        AddStrings(builder, IppTag.Keyword, "media-col-supported",
            "media-size",
            "media-type",
            "media-source",
            "media-top-margin",
            "media-bottom-margin",
            "media-left-margin",
            "media-right-margin");

        AddMediaSizeSupported(builder);

        // Windows' IPP parser expects these as plain integers (see the
        // "GetIntegerValue (media-left-margin-supported) - not an integer type"
        // error in the Application log when they are encoded as ranges).
        builder.AddInt(IppTag.Integer, "media-left-margin-supported", 4233);
        builder.AddInt(IppTag.Integer, "media-right-margin-supported", 4233);
        builder.AddInt(IppTag.Integer, "media-top-margin-supported", 4233);
        builder.AddInt(IppTag.Integer, "media-bottom-margin-supported", 4233);

        builder.AddString(IppTag.Keyword, "print-color-mode-default", "monochrome");
        AddStrings(builder, IppTag.Keyword, "print-color-mode-supported", "monochrome", "color");
        builder.AddBool(IppTag.Boolean, "color-supported", true);

        builder.AddString(IppTag.Keyword, "sides-default", "one-sided");
        AddStrings(builder, IppTag.Keyword, "sides-supported",
            "one-sided",
            "two-sided-long-edge",
            "two-sided-short-edge");

        builder.AddInt(IppTag.Integer, "copies-default", 1);
        builder.AddRange(IppTag.RangeOfInteger, "copies-supported", 1, 999);

        builder.AddInt(IppTag.Enum, "orientation-requested-default", 3);
        AddInts(builder, IppTag.Enum, "orientation-requested-supported", 3, 4, 5, 6);

        builder.AddInt(IppTag.Enum, "print-quality-default", 4);
        AddInts(builder, IppTag.Enum, "print-quality-supported", 3, 4, 5);

        builder.AddResolution(IppTag.Resolution, "printer-resolution-default", 600, 600, 3);
        builder.AddResolution(IppTag.Resolution, "printer-resolution-supported", 600, 600, 3);

        builder.AddResolution(IppTag.Resolution, "pwg-raster-document-resolution-supported", 600, 600, 3);
        AddStrings(builder, IppTag.Keyword, "pwg-raster-document-type-supported", "sgray_8", "srgb_8");
        builder.AddString(IppTag.Keyword, "pwg-raster-document-sheet-back", "normal");

        builder.AddString(IppTag.Keyword, "pclm-raster-back-side", "normal");
        builder.AddResolution(IppTag.Resolution, "pclm-source-resolution-supported", 600, 600, 3);

        builder.AddBool(IppTag.Boolean, "pdf-fit-to-page-default", false);
        AddBools(builder, IppTag.Boolean, "pdf-fit-to-page-supported", false, true);
        builder.AddRange(IppTag.RangeOfInteger, "pdf-k-octets-supported", 0, 500000);

        builder.AddString(IppTag.Keyword, "print-scaling-default", "auto");
        AddStrings(builder, IppTag.Keyword, "print-scaling-supported", "auto", "auto-fit", "fit", "fill", "none");

        builder.AddString(IppTag.Keyword, "multiple-document-handling-default", "separate-documents-uncollated-copies");
        AddStrings(builder, IppTag.Keyword, "multiple-document-handling-supported",
            "separate-documents-collated-copies",
            "separate-documents-uncollated-copies",
            "single-document");

        builder.AddInt(IppTag.Integer, "number-up-default", 1);
        AddInts(builder, IppTag.Integer, "number-up-supported", 1, 2, 4, 6, 9, 16);
        builder.AddString(IppTag.Keyword, "presentation-direction-number-up-default", "to-bottom-to-right");
        AddStrings(builder, IppTag.Keyword, "presentation-direction-number-up-supported",
            "to-bottom-to-right",
            "to-top-to-right");

        builder.AddInt(IppTag.Integer, "pages-per-minute", 20);
        builder.AddInt(IppTag.Integer, "pages-per-minute-color", 10);

        builder.AddString(IppTag.Keyword, "output-bin-default", "auto");
        AddStrings(builder, IppTag.Keyword, "output-bin-supported", "auto", "face-down");
        builder.AddString(IppTag.Keyword, "media-source-default", "auto");
        // finishings is an enum (3 = none), not a keyword.
        builder.AddInt(IppTag.Enum, "finishings-default", 3);
        AddInts(builder, IppTag.Enum, "finishings-supported", 3);

        builder.AddString(IppTag.Keyword, "pdl-override-supported", "attempted");
        AddStrings(builder, IppTag.Keyword, "compression-supported", "none", "gzip");

        AddStrings(builder, IppTag.Keyword, "job-creation-attributes-supported",
            "copies",
            "media",
            "sides",
            "print-color-mode",
            "print-quality",
            "printer-resolution",
            "job-name",
            "requesting-user-name",
            "document-format");

        builder.AddBool(IppTag.Boolean, "multiple-document-jobs-supported", false);
        builder.AddBool(IppTag.Boolean, "job-ids-supported", true);
        AddStrings(builder, IppTag.Keyword, "which-jobs-supported", "completed", "not-completed", "all");

        builder.AddRange(IppTag.RangeOfInteger, "job-impressions-supported", 1, 9999);
        builder.AddRange(IppTag.RangeOfInteger, "job-media-sheets-supported", 1, 9999);
        builder.AddRange(IppTag.RangeOfInteger, "job-pages-per-set-supported", 1, 9999);
        builder.AddInt(IppTag.Integer, "job-password-supported", 0);
        AddStrings(builder, IppTag.Keyword, "job-password-encryption-supported", "none");

        AddStrings(builder, IppTag.Keyword, "printer-mandatory-job-attributes",
            "attributes-charset",
            "attributes-natural-language");
        builder.AddBool(IppTag.Boolean, "client-info-supported", false);
        // Windows asks for this as a string/keyword list, not a boolean.
        AddStrings(builder, IppTag.Keyword, "document-format-details-supported",
            "document-format",
            "document-format-version",
            "document-format-device-id",
            "document-natural-language",
            "compression");
        builder.AddString(IppTag.Keyword, "mopria-certified", "1.3");
        AddStrings(builder, IppTag.NaturalLanguage, "printer-strings-languages-supported", "en", "zh-cn");
        builder.AddString(IppTag.NameWithoutLanguage, "printer-dns-sd-name", "PrintPal Virtual Printer");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-firmware-name", "PrintPal");
        builder.AddString(IppTag.TextWithoutLanguage, "printer-firmware-string-version", "0.1");
        builder.AddString(IppTag.Uri, "printer-more-info", _httpUrl);
        builder.AddString(IppTag.TextWithoutLanguage, "printer-device-id", "MFG:PrintPal;MDL:Virtual Printer;CMD:PDF,PWG;");
        builder.AddString(IppTag.Uri, "printer-uuid", "urn:uuid:4f7f2e6a-7d7b-4c1e-9f0a-0e6b1c2d3e4f");

        return builder.End();
    }

    private static byte[] BuildJobResponse(IppRequest request, int jobId, int jobState, string reasons)
    {
        var builder = new IppResponseBuilder(request.VersionMajor, request.VersionMinor, 0x0000, request.RequestId);
        BeginOperationAttributes(builder);
        builder.BeginGroup(IppTag.JobAttributes);
        builder.AddString(IppTag.Uri, "job-uri", $"{_ippUri}/jobs/{jobId}");
        builder.AddInt(IppTag.Integer, "job-id", jobId);
        builder.AddInt(IppTag.Enum, "job-state", jobState);
        builder.AddString(IppTag.Keyword, "job-state-reasons", reasons);
        return builder.End();
    }

    private static byte[] BuildOk(IppRequest request)
    {
        var builder = new IppResponseBuilder(request.VersionMajor, request.VersionMinor, 0x0000, request.RequestId);
        BeginOperationAttributes(builder);
        return builder.End();
    }

    private static byte[] BuildError(IppRequest request, ushort status, string message)
    {
        var builder = new IppResponseBuilder(request.VersionMajor, request.VersionMinor, status, request.RequestId);
        BeginOperationAttributes(builder);
        builder.AddString(IppTag.TextWithoutLanguage, "status-message", message);
        return builder.End();
    }

    private static void BeginOperationAttributes(IppResponseBuilder builder)
    {
        builder.BeginGroup(IppTag.OperationAttributes);
        builder.AddString(IppTag.Charset, "attributes-charset", "utf-8");
        builder.AddString(IppTag.NaturalLanguage, "attributes-natural-language", "en");
    }

    private static void AddStrings(IppResponseBuilder builder, IppTag tag, string name, params string[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            builder.AddString(tag, i == 0 ? name : string.Empty, values[i]);
        }
    }

    private static void AddInts(IppResponseBuilder builder, IppTag tag, string name, params int[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            builder.AddInt(tag, i == 0 ? name : string.Empty, values[i]);
        }
    }

    private static void AddBools(IppResponseBuilder builder, IppTag tag, string name, params bool[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            builder.AddBool(tag, i == 0 ? name : string.Empty, values[i]);
        }
    }

    /// <summary>
    /// media-size-supported is an IPP collection list. Dimensions are in 1/100 mm.
    /// The IPP Class Driver uses this to populate media choices and to size the
    /// rendered output; without it the queue exists but jobs can stall.
    /// </summary>
    private static void AddMediaSizeSupported(IppResponseBuilder builder)
    {
        var sizes = new (int Width, int Height)[]
        {
            (21000, 29700), // A4
            (29700, 42000), // A3
            (14800, 21000), // A5
            (21590, 27940), // Letter
            (21590, 35560), // Legal
        };

        for (var i = 0; i < sizes.Length; i++)
        {
            builder.BeginCollection(IppTag.BegCollection, i == 0 ? "media-size-supported" : string.Empty);
            builder.AddCollectionMemberInt("x-dimension", sizes[i].Width);
            builder.AddCollectionMemberInt("y-dimension", sizes[i].Height);
            builder.EndCollection();
        }
    }

    // ------------------------------------------------------------------ capture + logging

    private static SavedDocument? SaveDocument(
        IppRequest documentRequest,
        IppRequest jobContext,
        int jobId,
        string operation)
    {
        var rawDocument = documentRequest.Document;

        if (rawDocument.Length == 0)
        {
            Log($"JOB {jobId} {operation}: no document data (attributes only)");
            return null;
        }

        var compression = documentRequest.GetString("compression") ?? string.Empty;
        var documentBytes = DecompressIfNeeded(rawDocument.ToArray(), compression, out var compressionNote);

        var format = DocumentSniffer.Sniff(documentBytes);
        if (format == DocumentFormat.Unknown)
        {
            format = FormatFromAttribute(documentRequest.GetString("document-format"));
        }

        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{operation}-job{jobId}{format.Extension()}";
        var path = Path.Combine(_captureDir, fileName);

        File.WriteAllBytes(path, documentBytes);

        var hash = Convert.ToHexString(SHA256.HashData(documentBytes)).ToLowerInvariant();
        var pageCount = PageCounters.Count(path, format);
        var paper = ResolvePaper(jobContext);

        var saved = new SavedDocument(path, format, documentBytes.Length, hash, pageCount);
        var record = new
        {
            time = DateTimeOffset.Now,
            operation,
            jobId,
            paper,
            compression = compressionNote,
            jobAttributes = DescribeAttributes(jobContext),
            documentAttributes = DescribeAttributes(documentRequest),
            collections = DescribeCollections(jobContext),
            document = new
            {
                format = format.DisplayName(),
                saved = path,
                bytes = documentBytes.Length,
                compressedBytes = rawDocument.Length,
                sha256 = hash,
                pages = pageCount.Pages,
                pageDetails = pageCount.Details,
                pwg = pageCount.Pwg,
                firstBytesHex = Convert.ToHexString(documentBytes[..Math.Min(32, documentBytes.Length)]),
            },
        };

        AppendJson("jobs.jsonl", record);
        Log($"JOB {jobId} {operation}: format={format.DisplayName()} " +
            $"bytes={documentBytes.Length:N0} (raw {rawDocument.Length:N0}) pages={pageCount.Pages} " +
            $"paper={paper} compression={compressionNote} saved={fileName}");

        return saved;
    }

    private static void LogJobSummary(
        IppRequest jobContext,
        IppRequest documentRequest,
        int jobId,
        SavedDocument? saved)
    {
        var jobName = jobContext.GetString("job-name") ?? "(no job-name)";
        var copies = jobContext.GetInt("copies", 1);
        var sides = jobContext.GetString("sides") ?? "(none)";
        var color = jobContext.GetString("print-color-mode") ?? "(none)";
        var paper = ResolvePaper(jobContext);
        var pages = saved?.PageCount.Pages ?? 0;
        var format = documentRequest.GetString("document-format") ?? "(none)";
        var compression = documentRequest.GetString("compression") ?? "(none)";

        Console.WriteLine(
            $"  job={jobId} operation={documentRequest.OperationNameText} name=\"{jobName}\" " +
            $"copies={copies} sides={sides} color={color} paper={paper} " +
            $"doc={saved?.Format.DisplayName() ?? format} bytes={saved?.Bytes ?? 0:N0} pages={pages} " +
            $"formatAttr={format} compression={compression}");
    }

    private static byte[] DecompressIfNeeded(byte[] raw, string compression, out string note)
    {
        var isGzip = compression.Equals("gzip", StringComparison.OrdinalIgnoreCase) ||
                     (raw.Length > 2 && raw[0] == 0x1F && raw[1] == 0x8B);

        if (!isGzip)
        {
            note = string.Empty;
            return raw;
        }

        try
        {
            using var input = new MemoryStream(raw);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            var bytes = output.ToArray();
            note = $"gzip {raw.Length}->{bytes.Length}";
            return bytes;
        }
        catch (Exception ex)
        {
            note = $"gzip FAILED: {ex.GetType().Name}: {ex.Message}";
            return raw;
        }
    }

    private static DocumentFormat FormatFromAttribute(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DocumentFormat.Unknown;
        }

        var v = value.ToLowerInvariant();
        if (v.Contains("pdf", StringComparison.Ordinal)) return DocumentFormat.Pdf;
        if (v.Contains("pwg", StringComparison.Ordinal)) return DocumentFormat.Pwg;
        if (v.Contains("postscript", StringComparison.Ordinal)) return DocumentFormat.Ps;
        if (v.Contains("oxps", StringComparison.Ordinal) || v.Contains("xps", StringComparison.Ordinal)) return DocumentFormat.Oxps;
        if (v.Contains("pcl", StringComparison.Ordinal)) return DocumentFormat.Pcl;
        return DocumentFormat.Unknown;
    }

    /// <summary>
    /// Resolves the paper size from media or from the media-col/media-size
    /// collection. Windows' IPP Class Driver uses the collection, not the media
    /// keyword, for the normal job path.
    /// </summary>
    private static string ResolvePaper(IppRequest request)
    {
        var media = request.GetString("media");
        if (!string.IsNullOrWhiteSpace(media))
        {
            return media;
        }

        var mediaCol = request.GetCollection("media-col");
        var mediaSize = mediaCol?.GetCollection("media-size");
        var x = mediaSize?.GetInt("x-dimension", 0) ?? 0;
        var y = mediaSize?.GetInt("y-dimension", 0) ?? 0;

        if (x > 0 && y > 0)
        {
            var width = Math.Min(x, y);
            var height = Math.Max(x, y);
            return $"{PaperName(width, height)} ({x}x{y}/100mm)";
        }

        return "(unknown)";
    }

    private static string PaperName(int width, int height)
    {
        var sizes = new (string Name, int Width, int Height)[]
        {
            ("A4", 21000, 29700),
            ("A3", 29700, 42000),
            ("A5", 14800, 21000),
            ("Letter", 21590, 27940),
            ("Legal", 21590, 35560),
        };

        foreach (var size in sizes)
        {
            if (Math.Abs(width - size.Width) <= 300 && Math.Abs(height - size.Height) <= 300)
            {
                return size.Name;
            }
        }

        return "Custom";
    }

    private static object[] DescribeCollections(IppRequest request) =>
        request.Collections.Select(DescribeCollection).ToArray();

    private static object DescribeCollection(IppCollection collection) => new
    {
        name = collection.Name,
        members = collection.Members.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(a => DescribeValue(a.Tag, a.Values[0])).ToArray()),
        collections = collection.Collections.Select(DescribeCollection).ToArray(),
    };

    private static Dictionary<string, string[]> DescribeAttributes(IppRequest request)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in request.Attributes
                     .Where(a => !string.IsNullOrEmpty(a.Name))
                     .GroupBy(a => a.Name))
        {
            result[group.Key] = group
                .SelectMany(a => a.Values.Select(value => DescribeValue(a.Tag, value)))
                .ToArray();
        }

        return result;
    }

    private static string DescribeValue(IppTag tag, byte[] value)
    {
        try
        {
            switch (tag)
            {
                case IppTag.Integer:
                case IppTag.Enum:
                    return value.Length == 4
                        ? System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(value).ToString()
                        : $"<{value.Length} bytes>";

                case IppTag.Boolean:
                    return value.Length == 1 && value[0] != 0 ? "true" : "false";

                case IppTag.RangeOfInteger:
                    return value.Length == 8
                        ? $"{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(0, 4))}.." +
                          $"{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(4, 4))}"
                        : $"<{value.Length} bytes>";

                case IppTag.Resolution:
                    return value.Length == 9
                        ? $"{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(0, 4))}x" +
                          $"{System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(value.AsSpan(4, 4))}dpi"
                        : $"<{value.Length} bytes>";

                case IppTag.TextWithoutLanguage:
                case IppTag.NameWithoutLanguage:
                case IppTag.Keyword:
                case IppTag.Uri:
                case IppTag.UriScheme:
                case IppTag.Charset:
                case IppTag.NaturalLanguage:
                case IppTag.MimeMediaType:
                    {
                        var text = Encoding.UTF8.GetString(value);
                        return text.Length <= 200 ? text : text[..200] + "...";
                    }

                case IppTag.OctetString:
                    return Convert.ToHexString(value[..Math.Min(32, value.Length)]);

                default:
                    {
                        var text = Encoding.UTF8.GetString(value);
                        return text.Length <= 200 ? text : text[..200] + "...";
                    }
            }
        }
        catch
        {
            return $"<{value.Length} bytes>";
        }
    }

    private static void LogRequest(IppRequest request)
    {
        var record = new
        {
            time = DateTimeOffset.Now,
            operation = request.OperationNameText,
            operationId = $"0x{request.OperationId:X4}",
            version = $"{request.VersionMajor}.{request.VersionMinor}",
            requestId = request.RequestId,
            documentBytes = request.DocumentLength,
            attributes = DescribeAttributes(request),
            collections = DescribeCollections(request),
        };

        AppendJson("requests.jsonl", record);

        if (request.OperationId != 0x000B)
        {
            Console.WriteLine(
                $"REQUEST {request.OperationNameText} 0x{request.OperationId:X4} " +
                $"requestId={request.RequestId} attributes={request.Attributes.Count} " +
                $"docBytes={request.DocumentLength:N0}");
        }
    }

    private static void AppendJson(string fileName, object value)
    {
        try
        {
            var line = JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(_captureDir, fileName), line, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LOG WRITE FAILED ({fileName}): {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";

        Console.WriteLine(line);

        try
        {
            lock (LogLock)
            {
                File.AppendAllText(Path.Combine(_captureDir, "probe.log"), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never take the probe down.
        }
    }

    private sealed record SavedDocument(
        string Path,
        DocumentFormat Format,
        int Bytes,
        string Sha256,
        PageCountResult PageCount);
}
