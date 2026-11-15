using System;
using System.Collections.Generic;
using System.Numerics;

namespace visland.Gathering;

internal enum MovementResult { None, Running, Arrived, Cancelled, TimedOut, PathfindingFailed, Stuck }

// One waypoint owns its queries and movement. All calls run on the framework thread.
internal sealed class MovementOperation(Action stopNavigation, Action<List<Vector3>, bool> followPath,
    Func<Vector3, bool, bool> requestPath) {
    public NavigationStuckDetector Stuck { get; } = new();
    public JumpRecovery Jump { get; } = new();
    public MovementDetourPlanner Detour { get; } = new();
    public GroundShortcut Shortcut { get; } = new();
    public GroundShortcut OffMesh { get; } = new();
    public bool Requested;
    public long RequestedAt;
    public bool Active { get; private set; }
    public Vector3 Target { get; private set; }
    public int Waypoint { get; private set; } = -1;
    public MovementResult Result { get; private set; }
    public MovementResult LastFailure { get; private set; }
    private bool _ownsNavigation;
    private long? _lastTick;
    private long _elapsed;

    public void Begin(int waypoint, Vector3 target) {
        Cancel();
        Waypoint = waypoint;
        Target = target;
        Active = true;
        LastFailure = MovementResult.None;
        Result = MovementResult.Running;
        RestartAttempt();
    }

    public void Follow(List<Vector3> path, bool volume) {
        if (!Active) return;
        _ownsNavigation = true;
        try { followPath(path, volume); }
        catch { StopNavigation(); Result = LastFailure = MovementResult.PathfindingFailed; throw; }
    }

    public bool Request(Vector3 target, bool allowFlight) {
        if (!Active) return false;
        _ownsNavigation = true;
        try {
            if (requestPath(target, allowFlight)) return true;
            Result = LastFailure = MovementResult.PathfindingFailed;
            return false;
        }
        catch { StopNavigation(); Result = LastFailure = MovementResult.PathfindingFailed; throw; }
    }

    public void StopNavigation() {
        Requested = false;
        Stuck.Reset();
        if (!_ownsNavigation) return;
        _ownsNavigation = false;
        stopNavigation();
    }

    // Count only active movement updates; casting, mounting and interaction waits suspend the clock.
    public bool CheckTimeout(long now) {
        if (!Active) return false;
        if (_lastTick is { } last && now >= last) _elapsed += now - last;
        _lastTick = now;
        if (_elapsed < 180000) return false;
        Result = LastFailure = MovementResult.TimedOut;
        StopNavigation();
        return true;
    }

    public void Suspend() { _lastTick = null; Stuck.Reset(); }

    public void RestartAttempt() {
        _elapsed = 0;
        _lastTick = null;
        Stuck.Reset();
        Result = MovementResult.Running;
    }

    public void Fail(MovementResult result) { Result = LastFailure = result; StopNavigation(); }

    public bool Complete(Vector3 position, float radius, bool airborneInteraction) {
        var distanceSquared = Vector3.DistanceSquared(position, Target);
        if ((!Active && Result != MovementResult.Arrived) || airborneInteraction || !float.IsFinite(radius) || radius < 0
            || !float.IsFinite(distanceSquared) || distanceSquared > radius * radius) return false;
        if (Result == MovementResult.Arrived) return true;
        Release();
        Active = false;
        Result = MovementResult.Arrived;
        return true;
    }

    public void Cancel() {
        Release();
        if (Active) Result = MovementResult.Cancelled;
        Active = false;
    }

    private void Release() {
        // Invalidate calculations before stopping movement, so late results cannot restart it.
        Detour.Reset();
        Jump.Reset();
        Shortcut.Reset();
        OffMesh.Reset();
        Suspend();
        StopNavigation();
    }
}
