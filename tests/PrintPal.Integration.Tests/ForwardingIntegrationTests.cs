using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PrintPal.Core;
using PrintPal.Forwarding;
using PrintPal.Ipp;
using Xunit;

namespace PrintPal.Integration.Tests;

public sealed class ForwardingIntegrationTests
{
    private sealed class RecordingHandler : IIppJobHandler
    {
        public ConcurrentQueue<IppJobContext> Contexts { get; } = new();

        public Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken)
        {
            Contexts.Enqueue(context);
            return Task.FromResult(IppJobResult.Success());
        }
    }

    private static IppServer CreateServer(RecordingHandler handler, out int port)
    {
        port = GetFreePort();
        return new IppServer(
            new IppServerOptions
            {
                Name = "Integration Fake",
                HostName = "127.0.0.1",
                Port = port,
                BindAddress = IPAddress.Loopback,
                Capabilities = PrinterCapabilities.CreateDefault("Integration Fake"),
            },
            handler);
    }

    [Fact]
    public async Task Ipp_forwarder_sends_document_and_job_attributes()
    {
        var handler = new RecordingHandler();
        await using var server = CreateServer(handler, out var port);
        await server.StartAsync();

        var printer = new Printer
        {
            Name = "Fake",
            Protocol = PrinterProtocol.Ipp,
            Host = "127.0.0.1",
            Port = port,
            ResourcePath = "/ipp/print",
            Capabilities = PrinterCapabilities.CreateDefault("Fake"),
        };

        var pdf = "%PDF-1.7\n% forwarder test\n%%EOF"u8.ToArray();
        var job = new PrintJob
        {
            DocumentName = "report.pdf",
            UserName = "tester",
            DocumentFormat = DocumentFormat.Pdf,
            PageCount = 5,
            Copies = 3,
            PaperSize = PaperSize.A4,
            ColorMode = ColorMode.Color,
            DuplexMode = DuplexMode.TwoSidedLongEdge,
        };

        var forwarder = new PrintJobForwarder();
        var result = await forwarder.ForwardAsync(job, printer, pdf);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.IppResult);
        Assert.True(result.IppResult!.JobId > 0);
        Assert.Equal(IppJobState.Completed, result.IppResult.JobState);

        var context = Assert.Single(handler.Contexts);
        Assert.Equal(3, context.GetCopies());
        Assert.Equal(DuplexMode.TwoSidedLongEdge, context.GetDuplexMode());
        Assert.Equal(ColorMode.Color, context.GetColorMode());
        Assert.Equal(PaperSize.A4, context.GetPaperSize());
        Assert.Equal(DocumentFormat.Pdf, context.Format);
        Assert.Equal(pdf, context.Document);
    }

    [Fact]
    public async Task Raw_forwarder_writes_document_to_tcp_stream()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var acceptTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            received.TrySetResult(buffer.ToArray());
        });

        try
        {
            var printer = new Printer
            {
                Name = "Raw Fake",
                Protocol = PrinterProtocol.Raw,
                Host = "127.0.0.1",
                Port = port,
            };

            var document = "RAW-PRINT-DATA"u8.ToArray();
            var job = new PrintJob { DocumentFormat = DocumentFormat.Ps };

            var result = await new Raw9100Forwarder().ForwardAsync(job, printer, document);
            var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.Success, result.Message);
            Assert.Equal(document, actual);
            await acceptTask;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Ipp_forwarder_reports_a_connection_failure()
    {
        var port = GetFreePort();
        var printer = new Printer
        {
            Name = "Offline",
            Protocol = PrinterProtocol.Ipp,
            Host = "127.0.0.1",
            Port = port,
            Capabilities = PrinterCapabilities.CreateDefault("Offline"),
        };

        var job = new PrintJob
        {
            DocumentName = "offline.pdf",
            UserName = "tester",
            DocumentFormat = DocumentFormat.Pdf,
            Copies = 1,
            PageCount = 1,
        };

        var result = await new IppForwarder().ForwardAsync(
            job, printer, "%PDF-1.7\n%%EOF"u8.ToArray());

        Assert.False(result.Success);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Capture_price_route_forward_pipeline()
    {
        var handler = new RecordingHandler();
        await using var server = CreateServer(handler, out var port);
        await server.StartAsync();

        var printer = new Printer
        {
            Name = "Fake A4 Colour",
            Protocol = PrinterProtocol.Ipp,
            Host = "127.0.0.1",
            Port = port,
            ResourcePath = "/ipp/print",
            Capabilities = PrinterCapabilities.CreateDefault("Fake A4 Colour"),
        };

        var job = new PrintJob
        {
            DocumentName = "年度报告.pdf",
            UserName = "tester",
            DocumentFormat = DocumentFormat.Pdf,
            PageCount = 5,
            Copies = 3,
            PaperSize = PaperSize.A4,
            ColorMode = ColorMode.Color,
            DuplexMode = DuplexMode.TwoSidedLongEdge,
            Status = JobStatus.Captured,
        };

        var rule = PriceResolver.Resolve(DefaultPriceRules.Create(), job.PaperSize, job.ColorMode);
        Assert.NotNull(rule);

        var quote = PriceCalculator.Calculate(rule!, job.PageCount, job.Copies, job.DuplexMode);
        job.ApplyQuote(quote);

        // 5 pages x 3 copies, duplex colour: (2*1.20 + 1*0.80) * 3 = 9.60.
        Assert.Equal(960, job.AmountCents);
        Assert.Equal(9, job.TotalSheets);

        var routingRule = new RoutingRule
        {
            Name = "A4 colour",
            TargetPrinterId = printer.Id,
            MatchPaperSizes = { "A4" },
            MatchColorModes = { ColorMode.Color },
        };

        var route = RoutingEngine.Route(job, new[] { routingRule }, new[] { printer });
        Assert.True(route.IsRouted);
        Assert.Equal(printer.Id, route.Printer!.Id);

        job.TargetPrinterId = route.Printer.Id;
        var result = await new PrintJobForwarder().ForwardAsync(
            job, route.Printer, "%PDF-1.7\n% pipeline\n%%EOF"u8.ToArray());

        Assert.True(result.Success, result.Message);
        var context = Assert.Single(handler.Contexts);
        Assert.Equal(3, context.GetCopies());
        Assert.Equal(PaperSize.A4, context.GetPaperSize());
        Assert.Equal(ColorMode.Color, context.GetColorMode());
        Assert.Equal(DuplexMode.TwoSidedLongEdge, context.GetDuplexMode());
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
