using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace visland.Gathering;

// Poll on the framework thread so all IPC calls stay on that thread.
internal sealed class MovementDetourPlanner {
    private readonly Queue<Vector3> _candidates = new();
    private readonly HashSet<Vector3> _used = new();
    private Task<List<Vector3>>? _pending;
    private List<Vector3>? _firstLeg;
    private Vector3 _origin, _target, _via;
    private bool _fly;
    private long _started;
    public bool Active { get; private set; }
    public bool Initialized { get; private set; }

    public void Reset() {
        if (_pending != null)
            _ = _pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        _pending = null;
        _firstLeg = null;
        _candidates.Clear();
        _used.Clear();
        Active = Initialized = false;
    }

    public void Begin(Vector3 origin, Vector3 target, bool fly) {
        Reset();
        _target = target;
        _fly = fly;
        var forward = new Vector3(target.X - origin.X, 0, target.Z - origin.Z);
        forward = forward.LengthSquared() < 0.01f ? Vector3.UnitZ : Vector3.Normalize(forward);
        var side = new Vector3(-forward.Z, 0, forward.X);
        foreach (var radius in new[] { 4f, 8f, 12f }) {
            _candidates.Enqueue(origin + side * radius);
            _candidates.Enqueue(origin - side * radius);
            _candidates.Enqueue(origin + (side - forward) * radius);
            _candidates.Enqueue(origin + (-side - forward) * radius);
        }
        Initialized = true;
        Resume(origin);
    }

    public void Resume(Vector3 origin) { _origin = origin; Active = true; }

    // Null with Active=true means still calculating; null with Active=false means exhausted.
    public List<Vector3>? Tick(long now, Func<Vector3, Vector3?> project,
        Func<Vector3, Vector3, bool, Task<List<Vector3>>?> pathfind) {
        if (!Active) return null;
        if (_pending != null) {
            if (!_pending.IsCompleted) {
                if (now - _started > 15000) { Reset(); }
                return null;
            }
            var task = _pending;
            _pending = null;
            if (task.IsCanceled || task.IsFaulted) { _ = task.Exception; _firstLeg = null; return null; }
            var path = task.Result;
            var endpoint = _firstLeg == null ? _via : _target;
            if (path.Count == 0 || Vector3.DistanceSquared(path[^1], endpoint) > 4) {
                _firstLeg = null;
                return null;
            }
            if (_firstLeg == null) {
                _firstLeg = path;
                _pending = pathfind(_via, _target, _fly);
                _started = now;
                if (_pending == null) _firstLeg = null;
                return null;
            }
            var combined = new List<Vector3>(_firstLeg);
            combined.AddRange(path);
            _firstLeg = null;
            Active = false;
            return combined;
        }
        while (_candidates.TryDequeue(out var candidate)) {
            var projected = _fly ? candidate : project(candidate);
            if (!projected.HasValue || Vector3.DistanceSquared(_origin, projected.Value) < 4 || !_used.Add(projected.Value)) continue;
            _via = projected.Value;
            _pending = pathfind(_origin, _via, _fly);
            _started = now;
            if (_pending != null) return null;
        }
        Active = false;
        return null;
    }
}
