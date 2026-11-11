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
    public async Task Price_rules_roundtrip_exact_paper_and_color()
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
            Assert.Equal(6, loaded.Count);
            Assert.All(loaded, rule =>
            {
                Assert.False(string.IsNullOrWhiteSpace(rule.PaperSize));
                Assert.NotNull(rule.ColorMode);
            });

            var a4Color = loaded.Single(rule =>
                rule.PaperSize == "A4" && rule.ColorMode == ColorMode.Color);
            Assert.Equal(80, a4Color.SimplexPriceCents);
            Assert.Equal(120, a4Color.DuplexPriceCents);
            Assert.Equal("A4 彩色", a4Color.Name);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Printer_routing_capability_roundtrips()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);
            var printer = new Printer
            {
                Name = "P1",
                Protocol = PrinterProtocol.Ipp,
                Host = "127.0.0.1",
                Port = 8631,
                Enabled = true,
                Routing = new PrinterRoutingCapability
                {
                    PaperSizes = { "A4", "A5" },
                    ColorModes = { ColorMode.Monochrome, ColorMode.Color },
                    DuplexModes = { DuplexMode.Simplex, DuplexMode.TwoSidedLongEdge },
                },
            };

            await database.SavePrinterAsync(printer);

            var loaded = Assert.Single(await database.GetPrintersAsync());
            Assert.NotNull(loaded.Routing);
            Assert.Equal(new[] { "A4", "A5" }, loaded.Routing!.PaperSizes);
            Assert.Equal(new[] { ColorMode.Monochrome, ColorMode.Color }, loaded.Routing.ColorModes);
            Assert.Equal(new[] { DuplexMode.Simplex, DuplexMode.TwoSidedLongEdge }, loaded.Routing.DuplexModes);
        }
        finally
        {
            Cleanup(directory);
        }
    }


    [Fact]
    public async Task Printer_last_success_time_roundtrips_and_survives_failure()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);
            var successAt = new DateTimeOffset(2026, 11, 10, 2, 22, 7, TimeSpan.Zero);
            var printer = new Printer
            {
                Name = "P1",
                Protocol = PrinterProtocol.Ipp,
                Host = "127.0.0.1",
                Port = 8631,
                Enabled = true,
                LastTestOk = true,
                LastTestedAtUtc = successAt,
                LastSuccessAtUtc = successAt,
            };

            await database.SavePrinterAsync(printer);

            var loaded = Assert.Single(await database.GetPrintersAsync());
            Assert.Equal(successAt, loaded.LastSuccessAtUtc);

            // 之后失败：只更新时间与结果，最后一次成功时间保留
            var failedAt = successAt.AddHours(1);
            loaded.LastTestOk = false;
            loaded.LastTestedAtUtc = failedAt;
            await database.SavePrinterAsync(loaded);

            var failed = Assert.Single(await database.GetPrintersAsync());
            Assert.Equal(failedAt, failed.LastTestedAtUtc);
            Assert.Equal(successAt, failed.LastSuccessAtUtc);
            Assert.False(failed.LastTestOk);
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

    [Fact]
    public async Task Printer_usage_snapshot_counts_bill_and_other_open_bills()
    {
        var directory = NewDirectory();

        try
        {
            var database = await CreateDatabaseAsync(directory);

            var first = new Printer { Name = "P1", Protocol = PrinterProtocol.Ipp, Port = 8631, Enabled = true };
            var second = new Printer { Name = "P2", Protocol = PrinterProtocol.Ipp, Port = 8632, Enabled = true };
            await database.SavePrinterAsync(first);
            await database.SavePrinterAsync(second);

            // 账单 A：两个任务落在 P1，其中一个已成功转发
            var billA = await database.EnsureActiveBillAsync();
            await database.SaveJobAsync(new PrintJob
            {
                DocumentName = "a1.pdf",
                BillId = billA.Id,
                TargetPrinterId = first.Id,
                Status = JobStatus.Succeeded,
                DocumentFormat = DocumentFormat.Pdf,
                PaperSize = PaperSize.A4,
                PageCount = 1,
                Copies = 1,
                ForwardedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            await database.SaveJobAsync(new PrintJob
            {
                DocumentName = "a2.pdf",
                BillId = billA.Id,
                TargetPrinterId = first.Id,
                Status = JobStatus.Priced,
                DocumentFormat = DocumentFormat.Pdf,
                PaperSize = PaperSize.A4,
                PageCount = 1,
                Copies = 1,
            });

            // 挂起 A 后开新账单 B，任务落在 P2
            await database.ParkActiveBillAsync();
            var billB = await database.EnsureActiveBillAsync();
            await database.SaveJobAsync(new PrintJob
            {
                DocumentName = "b1.pdf",
                BillId = billB.Id,
                TargetPrinterId = second.Id,
                Status = JobStatus.Priced,
                DocumentFormat = DocumentFormat.Pdf,
                PaperSize = PaperSize.A4,
                PageCount = 1,
                Copies = 1,
            });

            var snapshot = await database.GetPrinterUsageSnapshotAsync(billB.Id);

            // 本单已用打印机
            Assert.Equal(1, snapshot.BillJobCounts[second.Id]);
            Assert.DoesNotContain(first.Id, snapshot.BillJobCounts.Keys);

            // 其它未结账单占用：A 仍挂起（未结），占着 P1
            Assert.Equal(1, snapshot.OtherOpenBillCounts[first.Id]);
            Assert.DoesNotContain(second.Id, snapshot.OtherOpenBillCounts.Keys);

            // 各机最近成功转发时间
            Assert.True(snapshot.LastUsed.ContainsKey(first.Id));
            Assert.False(snapshot.LastUsed.ContainsKey(second.Id));
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