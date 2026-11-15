using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace visland.Gathering;

internal enum PathMovementState { Pending, Moving, Arrived, Failed, Cancelled }

// Queries only calculate a path. This operation alone decides whether it may start movement.
internal sealed class PathMovement(Vector3 target, float tolerance,
    Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> query,
    Action<List<Vector3>> followPath, Action stopPath, Func<bool> isRunning) {
    private readonly PathfindRequest _request = new();
    private bool _started, _ownsPath;
    public PathMovementState State { get; private set; }
    public string? Failure { get; private set; }
    public bool Active => State is PathMovementState.Pending or PathMovementState.Moving;

    public void Begin(Vector3 origin, long now) {
        if (!Active || _started) return;
        _started = true;
        if (!_request.Begin(origin, target, now, query))
            Fail("clib-path-query-rejected");
    }

    public void Update(Vector3 position, long now, bool available) {
        if (!Active || !_started) return;
        if (!available) { Fail("clib-path-state-changed"); return; }
        var distance = Vector3.DistanceSquared(position, target);
        if (!float.IsFinite(distance)) { Fail("clib-path-position-invalid"); return; }
        if (distance <= tolerance * tolerance) {
            State = PathMovementState.Arrived;
            Release();
            return;
        }
        var result = _request.Poll(now, path => {
            if (!Active) return;
            _ownsPath = true;
            followPath(path);
            State = PathMovementState.Moving;
        });
        if (result != null && result != "query-applied") {
            Fail($"clib-path-{result}");
            return;
        }
        // Give the IPC consumer one frame to reflect the newly applied path.
        if (State == PathMovementState.Moving && result != "query-applied" && !isRunning())
            Fail("clib-path-move-incomplete");
    }

    public void Cancel() {
        if (!Active) return;
        State = PathMovementState.Cancelled;
        Release();
    }

    private void Fail(string reason) {
        Failure = reason;
        State = PathMovementState.Failed;
        Release();
    }

    private void Release() {
        // Drop the result synchronously even if an older vnavmesh ignores cancellation tokens.
        _request.Reset();
        if (!_ownsPath) return;
        _ownsPath = false;
        stopPath();
    }
}
