namespace KnowledgeMemory.Storage;

/// <summary>
/// Coalesces deletes into delayed background reclaim runs. Busy checkpoints are retried with backoff
/// and shown as "pending"/"busy" instead of failing silently.
/// </summary>
public sealed class MaintenanceScheduler : IAsyncDisposable
{
    private readonly DiskManager _disk;
    private readonly TimeSpan _delay;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _time;
    private int _pending;
    private Task? _loop;

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public MaintenanceScheduler(DiskManager disk, TimeSpan delay, TimeProvider? time = null)
    {
        _disk = disk;
        _delay = delay;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Completes whenever the loop finishes one run (for tests and diagnostics).</summary>
    public event Action<DiskManager.ReclaimReport>? RunCompleted;

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    public void RequestReclaim()
    {
        Interlocked.Exchange(ref _pending, 1);
        _disk.SetStatus(s => s.State == "running" ? s : s with { State = "pending", NextAttemptAtUtc = DateTime.UtcNow + _delay });
        _signal.Release();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct);
                // Coalesce close deletes: wait for the delay, swallowing further signals.
                await Task.Delay(_delay, _time, ct);
                while (_signal.CurrentCount > 0) await _signal.WaitAsync(ct);
                if (Interlocked.Exchange(ref _pending, 0) == 0) continue;

                _disk.SetStatus(s => s with { State = "running" });
                var report = await _disk.ReclaimIncrementalAsync(ct);
                var now = DateTime.UtcNow;
                switch (report.Outcome)
                {
                    case DiskManager.ReclaimOutcome.Done:
                        backoff = TimeSpan.FromSeconds(2);
                        _disk.SetStatus(s => s with
                        {
                            State = Volatile.Read(ref _pending) == 1 ? "pending" : "idle",
                            LastRunAtUtc = now, LastResult = report.Message, LastResultCode = "reclaim_done", LastError = null,
                            LastBytesBefore = report.BytesBefore, LastBytesAfter = report.BytesAfter, NextAttemptAtUtc = null,
                        });
                        break;
                    case DiskManager.ReclaimOutcome.MorePending:
                        _disk.SetStatus(s => s with { State = "pending", LastRunAtUtc = now, LastResult = report.Message, LastResultCode = "reclaim_partial", LastBytesBefore = report.BytesBefore, LastBytesAfter = report.BytesAfter });
                        Interlocked.Exchange(ref _pending, 1);
                        _signal.Release();
                        break;
                    case DiskManager.ReclaimOutcome.CheckpointBusy:
                        _disk.SetStatus(s => s with
                        {
                            State = "busy", LastRunAtUtc = now, LastResult = report.Message, LastResultCode = "checkpoint_busy",
                            LastBytesBefore = report.BytesBefore, LastBytesAfter = report.BytesAfter, NextAttemptAtUtc = now + backoff,
                        });
                        RunCompleted?.Invoke(report);
                        await Task.Delay(backoff, _time, ct);
                        backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                        Interlocked.Exchange(ref _pending, 1);
                        _signal.Release();
                        continue;
                }
                RunCompleted?.Invoke(report);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;
                _disk.SetStatus(s => s with { State = "error", LastRunAtUtc = now, LastResultCode = "maintenance_error", LastError = ex.Message, NextAttemptAtUtc = now + backoff });
                try { await Task.Delay(backoff, _time, ct); } catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                Interlocked.Exchange(ref _pending, 1);
                _signal.Release();
            }
        }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _signal.Dispose();
    }
}
