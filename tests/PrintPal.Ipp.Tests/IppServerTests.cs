using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using PrintPal.Core;
using PrintPal.Ipp;
using Xunit;

namespace PrintPal.Ipp.Tests;

public sealed class IppServerTests
{
    private sealed class RecordingHandler : IIppJobHandler
    {
        public ConcurrentQueue<IppJobContext> Contexts { get; } = new();

        public IppJobResult Result { get; set; } = IppJobResult.Success();

        public Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken)
        {
            Contexts.Enqueue(context);
            return Task.FromResult(Result);
        }
    }

    private static IppServer CreateServer(RecordingHandler handler, out int port, long maxDocumentBytes = 0)
    {
        port = GetFreePort();
        var options = new IppServerOptions
        {
            Name = "Test Printer",
            HostName = "127.0.0.1",
            Port = port,
            ResourcePath = "/ipp/print",
            BindAddress = IPAddress.Loopback,
            Capabilities = PrinterCapabilities.CreateDefault("Test Printer"),
            MaxDocumentBytes = maxDocumentBytes > 0 ? maxDocumentBytes : 500L * 1024 * 1024,
        };

        return new IppServer(options, handler);
    }

    [Fact]
    public async Task Get_printer_attributes_returns_capabilities()
    {
        var handler = new RecordingHandler();
        await using var server = CreateServer(handler, out _);
        await server.StartAsync();

        using var client = new IppClient();
        var capabilities = await client.GetPrinterAttributesAsync(new Uri(server.IppUri));

        Assert.Equal("Test Printer", capabilities.Name);
        Assert.Contains(DocumentFormat.Pdf, capabilities.DocumentFormats);
        Assert.Contains(PaperSize.A4, capabilities.MediaSizes);
        Assert.Contains(ColorMode.Color, capabilities.ColorModes);
        Assert.Contains(DuplexMode.TwoSidedLongEdge, capabilities.DuplexModes);
        Assert.Equal(1, capabilities.MinCopies);
        Assert.Equal(999, capabilities.MaxCopies);
    }

    [Fact]
    public async Task Print_job_reaches_handler_with_job_attributes()
    {
        var handler = new RecordingHandler();
        await using var server = CreateServer(handler, out _);
        await server.StartAsync();

        var pdf = "%PDF-1.7\n% PrintPal test\n%%EOF"u8.ToArray();
        using var client = new IppClient();
        var result = await client.PrintJobAsync(new IppPrintJobRequest(
            new Uri(server.IppUri),
            "job-name",
            "tester",
            DocumentFormat.Pdf,
            pdf,
            Copies: 3,
            PaperSize: PaperSize.A4,
            ColorMode: ColorMode.Color,
            DuplexMode: DuplexMode.TwoSidedLongEdge));

        Assert.True(result.IsSuccess);
        Assert.Equal(IppJobState.Completed, result.JobState);
        Assert.True(result.JobId > 0);

        var context = Assert.Single(handler.Contexts);
        Assert.Equal(3, context.GetInt("copies"));
        Assert.Equal("two-sided-long-edge", context.GetString("sides"));
        Assert.Equal("color", context.GetString("print-color-mode"));
        Assert.Equal("iso_a4_210x297mm", context.GetString("media"));
        Assert.Equal(DocumentFormat.Pdf, context.Format);
        Assert.Equal(pdf, context.Document);
    }

    [Fact]
    public async Task Create_job_and_send_document_with_gzip_reaches_handler()
    {
        var handler = new RecordingHandler();
        await using var server = CreateServer(handler, out _);
        await server.StartAsync();

        var pdf = "%PDF-1.7\n% Create-Job/Send-Document test\n%%EOF"u8.ToArray();

        var create = new IppMessageBuilder(1, 1, IppOperation.CreateJob, 1);
        create.BeginOperationAttributes();
        create.AddString(IppTag.Uri, "printer-uri", server.IppUri);
        create.AddString(IppTag.NameWithoutLanguage, "requesting-user-name", "tester");
        create.AddString(IppTag.NameWithoutLanguage, "job-name", "create-send");
        create.BeginGroup(IppTag.JobAttributes);
        create.AddInt(IppTag.Integer, "copies", 2);
        create.AddString(IppTag.Keyword, "sides", "two-sided-long-edge");
        create.AddString(IppTag.Keyword, "print-color-mode", "color");
        create.AddString(IppTag.Keyword, "media", "iso_a4_210x297mm");

        var createResponse = await PostAsync(server, create.End());
        Assert.Equal(IppStatus.SuccessfulOk, createResponse.StatusCode);
        Assert.Equal(IppJobState.Pending, createResponse.GetInt("job-state"));

        var jobId = createResponse.GetInt("job-id");
        Assert.True(jobId > 0);

        var gzip = Gzip(pdf);

        var send = new IppMessageBuilder(1, 1, IppOperation.SendDocument, 2);
        send.BeginOperationAttributes();
        send.AddString(IppTag.Uri, "printer-uri", server.IppUri);
        send.AddString(IppTag.NameWithoutLanguage, "requesting-user-name", "tester");
        send.AddInt(IppTag.Integer, "job-id", jobId);
        send.AddString(IppTag.MimeMediaType, "document-format", "application/pdf");
        send.AddString(IppTag.Keyword, "compression", "gzip");
        send.AddBool(IppTag.Boolean, "last-document", true);

        var body = send.End().Concat(gzip).ToArray();
        var sendResponse = await PostAsync(server, body);

        Assert.Equal(IppStatus.SuccessfulOk, sendResponse.StatusCode);
        Assert.Equal(IppJobState.Completed, sendResponse.GetInt("job-state"));

        var context = Assert.Single(handler.Contexts);
        Assert.Equal(2, context.GetInt("copies"));
        Assert.Equal("two-sided-long-edge", context.GetString("sides"));
        Assert.Equal("color", context.GetString("print-color-mode"));
        Assert.Equal(DocumentFormat.Pdf, context.Format);
        Assert.Equal(pdf, context.Document);
        Assert.Equal("gzip", context.Compression);
    }

    [Fact]
    public async Task Handler_failure_becomes_ipp_error()
    {
        var handler = new RecordingHandler
        {
            Result = IppJobResult.Failure(IppStatus.ServerErrorDeviceError, "printer offline"),
        };

        await using var server = CreateServer(handler, out _);
        await server.StartAsync();

        using var client = new IppClient();
        var exception = await Assert.ThrowsAsync<IppException>(() => client.PrintJobAsync(new IppPrintJobRequest(
            new Uri(server.IppUri),
            "fail",
            "tester",
            DocumentFormat.Pdf,
            "%PDF-1.7\n%%EOF"u8.ToArray())));

        Assert.Equal(IppStatus.ServerErrorDeviceError, exception.StatusCode);
    }

    [Fact]
    public async Task Oversized_document_is_rejected_with_413()
    {
        var handler = new RecordingHandler();
        await using var server = CreateServer(handler, out _, maxDocumentBytes: 128);
        await server.StartAsync();

        using var client = new IppClient();
        var exception = await Assert.ThrowsAsync<IppException>(() => client.PrintJobAsync(new IppPrintJobRequest(
            new Uri(server.IppUri),
            "too-large",
            "tester",
            DocumentFormat.Pdf,
            new byte[512])));

        Assert.Equal(IppStatus.ClientErrorRequestEntityTooLarge, exception.StatusCode);
        Assert.Empty(handler.Contexts);
    }

    private static async Task<IppResponse> PostAsync(IppServer server, byte[] body)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");

        using var response = await http.PostAsync(IppUri.ToHttp(new Uri(server.IppUri)), content);
        response.EnsureSuccessStatusCode();

        return IppResponse.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
