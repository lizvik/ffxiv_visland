using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace visland.Gathering;

// Nav.Pathfind only calculates. Only this owner may apply a still-current result to movement.
internal sealed class PathfindRequest {
    private Task<List<Vector3>>? _task;
    private CancellationTokenSource? _cancel;
    private Vector3 _destination;
    private long _started;
    public bool Pending => _task != null;

    public bool Begin(Vector3 from, Vector3 to, long now, Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> query) {
        Reset();
        _cancel = new();
        _destination = to;
        _started = now;
        try { _task = query(from, to, _cancel.Token); }
        catch { Reset(); throw; }
        if (_task != null) return true;
        Reset();
        return false;
    }

    public string? Poll(long now, Action<List<Vector3>> apply) {
        if (_task == null) return null;
        if (now - _started >= 10000) { Reset(); return "query-timeout"; }
        if (!_task.IsCompleted) return null;
        var completed = _task;
        _task = null;
        _cancel?.Dispose();
        _cancel = null;
        if (completed.IsFaulted) { _ = completed.Exception; return "query-faulted"; }
        if (completed.IsCanceled) return "query-cancelled";
        var path = completed.Result;
        if (path.Count == 0 || Vector3.DistanceSquared(path[^1], _destination) > 4)
            return "query-incomplete";
        apply(path);
        return "query-applied";
    }

    public void Reset() {
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
        if (_task != null)
            _ = _task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        _task = null;
    }
}
