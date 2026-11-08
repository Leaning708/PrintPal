using Microsoft.Data.Sqlite;
using PrintPal.Core;
using PrintPal.Storage;
using Xunit;

namespace PrintPal.Storage.Tests;

public sealed class DatabaseTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "printpal-storage-tests", Guid.NewGuid().ToString("N"));

    private static async Task<PrintPalDatabase> CreateDatabaseAsync(string directory)
    {
        var database = new PrintPalDatabase(Path.Combine(directory, "printpal.db"));
        await database.InitializeAsync();
        return database;
    }

    [Fact]
    public async Task Job_roundtrips_with_pricing_fields()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);
            var job = new PrintJob
            {
                DocumentName = "年度报告.docx",
                UserName = "tester",
                VirtualPrinterName = "PrintPal 虚拟打印机",
                Backend = "Ipp",
                BackendJobId = "42",
                DocumentFormat = DocumentFormat.Pdf,
                DocumentPath = @"C:\docs\job.pdf",
                DocumentSize = 12345,
                DocumentSha256 = "abc",
                PaperSize = PaperSize.A4,
                ColorMode = ColorMode.Color,
                DuplexMode = DuplexMode.TwoSidedLongEdge,
                Copies = 3,
                PageCount = 5,
                SimplexPriceCents = 80,
                DuplexPriceCents = 120,
                DuplexSheetsPerCopy = 2,
                SimplexSheetsPerCopy = 1,
                TotalDuplexSheets = 6,
                TotalSimplexSheets = 3,
                TotalSheets = 9,
                UnitPriceCents = 120,
                AmountCents = 640,
                PricingJson = "{\"amount\":640}",
                Status = JobStatus.Priced,
                IsEstimated = true,
                RawAttributesJson = "{}",
                PricedAtUtc = DateTimeOffset.UtcNow,
            };

            await database.SaveJobAsync(job);

            var loaded = await database.GetJobAsync(job.Id);

            Assert.NotNull(loaded);
            Assert.Equal(job.Id, loaded!.Id);
            Assert.Equal("年度报告.docx", loaded.DocumentName);
            Assert.Equal(PaperSize.A4, loaded.PaperSize);
            Assert.Equal(ColorMode.Color, loaded.ColorMode);
            Assert.Equal(DuplexMode.TwoSidedLongEdge, loaded.DuplexMode);
            Assert.Equal(3, loaded.Copies);
            Assert.Equal(5, loaded.PageCount);
            Assert.Equal(15, loaded.BillableSides);
            Assert.Equal(640, loaded.AmountCents);
            Assert.Equal(9, loaded.TotalSheets);
            Assert.True(loaded.IsEstimated);
            Assert.Equal(JobStatus.Priced, loaded.Status);
            Assert.Equal("42", loaded.BackendJobId);

            loaded.Status = JobStatus.Succeeded;
            loaded.ForwardedAtUtc = DateTimeOffset.UtcNow;
            await database.SaveJobAsync(loaded);

            var reloaded = await database.GetJobAsync(job.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(JobStatus.Succeeded, reloaded!.Status);
            Assert.NotNull(reloaded.ForwardedAtUtc);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Jobs_and_summary_exclude_deleted_jobs()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);
            var now = DateTimeOffset.UtcNow;

            var first = new PrintJob
            {
                DocumentName = "a",
                PageCount = 3,
                Copies = 2,
                AmountCents = 120,
                Status = JobStatus.Priced,
                CreatedAtUtc = now.AddMinutes(-1),
            };
            var second = new PrintJob
            {
                DocumentName = "b",
                PageCount = 5,
                Copies = 1,
                AmountCents = 80,
                Status = JobStatus.Priced,
                CreatedAtUtc = now,
            };

            await database.SaveJobAsync(first);
            await database.SaveJobAsync(second);

            var jobs = await database.GetJobsAsync();
            Assert.Equal(2, jobs.Count);
            Assert.Equal("b", jobs[0].DocumentName);

            var summary = await database.GetSummaryAsync(now.AddMinutes(-5), now.AddMinutes(5));
            Assert.Equal(2, summary.JobCount);
            Assert.Equal(11, summary.TotalPages);
            Assert.Equal(200, summary.AmountCents);

            await database.MarkJobDeletedAsync(first.Id);

            jobs = await database.GetJobsAsync();
            Assert.Single(jobs);

            summary = await database.GetSummaryAsync(now.AddMinutes(-5), now.AddMinutes(5));
            Assert.Equal(1, summary.JobCount);
            Assert.Equal(5, summary.TotalPages);
            Assert.Equal(80, summary.AmountCents);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Printer_roundtrips_capabilities()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);
            var printer = new Printer
            {
                Name = "Fake A4",
                Protocol = PrinterProtocol.Ipp,
                Host = "127.0.0.1",
                Port = 8631,
                ResourcePath = "/ipp/print",
                FormatPreference = DocumentFormat.Pdf,
                Enabled = true,
                IsDefault = true,
                Capabilities = PrinterCapabilities.CreateDefault("Fake A4"),
                LastTestOk = true,
                LastTestedAtUtc = DateTimeOffset.UtcNow,
                Notes = "test",
            };

            await database.SavePrinterAsync(printer);

            var loaded = Assert.Single(await database.GetPrintersAsync());
            Assert.Equal(printer.Id, loaded.Id);
            Assert.Equal("Fake A4", loaded.Name);
            Assert.Equal(PrinterProtocol.Ipp, loaded.Protocol);
            Assert.Equal(8631, loaded.Port);
            Assert.Equal(DocumentFormat.Pdf, loaded.FormatPreference);
            Assert.NotNull(loaded.Capabilities);
            Assert.Contains(PaperSize.A4, loaded.Capabilities!.MediaSizes);
            Assert.Contains(ColorMode.Color, loaded.Capabilities.ColorModes);
            Assert.Contains(DuplexMode.TwoSidedLongEdge, loaded.Capabilities.DuplexModes);

            await database.DeletePrinterAsync(printer.Id);
            Assert.Empty(await database.GetPrintersAsync());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Price_rules_roundtrip_including_wildcard_color()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);

            foreach (var rule in DefaultPriceRules.Create())
            {
                await database.SavePriceRuleAsync(rule);
            }

            var loaded = await database.GetPriceRulesAsync();
            Assert.Equal(8, loaded.Count);

            var a4Color = loaded.Single(rule =>
                rule.PaperSize == "A4" && rule.ColorMode == ColorMode.Color);
            Assert.Equal(80, a4Color.SimplexPriceCents);
            Assert.Equal(120, a4Color.DuplexPriceCents);

            var wildcard = loaded.Single(rule =>
                rule.PaperSize is null && rule.ColorMode == ColorMode.Monochrome);
            Assert.Null(wildcard.PaperSize);
            Assert.Equal(20, wildcard.SimplexPriceCents);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Routing_rules_roundtrip_lists_and_target()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);
            var targetId = Guid.NewGuid();
            var rule = new RoutingRule
            {
                Name = "A4 colour duplex",
                Priority = 10,
                Enabled = true,
                MatchPaperSizes = { "A4" },
                MatchColorModes = { ColorMode.Color },
                MatchDuplexModes = { DuplexMode.TwoSidedLongEdge },
                MinPages = 2,
                MaxPages = 100,
                SourceAppPattern = "WPS",
                DocumentNamePattern = "report",
                TargetPrinterId = targetId,
                Notes = "test rule",
            };

            await database.SaveRoutingRuleAsync(rule);

            var loaded = Assert.Single(await database.GetRoutingRulesAsync());
            Assert.Equal(rule.Id, loaded.Id);
            Assert.Equal(new[] { "A4" }, loaded.MatchPaperSizes);
            Assert.Equal(new[] { ColorMode.Color }, loaded.MatchColorModes);
            Assert.Equal(new[] { DuplexMode.TwoSidedLongEdge }, loaded.MatchDuplexModes);
            Assert.Equal(2, loaded.MinPages);
            Assert.Equal(100, loaded.MaxPages);
            Assert.Equal(targetId, loaded.TargetPrinterId);
            Assert.Equal("WPS", loaded.SourceAppPattern);
            Assert.Equal("report", loaded.DocumentNamePattern);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Settings_roundtrip()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);

            Assert.Null(await database.GetSettingAsync("missing"));

            await database.SetSettingAsync("autoForward", "true");
            Assert.Equal("true", await database.GetSettingAsync("autoForward"));

            await database.SetSettingAsync("autoForward", "false");
            Assert.Equal("false", await database.GetSettingAsync("autoForward"));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of temporary test files.
        }
    }
}
