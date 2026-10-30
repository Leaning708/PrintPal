using Microsoft.Data.Sqlite;
using PrintPal.Core;
using PrintPal.Storage;
using Xunit;

namespace PrintPal.Storage.Tests;

public sealed class BillingTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "printpal-billing-tests", Guid.NewGuid().ToString("N"));

    private static async Task<PrintPalDatabase> CreateAsync(string directory)
    {
        var database = new PrintPalDatabase(Path.Combine(directory, "printpal.db"));
        await database.InitializeAsync();
        return database;
    }

    private static PrintJob Job(Bill bill, JobStatus status, long amount = 640) => new()
    {
        DocumentName = "test.pdf",
        UserName = "tester",
        DocumentFormat = DocumentFormat.Pdf,
        PaperSize = PaperSize.A4,
        ColorMode = ColorMode.Color,
        DuplexMode = DuplexMode.TwoSidedLongEdge,
        Copies = 2,
        PageCount = 5,
        AmountCents = amount,
        Status = status,
        BillId = bill.Id,
        BillNameSnapshot = bill.Name,
    };

    [Fact]
    public async Task Ensure_active_creates_bill_客户1()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);

            var first = await db.EnsureActiveBillAsync();
            var second = await db.EnsureActiveBillAsync();

            Assert.Equal("客户1", first.Name);
            Assert.Equal(1, first.SequenceNumber);
            Assert.Equal(BillStatus.Active, first.Status);
            Assert.Equal(first.Id, second.Id);

            var open = await db.GetOpenBillsAsync();
            Assert.Single(open);
            Assert.Equal(BillStatus.Active, open[0].Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Park_parks_current_and_opens_new_active()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var first = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(first, JobStatus.Succeeded));

            var next = await db.ParkActiveBillAsync();

            Assert.Equal("客户2", next.Name);
            Assert.Equal(BillStatus.Active, next.Status);

            var open = await db.GetOpenBillsAsync();
            Assert.Equal(2, open.Count);

            var parked = Assert.Single(open, item => item.Status == BillStatus.Parked);
            Assert.Equal(first.Id, parked.Id);
            Assert.Equal(1, parked.JobCount);
            Assert.Equal(640, parked.AmountCents);
            Assert.NotNull(parked.ParkedAtUtc);

            var active = Assert.Single(open, item => item.Status == BillStatus.Active);
            Assert.Equal(next.Id, active.Id);
            Assert.Equal(0, active.JobCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Park_rejects_empty_bill()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            await db.EnsureActiveBillAsync();

            await Assert.ThrowsAsync<NoBillJobsException>(() => db.ParkActiveBillAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Resume_activates_bill_and_deletes_empty_active()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var first = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(first, JobStatus.Succeeded));

            var empty = await db.ParkActiveBillAsync();
            var resumed = await db.ActivateBillAsync(first.Id);

            Assert.Equal(BillStatus.Active, resumed.Status);
            Assert.Null(await db.GetBillAsync(empty.Id));

            var open = await db.GetOpenBillsAsync();
            var current = Assert.Single(open);
            Assert.Equal(first.Id, current.Id);
            Assert.Equal(1, current.JobCount);

            var jobs = await db.GetBillJobsAsync(first.Id);
            Assert.Single(jobs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Checkout_closes_bill_and_resumes_most_recently_parked()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);

            var first = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(first, JobStatus.Succeeded, 640));

            var second = await db.ParkActiveBillAsync();
            await db.SaveJobAsync(Job(second, JobStatus.Succeeded, 200));

            var third = await db.ParkActiveBillAsync();
            await db.SaveJobAsync(Job(third, JobStatus.Succeeded, 100));

            var result = await db.CheckoutActiveBillAsync("paid");

            Assert.Equal(BillStatus.Closed, result.ClosedBill.Status);
            Assert.Equal(1, result.ClosedBill.JobCount);
            Assert.Equal(100, result.ClosedBill.AmountCents);
            Assert.Equal(10, result.ClosedBill.TotalPages);
            Assert.NotNull(result.ClosedBill.ClosedAtUtc);

            // 最近挂起的是第二张（刚才从它切走的）。
            Assert.Equal(second.Id, result.NextBill.Id);
            Assert.Equal(BillStatus.Active, result.NextBill.Status);

            var history = await db.GetClosedBillsAsync(null, null);
            Assert.Single(history);
            Assert.Equal(third.Id, history[0].Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Checkout_without_parked_opens_new_bill()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var first = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(first, JobStatus.Succeeded));

            var result = await db.CheckoutActiveBillAsync(null);

            Assert.Equal("客户2", result.NextBill.Name);
            Assert.Equal(BillStatus.Active, result.NextBill.Status);
            Assert.Single(await db.GetClosedBillsAsync(null, null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Checkout_is_blocked_by_unfinished_job()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(bill, JobStatus.Priced));

            var ex = await Assert.ThrowsAsync<CheckoutBlockedException>(
                () => db.CheckoutActiveBillAsync(null));

            Assert.Single(ex.BlockingJobs);
            Assert.Empty(await db.GetClosedBillsAsync(null, null));
            Assert.NotNull(await db.GetActiveBillAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Twenty_checkouts_leave_at_most_one_open_bill()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);

            for (var i = 0; i < 20; i++)
            {
                var bill = await db.EnsureActiveBillAsync();
                await db.SaveJobAsync(Job(bill, JobStatus.Succeeded));
                await db.CheckoutActiveBillAsync(null);
            }

            var open = await db.GetOpenBillsAsync();
            var current = Assert.Single(open);
            Assert.Equal(BillStatus.Active, current.Status);
            Assert.Equal(20, (await db.GetClosedBillsAsync(null, null)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Move_job_updates_both_bills()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var first = await db.EnsureActiveBillAsync();
            var job = Job(first, JobStatus.Succeeded, 640);
            await db.SaveJobAsync(job);

            var second = await db.ParkActiveBillAsync();
            await db.SaveJobAsync(Job(second, JobStatus.Succeeded, 200));

            var moved = await db.MoveJobAsync(job.Id, second.Id, createNewActive: false);

            Assert.Equal(second.Id, moved.BillId);
            Assert.Equal("客户2", moved.BillNameSnapshot);

            var open = await db.GetOpenBillsAsync();
            var source = Assert.Single(open, item => item.Id == first.Id);
            var target = Assert.Single(open, item => item.Id == second.Id);
            Assert.Equal(0, source.JobCount);
            Assert.Equal(0, source.AmountCents);
            Assert.Equal(2, target.JobCount);
            Assert.Equal(840, target.AmountCents);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Move_job_to_new_bill_switches_active_bill()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var first = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(first, JobStatus.Succeeded));
            var moving = Job(first, JobStatus.Succeeded);
            await db.SaveJobAsync(moving);

            var moved = await db.MoveJobAsync(moving.Id, targetBillId: null, createNewActive: true);

            Assert.NotNull(moved.BillId);
            var active = await db.GetActiveBillAsync();
            Assert.Equal(moved.BillId, active!.Id);
            Assert.Equal("客户2", active.Name);

            var parked = Assert.Single(await db.GetOpenBillsAsync(),
                item => item.Status == BillStatus.Parked);
            Assert.Equal(first.Id, parked.Id);
            Assert.Equal(1, parked.JobCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Move_job_rejects_closed_bill()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var closed = await db.EnsureActiveBillAsync();
            await db.SaveJobAsync(Job(closed, JobStatus.Succeeded));
            await db.CheckoutActiveBillAsync(null);

            var current = await db.EnsureActiveBillAsync();
            var job = Job(current, JobStatus.Succeeded);
            await db.SaveJobAsync(job);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => db.MoveJobAsync(job.Id, closed.Id, createNewActive: false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Move_job_rejects_forwarding_job()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();
            var job = Job(bill, JobStatus.Forwarding);
            await db.SaveJobAsync(job);
            var other = await db.ParkActiveBillAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => db.MoveJobAsync(job.Id, other.Id, createNewActive: false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Closed_bills_filter_by_time_range()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var ids = new List<Guid>();

            for (var i = 0; i < 3; i++)
            {
                var bill = await db.EnsureActiveBillAsync();
                await db.SaveJobAsync(Job(bill, JobStatus.Succeeded));
                var result = await db.CheckoutActiveBillAsync(null);
                ids.Add(result.ClosedBill.Id);
            }

            await SetClosedAtAsync(db, ids[0], DateTimeOffset.UtcNow.AddDays(-10));
            await SetClosedAtAsync(db, ids[1], DateTimeOffset.UtcNow.AddDays(-2));
            await SetClosedAtAsync(db, ids[2], DateTimeOffset.UtcNow.AddMinutes(-5));

            var recent = await db.GetClosedBillsAsync(DateTimeOffset.UtcNow.AddDays(-3), null);
            Assert.Equal(2, recent.Count);
            Assert.All(recent, bill => Assert.Contains(bill.Id, new[] { ids[1], ids[2] }));

            var all = await db.GetClosedBillsAsync(null, null);
            Assert.Equal(3, all.Count);
            Assert.Equal(ids[2], all[0].Id); // 最近结账在前
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Update_bill_renames_open_bill_only()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var bill = await db.EnsureActiveBillAsync();

            await db.UpdateBillAsync(bill.Id, "王老师的名片", "13900000000");
            var updated = await db.GetBillAsync(bill.Id);
            Assert.NotNull(updated);
            Assert.Equal("王老师的名片", updated!.Name);
            Assert.Equal("13900000000", updated.Note);

            await db.SaveJobAsync(Job(updated, JobStatus.Succeeded));
            await db.CheckoutActiveBillAsync(null);

            var closed = Assert.Single(await db.GetClosedBillsAsync(null, null));
            Assert.Equal("王老师的名片", closed.Name);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => db.UpdateBillAsync(closed.Id, "改名", null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Migrate_old_billing_tables()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "printpal.db");

        try
        {
            // 1) 先用当前代码建库，写入一张账单 + 一条任务，再改回旧的标签/会话结构。
            var legacyTagId = Guid.NewGuid();
            var activeTagId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var jobId = Guid.NewGuid();

            var seed = new PrintPalDatabase(path);
            await seed.InitializeAsync();
            var bill = await seed.EnsureActiveBillAsync();
            await seed.SaveJobAsync(new PrintJob
            {
                Id = jobId,
                DocumentName = "legacy.pdf",
                UserName = "tester",
                DocumentFormat = DocumentFormat.Pdf,
                PaperSize = PaperSize.A4,
                ColorMode = ColorMode.Color,
                DuplexMode = DuplexMode.Simplex,
                Copies = 1,
                PageCount = 2,
                AmountCents = 640,
                Status = JobStatus.Succeeded,
                BillId = bill.Id,
                BillNameSnapshot = bill.Name,
            });

            SqliteConnection.ClearAllPools();

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await ExecuteAsync(connection, "DROP TABLE Bills;");
                await ExecuteAsync(connection, "ALTER TABLE PrintJobs RENAME COLUMN BillId TO BillingTagId;");
                await ExecuteAsync(connection, "ALTER TABLE PrintJobs RENAME COLUMN BillNameSnapshot TO TagNameSnapshot;");
                await ExecuteAsync(connection, "ALTER TABLE PrintJobs ADD COLUMN BillingSessionId TEXT NULL;");
                await ExecuteAsync(connection, "UPDATE PrintJobs SET BillingSessionId = $sid;",
                    ("$sid", sessionId.ToString("D")));

                await ExecuteAsync(connection, """
                    CREATE TABLE BillingTags (
                        Id TEXT PRIMARY KEY, SequenceNumber INTEGER NOT NULL, Name TEXT NOT NULL, Note TEXT NULL,
                        CreatedAtUtc TEXT NOT NULL, IsArchived INTEGER NOT NULL, IsDeleted INTEGER NOT NULL,
                        ArchivedAtUtc TEXT NULL, DeletedAtUtc TEXT NULL);
                    CREATE TABLE BillingSessions (
                        Id TEXT PRIMARY KEY, BillingTagId TEXT NOT NULL, TagNameSnapshot TEXT NOT NULL,
                        OpenedAtUtc TEXT NOT NULL, ClosedAtUtc TEXT NULL, Status TEXT NOT NULL,
                        JobCount INTEGER NOT NULL, TotalPages INTEGER NOT NULL, AmountCents INTEGER NOT NULL,
                        Note TEXT NULL);
                    """);

                await ExecuteAsync(connection, """
                    INSERT INTO BillingTags (Id, SequenceNumber, Name, Note, CreatedAtUtc, IsArchived, IsDeleted, ArchivedAtUtc, DeletedAtUtc)
                    VALUES ($id1, 1, '标签1', NULL, $now, 1, 0, $now, NULL),
                           ($id2, 2, '标签2', NULL, $now, 0, 0, NULL, NULL);
                    """,
                    ("$id1", legacyTagId.ToString("D")),
                    ("$id2", activeTagId.ToString("D")),
                    ("$now", DateTimeOffset.UtcNow.ToString("O")));

                await ExecuteAsync(connection, """
                    INSERT INTO BillingSessions (Id, BillingTagId, TagNameSnapshot, OpenedAtUtc, ClosedAtUtc, Status, JobCount, TotalPages, AmountCents, Note)
                    VALUES ($sid, $tag, '标签1', $now, $now, 'Closed', 1, 2, 640, NULL);
                    """,
                    ("$sid", sessionId.ToString("D")),
                    ("$tag", legacyTagId.ToString("D")),
                    ("$now", DateTimeOffset.UtcNow.ToString("O")));

                await ExecuteAsync(connection, "UPDATE PrintJobs SET BillingTagId = $tag;",
                    ("$tag", legacyTagId.ToString("D")));
                await ExecuteAsync(connection,
                    "INSERT OR REPLACE INTO Settings (Key, Value) VALUES ('activeBillingTagId', $value);",
                    ("$value", activeTagId.ToString("D")));
            }

            SqliteConnection.ClearAllPools();

            // 2) 重新初始化：应自动迁移到 Bills 模型。
            var db = new PrintPalDatabase(path);
            await db.InitializeAsync();

            var closed = Assert.Single(await db.GetClosedBillsAsync(null, null));
            Assert.Equal("客户1", closed.Name);
            Assert.Equal(1, closed.JobCount);
            Assert.Equal(640, closed.AmountCents);

            var active = await db.GetActiveBillAsync();
            Assert.NotNull(active);
            Assert.Equal("客户2", active!.Name);
            Assert.Equal(activeTagId, active.Id);

            var job = await db.GetJobAsync(jobId);
            Assert.NotNull(job);
            Assert.Equal(legacyTagId, job!.BillId);

            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            Assert.Equal(0L, await ScalarAsync(verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('BillingTags','BillingSessions');"));
            Assert.Equal(0L, await ScalarAsync(verify,
                "SELECT COUNT(*) FROM Settings WHERE Key = 'activeBillingTagId';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task SetClosedAtAsync(PrintPalDatabase db, Guid billId, DateTimeOffset closedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={db.DatabasePath}");
        await connection.OpenAsync();
        await ExecuteAsync(connection,
            "UPDATE Bills SET ClosedAtUtc = $value WHERE Id = $id;",
            ("$value", closedAt.ToUniversalTime().ToString("O")),
            ("$id", billId.ToString("D")));
        SqliteConnection.ClearAllPools();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
