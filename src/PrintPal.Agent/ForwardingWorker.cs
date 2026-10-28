using System.Collections.Concurrent;
using System.Threading.Channels;
using PrintPal.Capture;
using PrintPal.Core;
using PrintPal.Forwarding;
using PrintPal.Storage;

namespace PrintPal.Agent;

/// <summary>
/// Serial background forwarder with bounded retry and an explicit timeout/Unknown
/// outcome. Capture returns as soon as the job is priced and queued, so a slow or
/// failing printer never blocks the Windows spooler.
/// </summary>
public sealed class ForwardingWorker : IAsyncDisposable
{
    private readonly PrintPalDatabase _database;
    private readonly DocumentStore _documentStore;
    private readonly PrintJobForwarder _forwarder;
    private readonly AgentOptions _options;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly ConcurrentDictionary<Guid, byte> _queued = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public ForwardingWorker(
        PrintPalDatabase database,
        DocumentStore documentStore,
        PrintJobForwarder forwarder,
        AgentOptions options)
    {
        _database = database;
        _documentStore = documentStore;
        _forwarder = forwarder;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The forwarding worker is already running.");
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => ProcessLoopAsync(_cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public void Enqueue(Guid jobId)
    {
        if (_loop is null)
        {
            throw new InvalidOperationException("The forwarding worker is not running.");
        }

        if (!_queued.TryAdd(jobId, 0))
        {
            return;
        }

        if (!_queue.Writer.TryWrite(jobId))
        {
            _queued.TryRemove(jobId, out _);
            Log($"could not enqueue job {jobId}");
        }
    }

    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();

        var cancellation = _cancellation;
        cancellation?.Cancel();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        cancellation?.Dispose();
        _cancellation = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var jobId in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"job {jobId} worker error: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _queued.TryRemove(jobId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _database.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null ||
            job.Status is JobStatus.Succeeded or JobStatus.Canceled)
        {
            return;
        }

        if (job.TargetPrinterId is not Guid printerId)
        {
            await MarkFailedAsync(job, "没有目标打印机", cancellationToken).ConfigureAwait(false);
            return;
        }

        var printers = await _database.GetPrintersAsync(cancellationToken).ConfigureAwait(false);
        var printer = printers.FirstOrDefault(candidate => candidate.Id == printerId);
        if (printer is null || !printer.Enabled)
        {
            await MarkFailedAsync(job, "目标打印机不存在或已停用", cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] document;
        try
        {
            document = await _documentStore.ReadAsync(job.DocumentPath!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(job, $"读取文档失败：{ex.Message}", cancellationToken).ConfigureAwait(false);
            return;
        }

        var maxAttempts = Math.Max(1, _options.ForwardMaxAttempts);
        var delays = _options.ForwardRetryDelays;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            job.Status = JobStatus.Forwarding;
            job.StatusReason = $"转发中（第 {attempt}/{maxAttempts} 次）";
            await SaveAsync(job, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ForwardTimeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(30)
                : _options.ForwardTimeout);

            string? error = null;

            try
            {
                var result = await _forwarder.ForwardAsync(job, printer, document, timeout.Token)
                    .ConfigureAwait(false);

                if (result.Success)
                {
                    job.Status = JobStatus.Succeeded;
                    job.StatusReason = string.IsNullOrWhiteSpace(result.Message) ? null : result.Message;
                    job.ForwardedAtUtc = DateTimeOffset.UtcNow;
                    await SaveAsync(job, cancellationToken).ConfigureAwait(false);
                    Log($"job {job.Id} forwarded to {printer.Name} on attempt {attempt}");
                    return;
                }

                error = result.Message ?? "转发失败";
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                      !cancellationToken.IsCancellationRequested)
            {
                job.Status = JobStatus.Unknown;
                job.StatusReason = "转发超时，结果未知；未自动重试";
                await SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
                Log($"job {job.Id} forward timed out; marked Unknown");
                return;
            }
            catch (TimeoutException ex)
            {
                job.Status = JobStatus.Unknown;
                job.StatusReason = $"转发超时，结果未知；未自动重试：{ex.Message}";
                await SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
                Log($"job {job.Id} forward timed out; marked Unknown");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                job.Status = JobStatus.Unknown;
                job.StatusReason = "应用退出，转发结果未知";
                await SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (attempt >= maxAttempts)
            {
                job.Status = JobStatus.Failed;
                job.StatusReason = $"转发失败（已尝试 {attempt} 次）：{error}";
                await SaveAsync(job, cancellationToken).ConfigureAwait(false);
                Log($"job {job.Id} failed after {attempt} attempts: {error}");
                return;
            }

            job.Status = JobStatus.Priced;
            job.StatusReason = $"第 {attempt} 次失败，等待重试：{error}";
            await SaveAsync(job, cancellationToken).ConfigureAwait(false);

            var delay = delays.Count == 0
                ? TimeSpan.FromSeconds(2)
                : delays[Math.Min(attempt - 1, delays.Count - 1)];

            Log($"job {job.Id} attempt {attempt} failed: {error}; retrying in {delay.TotalSeconds:0.###}s");

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.Status = JobStatus.Unknown;
                job.StatusReason = "应用退出，等待重试被取消";
                await SaveAsync(job, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task MarkFailedAsync(PrintJob job, string reason, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Failed;
        job.StatusReason = reason;
        await SaveAsync(job, cancellationToken).ConfigureAwait(false);
        Log($"job {job.Id} not forwarded: {reason}");
    }

    private Task SaveAsync(PrintJob job, CancellationToken cancellationToken) =>
        _database.SaveJobAsync(job, cancellationToken);

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
