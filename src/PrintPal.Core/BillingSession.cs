namespace PrintPal.Core;

public enum BillingSessionStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// One open/closed billing period per tag. Totals are snapshotted when closed so
/// later edits/deletes cannot change historical bills.
/// </summary>
public sealed class BillingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BillingTagId { get; set; }

    public string TagNameSnapshot { get; set; } = string.Empty;

    public DateTimeOffset OpenedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ClosedAtUtc { get; set; }

    public BillingSessionStatus Status { get; set; } = BillingSessionStatus.Open;

    public int JobCount { get; set; }

    public int TotalPages { get; set; }

    public long AmountCents { get; set; }

    public string? Note { get; set; }
}

public sealed record BillingContext(BillingTag Tag, BillingSession Session);

public sealed record SessionSummary(
    Guid SessionId,
    int JobCount,
    int TotalPages,
    long AmountCents,
    int SucceededCount,
    int BlockedCount);

public sealed record CheckoutResult(
    BillingSession ClosedSession,
    BillingTag NextTag,
    BillingSession NextSession,
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
