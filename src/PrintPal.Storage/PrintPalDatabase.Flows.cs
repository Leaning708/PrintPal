using System.Globalization;
using Microsoft.Data.Sqlite;
using PrintPal.Core;

namespace PrintPal.Storage;

/// <summary>
/// 打印流水与经营统计。流水编号规则：
/// 账单 = 本地时间 yyyyMMddHHmmss + 3 位毫秒（17 位）；文件 = 账单编号 + 3 位序号（20 位）。
/// 流水保存快照，账单/任务被删除后仍能追溯。
/// </summary>
public sealed partial class PrintPalDatabase
{
    private const string SelectFlowColumns = """
        f.Id, f.FlowNo, f.Kind, f.ParentFlowNo, f.BillId, f.BillNameSnapshot, f.JobId,
        f.DocumentNameSnapshot, f.SpecSnapshot, f.AmountCents, f.StatusSnapshot,
        f.TargetPrinterSnapshot, f.UserNameSnapshot, f.SequenceInBill, f.CreatedAtUtc,
        f.IsDeleted, f.Note,
        CASE WHEN b.Id IS NULL THEN 0 ELSE 1 END AS BillExists,
        COALESCE(b.Name, f.BillNameSnapshot) AS EffectiveBillName,
        CASE WHEN f.Kind = 'Bill'
             THEN COALESCE((SELECT SUM(j.AmountCents) FROM PrintJobs j
                            WHERE j.BillId = f.BillId AND j.IsDeleted = 0), 0)
             ELSE f.AmountCents END AS EffectiveAmount,
        CASE WHEN f.Kind = 'Bill' THEN COALESCE(b.Status, f.StatusSnapshot) ELSE f.StatusSnapshot END AS EffectiveStatus
        """;

    // ------------------------------------------------------------------ flows: query

    public async Task<IReadOnlyList<PrintFlowEntry>> GetFlowsAsync(
        int limit = 500,
        string? search = null,
        PrintFlowKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectFlowColumns}
            FROM PrintFlows f
            LEFT JOIN Bills b ON b.Id = f.BillId
            WHERE f.IsDeleted = 0
              AND ($kind IS NULL OR f.Kind = $kind)
              AND ($search IS NULL OR f.FlowNo LIKE $like
                   OR COALESCE(f.BillNameSnapshot, '') LIKE $like
                   OR COALESCE(f.DocumentNameSnapshot, '') LIKE $like
                   OR COALESCE(f.TargetPrinterSnapshot, '') LIKE $like)
            ORDER BY f.CreatedAtUtc DESC, f.FlowNo DESC
            LIMIT $limit;
            """;
        Add(command, "$kind", kind?.ToString());
        Add(command, "$search", string.IsNullOrWhiteSpace(search) ? null : search.Trim());
        Add(command, "$like", string.IsNullOrWhiteSpace(search) ? null : "%" + search.Trim() + "%");
        Add(command, "$limit", Math.Max(1, limit));

        var list = new List<PrintFlowEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(MapFlowEntry(reader));
        }

        return list;
    }

    public async Task<PrintFlowEntry?> GetFlowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectFlowColumns}
            FROM PrintFlows f
            LEFT JOIN Bills b ON b.Id = f.BillId
            WHERE f.Id = $id AND f.IsDeleted = 0;
            """;
        Add(command, "$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapFlowEntry(reader) : null;
    }

    /// <summary>任务 Id → 文件流水号（用于收银台详情展示/复制）。</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> GetJobFlowNumbersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobId, FlowNo FROM PrintFlows
            WHERE Kind = 'Job' AND IsDeleted = 0 AND JobId IS NOT NULL;
            """;

        var map = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            map[Guid.Parse(GetString(reader, "JobId"))] = GetString(reader, "FlowNo");
        }

        return map;
    }

    /// <summary>账单流水号（用于收银台复制当前账单流水号）。</summary>
    public async Task<string?> GetBillFlowNoAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FlowNo FROM PrintFlows
            WHERE Kind = 'Bill' AND BillId = $bill AND IsDeleted = 0
            ORDER BY CreatedAtUtc
            LIMIT 1;
            """;
        Add(command, "$bill", billId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>手动删除一条流水（软删除，编号不会被复用；账单/任务本身不受影响）。</summary>
    public async Task DeleteFlowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PrintFlows SET IsDeleted = 1 WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ flows: creation / maintenance

    /// <summary>建账单时调用：生成该账单的流水号（17 位）。</summary>
    private static async Task<PrintFlow> CreateBillFlowAsync(
        SqliteConnection connection,
        Bill bill,
        CancellationToken cancellationToken)
    {
        var flowNo = await NextBillFlowNoAsync(connection, bill.CreatedAtUtc, cancellationToken).ConfigureAwait(false);
        var flow = new PrintFlow
        {
            FlowNo = flowNo,
            Kind = PrintFlowKind.Bill,
            BillId = bill.Id,
            BillNameSnapshot = bill.Name,
            CreatedAtUtc = bill.CreatedAtUtc,
            AmountCents = 0,
            StatusSnapshot = bill.Status.ToString(),
            SequenceInBill = 0,
        };

        await InsertFlowAsync(connection, flow, cancellationToken).ConfigureAwait(false);
        return flow;
    }

    /// <summary>新任务入库时调用：生成文件流水号（19/20 位 = 账单号 + 3 位序号）。</summary>
    private static async Task CreateJobFlowAsync(
        SqliteConnection connection,
        PrintJob job,
        CancellationToken cancellationToken)
    {
        PrintFlow? billFlow = null;
        if (job.BillId is Guid billId)
        {
            billFlow = await GetBillFlowAsync(connection, billId, cancellationToken).ConfigureAwait(false);
            if (billFlow is null)
            {
                var bill = await GetBillAsync(connection, billId, cancellationToken).ConfigureAwait(false);
                if (bill is not null)
                {
                    billFlow = await CreateBillFlowAsync(connection, bill, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        string flowNo;
        string? parentFlowNo;
        int sequence;
        if (billFlow is not null)
        {
            parentFlowNo = billFlow.FlowNo;
            sequence = await NextJobSequenceAsync(connection, parentFlowNo, cancellationToken).ConfigureAwait(false);
            flowNo = parentFlowNo + sequence.ToString("000", CultureInfo.InvariantCulture);
        }
        else
        {
            // 历史遗留/未归属任务：没有账单号可拼，仍保证 20 位可读编号。
            parentFlowNo = null;
            sequence = 0;
            flowNo = await NextBillFlowNoAsync(connection, job.CreatedAtUtc, cancellationToken).ConfigureAwait(false)
                     + "000";
        }

        var flow = new PrintFlow
        {
            FlowNo = flowNo,
            Kind = PrintFlowKind.Job,
            ParentFlowNo = parentFlowNo,
            BillId = job.BillId,
            BillNameSnapshot = job.BillNameSnapshot,
            JobId = job.Id,
            DocumentNameSnapshot = job.DocumentName,
            SpecSnapshot = BuildSpecSnapshot(job),
            AmountCents = job.AmountCents,
            StatusSnapshot = job.Status.ToString(),
            TargetPrinterSnapshot = await GetPrinterNameAsync(connection, job.TargetPrinterId, cancellationToken)
                .ConfigureAwait(false),
            UserNameSnapshot = job.UserName,
            SequenceInBill = sequence,
            CreatedAtUtc = job.CreatedAtUtc,
            Note = string.IsNullOrWhiteSpace(job.StatusReason) ? null : job.StatusReason,
        };

        await InsertFlowAsync(connection, flow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务状态/金额/目标变化时刷新流水快照。</summary>
    private static async Task UpdateJobFlowAsync(
        SqliteConnection connection,
        PrintJob job,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PrintFlows SET
                BillId = $bill,
                BillNameSnapshot = $billName,
                DocumentNameSnapshot = $doc,
                SpecSnapshot = $spec,
                AmountCents = $amount,
                StatusSnapshot = $status,
                TargetPrinterSnapshot = $target,
                UserNameSnapshot = $user,
                Note = $note
            WHERE Kind = 'Job' AND JobId = $job;
            """;
        Add(command, "$bill", job.BillId?.ToString("D"));
        Add(command, "$billName", job.BillNameSnapshot);
        Add(command, "$doc", job.DocumentName);
        Add(command, "$spec", BuildSpecSnapshot(job));
        Add(command, "$amount", job.AmountCents);
        Add(command, "$status", job.Status.ToString());
        Add(command, "$target", await GetPrinterNameAsync(connection, job.TargetPrinterId, cancellationToken)
            .ConfigureAwait(false));
        Add(command, "$user", job.UserName);
        Add(command, "$note", string.IsNullOrWhiteSpace(job.StatusReason) ? null : job.StatusReason);
        Add(command, "$job", job.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>账单改名/状态/金额变化时刷新账单流水快照（null 表示不更新该字段）。</summary>
    private static async Task UpdateBillFlowAsync(
        SqliteConnection connection,
        Guid billId,
        string? name,
        string? status,
        long? amountCents,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PrintFlows SET
                BillNameSnapshot = COALESCE($name, BillNameSnapshot),
                StatusSnapshot = COALESCE($status, StatusSnapshot),
                AmountCents = CASE WHEN $amount IS NULL THEN AmountCents ELSE $amount END
            WHERE Kind = 'Bill' AND BillId = $bill;
            """;
        Add(command, "$name", string.IsNullOrWhiteSpace(name) ? null : name);
        Add(command, "$status", status);
        Add(command, "$amount", amountCents);
        Add(command, "$bill", billId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>任务换账单后，流水号跟着新账单重新编号（账单号 + 新序号），保留可追溯性。</summary>
    private static async Task MoveJobFlowAsync(
        SqliteConnection connection,
        PrintJob job,
        Bill target,
        CancellationToken cancellationToken)
    {
        var billFlow = await GetBillFlowAsync(connection, target.Id, cancellationToken).ConfigureAwait(false);
        if (billFlow is null)
        {
            billFlow = await CreateBillFlowAsync(connection, target, cancellationToken).ConfigureAwait(false);
        }

        var sequence = await NextJobSequenceAsync(connection, billFlow.FlowNo, cancellationToken)
            .ConfigureAwait(false);
        var flowNo = billFlow.FlowNo + sequence.ToString("000", CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PrintFlows SET
                FlowNo = $flowNo,
                ParentFlowNo = $parent,
                BillId = $bill,
                BillNameSnapshot = $billName,
                SequenceInBill = $seq,
                Note = $note
            WHERE Kind = 'Job' AND JobId = $job;
            """;
        Add(command, "$flowNo", flowNo);
        Add(command, "$parent", billFlow.FlowNo);
        Add(command, "$bill", target.Id.ToString("D"));
        Add(command, "$billName", target.Name);
        Add(command, "$seq", sequence);
        Add(command, "$note", "由其他账单移入");
        Add(command, "$job", job.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>账单被删除（空账单清理）时一并清掉它的流水；有任务的历史账单不会走到这里。</summary>
    private static async Task DeleteBillFlowsAsync(
        SqliteConnection connection,
        Guid billId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PrintFlows WHERE BillId = $bill;";
        Add(command, "$bill", billId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteEmptyBillFlowsAsync(
        SqliteConnection connection,
        Guid? keepId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM PrintFlows
            WHERE BillId IN (
                SELECT b.Id FROM Bills b
                WHERE b.Status = 'Parked'
                  AND ($keep IS NULL OR b.Id <> $keep)
                  AND NOT EXISTS (
                      SELECT 1 FROM PrintJobs j WHERE j.BillId = b.Id AND j.IsDeleted = 0));
            """;
        Add(command, "$keep", keepId?.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PrintFlow?> GetBillFlowAsync(
        SqliteConnection connection,
        Guid billId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectFlowColumns}
            FROM PrintFlows f
            LEFT JOIN Bills b ON b.Id = f.BillId
            WHERE f.Kind = 'Bill' AND f.BillId = $bill AND f.IsDeleted = 0
            ORDER BY f.CreatedAtUtc
            LIMIT 1;
            """;
        Add(command, "$bill", billId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapFlowEntry(reader).Flow
            : null;
    }

    // ------------------------------------------------------------------ flows: numbering helpers

    /// <summary>17 位编号：本地时间 yyyyMMddHHmmss + 3 位毫秒；冲突时顺延 1ms 直到唯一。</summary>
    private static async Task<string> NextBillFlowNoAsync(
        SqliteConnection connection,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        var local = createdAtUtc.ToLocalTime();
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidateLocal = local.AddMilliseconds(attempt);
            var candidate = FormatFlowNo(candidateLocal);
            if (!await FlowNoExistsAsync(connection, candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("无法生成唯一的账单流水号（同一毫秒冲突过多）。");
    }

    private static string FormatFlowNo(DateTimeOffset local) =>
        local.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
        + local.Millisecond.ToString("000", CultureInfo.InvariantCulture);

    private static async Task<bool> FlowNoExistsAsync(
        SqliteConnection connection,
        string flowNo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM PrintFlows WHERE FlowNo = $flowNo LIMIT 1;";
        Add(command, "$flowNo", flowNo);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<int> NextJobSequenceAsync(
        SqliteConnection connection,
        string parentFlowNo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(MAX(SequenceInBill), 0) + 1 FROM PrintFlows WHERE ParentFlowNo = $parent;";
        Add(command, "$parent", parentFlowNo);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task InsertFlowAsync(
        SqliteConnection connection,
        PrintFlow flow,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PrintFlows (
                Id, FlowNo, Kind, ParentFlowNo, BillId, BillNameSnapshot, JobId,
                DocumentNameSnapshot, SpecSnapshot, AmountCents, StatusSnapshot,
                TargetPrinterSnapshot, UserNameSnapshot, SequenceInBill, CreatedAtUtc,
                IsDeleted, Note)
            VALUES (
                $id, $flowNo, $kind, $parent, $bill, $billName, $job,
                $doc, $spec, $amount, $status,
                $target, $user, $seq, $created,
                $deleted, $note);
            """;
        Add(command, "$id", flow.Id.ToString("D"));
        Add(command, "$flowNo", flow.FlowNo);
        Add(command, "$kind", flow.Kind.ToString());
        Add(command, "$parent", flow.ParentFlowNo);
        Add(command, "$bill", flow.BillId?.ToString("D"));
        Add(command, "$billName", flow.BillNameSnapshot);
        Add(command, "$job", flow.JobId?.ToString("D"));
        Add(command, "$doc", flow.DocumentNameSnapshot);
        Add(command, "$spec", flow.SpecSnapshot);
        Add(command, "$amount", flow.AmountCents);
        Add(command, "$status", flow.StatusSnapshot);
        Add(command, "$target", flow.TargetPrinterSnapshot);
        Add(command, "$user", flow.UserNameSnapshot);
        Add(command, "$seq", flow.SequenceInBill);
        Add(command, "$created", ToIso(flow.CreatedAtUtc));
        Add(command, "$deleted", flow.IsDeleted ? 1 : 0);
        Add(command, "$note", flow.Note);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> GetPrinterNameAsync(
        SqliteConnection connection,
        Guid? printerId,
        CancellationToken cancellationToken)
    {
        if (printerId is not Guid id)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Printers WHERE Id = $id;";
        Add(command, "$id", id.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static string BuildSpecSnapshot(PrintJob job) =>
        $"{job.PaperSize.Name} · {job.ColorMode.DisplayName()} · {job.DuplexMode.DisplayName()}";

    private static PrintFlowEntry MapFlowEntry(SqliteDataReader reader)
    {
        var flow = new PrintFlow
        {
            Id = Guid.Parse(GetString(reader, "Id")),
            FlowNo = GetString(reader, "FlowNo"),
            Kind = ParseEnum(GetString(reader, "Kind"), PrintFlowKind.Bill),
            ParentFlowNo = GetNullableString(reader, "ParentFlowNo"),
            BillId = GetNullableGuid(reader, "BillId"),
            BillNameSnapshot = GetNullableString(reader, "BillNameSnapshot"),
            JobId = GetNullableGuid(reader, "JobId"),
            DocumentNameSnapshot = GetNullableString(reader, "DocumentNameSnapshot"),
            SpecSnapshot = GetNullableString(reader, "SpecSnapshot"),
            AmountCents = GetInt64(reader, "AmountCents"),
            StatusSnapshot = GetNullableString(reader, "StatusSnapshot"),
            TargetPrinterSnapshot = GetNullableString(reader, "TargetPrinterSnapshot"),
            UserNameSnapshot = GetNullableString(reader, "UserNameSnapshot"),
            SequenceInBill = GetInt32(reader, "SequenceInBill"),
            CreatedAtUtc = ParseDate(GetString(reader, "CreatedAtUtc")),
            IsDeleted = GetInt32(reader, "IsDeleted") != 0,
            Note = GetNullableString(reader, "Note"),
        };

        return new PrintFlowEntry(
            flow,
            GetInt32(reader, "BillExists") != 0,
            GetNullableString(reader, "EffectiveBillName") ?? flow.BillNameSnapshot ?? "—",
            GetInt64(reader, "EffectiveAmount"),
            GetNullableString(reader, "EffectiveStatus") ?? flow.StatusSnapshot ?? "");
    }

    // ------------------------------------------------------------------ backfill

    private static async Task<bool> JobExistsAsync(
        SqliteConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM PrintJobs WHERE Id = $id LIMIT 1;";
        Add(command, "$id", jobId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>补齐缺失流水（UI 刷新流水页时调用，覆盖旧版服务/安装包写入的账单与任务）。</summary>
    public async Task EnsureFlowsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureFlowsCoreAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 补齐缺失的流水：旧版服务/安装包写入的账单与任务、以及老数据升级后首次启动都会走到这里。
    /// 只补「完全没有流水行」的账单/任务（含已被手动软删除的行不补），可重复执行。
    /// </summary>
    private static async Task EnsureFlowsCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var bills = new List<Bill>();
        await using (var billCommand = connection.CreateCommand())
        {
            billCommand.CommandText = """
                SELECT * FROM Bills b
                WHERE NOT EXISTS (
                    SELECT 1 FROM PrintFlows f WHERE f.Kind = 'Bill' AND f.BillId = b.Id)
                ORDER BY b.CreatedAtUtc, b.SequenceNumber;
                """;
            await using var reader = await billCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                bills.Add(MapBill(reader));
            }
        }

        foreach (var bill in bills)
        {
            await CreateBillFlowAsync(connection, bill, cancellationToken).ConfigureAwait(false);
        }

        var jobs = new List<PrintJob>();
        await using (var jobCommand = connection.CreateCommand())
        {
            jobCommand.CommandText = $"""
                SELECT {SelectJobColumns}
                FROM PrintJobs j
                WHERE NOT EXISTS (
                    SELECT 1 FROM PrintFlows f WHERE f.Kind = 'Job' AND f.JobId = j.Id)
                ORDER BY j.CreatedAtUtc;
                """;
            await using var reader = await jobCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                jobs.Add(MapJob(reader));
            }
        }

        foreach (var job in jobs)
        {
            await CreateJobFlowAsync(connection, job, cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ statistics

    /// <summary>按天汇总：营业额/账单数按结账日，任务/页数/张数/任务金额按打印日。</summary>
    public async Task<IReadOnlyList<DailyPrintStat>> GetDailyStatsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var byDate = new SortedDictionary<DateOnly, DailyAccumulator>();

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var billCommand = connection.CreateCommand())
        {
            billCommand.CommandText = """
                SELECT ClosedAtUtc, AmountCents
                FROM Bills
                WHERE Status = 'Closed' AND ClosedAtUtc >= $from AND ClosedAtUtc < $to;
                """;
            Add(billCommand, "$from", ToIso(fromUtc));
            Add(billCommand, "$to", ToIso(toUtc));
            await using var reader = await billCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var date = DateOnly.FromDateTime(ParseDate(GetString(reader, "ClosedAtUtc")).ToLocalTime().DateTime);
                var acc = GetOrAdd(byDate, date);
                acc.RevenueCents += GetInt64(reader, "AmountCents");
                acc.BillCount += 1;
            }
        }

        await using (var jobCommand = connection.CreateCommand())
        {
            jobCommand.CommandText = """
                SELECT CreatedAtUtc, PageCount, Copies, TotalSheets, AmountCents
                FROM PrintJobs
                WHERE IsDeleted = 0 AND CreatedAtUtc >= $from AND CreatedAtUtc < $to;
                """;
            Add(jobCommand, "$from", ToIso(fromUtc));
            Add(jobCommand, "$to", ToIso(toUtc));
            await using var reader = await jobCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var date = DateOnly.FromDateTime(ParseDate(GetString(reader, "CreatedAtUtc")).ToLocalTime().DateTime);
                var acc = GetOrAdd(byDate, date);
                acc.JobCount += 1;
                acc.PageCount += GetInt32(reader, "PageCount") * GetInt32(reader, "Copies");
                acc.SheetCount += GetInt32(reader, "TotalSheets");
                acc.JobAmountCents += GetInt64(reader, "AmountCents");
            }
        }

        return byDate
            .Select(pair => new DailyPrintStat(
                pair.Key,
                pair.Value.RevenueCents,
                pair.Value.BillCount,
                pair.Value.JobCount,
                pair.Value.PageCount,
                pair.Value.SheetCount,
                pair.Value.JobAmountCents))
            .ToList();
    }

    /// <summary>维度统计：paper / color / duplex / printer。</summary>
    public async Task<IReadOnlyList<FlowDimensionStat>> GetDimensionStatsAsync(
        string dimension,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var keyExpression = dimension switch
        {
            "color" => "j.ColorMode",
            "duplex" => "j.DuplexMode",
            "printer" => "COALESCE(p.Name, '未指定')",
            _ => "j.PaperSize",
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {keyExpression} AS KeyValue,
                   COUNT(*) AS JobCount,
                   COALESCE(SUM(j.PageCount * j.Copies), 0) AS PageCount,
                   COALESCE(SUM(j.TotalSheets), 0) AS SheetCount,
                   COALESCE(SUM(j.AmountCents), 0) AS AmountCents
            FROM PrintJobs j
            LEFT JOIN Printers p ON p.Id = j.TargetPrinterId
            WHERE j.IsDeleted = 0 AND j.CreatedAtUtc >= $from AND j.CreatedAtUtc < $to
            GROUP BY KeyValue
            ORDER BY AmountCents DESC, JobCount DESC;
            """;
        Add(command, "$from", ToIso(fromUtc));
        Add(command, "$to", ToIso(toUtc));

        // 单双面不再区分长边/短边：历史数据里可能同时存在两种双面值，
        // 这里把 key 归一到统一值，再把同名的行合并成一条。
        var merged = new Dictionary<string, FlowDimensionStat>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = GetNullableString(reader, "KeyValue") ?? "—";
            if (dimension == "duplex" &&
                Enum.TryParse<DuplexMode>(key, ignoreCase: true, out var duplex))
            {
                key = duplex.Canonical().ToString();
            }

            var jobCount = GetInt32(reader, "JobCount");
            var pageCount = GetInt32(reader, "PageCount");
            var sheetCount = GetInt32(reader, "SheetCount");
            var amountCents = GetInt64(reader, "AmountCents");
            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = new FlowDimensionStat(
                    dimension,
                    key,
                    existing.JobCount + jobCount,
                    existing.PageCount + pageCount,
                    existing.SheetCount + sheetCount,
                    existing.AmountCents + amountCents);
            }
            else
            {
                merged[key] = new FlowDimensionStat(dimension, key, jobCount, pageCount, sheetCount, amountCents);
            }
        }

        return merged.Values
            .OrderByDescending(stat => stat.AmountCents)
            .ThenByDescending(stat => stat.JobCount)
            .ToList();
    }

    /// <summary>时段分布：按本地小时聚合任务数与金额。</summary>
    public async Task<IReadOnlyList<HourPrintStat>> GetHourStatsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var counts = new int[24];
        var amounts = new long[24];

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CreatedAtUtc, AmountCents
            FROM PrintJobs
            WHERE IsDeleted = 0 AND CreatedAtUtc >= $from AND CreatedAtUtc < $to;
            """;
        Add(command, "$from", ToIso(fromUtc));
        Add(command, "$to", ToIso(toUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var hour = ParseDate(GetString(reader, "CreatedAtUtc")).ToLocalTime().Hour;
            counts[hour] += 1;
            amounts[hour] += GetInt64(reader, "AmountCents");
        }

        var list = new List<HourPrintStat>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            list.Add(new HourPrintStat(hour, counts[hour], amounts[hour]));
        }

        return list;
    }

    private sealed class DailyAccumulator
    {
        public long RevenueCents;
        public int BillCount;
        public int JobCount;
        public int PageCount;
        public int SheetCount;
        public long JobAmountCents;
    }

    private static DailyAccumulator GetOrAdd(
        SortedDictionary<DateOnly, DailyAccumulator> map,
        DateOnly date)
    {
        if (!map.TryGetValue(date, out var accumulator))
        {
            accumulator = new DailyAccumulator();
            map[date] = accumulator;
        }

        return accumulator;
    }
}
