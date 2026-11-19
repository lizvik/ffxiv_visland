using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using ECommons.Throttlers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace visland.Gathering;

public class GatherRouteExec : IDisposable {
    public GatherRouteDB RouteDB;
    public GatherRouteDB.Route? CurrentRoute;
    public GatherRouteDB.Route? SourceRoute { get; private set; }
    public int CurrentWaypoint;
    public bool ContinueToNext;
    public bool Paused;
    public bool Loop;
    public bool Waiting;
    public long WaitUntil;
    public bool Pathfind;

    public State CurrentState;
    public enum State {
        None,
        WaitingForNavmesh,
        Gathering,
        Paused,
        Pathfinding,
        Moving,
        WaitingForDestination,
        Mounting,
        Dismounting,
        WaitingForLanding,
        Jumping,
        Interacting,
        Waiting,
        AdjustingPosition,
    }

    private readonly MovementOperation _movement;
    private readonly GatheringActionGate _actionGate = new();
    private readonly DismountController _dismount = new(() => new ClibDismountTask());
    private HashSet<string>? _localizedInventoryFullMessages;
    private readonly NavigationRecovery _recovery = new();
    private int _recoveryWaypoint = -1;
    private bool NavigationUsesVolume => Player.InclusiveFlying;
    private bool _navigationWasDiving, _offMeshUsesVolume;
    private long _nextOffMeshMoveAt;
    private long _nextShortcutMoveAt, _nextShortcutCheckAt;
    private readonly EzThrottler<string> _interact = new();

    public GatherRouteExec() {
        RouteDB = Service.Config.Get<GatherRouteDB>();
        _movement = new(() => Service.Navmesh.Stop(),
            (path, volume) => Service.Navmesh.MoveTo(path, volume),
            (target, allowFlight) => Service.Navmesh.PathfindAndMoveTo(target, allowFlight,
                CurrentRoute?.Waypoints.ElementAtOrDefault(CurrentWaypoint)?.Radius, RouteDB.MountId, RouteDB.MountDistance));
        Service.Framework.Update += Update;
        Service.ToastGui.ErrorToast += OnGatheringError;
    }

    public void Dispose() {
        Service.Framework.Update -= Update;
        Service.ToastGui.ErrorToast -= OnGatheringError;
        Finish();
    }

    public void Start(GatherRouteDB.Route route, int waypoint, bool continueToNext, bool loopAtEnd, bool pathfind = false)
    {
        if (!IslandGatheringPolicy.CanRun(Player.Available, Player.IsOnIsland, Player.Territory, route.Waypoints.Select(w => w.ZoneID))) {
            Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.IslandRouteRequired));
            return;
        }
        if (waypoint < 0 || waypoint >= route.Waypoints.Count)
            return;
        if (!Service.Navmesh.IsEnabled) {
            Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.NavmeshRequired));
            return;
        }
        Finish();
        _navigationWasDiving = Player.IsDiving;
        _recovery.Reset();
        SourceRoute = route;
        // Preserve recorded order for every entry point; navigation only optimizes travel to the next waypoint.
        CurrentState = State.None;
        CurrentRoute = route;
        route.Waypoints.RemoveAll(x => x.IsPhantom);
        CurrentWaypoint = waypoint;
        ContinueToNext = continueToNext;
        Loop = loopAtEnd;
        Pathfind = true;
    }

    public void Finish() {
        _dismount.Reset();
        _movement.Cancel();
        _movement.Shortcut.Reset();
        _movement.OffMesh.Reset();

        _actionGate.Reset();
        _movement.Jump.Reset();
        _movement.Detour.Reset();
        _movement.Stuck.Reset();
        _recovery.ResetRetries();
        _recoveryWaypoint = -1;
        _movement.Requested = false;
        SetState(State.None);
        if (CurrentRoute == null) return;

        CurrentRoute.Waypoints.RemoveAll(x => x.IsPhantom);
        CurrentRoute = null;
        SourceRoute = null;
        CurrentWaypoint = 0;
        ContinueToNext = false;
        Waiting = false;
        Paused = false;
        if (Pathfind)
            _movement.StopNavigation();
    }

    public unsafe void Update(IFramework _) {
        if (CurrentRoute != null && !IslandGatheringPolicy.CanRun(Player.Available, Player.IsOnIsland, Player.Territory, CurrentRoute.Waypoints.Select(w => w.ZoneID))) {
            Finish();
            return;
        }
        if (CurrentRoute != null && !Service.Navmesh.IsEnabled) {
            Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.NavmeshRequired));
            Finish();
            return;
        }
        if (CurrentRoute != null && Paused) {
            _movement.Cancel();
            _movement.Shortcut.Cancel();
            _movement.OffMesh.Cancel();

            _movement.StopNavigation();
            _movement.Requested = false;
        }
        if (CurrentRoute != null && Service.TaskManager.IsBusy && _movement.Active) {
            _movement.Cancel();
        }

        if (!Player.Available || ((Player.IsCasting || Player.Mounting || Player.IsJumping)
            && !_dismount.Active && !Service.Navmesh.MovementTaskBusy)
            || Paused || CurrentRoute == null || Service.TaskManager.IsBusy || CurrentWaypoint >= CurrentRoute.Waypoints.Count) {
            _dismount.Reset();
            _actionGate.Reset();
            if (Paused || !Player.IsJumping) _movement.Jump.Reset();
            _movement.Suspend();
            return;
        }

        CompatModule.EnsureCompatibility(RouteDB, ensureGatherMode: Service.RareAnimalCapture?.Active != true);

        if (AddonUtils.IsOccupied()) {
            _dismount.Reset();
            _movement.Suspend(); _movement.Jump.Reset(); _actionGate.Reset(); return;
        }

        var wp = CurrentRoute.Waypoints[CurrentWaypoint];
        if (!float.IsFinite(wp.Position.X) || !float.IsFinite(wp.Position.Y) || !float.IsFinite(wp.Position.Z)
            || !float.IsFinite(wp.Radius) || wp.Radius < 0) {
            Service.Log.Warning($"Waypoint {CurrentWaypoint + 1} has an invalid position or radius; stopping gathering.");
            Finish();
            return;
        }
        if (_recoveryWaypoint != CurrentWaypoint || _movement.Target != wp.Position) {
            _dismount.Reset();
            _movement.Begin(CurrentWaypoint, wp.Position);
            _movement.StopNavigation();
            _movement.Requested = false;
            _recovery.ResetRetries();
            _movement.Detour.Reset();
            _movement.Jump.Reset();
            _actionGate.Reset();

            _movement.Shortcut.Reset();
            _movement.OffMesh.Reset();
            _nextShortcutCheckAt = 0;
            _recoveryWaypoint = CurrentWaypoint;
        }
        if (!_movement.Active && (_movement.Result != MovementResult.Arrived
            || Vector3.DistanceSquared(Player.Position, wp.Position) > wp.Radius * wp.Radius))
            _movement.Begin(CurrentWaypoint, wp.Position);
        if (_navigationWasDiving != Player.IsDiving) {
            _dismount.Reset();
            _movement.Cancel();
            _movement.Begin(CurrentWaypoint, wp.Position);
            // Queries are owned by this plugin, so a land/water transition can cancel them now.
            _movement.StopNavigation();
            _movement.Requested = false;
            _navigationWasDiving = Player.IsDiving;
            _movement.Detour.Reset();
            _movement.OffMesh.Reset();
            _movement.Shortcut.Cancel();
            _movement.Jump.Reset();
            _movement.Stuck.Reset();

        }
        if (_dismount.Active) {
            EnsureDismounted();
            return;
        }
        var toWaypoint = wp.Position - Player.Position;
        var needToGetCloser = !float.IsFinite(toWaypoint.LengthSquared()) || toWaypoint.LengthSquared() > wp.Radius * wp.Radius;
        Pathfind = true;
        if ((needToGetCloser || Service.Navmesh.MovementTaskBusy) && _movement.CheckTimeout(Environment.TickCount64)) {
            Service.Log.Warning($"Movement to waypoint {CurrentWaypoint + 1} timed out; recovering navigation.");
            RecoverNavigation(MovementResult.TimedOut);
            return;
        }
        if (needToGetCloser) {
            _actionGate.Reset();
            if (_movement.Jump.Active) {
                var jumpResult = _movement.Jump.Update(Player.Position, Environment.TickCount64);
                if (jumpResult == JumpRecoveryResult.Pending) return;
                _movement.Stuck.Reset();
                if (jumpResult == JumpRecoveryResult.Failed) {
                    Service.Log.Information("Jump did not clear the obstacle; recovering navigation.");
                    RecoverNavigation();
                    return;
                }
                Service.Log.Information("Jump restored movement; continuing the current path.");
            }
            // Check throughout movement, including direct paths and detours, so an expired
            // Isle Sprint can be renewed without waiting for another pathfinding request.
            if (!Player.Mounted && !Player.ExclusiveFlying && Service.Navmesh.IsRunning())
                Player.Sprint();
            if (UpdateOffMeshMovement(wp)) return;
            if ((_movement.Shortcut.Active || (!Service.Navmesh.MovementTaskBusy && _movement.Requested))
                && TryGroundShortcut(wp)) return;
            if (_movement.Detour.Active) {
                if (!Service.Navmesh.IsReady()) { SetState(State.WaitingForNavmesh); return; }
                SetState(State.Pathfinding);
                var detourPath = _movement.Detour.Tick(Environment.TickCount64,
                    p => Service.Navmesh.QueryMeshNearestPoint(p, 2, 5), Service.Navmesh.PathfindCancelable);
                if (detourPath != null) {
                    _movement.Follow(detourPath, NavigationUsesVolume);
                    _movement.Requested = true;
                    _movement.RequestedAt = Environment.TickCount64;
                    Service.Log.Information($"Using alternative movement path to waypoint {CurrentWaypoint + 1}.");
                }
                else if (!_movement.Detour.Active) {
                    Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.NoDetourAvailable));
                    Finish();
                }
                return;
            }
            // skip current waypoint if target isn't there

            if (Service.Navmesh.IsRunning()) {
                SetState(State.WaitingForDestination);
                if (_movement.Stuck.Update(Player.Position, wp.Position, CurrentWaypoint, Environment.TickCount64, RouteDB.StuckTimeoutSeconds,
                    Service.Navmesh.CurrentWaypoint(), Service.Navmesh.RemainingWaypointCount())) {
                    if (Player.InclusiveFlying) {
                        Service.Log.Information("Volume movement stuck; recovering navigation without jumping.");
                        RecoverNavigation();
                    }
                    else {
                        _movement.Jump.Begin(Player.Position, Environment.TickCount64);
                        Service.Log.Information("Movement stuck; trying a jump while keeping navigation active.");
                        Player.Jump();
                    }
                }
                return;
            }

            _movement.Stuck.Reset();
            if (_movement.Requested && Service.Navmesh.IsReady() && !Service.Navmesh.PathfindInProgress()) {
                // Allow the IPC consumer time to start its asynchronous request.
                if (Environment.TickCount64 - _movement.RequestedAt >= 500)
                    RecoverNavigation(Service.Navmesh.LastRequestFailure != null
                        ? MovementResult.PathfindingFailed : MovementResult.Stuck);
                return;
            }
            if (!Service.Navmesh.IsEnabled) {
                Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.NavmeshRequired));
                Finish();
                return;
            }
            if (!Service.Navmesh.IsReady() || Service.Navmesh.PathfindInProgress()) {
                SetState(State.WaitingForNavmesh);
                return;
            }
            SetState(State.Moving);
            if (!_movement.Request(wp.Position, RouteDB.AllowFlight)) {
                RecoverNavigation(MovementResult.PathfindingFailed);
                return;
            }

            _movement.Requested = true;
            _movement.RequestedAt = Environment.TickCount64;
            return;
        }

        // clib may still be mounting or landing after entering the waypoint radius.
        // Do not cancel it just because its path has reached the target.
        if (Service.Navmesh.MovementTaskBusy || Service.Navmesh.IsDrainingSimpleMove) {
            SetState(State.WaitingForDestination);
            return;
        }
        if (Player.Mounted && wp.InteractWithOID != 0) {
            if (!EnsureDismounted()) return;
        }
        if (!_movement.Complete(Player.Position, wp.Radius, Player.ExclusiveFlying && wp.InteractWithOID != 0)) return;
        _movement.Shortcut.Cancel();
        _movement.OffMesh.Cancel();
        _movement.Jump.Reset();
        _movement.Stuck.Reset();
        _recovery.ResetRetries();
        _movement.Detour.Reset();
        _movement.Requested = false;
        // force stop at destination to avoid a bug wherein you interact with the object and keep moving for a period of time
        if (Pathfind)
            _movement.StopNavigation();

        if (wp.InteractWithOID != 0) {
            // Stop first, then allow landing and dismount animations to settle before interacting.
            var blocked = Player.IsJumping || Player.ExclusiveFlying || Player.IsCasting || Player.Mounting
                || Player.AnimationLock > 0 || (!Player.Mounted && !Player.Normal && !Player.IsDiving);
            if (!_actionGate.Ready(Player.Position, Environment.TickCount64, blocked)) {
                SetState(State.WaitingForLanding);
                return;
            }
            if (Player.Mounted || _dismount.Active) {
                EnsureDismounted();
                return;
            }
        }

        switch (wp.Interaction) {
            case GatherRouteDB.InteractionType.Standard:
                var interactObj = !AddonUtils.IsOccupied() ? FindObjectToInteractWith(wp) : null;
                if (interactObj != null) {
                    if (RouteDB.StopOnInventoryFull && AreCurrentNodeMaterialsFull()) {
                        StopForFullNode();
                        return;
                    }
                    if (_interact.Throttle("gather-interaction", 500)) {
                        SetState(State.Interacting);
                        Service.Log.Debug("Interacting...");
                        TargetSystem.Instance()->OpenObjectInteraction(interactObj);
                    }
                    return;
                }
                break;
            case GatherRouteDB.InteractionType.StartRoute:
                var route = RouteDB.Routes.Find(r => r.Name == wp.RouteName);
                if (route is { Waypoints.Count: > 0 }) {
                    Finish();
                    Start(route, 0, true, false, route.Waypoints[0].Pathfind);
                    return;
                }
                break;

        }

        if (Service.TaskManager.IsBusy) return; // let interactions play out

        if (!ContinueToNext) {
            Finish();
            return;
        }

        if (!Waiting && wp.WaitTimeET != default && wp.WaitTimeET != (Utils.EorzeanHour(), Utils.EorzeanMinute()).ToVec2()) return;

        if (!Waiting && wp.WaitTimeMs != default) {
            WaitUntil = Environment.TickCount64 + wp.WaitTimeMs;
            Waiting = true;
            SetState(State.Waiting);
        }

        if (Waiting && Environment.TickCount64 <= WaitUntil) return;

        if (wp.WaitForCondition != default && !Service.Condition[wp.WaitForCondition]) return;

        Waiting = false;

        if (++CurrentWaypoint >= CurrentRoute!.Waypoints.Count) {
            if (Loop) {
                CurrentRoute.Waypoints.RemoveAll(x => x.IsPhantom);
                CurrentWaypoint = 0;
            }
            else
                Finish();
        }
    }

    private bool NodeExists(Vector3 nodePos) {
        if (Vector3.DistanceSquared(Player.Position, nodePos) < 100)
            if (Service.ObjectTable.FirstOrDefault(x => x?.Position == nodePos, null) != null)
                return true;
        return false;
    }

    private bool TryBeginOffMeshMovement(GatherRouteDB.Waypoint waypoint, bool navigationFailed = false) {
        var diving = Player.IsDiving;
        if (_movement.OffMesh.Attempted || (NavigationUsesVolume && !diving) || Player.ExclusiveFlying
            || (diving && !navigationFailed) || _dismount.Active
            || !Service.Navmesh.IsReady() || Service.Navmesh.PathfindInProgress())
            return false;
        var origin = Player.Position;
        // Ground polygons do not determine whether underwater movement is possible.
        // Underwater direct movement is tried only after a volume path has failed or stalled.
        bool? startOnMesh = diving ? null : Service.Navmesh.IsPointOnMesh(origin);
        bool? targetOnMesh = diving ? null : Service.Navmesh.IsPointOnMesh(waypoint.Position);
        if (startOnMesh == true && targetOnMesh == true)
            return false;

        _movement.StopNavigation();
        _movement.Shortcut.Cancel();
        _movement.Detour.Reset();
        _movement.Requested = false;
        var now = Environment.TickCount64;
        // Bound each attempt even when normal stuck detection is disabled.
        var stallSeconds = RouteDB.StuckTimeoutSeconds > 0 ? Math.Clamp(RouteDB.StuckTimeoutSeconds, 1, 30) : 3;
        _movement.OffMesh.Begin(now, Player.DistanceTo(waypoint.Position), 30000, stallSeconds * 1000);
        _offMeshUsesVolume = diving;
        _nextOffMeshMoveAt = 0;
        SetState(State.Moving);
        return true;
    }

    private bool UpdateOffMeshMovement(GatherRouteDB.Waypoint waypoint) {
        if (!_movement.OffMesh.Active)
            return false;
        if (_offMeshUsesVolume != Player.IsDiving || Player.ExclusiveFlying) {
            _movement.OffMesh.Cancel();
            _movement.StopNavigation();
            return false;
        }
        var now = Environment.TickCount64;
        var action = _movement.OffMesh.Update(now, Player.DistanceTo(waypoint.Position), !Player.IsDiving && !Player.Mounted && !Player.IsJumping);
        if (action == GroundShortcutAction.Fallback) {
            _movement.StopNavigation();
            Service.Log.Warning($"[OffMeshMovement] waypoint={CurrentWaypoint + 1}; no progress or time limit, returning to navigation recovery.");
            RecoverNavigation();
            return true;
        }
        if (action == GroundShortcutAction.Jump) {
            Player.Jump();
        }
        if (!Service.Navmesh.IsRunning() && now >= _nextOffMeshMoveAt) {
            // Path.MoveTo follows coordinates directly; underwater movement needs vertical control.
            _movement.Follow([waypoint.Position], _offMeshUsesVolume);
            _nextOffMeshMoveAt = now + 1000;
        }
        SetState(State.Moving);
        return true;
    }

    private bool TryGroundShortcut(GatherRouteDB.Waypoint waypoint) {
        if (!RouteDB.GroundShortcuts || Player.InclusiveFlying
            || _dismount.Active || _movement.Detour.Active || _recovery.Retries > 0) {
            if (_movement.Shortcut.Active) {
                _movement.Shortcut.Cancel();
                _movement.StopNavigation();
                _movement.Requested = false;
            }
            return false;
        }
        var now = Environment.TickCount64;
        var distance = Player.DistanceTo(waypoint.Position);
        if (!_movement.Shortcut.Active) {
            if (_movement.Shortcut.Attempted || Service.Navmesh.PathfindInProgress() || now < _nextShortcutCheckAt) return false;
            _nextShortcutCheckAt = now + 500;
            if (!GroundShortcut.CanTraverse(Player.Position, waypoint.Position, GroundCollision.ClearBody, GroundCollision.FloorBelow)) return false;
            _movement.Shortcut.Begin(now, distance);
            _movement.StopNavigation();
            _movement.Requested = false;
            _nextShortcutMoveAt = now;
            Service.Log.Debug($"[GroundShortcut] trying direct movement to waypoint {CurrentWaypoint + 1}, distance={distance:F2}");
        }
        var action = _movement.Shortcut.Update(now, distance, !Player.Mounted && !Player.IsJumping);
        if (action == GroundShortcutAction.Fallback) {
            _movement.StopNavigation();
            _movement.Requested = false;
            Service.Log.Information($"[GroundShortcut] no progress or time limit; using vnavmesh for waypoint {CurrentWaypoint + 1}");
            return false;
        }
        if (action == GroundShortcutAction.Jump) {
            Player.Jump();
            Service.Log.Debug("[GroundShortcut] trying a jump while moving");
        }
        if (!Service.Navmesh.IsRunning() && now >= _nextShortcutMoveAt) {
            _movement.Follow([waypoint.Position], false);
            _nextShortcutMoveAt = now + 1000;
        }
        SetState(State.Moving);
        return true;
    }

    private bool EnsureDismounted() {
        _movement.Suspend();
        SetState(State.Dismounting);
        var now = Environment.TickCount64;
        var ready = _dismount.Active || _actionGate.Ready(Player.Position, now,
            Player.IsJumping || Player.IsCasting || Player.Mounting || Player.AnimationLock > 0);
        if (!_dismount.Active) {
            _movement.StopNavigation();
            }
        if (Service.Navmesh.IsDrainingSimpleMove) ready = false;
        var result = _dismount.Update(now, Player.Mounted, ready, RouteDB.StuckMaxRetries,
            Player.ExclusiveFlying ? 60000 : 8000);
        if (result == DismountResult.Pending) return false;
        _actionGate.Reset();
        if (result == DismountResult.Dismounted) {
            return true;
        }
        Service.Log.Warning($"clib dismount failed after {_dismount.Attempts} attempt(s): {_dismount.Failure}");
        Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.DismountFailed));
        Finish();
        return false;
    }

    private void RecoverNavigation(MovementResult failure = MovementResult.Stuck) {
        _movement.Fail(failure);
        _movement.RestartAttempt();
        _movement.OffMesh.Cancel();
        _movement.Shortcut.Cancel();
        _movement.Jump.Reset();
        _movement.Requested = false;
        _movement.StopNavigation();
        _movement.Stuck.Reset();
        if (CurrentRoute != null && TryBeginOffMeshMovement(CurrentRoute.Waypoints[CurrentWaypoint], navigationFailed: true))
            return;
        if (_recovery.TryRetry(RouteDB.StuckMaxRetries)) {
            Service.Log.Information($"Recalculating navigation to waypoint {CurrentWaypoint + 1} ({failure}), retry {_recovery.Retries}/{RouteDB.StuckMaxRetries}.");
            SetState(State.Pathfinding);
            return;
        }

        var waypoint = CurrentRoute!.Waypoints[CurrentWaypoint];
        var fly = NavigationUsesVolume;
        if (!_movement.Detour.Initialized)
            _movement.Detour.Begin(Player.Position, waypoint.Position, fly);
        else
            _movement.Detour.Resume(Player.Position);
        Service.ChatGui.Print(Localization.GetText(LocalizationKeys.Gathering.TryingDetour));
        SetState(State.Pathfinding);
    }

    private void SetState(State state) {
        if (state != State.WaitingForDestination)
            _movement.Stuck.Reset();
        if (state != CurrentState) {
            CurrentState = state;
            Service.Log.Debug($"State: {CurrentState}");
        }
    }

    #region Interactions
    private unsafe GameObject* FindObjectToInteractWith(GatherRouteDB.Waypoint wp) {
        if (wp.InteractWithOID == 0)
            return null;

        foreach (var obj in Service.ObjectTable.Where(o => o.BaseId == wp.InteractWithOID && (o.Position - wp.Position).LengthSquared() < 1))
            return obj.IsTargetable ? (GameObject*)obj.Address : null;
        return null;
    }

    #endregion

    private unsafe bool AreCurrentNodeMaterialsFull() {
        if (CurrentRoute == null || CurrentWaypoint < 0 || CurrentWaypoint >= CurrentRoute.Waypoints.Count)
            return false;
        var nodeId = CurrentRoute.Waypoints[CurrentWaypoint].InteractWithOID;
        if (!IslandNodeMaterials.ByObjectId.TryGetValue(nodeId, out var materials))
            return false;
        var manager = MJIManager.Instance();
        if (manager == null || !manager->IsPlayerInSanctuary)
            return false;
        // The game's unlock state includes rank progression and the tools actually crafted.
        return NodeInventoryPolicy.AllAvailableMaterialsFull(materials,
            item => !MJIManager.Instance()->IsItemLocked(item), Utils.NumItems,
            item => Item.GetRow(item)?.StackSize ?? 0);
    }

    private void StopForFullNode() {
        Service.Log.Information("All currently unlocked materials at the current gathering node are at capacity; stopping.");
        Finish();
        Service.ChatGui.Print(Localization.GetText(LocalizationKeys.Gathering.NodeMaterialsFull));
    }

    private bool IsInventoryFullError(string message) {
        if (InventoryFullError.Matches(message)) return true;
        // Resolve the game's localized text using capacity-error rows from its English sheet.
        _localizedInventoryFullMessages ??= LogMessage.Get(Dalamud.Game.ClientLanguage.English)
            .Where(row => InventoryFullError.Matches(row.Text.ExtractText()))
            .Select(row => row.WithLanguage(Service.ClientState.ClientLanguage).Text.ExtractText().Trim())
            .Where(text => text.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return _localizedInventoryFullMessages.Contains(message.Trim());
    }

    private void OnGatheringError(ref SeString message, ref bool isHandled) {
        if (CurrentRoute == null || Paused || !Player.IsOnIsland
            || CurrentState != State.Interacting)
            return;

        var error = message.TextValue;
        if (Player.IsJumping || GatheringActionGate.IsTransientJumpError(error)) {
            _actionGate.Reset();
            _movement.Stuck.Reset();
            SetState(State.WaitingForLanding);
            Service.Log.Debug($"Waiting for landing before retrying interaction: {error}");
            return;
        }
        if (!RouteDB.StopOnInventoryFull || !IsInventoryFullError(error))
            return;
        if (AreCurrentNodeMaterialsFull())
            StopForFullNode();
    }
}
