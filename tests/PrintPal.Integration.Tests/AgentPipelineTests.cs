using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using PrintPal.Agent;
using PrintPal.Core;
using PrintPal.Ipp;
using PrintPal.Storage;
using Xunit;
using Xunit.Sdk;

namespace PrintPal.Integration.Tests;

public sealed class AgentPipelineTests
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

    private sealed class FlakyHandler : IIppJobHandler
    {
        private int _attempts;

        public FlakyHandler(int failures)
        {
            Failures = failures;
        }

        public int Failures { get; }

        public int Attempts => Volatile.Read(ref _attempts);

        public ConcurrentQueue<IppJobContext> Contexts { get; } = new();

        public Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            Contexts.Enqueue(context);

            return Task.FromResult(attempt <= Failures
                ? IppJobResult.Failure(IppStatus.ServerErrorDeviceError, "simulated failure")
                : IppJobResult.Success());
        }
    }

    private sealed class AlwaysFailHandler : IIppJobHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(IppJobResult.Failure(IppStatus.ServerErrorDeviceError, "always failing"));
        }
    }

    private sealed class SlowHandler : IIppJobHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public async Task<IppJobResult> HandleJobAsync(IppJobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            return IppJobResult.Success();
        }
    }

    private static string NewDataDirectory() =>
        Path.Combine(Path.GetTempPath(), "printpal-agent-tests", Guid.NewGuid().ToString("N"));

    private static byte[] FixturePdf() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "wps-3pages.pdf"));

    private static PrintPalAgent CreateAgent(
        string dataDirectory,
        bool autoForward = true,
        bool seedDemo = false,
        int maxAttempts = 3,
        int timeoutMs = 5000)
    {
        var options = new AgentOptions
        {
            DataDirectory = dataDirectory,
            IppPort = GetFreePort(),
            AdvertiseHost = "127.0.0.1",
            BindAddress = "127.0.0.1",
            AutoForward = autoForward,
            SeedDemoPrinters = seedDemo,
            ForwardMaxAttempts = maxAttempts,
            ForwardTimeout = TimeSpan.FromMilliseconds(timeoutMs),
            ForwardRetryDelays = new List<TimeSpan>
            {
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(20),
            },
        };

        return new PrintPalAgent(options);
    }

    private static IppServer CreateTarget(IIppJobHandler handler, out int port)
    {
        port = GetFreePort();
        return new IppServer(
            new IppServerOptions
            {
                Name = "Target Fake",
                HostName = "127.0.0.1",
                Port = port,
                BindAddress = IPAddress.Loopback,
                Capabilities = PrinterCapabilities.CreateDefault("Target Fake"),
            },
            handler);
    }

    private static async Task<Printer> AddTargetPrinterAsync(PrintPalAgent agent, int port)
    {
        var printer = new Printer
        {
            Name = "Target",
            Protocol = PrinterProtocol.Ipp,
            Host = "127.0.0.1",
            Port = port,
            ResourcePath = "/ipp/print",
            Enabled = true,
            IsDefault = true,
            Capabilities = PrinterCapabilities.CreateDefault("Target"),
        };

        await agent.Database.SavePrinterAsync(printer);
        await agent.Database.SaveRoutingRuleAsync(new RoutingRule
        {
            Name = "all",
            Priority = 1,
            TargetPrinterId = printer.Id,
        });

        return printer;
    }

    private static async Task SendJobAsync(
        PrintPalAgent agent,
        string name = "agent.pdf",
        int copies = 2,
        byte[]? pdf = null)
    {
        using var client = new IppClient();
        var result = await client.PrintJobAsync(new IppPrintJobRequest(
            new Uri(agent.IppUri),
            name,
            "tester",
            DocumentFormat.Pdf,
            pdf ?? FixturePdf(),
            Copies: copies,
            PaperSize: PaperSize.A4,
            ColorMode: ColorMode.Color,
            DuplexMode: DuplexMode.TwoSidedLongEdge));

        Assert.True(result.IsSuccess);
    }

    private static async Task<PrintJob> WaitForJobAsync(
        PrintPalDatabase database,
        Func<PrintJob, bool> predicate,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        PrintJob? last = null;

        while (DateTime.UtcNow < deadline)
        {
            var jobs = await database.GetJobsAsync(50);
            last = jobs.FirstOrDefault();
            if (last is not null && predicate(last))
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new XunitException(
            $"Timed out waiting for job. Last status: {last?.Status}, reason: {last?.StatusReason}");
    }

    [Fact]
    public async Task Agent_captures_prices_routes_and_forwards()
    {
        var dataDirectory = NewDataDirectory();
        var handler = new RecordingHandler();
        await using var target = CreateTarget(handler, out var targetPort);
        await target.StartAsync();

        var agent = CreateAgent(dataDirectory);
        try
        {
            await agent.StartAsync();
            var printer = await AddTargetPrinterAsync(agent, targetPort);

            var pdf = FixturePdf();
            await SendJobAsync(agent, pdf: pdf);

            var job = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.Succeeded);

            Assert.Equal("agent.pdf", job.DocumentName);
            Assert.Equal(3, job.PageCount);
            Assert.Equal(2, job.Copies);
            Assert.Equal(PaperSize.A4, job.PaperSize);
            Assert.Equal(ColorMode.Color, job.ColorMode);
            Assert.Equal(DuplexMode.TwoSidedLongEdge, job.DuplexMode);
            Assert.Equal(400, job.AmountCents);
            Assert.Equal(printer.Id, job.TargetPrinterId);
            Assert.NotNull(job.ForwardedAtUtc);
            Assert.NotNull(job.DocumentPath);
            Assert.True(File.Exists(job.DocumentPath));

            var context = Assert.Single(handler.Contexts);
            Assert.Equal(pdf, context.Document);
            Assert.Equal(2, context.GetCopies());
            Assert.Equal(DuplexMode.TwoSidedLongEdge, context.GetDuplexMode());
            Assert.Equal(ColorMode.Color, context.GetColorMode());
            Assert.Equal(PaperSize.A4, context.GetPaperSize());
            Assert.Contains($"[PrintPal:{job.Id:N}]", context.JobName ?? "");
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Agent_marks_job_unrouted_when_no_printer_matches()
    {
        var dataDirectory = NewDataDirectory();
        var agent = CreateAgent(dataDirectory);

        try
        {
            await agent.StartAsync();
            await SendJobAsync(agent, "unrouted.pdf");

            var job = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.Unrouted);
            Assert.Equal(400, job.AmountCents);
            Assert.Null(job.TargetPrinterId);
            Assert.Null(job.ForwardedAtUtc);
            Assert.Contains("没有匹配", job.StatusReason);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Agent_marks_job_needs_review_when_page_count_is_unknown()
    {
        var dataDirectory = NewDataDirectory();
        var agent = CreateAgent(dataDirectory);

        try
        {
            await agent.StartAsync();
            await SendJobAsync(agent, "broken.pdf", copies: 1, pdf: "not a real pdf"u8.ToArray());

            var job = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.NeedsReview);
            Assert.Equal(0, job.PageCount);
            Assert.Equal(0, job.AmountCents);
            Assert.Null(job.TargetPrinterId);
            Assert.Contains("页数", job.StatusReason);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Agent_seed_demo_creates_printers_prices_and_rules()
    {
        var dataDirectory = NewDataDirectory();
        var agent = CreateAgent(dataDirectory, seedDemo: true);

        try
        {
            await agent.StartAsync();

            var printers = await agent.Database.GetPrintersAsync();
            var rules = await agent.Database.GetRoutingRulesAsync();
            var prices = await agent.Database.GetPriceRulesAsync();

            Assert.Equal(3, printers.Count);
            Assert.Equal(3, rules.Count);
            Assert.Equal(8, prices.Count);
            Assert.Contains(printers, printer => printer.Name == "Fake-A4-Color");
            Assert.Contains(printers, printer => printer.Name == "Fake-A4-Mono" && printer.IsDefault);
            Assert.Contains(printers, printer => printer.Name == "Fake-A3");
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Forwarding_retries_until_success()
    {
        var dataDirectory = NewDataDirectory();
        var handler = new FlakyHandler(failures: 1);
        await using var target = CreateTarget(handler, out var targetPort);
        await target.StartAsync();

        var agent = CreateAgent(dataDirectory, maxAttempts: 3);
        try
        {
            await agent.StartAsync();
            await AddTargetPrinterAsync(agent, targetPort);
            await SendJobAsync(agent);

            var job = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.Succeeded);
            Assert.Equal(2, handler.Attempts);
            Assert.NotNull(job.ForwardedAtUtc);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Forwarding_marks_failed_after_max_attempts()
    {
        var dataDirectory = NewDataDirectory();
        var handler = new AlwaysFailHandler();
        await using var target = CreateTarget(handler, out var targetPort);
        await target.StartAsync();

        var agent = CreateAgent(dataDirectory, maxAttempts: 3);
        try
        {
            await agent.StartAsync();
            await AddTargetPrinterAsync(agent, targetPort);
            await SendJobAsync(agent);

            var job = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.Failed);
            Assert.Equal(3, handler.Attempts);
            Assert.Contains("3 次", job.StatusReason);
            Assert.Null(job.ForwardedAtUtc);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Forwarding_timeout_marks_unknown_without_retry()
    {
        var dataDirectory = NewDataDirectory();
        var handler = new SlowHandler();
        await using var target = CreateTarget(handler, out var targetPort);
        await target.StartAsync();

        var agent = CreateAgent(dataDirectory, maxAttempts: 3, timeoutMs: 200);
        try
        {
            await agent.StartAsync();
            await AddTargetPrinterAsync(agent, targetPort);
            await SendJobAsync(agent);

            var job = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.Unknown);
            Assert.Equal(1, handler.Attempts);
            Assert.Contains("超时", job.StatusReason);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Manual_confirmation_holds_then_forwards()
    {
        var dataDirectory = NewDataDirectory();
        var handler = new RecordingHandler();
        await using var target = CreateTarget(handler, out var targetPort);
        await target.StartAsync();

        var agent = CreateAgent(dataDirectory, autoForward: false);
        try
        {
            await agent.StartAsync();
            await AddTargetPrinterAsync(agent, targetPort);
            await SendJobAsync(agent);

            var held = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.Held);
            Assert.Empty(handler.Contexts);

            await agent.EnqueueForwardAsync(held.Id);
            var succeeded = await WaitForJobAsync(
                agent.Database,
                candidate => candidate.Status == JobStatus.Succeeded);

            Assert.Single(handler.Contexts);
            Assert.Equal(held.Id, succeeded.Id);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Manual_target_override_for_unrouted_job()
    {
        var dataDirectory = NewDataDirectory();
        var handler = new RecordingHandler();
        await using var target = CreateTarget(handler, out var targetPort);
        await target.StartAsync();

        var agent = CreateAgent(dataDirectory);
        try
        {
            await agent.StartAsync();
            await SendJobAsync(agent);

            var unrouted = await WaitForJobAsync(agent.Database, candidate => candidate.Status == JobStatus.Unrouted);
            Assert.Empty(handler.Contexts);

            var printer = new Printer
            {
                Name = "Override",
                Protocol = PrinterProtocol.Ipp,
                Host = "127.0.0.1",
                Port = targetPort,
                ResourcePath = "/ipp/print",
                Enabled = true,
                Capabilities = PrinterCapabilities.CreateDefault("Override"),
            };
            await agent.Database.SavePrinterAsync(printer);

            await agent.EnqueueForwardAsync(unrouted.Id, printer.Id);
            var succeeded = await WaitForJobAsync(
                agent.Database,
                candidate => candidate.Status == JobStatus.Succeeded);

            Assert.Equal(printer.Id, succeeded.TargetPrinterId);
            Assert.Single(handler.Contexts);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    [Fact]
    public async Task Interrupted_forwarding_job_becomes_unknown_on_restart()
    {
        var dataDirectory = NewDataDirectory();
        var agent = CreateAgent(dataDirectory, autoForward: false);

        try
        {
            await agent.StartAsync();

            var printer = new Printer
            {
                Name = "InterruptedTarget",
                Protocol = PrinterProtocol.Ipp,
                Host = "127.0.0.1",
                Port = GetFreePort(),
                Enabled = true,
                Capabilities = PrinterCapabilities.CreateDefault("InterruptedTarget"),
            };
            await agent.Database.SavePrinterAsync(printer);

            var job = new PrintJob
            {
                DocumentName = "interrupted.pdf",
                TargetPrinterId = printer.Id,
                Status = JobStatus.Forwarding,
                StatusReason = "in flight",
                PageCount = 1,
                Copies = 1,
                AmountCents = 20,
            };
            await agent.Database.SaveJobAsync(job);

            await agent.StopAsync();
            await agent.StartAsync();

            var recovered = await agent.Database.GetJobAsync(job.Id);
            Assert.NotNull(recovered);
            Assert.Equal(JobStatus.Unknown, recovered!.Status);
            Assert.Contains("中断", recovered.StatusReason);
        }
        finally
        {
            await agent.StopAsync();
            Cleanup(dataDirectory);
        }
    }

    private static void Cleanup(string dataDirectory)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
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
