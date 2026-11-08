using Microsoft.Data.Sqlite;
using PrintPal.Core;
using PrintPal.Storage;
using Xunit;

namespace PrintPal.Storage.Tests;

public sealed class FlowTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "printpal-flow-tests", Guid.NewGuid().ToString("N"));

    private static async Task<PrintPalDatabase> CreateAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var database = new PrintPalDatabase(Path.Combine(directory, "printpal.db"));
        await database.InitializeAsync();
        return database;
    }

    private static PrintJob Job(Bill bill, long amount, string documentName = "test.pdf") => new()
    {
        DocumentName = documentName,
        UserName = "tester",
        DocumentFormat = DocumentFormat.Pdf,
        PaperSize = PaperSize.A4,
        ColorMode = ColorMode.Color,
        DuplexMode = DuplexMode.TwoSidedLongEdge,
        Copies = 1,
        PageCount = 1,
        TotalSheets = 1,
        AmountCents = amount,
        Status = JobStatus.Succeeded,
        BillId = bill.Id,
        BillNameSnapshot = bill.Name,
    };

    [Fact]
    public async Task Bill_and_job_flows_follow_numbering_rule()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(bill, 200, "a.pdf"));
            await db.SaveJobAsync(Job(bill, 640, "b.pdf"));

            var flows = await db.GetFlowsAsync();
            Assert.Equal(3, flows.Count);

            var billFlow = Assert.Single(flows, entry => entry.Flow.Kind == PrintFlowKind.Bill);
            Assert.Matches(@"^\d{17}$", billFlow.Flow.FlowNo);
            Assert.True(billFlow.BillExists);

            var jobFlows = flows
                .Where(entry => entry.Flow.Kind == PrintFlowKind.Job)
                .OrderBy(entry => entry.Flow.FlowNo)
                .ToList();
            Assert.Equal(2, jobFlows.Count);
            Assert.Equal(billFlow.Flow.FlowNo + "001", jobFlows[0].Flow.FlowNo);
            Assert.Equal(billFlow.Flow.FlowNo + "002", jobFlows[1].Flow.FlowNo);
            Assert.All(jobFlows, entry =>
            {
                Assert.Equal(billFlow.Flow.FlowNo, entry.Flow.ParentFlowNo);
                Assert.Equal(bill.Id, entry.Flow.BillId);
                Assert.Equal("A4 · 彩色 · 双面·长边", entry.Flow.SpecSnapshot);
            });
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Job_status_updates_refresh_flow_snapshot()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            var job = Job(bill, 640, "c.pdf");
            job.Status = JobStatus.Held;
            await db.SaveJobAsync(job);

            job.Status = JobStatus.Succeeded;
            job.AmountCents = 640;
            await db.SaveJobAsync(job);

            var flows = await db.GetFlowsAsync();
            Assert.Equal(2, flows.Count); // 账单 1 + 文件 1，不会因为多次保存重复生成
            var jobFlow = Assert.Single(flows, entry => entry.Flow.Kind == PrintFlowKind.Job);
            Assert.Equal("Succeeded", jobFlow.Flow.StatusSnapshot);
            Assert.Equal(640, jobFlow.Flow.AmountCents);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Flow_snapshot_survives_bill_deletion()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(bill, 200, "gone.pdf"));

            await using (var connection = new SqliteConnection($"Data Source={db.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Bills;";
                await command.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();

            var flows = await db.GetFlowsAsync();
            Assert.Equal(2, flows.Count);
            var billFlow = Assert.Single(flows, entry => entry.Flow.Kind == PrintFlowKind.Bill);
            Assert.False(billFlow.BillExists);
            Assert.Equal("散客", billFlow.BillDisplayName);
            Assert.Equal("散客", billFlow.Flow.BillNameSnapshot);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Manual_flow_delete_keeps_bill_and_job()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            var job = Job(bill, 200, "keep.pdf");
            await db.SaveJobAsync(job);

            var flows = await db.GetFlowsAsync();
            var target = Assert.Single(flows, entry => entry.Flow.Kind == PrintFlowKind.Job);

            await db.DeleteFlowAsync(target.Flow.Id);

            var after = await db.GetFlowsAsync();
            Assert.DoesNotContain(after, entry => entry.Flow.Id == target.Flow.Id);
            Assert.NotNull(await db.GetJobAsync(job.Id));
            Assert.NotNull(await db.GetBillAsync(bill.Id));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Moving_job_renumbers_flow_under_target_bill()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var first = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(first, 200, "m.pdf"));

            var second = await db.ParkActiveBillAsync();

            // 挂起后第一张账单仍有任务，取回它再移动第一条记录到第二张账单。
            var moved = await db.MoveJobAsync(
                (await db.GetBillJobsAsync(first.Id, false)).Single().Id,
                second.Id,
                createNewActive: false);
            Assert.Equal(second.Id, moved.BillId);

            var flows = await db.GetFlowsAsync();
            var secondFlow = Assert.Single(flows, entry =>
                entry.Flow.Kind == PrintFlowKind.Bill && entry.Flow.BillId == second.Id);
            var jobFlow = Assert.Single(flows, entry =>
                entry.Flow.Kind == PrintFlowKind.Job && entry.Flow.JobId == moved.Id);
            Assert.Equal(secondFlow.Flow.FlowNo + "001", jobFlow.Flow.FlowNo);
            Assert.Equal(secondFlow.Flow.FlowNo, jobFlow.Flow.ParentFlowNo);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Daily_and_dimension_stats_use_bills_and_jobs()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(bill, 200, "s1.pdf"));
            await db.SaveJobAsync(Job(bill, 640, "s2.pdf"));
            await db.CheckoutActiveBillAsync(null);

            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Today);
            var from = new DateTimeOffset(DateTime.Today.AddDays(-1), offset);
            var to = new DateTimeOffset(DateTime.Today.AddDays(1), offset);

            var daily = await db.GetDailyStatsAsync(from, to);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var row = Assert.Single(daily, item => item.Date == today);
            Assert.Equal(840, row.RevenueCents);
            Assert.Equal(1, row.BillCount);
            Assert.Equal(2, row.JobCount);
            Assert.Equal(2, row.PageCount);

            var paper = await db.GetDimensionStatsAsync("paper", from, to);
            var a4 = Assert.Single(paper);
            Assert.Equal("A4", a4.Key);
            Assert.Equal(2, a4.JobCount);
            Assert.Equal(840, a4.AmountCents);

            var printer = await db.GetDimensionStatsAsync("printer", from, to);
            Assert.Equal("未指定", Assert.Single(printer).Key);

            var hours = await db.GetHourStatsAsync(from, to);
            Assert.Equal(24, hours.Count);
            Assert.Equal(2, hours.Sum(item => item.JobCount));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public async Task Flows_are_backfilled_for_existing_bills_and_jobs()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(bill, 200, "b1.pdf"));
            await db.SaveJobAsync(Job(bill, 640, "b2.pdf"));

            await using (var connection = new SqliteConnection($"Data Source={db.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM PrintFlows;";
                await command.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();

            await db.InitializeAsync();

            var flows = await db.GetFlowsAsync();
            Assert.Equal(3, flows.Count);
            var billFlow = Assert.Single(flows, entry => entry.Flow.Kind == PrintFlowKind.Bill);
            Assert.Matches(@"^\d{17}$", billFlow.Flow.FlowNo);
            Assert.Equal(
                new[] { billFlow.Flow.FlowNo + "001", billFlow.Flow.FlowNo + "002" },
                flows.Where(entry => entry.Flow.Kind == PrintFlowKind.Job)
                    .OrderBy(entry => entry.Flow.FlowNo)
                    .Select(entry => entry.Flow.FlowNo)
                    .ToArray());
        }
        finally
        {
            Cleanup(directory);
        }
    }

    private static void Cleanup(string directory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
