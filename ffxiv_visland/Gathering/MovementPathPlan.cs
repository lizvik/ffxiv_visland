using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace visland.Gathering;

// Measure once per movement leg before choosing a mode. The query never starts movement itself.
internal sealed class MovementPathPlan {
    private readonly PathfindRequest _request = new();
    private Vector3 _destination;
    private bool _allowVolumeFallback;
    public Vector3 Origin { get; private set; }
    public bool Started { get; private set; }
    public bool Ready { get; private set; }
    public bool UsesVolume { get; private set; }
    public float? Distance { get; private set; }
    public List<Vector3>? Path { get; private set; }

    public void Reset() {
        _request.Reset();
        Started = Ready = UsesVolume = false;
        Distance = null;
        Path = null;
    }

    public void Begin(Vector3 origin, Vector3 destination, long now, bool usesVolume, bool allowVolumeFallback,
        Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>?> query) {
        Reset();
        Origin = origin;
        _destination = destination;
        UsesVolume = usesVolume;
        _allowVolumeFallback = allowVolumeFallback;
        Started = true;
        BeginQuery(now, query);
    }

    public void Tick(long now, Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>?> query) {
        if (!Started || Ready) return;
        _request.Poll(now, path => {
            if (!TryCalculateDistance(Origin, path, out var distance)) return;
            Distance = distance;
            Path = path;
            Ready = true;
        });
        if (Ready || _request.Pending) return;
        if (!UsesVolume && _allowVolumeFallback) {
            UsesVolume = true;
            BeginQuery(now, query);
        }
        else {
            // No usable path: let direct movement/recovery try, without guessing its length.
            Ready = true;
        }
    }

    private void BeginQuery(long now, Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>?> query)
        => _request.Begin(Origin, _destination, now, (from, to, cancellation) => query(from, to, UsesVolume, cancellation));

    public List<Vector3>? TakePath() {
        var path = Path;
        Path = null;
        return path;
    }

    public static bool TryCalculateDistance(Vector3 origin, IReadOnlyList<Vector3> path, out float distance) {
        distance = 0;
        if (path.Count == 0) return false;
        var previous = origin;
        foreach (var point in path) {
            distance += Vector3.Distance(previous, point);
            if (!float.IsFinite(distance)) return false;
            previous = point;
        }
        return true;
    }
}
