namespace PrintPal.Core;

/// <summary>猛水类型：账单 / 打印任务（文件）。</summary>
public enum PrintFlowKind
{
    Bill,
    Job,
}

/// <summary>
/// 打印流水：账单与打印任务各一条，用于审计与追溯。
/// 编号规则：
///   账单：yyyyMMddHHmmss + 3 位毫秒，共 17 位，如 20261102223812001；
///   文件：账单编号 + 3 位文件序号，共 20 位，如 20261102223812001001，同一账单内序号递增。
/// 流水保存账单/任务的快照，账单或任务被删除后仍可追溯。
/// </summary>
public sealed class PrintFlow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>流水编号（17 位账单 / 20 位文件），全局唯一。</summary>
    public string FlowNo { get; set; } = "";

    public PrintFlowKind Kind { get; set; }

    /// <summary>文件流水对应的账单流水号；账单流水为 null。</summary>
    public string? ParentFlowNo { get; set; }

    public Guid? BillId { get; set; }

    public string? BillNameSnapshot { get; set; }

    public Guid? JobId { get; set; }

    public string? DocumentNameSnapshot { get; set; }

    /// <summary>纸张 · 颜色 · 单双面 快照。</summary>
    public string? SpecSnapshot { get; set; }

    public long AmountCents { get; set; }

    public string? StatusSnapshot { get; set; }

    public string? TargetPrinterSnapshot { get; set; }

    public string? UserNameSnapshot { get; set; }

    /// <summary>文件在所属账单内的序号（1 起，对应编号末 3 位）；账单流水为 0。</summary>
    public int SequenceInBill { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public bool IsDeleted { get; set; }

    public string? Note { get; set; }
}

/// <summary>流水列表条目：流水本身 + 当前账单是否还存在（缺失时用快照展示）。</summary>
public sealed record PrintFlowEntry(
    PrintFlow Flow,
    bool BillExists,
    string BillDisplayName,
    long AmountCents,
    string StatusText);

/// <summary>按天汇总：营业额按结账日，任务/页数/张数按打印时间。</summary>
public sealed record DailyPrintStat(
    DateOnly Date,
    long RevenueCents,
    int BillCount,
    int JobCount,
    int PageCount,
    int SheetCount,
    long JobAmountCents);

/// <summary>维度统计（纸张/颜色/单双面/目标打印机）。</summary>
public sealed record FlowDimensionStat(string Dimension, string Key, int JobCount, int PageCount, int SheetCount, long AmountCents);

/// <summary>时段分布（本地时间 0–23 点）。</summary>
public sealed record HourPrintStat(int Hour, int JobCount, long AmountCents);
