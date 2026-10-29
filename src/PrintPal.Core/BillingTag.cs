namespace PrintPal.Core;

/// <summary>
/// A billing tab. Tags are created automatically as 标签1, 标签2, ... and can be
/// renamed by the operator. Archiving/checkout removes the tag from the tab strip
/// while keeping the session history.
/// </summary>
public sealed class BillingTag
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int SequenceNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Note { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsArchived { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? ArchivedAtUtc { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public bool IsOpen => !IsArchived && !IsDeleted;
}
