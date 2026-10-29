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

    private static PrintJob Job(BillingContext context, JobStatus status, long amount = 640) => new()
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
        BillingTagId = context.Tag.Id,
        BillingSessionId = context.Session.Id,
        TagNameSnapshot = context.Tag.Name,
    };

    [Fact]
    public async Task Ensure_active_creates_tag1_and_reuses_it()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var first = await db.EnsureActiveBillingContextAsync();
            var second = await db.EnsureActiveBillingContextAsync();

            Assert.Equal("标签1", first.Tag.Name);
            Assert.Equal(1, first.Tag.SequenceNumber);
            Assert.Equal(first.Tag.Id, second.Tag.Id);
            Assert.Equal(first.Session.Id, second.Session.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Checkout_archives_tag_and_creates_next_tag()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var context = await db.EnsureActiveBillingContextAsync();
            await db.SaveJobAsync(Job(context, JobStatus.Succeeded));

            var result = await db.CheckoutAndAdvanceAsync(context.Session.Id, "paid");

            Assert.Equal(640, result.ClosedSession.AmountCents);
            Assert.Equal(1, result.ClosedSession.JobCount);
            Assert.Equal(10, result.ClosedSession.TotalPages);
            Assert.Equal("标签2", result.NextTag.Name);
            Assert.Equal(BillingSessionStatus.Open, result.NextSession.Status);

            var active = await db.GetBillingTagsAsync();
            Assert.Single(active);
            Assert.Equal("标签2", active[0].Name);
            Assert.Equal("标签2", result.NextTag.Name);

            var history = await db.GetClosedSessionsAsync(null, null, null);
            Assert.Single(history);
            Assert.Equal("标签1", history[0].TagNameSnapshot);
            Assert.Equal(640, history[0].AmountCents);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Checkout_is_blocked_by_pending_job()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var context = await db.EnsureActiveBillingContextAsync();
            await db.SaveJobAsync(Job(context, JobStatus.Priced));

            var ex = await Assert.ThrowsAsync<CheckoutBlockedException>(
                () => db.CheckoutAndAdvanceAsync(context.Session.Id, null));

            Assert.Single(ex.BlockingJobs);
            var history = await db.GetClosedSessionsAsync(null, null, null);
            Assert.Empty(history);

            var open = await db.GetOpenSessionAsync(context.Tag.Id);
            Assert.NotNull(open);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Empty_checkout_does_not_create_history()
    {
        var directory = NewDirectory();
        try
        {
            var db = await CreateAsync(directory);
            var context = await db.EnsureActiveBillingContextAsync();
            var result = await db.CheckoutAndAdvanceAsync(context.Session.Id, null);

            Assert.Equal("标签2", result.NextTag.Name);
            Assert.Empty(await db.GetClosedSessionsAsync(null, null, null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

