namespace PrintPal.Core;

/// <summary>账单状态：当前（收银台正在记账的一张）、已挂起、已结账。</summary>
public enum BillStatus
{
    Active = 0,
    Parked = 1,
    Closed = 2,
}

/// <summary>
/// 一张账单 = 一个客户的一次消费。全局恰好一张 <see cref="BillStatus.Active"/> 账单，
/// 新打印任务只归属当前账单。客户插队时把当前账单挂起（<see cref="BillStatus.Parked"/>），
/// 任务与金额原地保留，取回即可继续；结账写入快照字段并转为 <see cref="BillStatus.Closed"/>，
/// 之后只读。
/// </summary>
public sealed class Bill
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int SequenceNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>备注：客户电话、单号等。</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public BillStatus Status { get; set; } = BillStatus.Parked;

    /// <summary>最近一次被挂起的时间，用于挂起区排序与展示。</summary>
    public DateTimeOffset? ParkedAtUtc { get; set; }

    public DateTimeOffset? ClosedAtUtc { get; set; }

    // ---- checkout snapshot (only meaningful once Status == Closed) ----

    public int JobCount { get; set; }

    public int TotalPages { get; set; }

    public long AmountCents { get; set; }

    public bool IsOpen => Status is BillStatus.Active or BillStatus.Parked;
}

/// <summary>未结账账单 + 实时汇总，用于挂起区、"移到其他账单"对话框与历史筛选。</summary>
public sealed record BillSummary(
    Guid Id,
    string Name,
    int SequenceNumber,
    BillStatus Status,
    int JobCount,
    int TotalPages,
    long AmountCents,
    DateTimeOffset? ParkedAtUtc);

public sealed record CheckoutResult(
    Bill ClosedBill,
    Bill NextBill,
    IReadOnlyList<PrintJob> BlockingJobs);

public sealed class CheckoutBlockedException : InvalidOperationException
{
    public CheckoutBlockedException(IReadOnlyList<PrintJob> blockingJobs)
        : base("存在未完成的打印任务，不能结账。")
    {
        BlockingJobs = blockingJobs;
    }

    public IReadOnlyList<PrintJob> BlockingJobs { get; }
}

/// <summary>挂起一张没有任何打印记录的账单没有意义，由 UI 与存储层共同拒绝。</summary>
public sealed class NoBillJobsException : InvalidOperationException
{
    public NoBillJobsException()
        : base("当前账单还没有打印记录，不能挂起。")
    {
    }
}
