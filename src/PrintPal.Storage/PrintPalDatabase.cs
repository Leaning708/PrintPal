using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PrintPal.Core;

namespace PrintPal.Storage;

public sealed record JobSummary(int JobCount, long TotalPages, long AmountCents);

/// <summary>SQLite persistence for jobs, printers, prices, routing rules and settings.</summary>
public sealed class PrintPalDatabase
{
    private const string SelectJobColumns = """
        Id, CreatedAtUtc, CapturedAtUtc, DocumentName, UserName, SourceApp,
        VirtualPrinterName, Backend, BackendJobId, DocumentFormat, DocumentPath,
        DocumentSize, DocumentSha256, PaperSize, PaperWidthHundredthMm, PaperHeightHundredthMm,
        ColorMode, DuplexMode, Copies, PageCount, SimplexPriceCents, DuplexPriceCents,
        DuplexSheetsPerCopy, SimplexSheetsPerCopy, TotalDuplexSheets, TotalSimplexSheets,
        TotalSheets, UnitPriceCents, AmountCents, PricingJson, TargetPrinterId, Status,
        StatusReason, BillingTagId, BillingSessionId, TagNameSnapshot,
        IsEstimated, IsAdjusted, IsDeleted, RawAttributesJson,
        PricedAtUtc, ForwardedAtUtc
        """;

    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public PrintPalDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS PrintJobs (
                Id TEXT PRIMARY KEY,
                CreatedAtUtc TEXT NOT NULL,
                CapturedAtUtc TEXT NOT NULL,
                DocumentName TEXT NOT NULL,
                UserName TEXT NOT NULL,
                SourceApp TEXT NULL,
                VirtualPrinterName TEXT NOT NULL,
                Backend TEXT NOT NULL,
                BackendJobId TEXT NULL,
                DocumentFormat TEXT NOT NULL,
                DocumentPath TEXT NULL,
                DocumentSize INTEGER NOT NULL,
                DocumentSha256 TEXT NULL,
                PaperSize TEXT NOT NULL,
                PaperWidthHundredthMm INTEGER NOT NULL,
                PaperHeightHundredthMm INTEGER NOT NULL,
                ColorMode TEXT NOT NULL,
                DuplexMode TEXT NOT NULL,
                Copies INTEGER NOT NULL,
                PageCount INTEGER NOT NULL,
                SimplexPriceCents INTEGER NOT NULL,
                DuplexPriceCents INTEGER NOT NULL,
                DuplexSheetsPerCopy INTEGER NOT NULL,
                SimplexSheetsPerCopy INTEGER NOT NULL,
                TotalDuplexSheets INTEGER NOT NULL,
                TotalSimplexSheets INTEGER NOT NULL,
                TotalSheets INTEGER NOT NULL,
                UnitPriceCents INTEGER NOT NULL,
                AmountCents INTEGER NOT NULL,
                PricingJson TEXT NULL,
                TargetPrinterId TEXT NULL,
                Status TEXT NOT NULL,
                StatusReason TEXT NULL,
                IsEstimated INTEGER NOT NULL,
                IsAdjusted INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL,
                RawAttributesJson TEXT NULL,
                PricedAtUtc TEXT NULL,
                ForwardedAtUtc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PrintJobs_CreatedAt ON PrintJobs(CreatedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_PrintJobs_Status ON PrintJobs(Status);
            CREATE INDEX IF NOT EXISTS IX_PrintJobs_Target ON PrintJobs(TargetPrinterId);

            CREATE TABLE IF NOT EXISTS Printers (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Protocol TEXT NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL,
                ResourcePath TEXT NOT NULL,
                FormatPreference TEXT NOT NULL,
                Enabled INTEGER NOT NULL,
                IsDefault INTEGER NOT NULL,
                CapabilitiesJson TEXT NULL,
                LastTestedAtUtc TEXT NULL,
                LastTestOk INTEGER NULL,
                Notes TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS PriceRules (
                Id TEXT PRIMARY KEY,
                Name TEXT NULL,
                PaperSize TEXT NULL,
                ColorMode TEXT NULL,
                SimplexPriceCents INTEGER NOT NULL,
                DuplexPriceCents INTEGER NOT NULL,
                Enabled INTEGER NOT NULL,
                Priority INTEGER NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS RoutingRules (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Priority INTEGER NOT NULL,
                Enabled INTEGER NOT NULL,
                MatchPaperSizesJson TEXT NOT NULL,
                MatchColorModesJson TEXT NOT NULL,
                MatchDuplexModesJson TEXT NOT NULL,
                MinPages INTEGER NULL,
                MaxPages INTEGER NULL,
                SourceAppPattern TEXT NULL,
                DocumentNamePattern TEXT NULL,
                TargetPrinterId TEXT NULL,
                Notes TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS BillingTags (
                Id TEXT PRIMARY KEY,
                SequenceNumber INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Note TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                IsArchived INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL,
                ArchivedAtUtc TEXT NULL,
                DeletedAtUtc TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BillingTags_Sequence ON BillingTags(SequenceNumber);

            CREATE TABLE IF NOT EXISTS BillingSessions (
                Id TEXT PRIMARY KEY,
                BillingTagId TEXT NOT NULL,
                TagNameSnapshot TEXT NOT NULL,
                OpenedAtUtc TEXT NOT NULL,
                ClosedAtUtc TEXT NULL,
                Status TEXT NOT NULL,
                JobCount INTEGER NOT NULL,
                TotalPages INTEGER NOT NULL,
                AmountCents INTEGER NOT NULL,
                Note TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_BillingSessions_OpenTag
                ON BillingSessions(BillingTagId) WHERE Status = 'Open';
            """, cancellationToken).ConfigureAwait(false);

        await EnsureColumnAsync(connection, "PrintJobs", "BillingTagId", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "PrintJobs", "BillingSessionId", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "PrintJobs", "TagNameSnapshot", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection,
            """
            CREATE INDEX IF NOT EXISTS IX_PrintJobs_BillingTag ON PrintJobs(BillingTagId);
            CREATE INDEX IF NOT EXISTS IX_PrintJobs_BillingSession ON PrintJobs(BillingSessionId);
            """, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ jobs

    public async Task SaveJobAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR REPLACE INTO PrintJobs (
                Id, CreatedAtUtc, CapturedAtUtc, DocumentName, UserName, SourceApp,
                VirtualPrinterName, Backend, BackendJobId, DocumentFormat, DocumentPath,
                DocumentSize, DocumentSha256, PaperSize, PaperWidthHundredthMm, PaperHeightHundredthMm,
                ColorMode, DuplexMode, Copies, PageCount, SimplexPriceCents, DuplexPriceCents,
                DuplexSheetsPerCopy, SimplexSheetsPerCopy, TotalDuplexSheets, TotalSimplexSheets,
                TotalSheets, UnitPriceCents, AmountCents, PricingJson, TargetPrinterId, Status,
                StatusReason, BillingTagId, BillingSessionId, TagNameSnapshot,
                IsEstimated, IsAdjusted, IsDeleted, RawAttributesJson,
                PricedAtUtc, ForwardedAtUtc
            ) VALUES (
                $id, $created, $captured, $documentName, $userName, $sourceApp,
                $virtualPrinterName, $backend, $backendJobId, $documentFormat, $documentPath,
                $documentSize, $documentSha256, $paperSize, $paperWidth, $paperHeight,
                $colorMode, $duplexMode, $copies, $pageCount, $simplexPrice, $duplexPrice,
                $duplexSheetsPerCopy, $simplexSheetsPerCopy, $totalDuplexSheets, $totalSimplexSheets,
                $totalSheets, $unitPrice, $amount, $pricingJson, $targetPrinterId, $status,
                $statusReason, $billingTagId, $billingSessionId, $tagNameSnapshot,
                $isEstimated, $isAdjusted, $isDeleted, $rawAttributesJson,
                $pricedAt, $forwardedAt
            );
            """;

        Add(command, "$id", job.Id.ToString("D"));
        Add(command, "$created", ToIso(job.CreatedAtUtc));
        Add(command, "$captured", ToIso(job.CapturedAtUtc));
        Add(command, "$documentName", job.DocumentName);
        Add(command, "$userName", job.UserName);
        Add(command, "$sourceApp", job.SourceApp);
        Add(command, "$virtualPrinterName", job.VirtualPrinterName);
        Add(command, "$backend", job.Backend);
        Add(command, "$backendJobId", job.BackendJobId);
        Add(command, "$documentFormat", job.DocumentFormat.ToString());
        Add(command, "$documentPath", job.DocumentPath);
        Add(command, "$documentSize", job.DocumentSize);
        Add(command, "$documentSha256", job.DocumentSha256);
        Add(command, "$paperSize", job.PaperSize.Name);
        Add(command, "$paperWidth", job.PaperSize.WidthHundredthMm);
        Add(command, "$paperHeight", job.PaperSize.HeightHundredthMm);
        Add(command, "$colorMode", job.ColorMode.ToString());
        Add(command, "$duplexMode", job.DuplexMode.ToString());
        Add(command, "$copies", job.Copies);
        Add(command, "$pageCount", job.PageCount);
        Add(command, "$simplexPrice", job.SimplexPriceCents);
        Add(command, "$duplexPrice", job.DuplexPriceCents);
        Add(command, "$duplexSheetsPerCopy", job.DuplexSheetsPerCopy);
        Add(command, "$simplexSheetsPerCopy", job.SimplexSheetsPerCopy);
        Add(command, "$totalDuplexSheets", job.TotalDuplexSheets);
        Add(command, "$totalSimplexSheets", job.TotalSimplexSheets);
        Add(command, "$totalSheets", job.TotalSheets);
        Add(command, "$unitPrice", job.UnitPriceCents);
        Add(command, "$amount", job.AmountCents);
        Add(command, "$pricingJson", job.PricingJson);
        Add(command, "$targetPrinterId", job.TargetPrinterId?.ToString("D"));
        Add(command, "$status", job.Status.ToString());
        Add(command, "$statusReason", job.StatusReason);
        Add(command, "$billingTagId", job.BillingTagId?.ToString("D"));
        Add(command, "$billingSessionId", job.BillingSessionId?.ToString("D"));
        Add(command, "$tagNameSnapshot", job.TagNameSnapshot);
        Add(command, "$isEstimated", job.IsEstimated ? 1 : 0);
        Add(command, "$isAdjusted", job.IsAdjusted ? 1 : 0);
        Add(command, "$isDeleted", job.IsDeleted ? 1 : 0);
        Add(command, "$rawAttributesJson", job.RawAttributesJson);
        Add(command, "$pricedAt", ToIso(job.PricedAtUtc));
        Add(command, "$forwardedAt", ToIso(job.ForwardedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrintJob?> GetJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectJobColumns} FROM PrintJobs WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    public async Task<IReadOnlyList<PrintJob>> GetJobsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectJobColumns}
            FROM PrintJobs
            WHERE IsDeleted = 0
            ORDER BY CreatedAtUtc DESC
            LIMIT $limit;
            """;
        Add(command, "$limit", Math.Max(1, limit));

        var jobs = new List<PrintJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(MapJob(reader));
        }

        return jobs;
    }

    public async Task<JobSummary> GetSummaryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(PageCount * Copies), 0),
                   COALESCE(SUM(AmountCents), 0)
            FROM PrintJobs
            WHERE IsDeleted = 0 AND CreatedAtUtc >= $from AND CreatedAtUtc < $to;
            """;
        Add(command, "$from", ToIso(from));
        Add(command, "$to", ToIso(to));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new JobSummary(0, 0, 0);
        }

        return new JobSummary(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task MarkJobDeletedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PrintJobs SET IsDeleted = 1 WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ billing tags

    public async Task<IReadOnlyList<BillingTag>> GetBillingTagsAsync(
        bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = includeArchived
            ? "SELECT * FROM BillingTags WHERE IsDeleted = 0 ORDER BY SequenceNumber;"
            : "SELECT * FROM BillingTags WHERE IsDeleted = 0 AND IsArchived = 0 ORDER BY SequenceNumber;";
        var tags = new List<BillingTag>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tags.Add(MapBillingTag(reader));
        }
        return tags;
    }

    public async Task<BillingTag?> GetBillingTagAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetBillingTagAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveBillingTagAsync(BillingTag tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO BillingTags
                (Id, SequenceNumber, Name, Note, CreatedAtUtc, IsArchived, IsDeleted, ArchivedAtUtc, DeletedAtUtc)
            VALUES
                ($id, $sequence, $name, $note, $created, $archived, $deleted, $archivedAt, $deletedAt);
            """;
        Add(command, "$id", tag.Id.ToString("D"));
        Add(command, "$sequence", tag.SequenceNumber);
        Add(command, "$name", tag.Name);
        Add(command, "$note", tag.Note);
        Add(command, "$created", ToIso(tag.CreatedAtUtc));
        Add(command, "$archived", tag.IsArchived ? 1 : 0);
        Add(command, "$deleted", tag.IsDeleted ? 1 : 0);
        Add(command, "$archivedAt", ToIso(tag.ArchivedAtUtc));
        Add(command, "$deletedAt", ToIso(tag.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ArchiveBillingTagAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BillingTags
            SET IsArchived = 1, ArchivedAtUtc = $now
            WHERE Id = $id;
            """;
        Add(command, "$now", ToIso(DateTimeOffset.UtcNow));
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBillingTagAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var open = await GetOpenSessionAsync(connection, id, cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            throw new InvalidOperationException("该标签还有未结账会话，请先结账。");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE BillingTags
            SET IsDeleted = 1, DeletedAtUtc = $now
            WHERE Id = $id;
            """;
        Add(command, "$now", ToIso(DateTimeOffset.UtcNow));
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ billing sessions

    public async Task<BillingContext> EnsureActiveBillingContextAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var tagId = await GetSettingAsync(connection, "activeBillingTagId", cancellationToken).ConfigureAwait(false);
        var sessionId = await GetSettingAsync(connection, "activeBillingSessionId", cancellationToken).ConfigureAwait(false);

        if (Guid.TryParse(tagId, out var parsedTagId) &&
            Guid.TryParse(sessionId, out var parsedSessionId))
        {
            var tag = await GetBillingTagAsync(connection, parsedTagId, cancellationToken).ConfigureAwait(false);
            var session = await GetSessionAsync(connection, parsedSessionId, cancellationToken).ConfigureAwait(false);
            if (tag is { IsOpen: true } && session is { Status: BillingSessionStatus.Open } &&
                session.BillingTagId == tag.Id)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new BillingContext(tag, session);
            }
        }

        var nextTag = await GetNextOpenTagAsync(connection, null, cancellationToken).ConfigureAwait(false);
        nextTag ??= await CreateNextTagAsync(connection, cancellationToken).ConfigureAwait(false);

        var nextSession = await GetOpenSessionAsync(connection, nextTag.Id, cancellationToken).ConfigureAwait(false)
                          ?? await CreateSessionAsync(connection, nextTag, cancellationToken).ConfigureAwait(false);

        await SetSettingAsync(connection, "activeBillingTagId", nextTag.Id.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        await SetSettingAsync(connection, "activeBillingSessionId", nextSession.Id.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new BillingContext(nextTag, nextSession);
    }

    public async Task<BillingSession?> GetOpenSessionAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetOpenSessionAsync(connection, tagId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BillingSession> OpenSessionAsync(Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var tag = await GetBillingTagAsync(connection, tagId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("标签不存在。");
        if (!tag.IsOpen)
        {
            throw new InvalidOperationException("标签已归档。");
        }

        return await GetOpenSessionAsync(connection, tagId, cancellationToken).ConfigureAwait(false)
               ?? await CreateSessionAsync(connection, tag, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionSummary> GetSessionSummaryAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(PageCount * Copies), 0),
                   COALESCE(SUM(AmountCents), 0),
                   COALESCE(SUM(CASE WHEN Status = 'Succeeded' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN Status NOT IN ('Succeeded','Canceled') THEN 1 ELSE 0 END), 0)
            FROM PrintJobs
            WHERE IsDeleted = 0 AND BillingSessionId = $session;
            """;
        Add(command, "$session", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new SessionSummary(sessionId, 0, 0, 0, 0, 0);
        }
        return new SessionSummary(
            sessionId,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    public async Task<CheckoutResult> CheckoutAndAdvanceAsync(
        Guid sessionId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var session = await GetSessionAsync(connection, sessionId, cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("会话不存在。");
        if (session.Status != BillingSessionStatus.Open)
        {
            throw new InvalidOperationException("该会话已经结账。");
        }

        var tag = await GetBillingTagAsync(connection, session.BillingTagId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("标签不存在。");

        var jobs = await GetSessionJobsAsync(connection, sessionId, cancellationToken).ConfigureAwait(false);
        var blocking = jobs.Where(job => job.Status is not (JobStatus.Succeeded or JobStatus.Canceled)).ToList();
        if (blocking.Count > 0)
        {
            throw new CheckoutBlockedException(blocking);
        }

        var succeeded = jobs.Where(job => job.Status == JobStatus.Succeeded).ToList();
        session.TagNameSnapshot = tag.Name;
        session.Status = BillingSessionStatus.Closed;
        session.ClosedAtUtc = DateTimeOffset.UtcNow;
        session.JobCount = succeeded.Count;
        session.TotalPages = succeeded.Sum(job => job.PageCount * job.Copies);
        session.AmountCents = succeeded.Sum(job => job.AmountCents);
        session.Note = note;

        if (succeeded.Count == 0)
        {
            await using var deleteSession = connection.CreateCommand();
            deleteSession.CommandText = "DELETE FROM BillingSessions WHERE Id = $id;";
            Add(deleteSession, "$id", sessionId.ToString("D"));
            await deleteSession.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var close = connection.CreateCommand();
            close.CommandText = """
                UPDATE BillingSessions
                SET TagNameSnapshot = $name, ClosedAtUtc = $closed, Status = 'Closed', JobCount = $jobs,
                    TotalPages = $pages, AmountCents = $amount, Note = $note
                WHERE Id = $id;
                """;
            Add(close, "$name", session.TagNameSnapshot);
            Add(close, "$closed", ToIso(session.ClosedAtUtc));
            Add(close, "$jobs", session.JobCount);
            Add(close, "$pages", session.TotalPages);
            Add(close, "$amount", session.AmountCents);
            Add(close, "$note", note);
            Add(close, "$id", sessionId.ToString("D"));
            await close.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var archive = connection.CreateCommand())
        {
            archive.CommandText = """
                UPDATE BillingTags SET IsArchived = 1, ArchivedAtUtc = $now WHERE Id = $id;
                """;
            Add(archive, "$now", ToIso(DateTimeOffset.UtcNow));
            Add(archive, "$id", tag.Id.ToString("D"));
            await archive.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var nextTag = await GetNextOpenTagAsync(connection, tag.SequenceNumber, cancellationToken).ConfigureAwait(false)
                      ?? await CreateNextTagAsync(connection, cancellationToken).ConfigureAwait(false);
        var nextSession = await GetOpenSessionAsync(connection, nextTag.Id, cancellationToken).ConfigureAwait(false)
                          ?? await CreateSessionAsync(connection, nextTag, cancellationToken).ConfigureAwait(false);

        await SetSettingAsync(connection, "activeBillingTagId", nextTag.Id.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        await SetSettingAsync(connection, "activeBillingSessionId", nextSession.Id.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CheckoutResult(session, nextTag, nextSession, Array.Empty<PrintJob>());
    }

    public async Task<IReadOnlyList<BillingSession>> GetClosedSessionsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        Guid? tagId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM BillingSessions
            WHERE Status = 'Closed'
              AND ($from IS NULL OR ClosedAtUtc >= $from)
              AND ($to IS NULL OR ClosedAtUtc < $to)
              AND ($tag IS NULL OR BillingTagId = $tag)
            ORDER BY ClosedAtUtc DESC
            LIMIT $limit;
            """;
        Add(command, "$from", ToIso(from));
        Add(command, "$to", ToIso(to));
        Add(command, "$tag", tagId?.ToString("D"));
        Add(command, "$limit", Math.Max(1, limit));
        var sessions = new List<BillingSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(MapBillingSession(reader));
        }
        return sessions;
    }

    public async Task<IReadOnlyList<PrintJob>> GetSessionJobsAsync(
        Guid sessionId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetSessionJobsAsync(connection, sessionId, cancellationToken, includeDeleted)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PrintJob>> GetLegacyUnassignedJobsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectJobColumns}
            FROM PrintJobs
            WHERE IsDeleted = 0 AND BillingTagId IS NULL
            ORDER BY CreatedAtUtc DESC
            LIMIT $limit;
            """;
        Add(command, "$limit", Math.Max(1, limit));
        var jobs = new List<PrintJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(MapJob(reader));
        }
        return jobs;
    }

    // ------------------------------------------------------------------ billing helpers

    private static async Task<BillingTag?> GetBillingTagAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM BillingTags WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBillingTag(reader) : null;
    }

    private static async Task<BillingTag?> GetNextOpenTagAsync(
        SqliteConnection connection,
        int? afterSequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM BillingTags
            WHERE IsArchived = 0 AND IsDeleted = 0
              AND ($after IS NULL OR SequenceNumber > $after)
            ORDER BY SequenceNumber
            LIMIT 1;
            """;
        Add(command, "$after", afterSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBillingTag(reader) : null;
    }

    private static async Task<BillingTag> CreateNextTagAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var maxCommand = connection.CreateCommand();
        maxCommand.CommandText = "SELECT COALESCE(MAX(SequenceNumber), 0) FROM BillingTags;";
        var max = Convert.ToInt32(await maxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        var tag = new BillingTag
        {
            SequenceNumber = max + 1,
            Name = $"标签{max + 1}",
        };

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO BillingTags
                (Id, SequenceNumber, Name, Note, CreatedAtUtc, IsArchived, IsDeleted, ArchivedAtUtc, DeletedAtUtc)
            VALUES
                ($id, $sequence, $name, NULL, $created, 0, 0, NULL, NULL);
            """;
        Add(insert, "$id", tag.Id.ToString("D"));
        Add(insert, "$sequence", tag.SequenceNumber);
        Add(insert, "$name", tag.Name);
        Add(insert, "$created", ToIso(tag.CreatedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return tag;
    }

    private static async Task<BillingSession?> GetOpenSessionAsync(
        SqliteConnection connection,
        Guid tagId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM BillingSessions WHERE BillingTagId = $tag AND Status = 'Open' LIMIT 1;";
        Add(command, "$tag", tagId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBillingSession(reader) : null;
    }

    private static async Task<BillingSession?> GetSessionAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM BillingSessions WHERE Id = $id;";
        Add(command, "$id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBillingSession(reader) : null;
    }

    private static async Task<BillingSession> CreateSessionAsync(
        SqliteConnection connection,
        BillingTag tag,
        CancellationToken cancellationToken)
    {
        var session = new BillingSession
        {
            BillingTagId = tag.Id,
            TagNameSnapshot = tag.Name,
        };
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO BillingSessions
                (Id, BillingTagId, TagNameSnapshot, OpenedAtUtc, ClosedAtUtc, Status, JobCount, TotalPages, AmountCents, Note)
            VALUES
                ($id, $tag, $name, $opened, NULL, 'Open', 0, 0, 0, NULL);
            """;
        Add(insert, "$id", session.Id.ToString("D"));
        Add(insert, "$tag", tag.Id.ToString("D"));
        Add(insert, "$name", tag.Name);
        Add(insert, "$opened", ToIso(session.OpenedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    private static async Task<IReadOnlyList<PrintJob>> GetSessionJobsAsync(
        SqliteConnection connection,
        Guid sessionId,
        CancellationToken cancellationToken,
        bool includeDeleted = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectJobColumns}
            FROM PrintJobs
            WHERE BillingSessionId = $session
              AND ($includeDeleted = 1 OR IsDeleted = 0)
            ORDER BY CreatedAtUtc;
            """;
        Add(command, "$session", sessionId.ToString("D"));
        Add(command, "$includeDeleted", includeDeleted ? 1 : 0);
        var jobs = new List<PrintJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(MapJob(reader));
        }
        return jobs;
    }

    private static async Task<string?> GetSettingAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        Add(command, "$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task SetSettingAsync(
        SqliteConnection connection,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES ($key, $value);";
        Add(command, "$key", key);
        Add(command, "$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static BillingTag MapBillingTag(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(GetString(reader, "Id")),
        SequenceNumber = GetInt32(reader, "SequenceNumber"),
        Name = GetString(reader, "Name"),
        Note = GetNullableString(reader, "Note"),
        CreatedAtUtc = ParseDate(GetString(reader, "CreatedAtUtc")),
        IsArchived = GetInt32(reader, "IsArchived") != 0,
        IsDeleted = GetInt32(reader, "IsDeleted") != 0,
        ArchivedAtUtc = ParseNullableDate(GetNullableString(reader, "ArchivedAtUtc")),
        DeletedAtUtc = ParseNullableDate(GetNullableString(reader, "DeletedAtUtc")),
    };

    private static BillingSession MapBillingSession(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(GetString(reader, "Id")),
        BillingTagId = Guid.Parse(GetString(reader, "BillingTagId")),
        TagNameSnapshot = GetString(reader, "TagNameSnapshot"),
        OpenedAtUtc = ParseDate(GetString(reader, "OpenedAtUtc")),
        ClosedAtUtc = ParseNullableDate(GetNullableString(reader, "ClosedAtUtc")),
        Status = ParseEnum(GetString(reader, "Status"), BillingSessionStatus.Open),
        JobCount = GetInt32(reader, "JobCount"),
        TotalPages = GetInt32(reader, "TotalPages"),
        AmountCents = GetInt64(reader, "AmountCents"),
        Note = GetNullableString(reader, "Note"),
    };

    // ------------------------------------------------------------------ printers

    public async Task SavePrinterAsync(Printer printer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(printer);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Printers (
                Id, Name, Protocol, Host, Port, ResourcePath, FormatPreference,
                Enabled, IsDefault, CapabilitiesJson, LastTestedAtUtc, LastTestOk, Notes
            ) VALUES (
                $id, $name, $protocol, $host, $port, $resourcePath, $formatPreference,
                $enabled, $isDefault, $capabilitiesJson, $lastTestedAt, $lastTestOk, $notes
            );
            """;
        Add(command, "$id", printer.Id.ToString("D"));
        Add(command, "$name", printer.Name);
        Add(command, "$protocol", printer.Protocol.ToString());
        Add(command, "$host", printer.Host);
        Add(command, "$port", printer.Port);
        Add(command, "$resourcePath", printer.ResourcePath);
        Add(command, "$formatPreference", printer.FormatPreference.ToString());
        Add(command, "$enabled", printer.Enabled ? 1 : 0);
        Add(command, "$isDefault", printer.IsDefault ? 1 : 0);
        Add(command, "$capabilitiesJson",
            printer.Capabilities is null ? null : JsonSerializer.Serialize(printer.Capabilities, JsonOptions));
        Add(command, "$lastTestedAt", ToIso(printer.LastTestedAtUtc));
        Add(command, "$lastTestOk", printer.LastTestOk.HasValue ? (printer.LastTestOk.Value ? 1 : 0) : null);
        Add(command, "$notes", printer.Notes);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Printer>> GetPrintersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Protocol, Host, Port, ResourcePath, FormatPreference,
                   Enabled, IsDefault, CapabilitiesJson, LastTestedAtUtc, LastTestOk, Notes
            FROM Printers
            ORDER BY Name;
            """;

        var printers = new List<Printer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            printers.Add(MapPrinter(reader));
        }

        return printers;
    }

    public async Task DeletePrinterAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Printers WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ price rules

    public async Task SavePriceRuleAsync(PriceRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO PriceRules (
                Id, Name, PaperSize, ColorMode, SimplexPriceCents, DuplexPriceCents,
                Enabled, Priority, UpdatedAtUtc
            ) VALUES (
                $id, $name, $paperSize, $colorMode, $simplexPrice, $duplexPrice,
                $enabled, $priority, $updatedAt
            );
            """;
        Add(command, "$id", rule.Id.ToString("D"));
        Add(command, "$name", rule.Name);
        Add(command, "$paperSize", rule.PaperSize);
        Add(command, "$colorMode", rule.ColorMode?.ToString());
        Add(command, "$simplexPrice", rule.SimplexPriceCents);
        Add(command, "$duplexPrice", rule.DuplexPriceCents);
        Add(command, "$enabled", rule.Enabled ? 1 : 0);
        Add(command, "$priority", rule.Priority);
        Add(command, "$updatedAt", ToIso(rule.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PriceRule>> GetPriceRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, PaperSize, ColorMode, SimplexPriceCents, DuplexPriceCents,
                   Enabled, Priority, UpdatedAtUtc
            FROM PriceRules
            ORDER BY Priority;
            """;

        var rules = new List<PriceRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(MapPriceRule(reader));
        }

        return rules;
    }

    public async Task DeletePriceRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PriceRules WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ routing rules

    public async Task SaveRoutingRuleAsync(RoutingRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO RoutingRules (
                Id, Name, Priority, Enabled, MatchPaperSizesJson, MatchColorModesJson,
                MatchDuplexModesJson, MinPages, MaxPages, SourceAppPattern, DocumentNamePattern,
                TargetPrinterId, Notes, UpdatedAtUtc
            ) VALUES (
                $id, $name, $priority, $enabled, $paperSizes, $colorModes,
                $duplexModes, $minPages, $maxPages, $sourceApp, $documentName,
                $targetPrinterId, $notes, $updatedAt
            );
            """;
        Add(command, "$id", rule.Id.ToString("D"));
        Add(command, "$name", rule.Name);
        Add(command, "$priority", rule.Priority);
        Add(command, "$enabled", rule.Enabled ? 1 : 0);
        Add(command, "$paperSizes", JsonSerializer.Serialize(rule.MatchPaperSizes));
        Add(command, "$colorModes", JsonSerializer.Serialize(rule.MatchColorModes));
        Add(command, "$duplexModes", JsonSerializer.Serialize(rule.MatchDuplexModes));
        Add(command, "$minPages", rule.MinPages);
        Add(command, "$maxPages", rule.MaxPages);
        Add(command, "$sourceApp", rule.SourceAppPattern);
        Add(command, "$documentName", rule.DocumentNamePattern);
        Add(command, "$targetPrinterId", rule.TargetPrinterId?.ToString("D"));
        Add(command, "$notes", rule.Notes);
        Add(command, "$updatedAt", ToIso(rule.UpdatedAtUtc));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RoutingRule>> GetRoutingRulesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Priority, Enabled, MatchPaperSizesJson, MatchColorModesJson,
                   MatchDuplexModesJson, MinPages, MaxPages, SourceAppPattern, DocumentNamePattern,
                   TargetPrinterId, Notes, UpdatedAtUtc
            FROM RoutingRules
            ORDER BY Priority, Name;
            """;

        var rules = new List<RoutingRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(MapRoutingRule(reader));
        }

        return rules;
    }

    public async Task DeleteRoutingRuleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM RoutingRules WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ settings

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        Add(command, "$key", key);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : (string)value;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO Settings (Key, Value) VALUES ($key, $value);";
        Add(command, "$key", key);
        Add(command, "$value", value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ mapping

    private static PrintJob MapJob(SqliteDataReader reader)
    {
        var paperName = GetString(reader, "PaperSize");
        var paperWidth = GetInt32(reader, "PaperWidthHundredthMm");
        var paperHeight = GetInt32(reader, "PaperHeightHundredthMm");

        return new PrintJob
        {
            Id = Guid.Parse(GetString(reader, "Id")),
            CreatedAtUtc = ParseDate(GetString(reader, "CreatedAtUtc")),
            CapturedAtUtc = ParseDate(GetString(reader, "CapturedAtUtc")),
            DocumentName = GetString(reader, "DocumentName"),
            UserName = GetString(reader, "UserName"),
            SourceApp = GetNullableString(reader, "SourceApp"),
            VirtualPrinterName = GetString(reader, "VirtualPrinterName"),
            Backend = GetString(reader, "Backend"),
            BackendJobId = GetNullableString(reader, "BackendJobId"),
            DocumentFormat = ParseEnum(GetString(reader, "DocumentFormat"), DocumentFormat.Unknown),
            DocumentPath = GetNullableString(reader, "DocumentPath"),
            DocumentSize = GetInt64(reader, "DocumentSize"),
            DocumentSha256 = GetNullableString(reader, "DocumentSha256"),
            PaperSize = paperWidth > 0 && paperHeight > 0
                ? new PaperSize(paperName, paperWidth, paperHeight)
                : PaperSize.Unknown,
            ColorMode = ParseEnum(GetString(reader, "ColorMode"), ColorMode.Unknown),
            DuplexMode = ParseEnum(GetString(reader, "DuplexMode"), DuplexMode.Unknown),
            Copies = GetInt32(reader, "Copies"),
            PageCount = GetInt32(reader, "PageCount"),
            SimplexPriceCents = GetInt64(reader, "SimplexPriceCents"),
            DuplexPriceCents = GetInt64(reader, "DuplexPriceCents"),
            DuplexSheetsPerCopy = GetInt32(reader, "DuplexSheetsPerCopy"),
            SimplexSheetsPerCopy = GetInt32(reader, "SimplexSheetsPerCopy"),
            TotalDuplexSheets = GetInt32(reader, "TotalDuplexSheets"),
            TotalSimplexSheets = GetInt32(reader, "TotalSimplexSheets"),
            TotalSheets = GetInt32(reader, "TotalSheets"),
            UnitPriceCents = GetInt64(reader, "UnitPriceCents"),
            AmountCents = GetInt64(reader, "AmountCents"),
            PricingJson = GetNullableString(reader, "PricingJson"),
            TargetPrinterId = GetNullableGuid(reader, "TargetPrinterId"),
            Status = ParseEnum(GetString(reader, "Status"), JobStatus.Captured),
            StatusReason = GetNullableString(reader, "StatusReason"),
            BillingTagId = GetNullableGuid(reader, "BillingTagId"),
            BillingSessionId = GetNullableGuid(reader, "BillingSessionId"),
            TagNameSnapshot = GetNullableString(reader, "TagNameSnapshot"),
            IsEstimated = GetInt32(reader, "IsEstimated") != 0,
            IsAdjusted = GetInt32(reader, "IsAdjusted") != 0,
            IsDeleted = GetInt32(reader, "IsDeleted") != 0,
            RawAttributesJson = GetNullableString(reader, "RawAttributesJson"),
            PricedAtUtc = ParseNullableDate(GetNullableString(reader, "PricedAtUtc")),
            ForwardedAtUtc = ParseNullableDate(GetNullableString(reader, "ForwardedAtUtc")),
        };
    }

    private static Printer MapPrinter(SqliteDataReader reader)
    {
        var capabilitiesJson = GetNullableString(reader, "CapabilitiesJson");
        PrinterCapabilities? capabilities = null;

        if (!string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            try
            {
                capabilities = JsonSerializer.Deserialize<PrinterCapabilities>(capabilitiesJson, JsonOptions);
            }
            catch (JsonException)
            {
                capabilities = null;
            }
        }

        var lastTestOk = GetNullableInt32(reader, "LastTestOk");

        return new Printer
        {
            Id = Guid.Parse(GetString(reader, "Id")),
            Name = GetString(reader, "Name"),
            Protocol = ParseEnum(GetString(reader, "Protocol"), PrinterProtocol.Ipp),
            Host = GetString(reader, "Host"),
            Port = GetInt32(reader, "Port"),
            ResourcePath = GetString(reader, "ResourcePath"),
            FormatPreference = ParseEnum(GetString(reader, "FormatPreference"), DocumentFormat.Unknown),
            Enabled = GetInt32(reader, "Enabled") != 0,
            IsDefault = GetInt32(reader, "IsDefault") != 0,
            Capabilities = capabilities,
            LastTestedAtUtc = ParseNullableDate(GetNullableString(reader, "LastTestedAtUtc")),
            LastTestOk = lastTestOk.HasValue ? lastTestOk.Value != 0 : null,
            Notes = GetNullableString(reader, "Notes"),
        };
    }

    private static PriceRule MapPriceRule(SqliteDataReader reader)
    {
        var colorModeText = GetNullableString(reader, "ColorMode");

        return new PriceRule
        {
            Id = Guid.Parse(GetString(reader, "Id")),
            Name = GetNullableString(reader, "Name"),
            PaperSize = GetNullableString(reader, "PaperSize"),
            ColorMode = colorModeText is null
                ? null
                : ParseEnum(colorModeText, ColorMode.Unknown),
            SimplexPriceCents = GetInt64(reader, "SimplexPriceCents"),
            DuplexPriceCents = GetInt64(reader, "DuplexPriceCents"),
            Enabled = GetInt32(reader, "Enabled") != 0,
            Priority = GetInt32(reader, "Priority"),
            UpdatedAtUtc = ParseDate(GetString(reader, "UpdatedAtUtc")),
        };
    }

    private static RoutingRule MapRoutingRule(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(GetString(reader, "Id")),
        Name = GetString(reader, "Name"),
        Priority = GetInt32(reader, "Priority"),
        Enabled = GetInt32(reader, "Enabled") != 0,
        MatchPaperSizes = DeserializeList<string>(GetString(reader, "MatchPaperSizesJson")),
        MatchColorModes = DeserializeList<ColorMode>(GetString(reader, "MatchColorModesJson")),
        MatchDuplexModes = DeserializeList<DuplexMode>(GetString(reader, "MatchDuplexModesJson")),
        MinPages = GetNullableInt32(reader, "MinPages"),
        MaxPages = GetNullableInt32(reader, "MaxPages"),
        SourceAppPattern = GetNullableString(reader, "SourceAppPattern"),
        DocumentNamePattern = GetNullableString(reader, "DocumentNamePattern"),
        TargetPrinterId = GetNullableGuid(reader, "TargetPrinterId"),
        Notes = GetNullableString(reader, "Notes"),
        UpdatedAtUtc = ParseDate(GetString(reader, "UpdatedAtUtc")),
    };

    private static List<T> DeserializeList<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch (JsonException)
        {
            return new List<T>();
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string ToIso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? ToIso(DateTimeOffset? value) =>
        value.HasValue ? ToIso(value.Value) : null;

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullableDate(string? value) =>
        value is null ? null : ParseDate(value);

    private static string GetString(SqliteDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int GetInt32(SqliteDataReader reader, string column) =>
        reader.GetInt32(reader.GetOrdinal(column));

    private static long GetInt64(SqliteDataReader reader, string column) =>
        reader.GetInt64(reader.GetOrdinal(column));

    private static int? GetNullableInt32(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static Guid? GetNullableGuid(SqliteDataReader reader, string column)
    {
        var value = GetNullableString(reader, column);
        return value is null ? null : Guid.Parse(value);
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

