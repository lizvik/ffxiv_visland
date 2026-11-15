using System;
using System.Collections.Generic;
using System.Numerics;

namespace visland.Gathering;

internal readonly record struct MovementSettings(uint MountId, float MountDistance, bool AllowFlight) {
    public bool ShouldMount(float? pathDistance) => pathDistance is { } distance
        && float.IsFinite(distance) && distance >= MountDistance;
}

internal interface IMovementBackend : IDisposable {
    bool Busy { get; }
    string? Failure { get; }
    bool Start(Vector3 target, float tolerance, MovementSettings settings);
    void Cancel();
}

// Serialize backend cleanup and path replacement, including switches to flight or direct movement.
internal sealed class NavigationController(IMovementBackend backend,
    Action<List<Vector3>, bool> followPath) : IDisposable {
    private (List<Vector3> Path, bool Volume)? _queuedPath;
    private bool _active, _disposed;
    public bool Busy => backend.Busy || _queuedPath.HasValue;
    public bool Following => _active && backend.Busy;

    public bool Request(Vector3 target, float tolerance, MovementSettings settings) {
        if (_disposed || Busy) return false;
        return _active = backend.Start(target, tolerance, settings);
    }

    public void Follow(List<Vector3> path, bool volume) {
        if (_disposed) return;
        backend.Cancel();
        _active = false;
        _queuedPath = (new(path), volume);
        ApplyQueuedPath();
    }

    public void Stop() {
        _queuedPath = null;
        backend.Cancel();
        _active = false;
    }

    public string? Update() {
        if (_disposed) return null;
        string? failure = null;
        if (_active && !backend.Busy) {
            _active = false;
            failure = backend.Failure;
        }
        ApplyQueuedPath();
        return failure;
    }

    private void ApplyQueuedPath() {
        if (backend.Busy || _queuedPath is not { } queued) return;
        _queuedPath = null;
        followPath(queued.Path, queued.Volume);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        Stop();
        backend.Dispose();
    }
}
