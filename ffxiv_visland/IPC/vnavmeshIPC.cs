using Dalamud.Plugin.Ipc;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using System.Threading;
using Dalamud.Plugin.Services;
using visland.Gathering;
using visland.Helpers;

namespace visland.IPC;

public class NavmeshIPC : IDisposable {
    public const string Name = "vnavmesh";

    private readonly ICallGateSubscriber<bool> _navIsReady;
    private readonly ICallGateSubscriber<bool> _simpleMovePending;
    private readonly ICallGateSubscriber<float> _navBuildProgress;
    private readonly ICallGateSubscriber<bool> _navReload;
    private readonly ICallGateSubscriber<bool> _navRebuild;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> _navPathfind;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> _navPathfindCancelable;
    private readonly ICallGateSubscriber<bool> _navIsAutoLoad;
    private readonly ICallGateSubscriber<bool, object> _navSetAutoLoad;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> _queryMeshNearestPoint;
    private readonly ICallGateSubscriber<Vector3, float, bool, bool> _queryMeshIsPointOnMesh;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> _queryMeshPointOnFloor;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> _pathMoveTo;
    private readonly ICallGateSubscriber<object> _pathStop;
    private readonly ICallGateSubscriber<bool> _pathIsRunning;
    private readonly ICallGateSubscriber<int> _pathNumWaypoints;
    private readonly ICallGateSubscriber<List<Vector3>> _pathListWaypoints;
    private readonly ICallGateSubscriber<bool> _pathGetMovementAllowed;
    private readonly ICallGateSubscriber<bool, object> _pathSetMovementAllowed;
    private readonly ICallGateSubscriber<bool> _pathGetAlignCamera;
    private readonly ICallGateSubscriber<bool, object> _pathSetAlignCamera;
    private readonly ICallGateSubscriber<float> _pathGetTolerance;
    private readonly ICallGateSubscriber<float, object> _pathSetTolerance;
    private readonly NavigationController _navigation;
    private readonly SimpleMoveDrain _simpleMoveDrain;
    private (List<Vector3> Path, bool Volume)? _deferredPath;
    public bool IsDrainingSimpleMove => _simpleMoveDrain.Active;
    private bool _disposed;
    public string? LastRequestFailure { get; private set; }

    public NavmeshIPC() : this(null) { }

    internal NavmeshIPC(IMovementBackend? movementBackend) {
        _navIsReady = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Nav.IsReady");
        _simpleMovePending = Service.Interface.GetIpcSubscriber<bool>($"{Name}.SimpleMove.PathfindInProgress");
        _navBuildProgress = Service.Interface.GetIpcSubscriber<float>($"{Name}.Nav.BuildProgress");
        _navReload = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Nav.Reload");
        _navRebuild = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Nav.Rebuild");
        _navPathfind = Service.Interface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>($"{Name}.Nav.Pathfind");
        _navPathfindCancelable = Service.Interface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>($"{Name}.Nav.PathfindCancelable");
        _navIsAutoLoad = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Nav.IsAutoLoad");
        _navSetAutoLoad = Service.Interface.GetIpcSubscriber<bool, object>($"{Name}.Nav.SetAutoLoad");
        _queryMeshIsPointOnMesh = Service.Interface.GetIpcSubscriber<Vector3, float, bool, bool>($"{Name}.Query.Mesh.IsPointOnMesh");
        _queryMeshNearestPoint = Service.Interface.GetIpcSubscriber<Vector3, float, float, Vector3?>($"{Name}.Query.Mesh.NearestPoint");
        _queryMeshPointOnFloor = Service.Interface.GetIpcSubscriber<Vector3, bool, float, Vector3?>($"{Name}.Query.Mesh.PointOnFloor");
        _pathMoveTo = Service.Interface.GetIpcSubscriber<List<Vector3>, bool, object>($"{Name}.Path.MoveTo");
        _pathStop = Service.Interface.GetIpcSubscriber<object>($"{Name}.Path.Stop");
        _pathIsRunning = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Path.IsRunning");
        _pathListWaypoints = Service.Interface.GetIpcSubscriber<List<Vector3>>($"{Name}.Path.ListWaypoints");
        _pathNumWaypoints = Service.Interface.GetIpcSubscriber<int>($"{Name}.Path.NumWaypoints");
        _pathGetMovementAllowed = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Path.GetMovementAllowed");
        _pathSetMovementAllowed = Service.Interface.GetIpcSubscriber<bool, object>($"{Name}.Path.SetMovementAllowed");
        _pathGetAlignCamera = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Path.GetAlignCamera");
        _pathSetAlignCamera = Service.Interface.GetIpcSubscriber<bool, object>($"{Name}.Path.SetAlignCamera");
        _pathGetTolerance = Service.Interface.GetIpcSubscriber<float>($"{Name}.Path.GetTolerance");
        _pathSetTolerance = Service.Interface.GetIpcSubscriber<float, object>($"{Name}.Path.SetTolerance");
        _simpleMoveDrain = new(() => _simpleMovePending.HasFunction && _simpleMovePending.InvokeFunc(),
            () => { if (_pathStop.HasAction) _pathStop.InvokeAction(); });
        movementBackend ??= new ClibMovementBackend(
            (from, to, cancellation) => PathfindCancelable(from, to, true, cancellation),
            path => {
                if (!_pathMoveTo.HasAction) throw new InvalidOperationException("vnavmesh Path.MoveTo is unavailable");
                _pathMoveTo.InvokeAction(path, true);
            },
            () => { if (_pathStop.HasAction) _pathStop.InvokeAction(); }, IsRunning);
        _navigation = new(movementBackend, (path, volume) => {
            if (_pathMoveTo.HasAction) _pathMoveTo.InvokeAction(path, volume);
        });
        Service.Framework.Update += Update;
    }

    public bool IsEnabled => _navIsReady.HasFunction;

    public bool IsReady() => _navIsReady.HasFunction && _navIsReady.InvokeFunc();
    public float BuildProgress() => _navBuildProgress.HasFunction ? _navBuildProgress.InvokeFunc() : 0;
    public void Reload() { if (_navReload.HasFunction) _navReload.InvokeFunc(); }
    public void Rebuild() { if (_navRebuild.HasFunction) _navRebuild.InvokeFunc(); }
    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to, bool fly = false)
        => _navPathfind.HasFunction ? _navPathfind.InvokeFunc(from, to, fly) : null;
    public Task<List<Vector3>>? PathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken cancellation)
        => _navPathfindCancelable.HasFunction ? _navPathfindCancelable.InvokeFunc(from, to, fly, cancellation) : Pathfind(from, to, fly);
    public bool IsAutoLoad() => _navIsAutoLoad.HasFunction && _navIsAutoLoad.InvokeFunc();
    public void SetAutoLoad(bool value) { if (_navSetAutoLoad.HasAction) _navSetAutoLoad.InvokeAction(value); }

    public bool IsPointOnMesh(Vector3 position) {
        if (_queryMeshIsPointOnMesh.HasFunction)
            return _queryMeshIsPointOnMesh.InvokeFunc(position, 5, true);
        // Older versions only expose nearest-point queries: require a point under the character,
        // rather than accepting any polygon somewhere inside a wide search box.
        var nearest = QueryMeshNearestPoint(position, 0.1f, 5);
        return nearest.HasValue && float.IsFinite(nearest.Value.X) && float.IsFinite(nearest.Value.Y)
            && float.IsFinite(nearest.Value.Z)
            && Vector2.DistanceSquared(new(position.X, position.Z), new(nearest.Value.X, nearest.Value.Z)) <= 0.01f
            && Math.Abs(position.Y - nearest.Value.Y) <= 5;
    }

    public Vector3? QueryMeshNearestPoint(Vector3 pos, float halfExtentXZ, float halfExtentY)
        => _queryMeshNearestPoint.HasFunction ? _queryMeshNearestPoint.InvokeFunc(pos, halfExtentXZ, halfExtentY) : null;
    public Vector3? QueryMeshPointOnFloor(Vector3 pos, bool allowUnlandable, float halfExtentXZ)
        => _queryMeshPointOnFloor.HasFunction ? _queryMeshPointOnFloor.InvokeFunc(pos, allowUnlandable, halfExtentXZ) : null;

    public void MoveTo(List<Vector3> waypoints, bool fly) {
        LastRequestFailure = null;
        if (IsDrainingSimpleMove) {
            _deferredPath = (new(waypoints), fly);
            return;
        }
        _navigation.Follow(waypoints, fly);
    }
    public void Stop() {
        _deferredPath = null;
        _navigation.Stop();
        LastRequestFailure = null;
        if (_pathStop.HasAction) _pathStop.InvokeAction();
    }
    public bool IsRunning() => _pathIsRunning.HasFunction && _pathIsRunning.InvokeFunc();
    public List<Vector3>? ListWaypoints() => _pathListWaypoints.HasFunction ? _pathListWaypoints.InvokeFunc() : null;
    public Vector3? CurrentWaypoint() {
        var points = ListWaypoints();
        if (points is not { Count: > 0 }) return null;
        var point = points[0];
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z) ? point : null;
    }
    public int? RemainingWaypointCount() => _pathNumWaypoints.HasFunction ? _pathNumWaypoints.InvokeFunc() : null;
    public int NumWaypoints() => _pathNumWaypoints.HasFunction ? _pathNumWaypoints.InvokeFunc() : 0;
    public bool GetMovementAllowed() => _pathGetMovementAllowed.HasFunction && _pathGetMovementAllowed.InvokeFunc();
    public void SetMovementAllowed(bool value) { if (_pathSetMovementAllowed.HasAction) _pathSetMovementAllowed.InvokeAction(value); }
    public bool GetAlignCamera() => _pathGetAlignCamera.HasFunction && _pathGetAlignCamera.InvokeFunc();
    public void SetAlignCamera(bool value) { if (_pathSetAlignCamera.HasAction) _pathSetAlignCamera.InvokeAction(value); }
    public float GetTolerance() => _pathGetTolerance.HasFunction ? _pathGetTolerance.InvokeFunc() : 0;
    public void SetTolerance(float tolerance) { if (_pathSetTolerance.HasAction) _pathSetTolerance.InvokeAction(tolerance); }

    public bool PathfindAndMoveTo(Vector3 pos, bool allowFlight, float? tolerance = null,
        uint mountId = 0, float mountDistance = 20) {
        if (IsDrainingSimpleMove || _navigation.Busy) return false;
        LastRequestFailure = null;
        return _navigation.Request(pos, tolerance ?? Math.Max(0.1f, GetTolerance()), new(mountId, mountDistance, allowFlight));
    }
    public bool MovementTaskBusy => _navigation.Busy;
    public bool PathfindInProgress() => IsDrainingSimpleMove || _deferredPath.HasValue
        || _navigation.Busy && (!_navigation.Following || !IsRunning());

    private void Update(IFramework framework) {
        _simpleMoveDrain.Update();
        if (IsDrainingSimpleMove) return;
        if (_disposed) {
            Service.Framework.Update -= Update;
            return;
        }
        if (_deferredPath is { } deferred) {
            _deferredPath = null;
            _navigation.Follow(deferred.Path, deferred.Volume);
        }
        if (_navigation.Update() is { } failure) {
            LastRequestFailure = failure;
            Service.Log.Warning($"Navigation request failed: {failure}");
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _navigation.Dispose();
        // A SimpleMove query outlives clib cancellation. Keep only its cleanup callback until drained.
        if (!IsDrainingSimpleMove) Service.Framework.Update -= Update;
    }

    internal void DrainSimpleMove(Func<bool> cleanupCompleted) {
        _simpleMoveDrain.Begin(cleanupCompleted);
    }
}
