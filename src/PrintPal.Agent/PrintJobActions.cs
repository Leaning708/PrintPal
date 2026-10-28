using PrintPal.Core;
using PrintPal.Storage;

namespace PrintPal.Agent;

/// <summary>
/// Manual-forward actions shared by the in-process agent and the service-mode
/// UI proxy. Preparing the job is enough: the agent's pending-job scanner will
/// enqueue it, and the service worker performs the actual forwarding.
/// </summary>
public static class PrintJobActions
{
    public static async Task<PrintJob> PrepareManualForwardAsync(
        PrintPalDatabase database,
        Guid jobId,
        Guid? targetPrinterId = null,
        CancellationToken cancellationToken = default)
    {
        var job = await database.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false) ??
                  throw new InvalidOperationException($"Job {jobId} not found.");

        if (job.Status == JobStatus.Succeeded)
        {
            throw new InvalidOperationException("该任务已经转发成功。");
        }

        if (job.Status == JobStatus.Forwarding)
        {
            throw new InvalidOperationException("该任务正在转发中，请稍后再试。");
        }

        if (job.Status == JobStatus.NeedsReview)
        {
            throw new InvalidOperationException("待确认任务还没有有效计价，不能转发。");
        }

        if (targetPrinterId is Guid printerId)
        {
            var printers = await database.GetPrintersAsync(cancellationToken).ConfigureAwait(false);
            var printer = printers.FirstOrDefault(candidate => candidate.Id == printerId);
            if (printer is null || !printer.Enabled)
            {
                throw new InvalidOperationException("目标打印机不存在或已停用。");
            }

            job.TargetPrinterId = printer.Id;
        }

        if (job.TargetPrinterId is null)
        {
            throw new InvalidOperationException("该任务没有目标打印机，请先选择目标。");
        }

        job.Status = JobStatus.Priced;
        job.StatusReason = targetPrinterId.HasValue
            ? "手动选择目标，等待转发"
            : "手动重试，等待转发";
        job.ForwardedAtUtc = null;
        await database.SaveJobAsync(job, cancellationToken).ConfigureAwait(false);
        return job;
    }
}
