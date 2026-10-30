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
        StatusReason, BillId, BillNameSnapshot,
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
                BillId TEXT NULL,
                BillNameSnapshot TEXT NULL,
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

            CREATE TABLE IF NOT EXISTS Bills (
                Id TEXT PRIMARY KEY,
                SequenceNumber INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Note TEXT NULL,
                CreatedAtUtc TEXT NOT NULL,
                Status TEXT NOT NULL,
                ParkedAtUtc TEXT NULL,
                ClosedAtUtc TEXT NULL,
                JobCount INTEGER NOT NULL,
                TotalPages INTEGER NOT NULL,
                AmountCents INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Bills_Sequence ON Bills(SequenceNumber);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Bills_Active ON Bills(Status) WHERE Status = 'Active';
            CREATE INDEX IF NOT EXISTS IX_Bills_Status ON Bills(Status);
            CREATE INDEX IF NOT EXISTS IX_Bills_ClosedAt ON Bills(ClosedAtUtc);
            """, cancellationToken).ConfigureAwait(false);

        await MigrateBillingModelAsync(connection, cancellationToken).ConfigureAwait(false);

        await EnsureColumnAsync(connection, "PrintJobs", "BillId", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);
        await EnsureColumnAsync(connection, "PrintJobs", "BillNameSnapshot", "TEXT NULL", cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(connection,
            "CREATE INDEX IF NOT EXISTS IX_PrintJobs_Bill ON PrintJobs(BillId);",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 一次性把旧的"标签 + 会话"模型迁移成账单模型：
    /// BillingTags/BillingSessions → Bills，PrintJobs.BillingTagId → BillId，
    /// 并清掉旧的 activeBilling* 设置。迁移只跑一次，可重复执行。
    /// </summary>
    private static async Task MigrateBillingModelAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "BillingTags", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await CountRowsAsync(connection, "Bills", cancellationToken).ConfigureAwait(false) == 0)
        {
            var activeTagId = await GetSettingAsync(connection, "activeBillingTagId", cancellationToken)
                .ConfigureAwait(false);

            await using var migrate = connection.CreateCommand();
            migrate.CommandText = """
                INSERT INTO Bills (
                    Id, SequenceNumber, Name, Note, CreatedAtUtc, Status,
                    ParkedAtUtc, ClosedAtUtc, JobCount, TotalPages, AmountCents)
                SELECT t.Id,
                       t.SequenceNumber,
                       CASE WHEN t.Name = '标签' || t.SequenceNumber
                            THEN '客户' || t.SequenceNumber ELSE t.Name END,
                       t.Note,
                       t.CreatedAtUtc,
                       CASE WHEN t.IsArchived = 1 OR s.Status = 'Closed' THEN 'Closed'
                            WHEN t.Id = $active THEN 'Active'
                            ELSE 'Parked' END,
                       CASE WHEN t.IsArchived = 1 OR s.Status = 'Closed' OR t.Id = $active
                            THEN NULL ELSE $now END,
                       s.ClosedAtUtc,
                       COALESCE(s.JobCount, 0),
                       COALESCE(s.TotalPages, 0),
                       COALESCE(s.AmountCents, 0)
                FROM BillingTags t
                LEFT JOIN BillingSessions s ON s.BillingTagId = t.Id
                WHERE t.IsDeleted = 0
                  AND (s.Id IS NOT NULL OR t.IsArchived = 0);
                """;
            Add(migrate, "$active", activeTagId);
            Add(migrate, "$now", ToIso(DateTimeOffset.UtcNow));
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await ColumnExistsAsync(connection, "PrintJobs", "BillingTagId", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, "ALTER TABLE PrintJobs RENAME COLUMN BillingTagId TO BillId;",
                cancellationToken).ConfigureAwait(false);
        }

        if (await ColumnExistsAsync(connection, "PrintJobs", "TagNameSnapshot", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, "ALTER TABLE PrintJobs RENAME COLUMN TagNameSnapshot TO BillNameSnapshot;",
                cancellationToken).ConfigureAwait(false);
        }

        if (await ColumnExistsAsync(connection, "PrintJobs", "BillingSessionId", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, "DROP INDEX IF EXISTS IX_PrintJobs_BillingSession;", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, "ALTER TABLE PrintJobs DROP COLUMN BillingSessionId;", cancellationToken)
                .ConfigureAwait(false);
        }

        await ExecuteAsync(connection, "DROP INDEX IF EXISTS IX_PrintJobs_BillingTag;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS BillingSessions;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS BillingTags;", cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection,
            "DELETE FROM Settings WHERE Key IN ('activeBillingTagId', 'activeBillingSessionId');",
            cancellationToken).ConfigureAwait(false);
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
                StatusReason, BillId, BillNameSnapshot,
                IsEstimated, IsAdjusted, IsDeleted, RawAttributesJson,
                PricedAtUtc, ForwardedAtUtc
            ) VALUES (
                $id, $created, $captured, $documentName, $userName, $sourceApp,
                $virtualPrinterName, $backend, $backendJobId, $documentFormat, $documentPath,
                $documentSize, $documentSha256, $paperSize, $paperWidth, $paperHeight,
                $colorMode, $duplexMode, $copies, $pageCount, $simplexPrice, $duplexPrice,
                $duplexSheetsPerCopy, $simplexSheetsPerCopy, $totalDuplexSheets, $totalSimplexSheets,
                $totalSheets, $unitPrice, $amount, $pricingJson, $targetPrinterId, $status,
                $statusReason, $billId, $billNameSnapshot,
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
        Add(command, "$billId", job.BillId?.ToString("D"));
        Add(command, "$billNameSnapshot", job.BillNameSnapshot);
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

    // ------------------------------------------------------------------ bills

    public async Task<Bill?> GetActiveBillAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetActiveBillAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回当前账单；一张都没有时创建 <c>客户N</c>。</summary>
    public async Task<Bill> EnsureActiveBillAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var active = await GetActiveBillAsync(connection, cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            return active;
        }

        try
        {
            return await CreateBillAsync(connection, BillStatus.Active, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // 服务与 UI 同时启动：唯一索引冲突后重新读取。
            return await GetActiveBillAsync(connection, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("无法创建当前账单。");
        }
    }

    /// <summary>所有未结账账单（当前 + 挂起），带实时金额与单数，供挂起区与"移到其他账单"使用。</summary>
    public async Task<IReadOnlyList<BillSummary>> GetOpenBillsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.Id, b.Name, b.SequenceNumber, b.Status, b.ParkedAtUtc,
                   COALESCE(j.JobCount, 0) AS JobCount,
                   COALESCE(j.TotalPages, 0) AS TotalPages,
                   COALESCE(j.AmountCents, 0) AS AmountCents
            FROM Bills b
            LEFT JOIN (
                SELECT BillId,
                       COUNT(*) AS JobCount,
                       COALESCE(SUM(PageCount * Copies), 0) AS TotalPages,
                       COALESCE(SUM(AmountCents), 0) AS AmountCents
                FROM PrintJobs
                WHERE IsDeleted = 0 AND BillId IS NOT NULL
                GROUP BY BillId
            ) j ON j.BillId = b.Id
            WHERE b.Status IN ('Active', 'Parked')
            ORDER BY CASE b.Status WHEN 'Active' THEN 0 ELSE 1 END,
                     COALESCE(b.ParkedAtUtc, b.CreatedAtUtc) DESC,
                     b.SequenceNumber DESC;
            """;

        var bills = new List<BillSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bills.Add(new BillSummary(
                Guid.Parse(GetString(reader, "Id")),
                GetString(reader, "Name"),
                GetInt32(reader, "SequenceNumber"),
                ParseEnum(GetString(reader, "Status"), BillStatus.Parked),
                GetInt32(reader, "JobCount"),
                GetInt32(reader, "TotalPages"),
                GetInt64(reader, "AmountCents"),
                ParseNullableDate(GetNullableString(reader, "ParkedAtUtc"))));
        }

        return bills;
    }

    public async Task<Bill?> GetBillAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetBillAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>改账单名/备注；已结账账单不可改。</summary>
    public async Task UpdateBillAsync(Guid id, string name, string? note, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var bill = await GetBillAsync(connection, id, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("账单不存在。");
        if (!bill.IsOpen)
        {
            throw new InvalidOperationException("已结账的账单不能修改。");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Bills SET Name = $name, Note = $note WHERE Id = $id;";
        Add(command, "$name", name.Trim());
        Add(command, "$note", string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>挂起当前账单，并新开一张空白账单（客户插队时使用）。</summary>
    public async Task<Bill> ParkActiveBillAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var active = await GetActiveBillAsync(connection, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("没有正在记账的账单。");

        var jobs = await GetBillJobsAsync(connection, active.Id, includeDeleted: false, cancellationToken)
            .ConfigureAwait(false);
        if (jobs.Count == 0)
        {
            throw new NoBillJobsException();
        }

        await ParkBillAsync(connection, active.Id, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await DeleteEmptyBillsAsync(connection, keepId: null, cancellationToken).ConfigureAwait(false);

        var next = await CreateBillAsync(connection, BillStatus.Active, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    /// <summary>取回一张已挂起的账单：当前账单没有记录就丢弃，有记录则自动挂起。</summary>
    public async Task<Bill> ActivateBillAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var target = await GetBillAsync(connection, billId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("账单不存在。");
        if (target.Status != BillStatus.Parked)
        {
            throw new InvalidOperationException("只有已挂起的账单才能取回。");
        }

        var active = await GetActiveBillAsync(connection, cancellationToken).ConfigureAwait(false);
        if (active is not null && active.Id != target.Id)
        {
            var activeJobs = await GetBillJobsAsync(connection, active.Id, false, cancellationToken)
                .ConfigureAwait(false);
            if (activeJobs.Count == 0)
            {
                await DeleteBillRowAsync(connection, active.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ParkBillAsync(connection, active.Id, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await SetBillActiveAsync(connection, target.Id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        target.Status = BillStatus.Active;
        target.ParkedAtUtc = null;
        return target;
    }

    /// <summary>结账：写快照并归档，然后取回最近挂起的账单；没有就新建一张。</summary>
    public async Task<CheckoutResult> CheckoutActiveBillAsync(
        string? note,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var active = await GetActiveBillAsync(connection, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("没有正在记账的账单。");

        var jobs = await GetBillJobsAsync(connection, active.Id, false, cancellationToken).ConfigureAwait(false);
        var blocking = jobs.Where(job => job.Status is not (JobStatus.Succeeded or JobStatus.Canceled)).ToList();
        if (blocking.Count > 0)
        {
            throw new CheckoutBlockedException(blocking);
        }

        var succeeded = jobs.Where(job => job.Status == JobStatus.Succeeded).ToList();
        Bill next;

        if (succeeded.Count == 0)
        {
            // 空账单不写历史，直接丢弃。
            await DeleteBillRowAsync(connection, active.Id, cancellationToken).ConfigureAwait(false);
            active.Status = BillStatus.Closed;
            next = await ActivateNextBillAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            active.Status = BillStatus.Closed;
            active.ClosedAtUtc = now;
            active.ParkedAtUtc = null;
            active.JobCount = succeeded.Count;
            active.TotalPages = succeeded.Sum(job => job.PageCount * job.Copies);
            active.AmountCents = succeeded.Sum(job => job.AmountCents);
            if (!string.IsNullOrWhiteSpace(note))
            {
                active.Note = note.Trim();
            }

            await using var close = connection.CreateCommand();
            close.CommandText = """
                UPDATE Bills
                SET Status = 'Closed', ClosedAtUtc = $closed, ParkedAtUtc = NULL,
                    JobCount = $jobs, TotalPages = $pages, AmountCents = $amount, Note = $note
                WHERE Id = $id;
                """;
            Add(close, "$closed", ToIso(now));
            Add(close, "$jobs", active.JobCount);
            Add(close, "$pages", active.TotalPages);
            Add(close, "$amount", active.AmountCents);
            Add(close, "$note", active.Note);
            Add(close, "$id", active.Id.ToString("D"));
            await close.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            next = await ActivateNextBillAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CheckoutResult(active, next, Array.Empty<PrintJob>());
    }

    /// <summary>
    /// 把一条打印记录移到另一张账单。<paramref name="createNewActive"/> 为 true 时新建账单并切换过去
    /// （当前账单自动挂起），用于"客户 B 的任务错记进客户 A"的现场纠正。
    /// </summary>
    public async Task<PrintJob> MoveJobAsync(
        Guid jobId,
        Guid? targetBillId,
        bool createNewActive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var job = await GetJobAsync(connection, jobId, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("打印记录不存在。");
        if (job.Status == JobStatus.Forwarding)
        {
            throw new InvalidOperationException("任务正在转发，稍后再移动。");
        }

        if (job.BillId is Guid sourceId)
        {
            var source = await GetBillAsync(connection, sourceId, cancellationToken).ConfigureAwait(false);
            if (source is { Status: BillStatus.Closed })
            {
                throw new InvalidOperationException("已结账的账单不能再移动记录。");
            }
        }

        Bill target;
        if (createNewActive)
        {
            var active = await GetActiveBillAsync(connection, cancellationToken).ConfigureAwait(false);
            if (active is not null)
            {
                var activeJobs = await GetBillJobsAsync(connection, active.Id, false, cancellationToken)
                    .ConfigureAwait(false);

                // 这条记录马上会被移走，所以"只剩它一条"也算搬空。
                var remaining = activeJobs.Count - (job.BillId == active.Id ? 1 : 0);
                if (remaining <= 0)
                {
                    await DeleteBillRowAsync(connection, active.Id, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ParkBillAsync(connection, active.Id, DateTimeOffset.UtcNow, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            target = await CreateBillAsync(connection, BillStatus.Active, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (targetBillId is not Guid billId)
            {
                throw new InvalidOperationException("请选择目标账单。");
            }

            target = await GetBillAsync(connection, billId, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("目标账单不存在。");
            if (!target.IsOpen)
            {
                throw new InvalidOperationException("不能移到已结账的账单。");
            }
        }

        if (job.BillId != target.Id)
        {
            await using var move = connection.CreateCommand();
            move.CommandText = "UPDATE PrintJobs SET BillId = $bill, BillNameSnapshot = $name WHERE Id = $id;";
            Add(move, "$bill", target.Id.ToString("D"));
            Add(move, "$name", target.Name);
            Add(move, "$id", job.Id.ToString("D"));
            await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            job.BillId = target.Id;
            job.BillNameSnapshot = target.Name;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    /// <summary>删除一张没有记录的未结账账单（空账单清理；UI 不再提供删除按钮）。</summary>
    public async Task DeleteBillAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var bill = await GetBillAsync(connection, billId, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException("账单不存在。");
        if (bill.Status == BillStatus.Active)
        {
            throw new InvalidOperationException("不能删除当前账单。");
        }

        var jobs = await GetBillJobsAsync(connection, billId, false, cancellationToken).ConfigureAwait(false);
        if (jobs.Count > 0)
        {
            throw new InvalidOperationException("账单里还有打印记录，不能删除。");
        }

        await DeleteBillRowAsync(connection, billId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>已结账账单，按结账时间倒序；<paramref name="from"/>/<paramref name="to"/> 为筛选区间。</summary>
    public async Task<IReadOnlyList<Bill>> GetClosedBillsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM Bills
            WHERE Status = 'Closed'
              AND ($from IS NULL OR ClosedAtUtc >= $from)
              AND ($to IS NULL OR ClosedAtUtc < $to)
            ORDER BY ClosedAtUtc DESC
            LIMIT $limit;
            """;
        Add(command, "$from", ToIso(from));
        Add(command, "$to", ToIso(to));
        Add(command, "$limit", Math.Max(1, limit));

        var bills = new List<Bill>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bills.Add(MapBill(reader));
        }

        return bills;
    }

    public async Task<IReadOnlyList<PrintJob>> GetBillJobsAsync(
        Guid billId,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetBillJobsAsync(connection, billId, includeDeleted, cancellationToken).ConfigureAwait(false);
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
            WHERE IsDeleted = 0 AND BillId IS NULL
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

    // ------------------------------------------------------------------ bill helpers

    private static async Task<Bill?> GetBillAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Bills WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBill(reader) : null;
    }

    private static async Task<Bill?> GetActiveBillAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Bills WHERE Status = 'Active' ORDER BY SequenceNumber LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBill(reader) : null;
    }

    private static async Task<Bill?> GetMostRecentlyParkedBillAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM Bills
            WHERE Status = 'Parked'
            ORDER BY COALESCE(ParkedAtUtc, CreatedAtUtc) DESC, SequenceNumber DESC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapBill(reader) : null;
    }

    private static async Task<Bill> CreateBillAsync(
        SqliteConnection connection,
        BillStatus status,
        CancellationToken cancellationToken)
    {
        var sequence = await NextBillSequenceAsync(connection, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var bill = new Bill
        {
            SequenceNumber = sequence,
            Name = $"客户{sequence}",
            Status = status,
            CreatedAtUtc = now,
            ParkedAtUtc = status == BillStatus.Parked ? now : null,
        };

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Bills
                (Id, SequenceNumber, Name, Note, CreatedAtUtc, Status, ParkedAtUtc, ClosedAtUtc,
                 JobCount, TotalPages, AmountCents)
            VALUES
                ($id, $sequence, $name, NULL, $created, $status, $parked, NULL, 0, 0, 0);
            """;
        Add(insert, "$id", bill.Id.ToString("D"));
        Add(insert, "$sequence", bill.SequenceNumber);
        Add(insert, "$name", bill.Name);
        Add(insert, "$created", ToIso(bill.CreatedAtUtc));
        Add(insert, "$status", bill.Status.ToString());
        Add(insert, "$parked", ToIso(bill.ParkedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return bill;
    }

    /// <summary>
    /// 账单编号单调递增（存在 Settings.billSequence）。账单被删除后编号不会被复用，
    /// 避免"客户1"在不同客户之间来回跳。
    /// </summary>
    private static async Task<int> NextBillSequenceAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var stored = await GetSettingAsync(connection, "billSequence", cancellationToken).ConfigureAwait(false);
        int next;
        if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            next = parsed;
        }
        else
        {
            await using var maxCommand = connection.CreateCommand();
            maxCommand.CommandText = "SELECT COALESCE(MAX(SequenceNumber), 0) FROM Bills;";
            next = Convert.ToInt32(await maxCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) + 1;
        }

        await SetSettingAsync(connection, "billSequence",
            (next + 1).ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static async Task<Bill> ActivateNextBillAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var parked = await GetMostRecentlyParkedBillAsync(connection, cancellationToken).ConfigureAwait(false);
        if (parked is not null)
        {
            await SetBillActiveAsync(connection, parked.Id, cancellationToken).ConfigureAwait(false);
            parked.Status = BillStatus.Active;
            parked.ParkedAtUtc = null;
            return parked;
        }

        return await CreateBillAsync(connection, BillStatus.Active, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ParkBillAsync(
        SqliteConnection connection,
        Guid id,
        DateTimeOffset parkedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Bills SET Status = 'Parked', ParkedAtUtc = $now WHERE Id = $id;";
        Add(command, "$now", ToIso(parkedAt));
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetBillActiveAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Bills SET Status = 'Active', ParkedAtUtc = NULL WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteBillRowAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Bills WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteEmptyBillsAsync(
        SqliteConnection connection,
        Guid? keepId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM Bills
            WHERE Status = 'Parked'
              AND ($keep IS NULL OR Id <> $keep)
              AND NOT EXISTS (
                  SELECT 1 FROM PrintJobs j WHERE j.BillId = Bills.Id AND j.IsDeleted = 0);
            """;
        Add(command, "$keep", keepId?.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PrintJob>> GetBillJobsAsync(
        SqliteConnection connection,
        Guid billId,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectJobColumns}
            FROM PrintJobs
            WHERE BillId = $bill
              AND ($includeDeleted = 1 OR IsDeleted = 0)
            ORDER BY CreatedAtUtc;
            """;
        Add(command, "$bill", billId.ToString("D"));
        Add(command, "$includeDeleted", includeDeleted ? 1 : 0);
        var jobs = new List<PrintJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(MapJob(reader));
        }
        return jobs;
    }

    private static async Task<PrintJob?> GetJobAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectJobColumns} FROM PrintJobs WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapJob(reader) : null;
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

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        Add(command, "$name", table);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<long> CountRowsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static Bill MapBill(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(GetString(reader, "Id")),
        SequenceNumber = GetInt32(reader, "SequenceNumber"),
        Name = GetString(reader, "Name"),
        Note = GetNullableString(reader, "Note"),
        CreatedAtUtc = ParseDate(GetString(reader, "CreatedAtUtc")),
        Status = ParseEnum(GetString(reader, "Status"), BillStatus.Parked),
        ParkedAtUtc = ParseNullableDate(GetNullableString(reader, "ParkedAtUtc")),
        ClosedAtUtc = ParseNullableDate(GetNullableString(reader, "ClosedAtUtc")),
        JobCount = GetInt32(reader, "JobCount"),
        TotalPages = GetInt32(reader, "TotalPages"),
        AmountCents = GetInt64(reader, "AmountCents"),
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
            BillId = GetNullableGuid(reader, "BillId"),
            BillNameSnapshot = GetNullableString(reader, "BillNameSnapshot"),
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

