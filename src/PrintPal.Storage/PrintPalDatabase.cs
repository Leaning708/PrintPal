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
        StatusReason, IsEstimated, IsAdjusted, IsDeleted, RawAttributesJson,
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
                StatusReason, IsEstimated, IsAdjusted, IsDeleted, RawAttributesJson,
                PricedAtUtc, ForwardedAtUtc
            ) VALUES (
                $id, $created, $captured, $documentName, $userName, $sourceApp,
                $virtualPrinterName, $backend, $backendJobId, $documentFormat, $documentPath,
                $documentSize, $documentSha256, $paperSize, $paperWidth, $paperHeight,
                $colorMode, $duplexMode, $copies, $pageCount, $simplexPrice, $duplexPrice,
                $duplexSheetsPerCopy, $simplexSheetsPerCopy, $totalDuplexSheets, $totalSimplexSheets,
                $totalSheets, $unitPrice, $amount, $pricingJson, $targetPrinterId, $status,
                $statusReason, $isEstimated, $isAdjusted, $isDeleted, $rawAttributesJson,
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
