using System;

namespace visland.Gathering;

internal interface IDismountTask : IDisposable {
    bool Completed { get; }
    Exception? Fault { get; }
    void Start();
    void Stop();
}

internal enum DismountResult { Pending, Dismounted, Failed }

// Frame-driven watchdog around clib's coroutine. Never start a retry before the old task exits.
internal sealed class DismountController(Func<IDismountTask> createTask) {
    private IDismountTask? _task;
    private long _startedAt, _nextAttemptAt;
    private long? _cancelledAt;
    private int _attemptTimeoutMs;
    public bool Active => _task != null || Attempts > 0;
    public int Attempts { get; private set; }
    public string? Failure { get; private set; }

    public DismountResult Update(long now, bool mounted, bool ready, int retries, int attemptTimeoutMs = 8000) {
        var maximumAttempts = Math.Clamp(retries, 0, 20) + 1;
        if (!mounted) {
            _task?.Stop();
            if (_task is { Completed: false }) return WaitForCancellation(now);
            Reset();
            return DismountResult.Dismounted;
        }
        if (_task != null) {
            if (_task.Completed) {
                Failure ??= _task.Fault?.Message ?? "still-mounted";
                _task.Dispose();
                _task = null;
                _cancelledAt = null;
                _nextAttemptAt = now + 500;
                return Attempts >= maximumAttempts ? DismountResult.Failed : DismountResult.Pending;
            }
            if (_cancelledAt.HasValue) return WaitForCancellation(now);
            if (now - _startedAt >= _attemptTimeoutMs) {
                Failure = "dismount-timeout";
                _task.Stop();
                _cancelledAt = now;
            }
            return DismountResult.Pending;
        }
        if (Attempts >= maximumAttempts) return DismountResult.Failed;
        if (!ready || now < _nextAttemptAt) return DismountResult.Pending;
        ++Attempts;
        Failure = null;
        try {
            _task = createTask();
            _attemptTimeoutMs = Math.Max(1000, attemptTimeoutMs);
            _startedAt = now;
            _task.Start();
        }
        catch (Exception ex) {
            Failure = ex.Message;
            _task?.Dispose();
            _task = null;
            _nextAttemptAt = now + 500;
            return Attempts >= maximumAttempts ? DismountResult.Failed : DismountResult.Pending;
        }
        return DismountResult.Pending;
    }

    private DismountResult WaitForCancellation(long now) {
        _cancelledAt ??= now;
        if (now - _cancelledAt.Value < 5000) return DismountResult.Pending;
        Failure = "dismount-cancel-timeout";
        return DismountResult.Failed;
    }

    public void Reset() {
        _task?.Dispose();
        _task = null;
        Attempts = 0;
        Failure = null;
        _cancelledAt = null;
        _nextAttemptAt = 0;
    }
}
