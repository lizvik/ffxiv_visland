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
    private readonly PathfindRequest _movementRequest = new();
    private bool _movementUsesVolume;

    public NavmeshIPC() {
        _navIsReady = Service.Interface.GetIpcSubscriber<bool>($"{Name}.Nav.IsReady");
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
        Service.Framework.Update += Update;
    }

    public bool IsEnabled => _navIsReady.HasFunction;

    public bool IsReady() => _navIsReady.HasFunction && _navIsReady.InvokeFunc();
    public float BuildProgress() => _navBuildProgress.HasFunction ? _navBuildProgress.InvokeFunc() : 0;
    public void Reload() { if (_navReload.HasFunction) _navReload.InvokeFunc(); }
    public void Rebuild() { if (_navRebuild.HasFunction) _navRebuild.InvokeFunc(); }
    public Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to, bool fly = false)
        => _navPathfind.HasFunction ? _navPathfind.InvokeFunc(from, to, fly) : null;
    public Task<List<Vector3>>? PathfindForLanding(Vector3 from, Vector3 to, CancellationToken cancellation)
        => PathfindCancelable(from, to, true, cancellation);
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
        _movementRequest.Reset();
        if (_pathMoveTo.HasAction) _pathMoveTo.InvokeAction(waypoints, fly);
    }
    public void Stop() {
        _movementRequest.Reset();
        if (_pathStop.HasAction) _pathStop.InvokeAction();
    }
    public bool IsRunning() => _pathIsRunning.HasFunction && _pathIsRunning.InvokeFunc();
    public List<Vector3>? ListWaypoints() => _pathListWaypoints.HasFunction ? _pathListWaypoints.InvokeFunc() : null;
    public int NumWaypoints() => _pathNumWaypoints.HasFunction ? _pathNumWaypoints.InvokeFunc() : 0;
    public bool GetMovementAllowed() => _pathGetMovementAllowed.HasFunction && _pathGetMovementAllowed.InvokeFunc();
    public void SetMovementAllowed(bool value) { if (_pathSetMovementAllowed.HasAction) _pathSetMovementAllowed.InvokeAction(value); }
    public bool GetAlignCamera() => _pathGetAlignCamera.HasFunction && _pathGetAlignCamera.InvokeFunc();
    public void SetAlignCamera(bool value) { if (_pathSetAlignCamera.HasAction) _pathSetAlignCamera.InvokeAction(value); }
    public float GetTolerance() => _pathGetTolerance.HasFunction ? _pathGetTolerance.InvokeFunc() : 0;
    public void SetTolerance(float tolerance) { if (_pathSetTolerance.HasAction) _pathSetTolerance.InvokeAction(tolerance); }

    public bool PathfindAndMoveTo(Vector3 pos, bool fly) {
        if (_movementRequest.Pending) return false;
        _movementUsesVolume = fly;
        return _movementRequest.Begin(Player.Position, pos, Environment.TickCount64,
            (from, to, cancellation) => PathfindCancelable(from, to, fly, cancellation));
    }
    public bool PathfindInProgress() => _movementRequest.Pending;

    private void Update(IFramework framework) {
        var result = _movementRequest.Poll(Environment.TickCount64, path => MoveTo(path, _movementUsesVolume));
        if (result != null && result != "query-applied")
            Service.Log.Warning($"Navigation request failed: {result}");
    }

    public void Dispose() {
        Service.Framework.Update -= Update;
        Stop();
    }
}
