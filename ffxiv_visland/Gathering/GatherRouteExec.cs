using FFXIVClientStructs.FFXIV.Client.Game.MJI;
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

    private readonly NavigationStuckDetector _stuck = new();
    private readonly JumpRecovery _jumpRecovery = new();
    private readonly GatheringActionGate _actionGate = new();
    private HashSet<string>? _localizedInventoryFullMessages;
    private readonly NavigationRecovery _recovery = new();
    private readonly MovementDetourPlanner _detour = new();
    private int _recoveryWaypoint = -1;
    private bool _navigationRequested;
    private long _navigationRequestedAt;
    private AutomaticMovementMode _movementMode;
    private long _mountAttemptAt, _takeoffAttemptAt;
    private readonly LandingController _landing = new();
    private readonly LandingPathRequest _landingPath = new();
    private float? _landingSavedTolerance;
    private bool _wasFlyingForWaypoint;
    private bool _landingDiagnosticsActive;
    private long _landingDiagnosticsStartedAt, _nextLandingDiagnosticAt;
    private Vector3 _landingDiagnosticOrigin, _landingDiagnosticPrevious;
    private Vector3? _landingDiagnosticFloor;
    private bool? _landingDiagnosticPathAccepted;
    private int _landingFloorMisses, _landingPathRequests, _landingPathRejected;
    private bool NavigationFlying => _movementMode == AutomaticMovementMode.Fly && Player.Mounted && Player.InclusiveFlying;
    private readonly GroundShortcut _groundShortcut = new();
    private long _nextShortcutMoveAt, _nextShortcutCheckAt;
    private readonly Throttle _interact = new();

    public GatherRouteExec() {
        RouteDB = Service.Config.Get<GatherRouteDB>();
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
        _groundShortcut.Reset();
        if (_landingDiagnosticsActive)
            LogLandingDiagnostic("route-stopped", true);
        ResetLanding();
        ResetLandingDiagnostics();
        _actionGate.Reset();
        _jumpRecovery.Reset();
        _detour.Reset();
        _stuck.Reset();
        _recovery.ResetRetries();
        _recoveryWaypoint = -1;
        _navigationRequested = false;
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
            Service.Navmesh.Stop();
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
            _groundShortcut.Cancel();            ResetLanding();
            Service.Navmesh.Stop();
            _navigationRequested = false;
        }

        if (!Player.Available || Player.IsCasting || Player.Mounting || (Player.IsJumping && !_landing.Active) || Paused || CurrentRoute == null || Service.TaskManager.IsBusy || CurrentWaypoint >= CurrentRoute.Waypoints.Count) {
            if (_landingDiagnosticsActive) LogLandingDiagnostic("waiting-player-state");
            _actionGate.Reset();
            if (Paused || !Player.IsJumping) _jumpRecovery.Reset();
            _stuck.Reset();
            return;
        }

        CompatModule.EnsureCompatibility(RouteDB);

        if (AddonUtils.IsOccupied()) {
            if (_landingDiagnosticsActive) LogLandingDiagnostic("occupied");
            _stuck.Reset(); _jumpRecovery.Reset(); _actionGate.Reset(); return;
        }

        var wp = CurrentRoute.Waypoints[CurrentWaypoint];
        if (_recoveryWaypoint != CurrentWaypoint) {
            _recovery.ResetRetries();
            _detour.Reset();
            _jumpRecovery.Reset();
            _actionGate.Reset();
            ResetLanding();
            ResetLandingDiagnostics();
            _wasFlyingForWaypoint = false;
            _groundShortcut.Reset();
            _nextShortcutCheckAt = 0;
            _recoveryWaypoint = CurrentWaypoint;
            _movementMode = AutomaticMovementPolicy.Select(Player.DistanceTo(wp.Position), RouteDB.MountDistance, RouteDB.FlightDistance, RouteDB.AllowFlight);
            _mountAttemptAt = _takeoffAttemptAt = 0;
        }
        if (Player.ExclusiveFlying) _wasFlyingForWaypoint = true;
        else if (_wasFlyingForWaypoint) {
            // A completed flight leg must not trigger another takeoff while approaching the node.
            _wasFlyingForWaypoint = false;
            if (_movementMode == AutomaticMovementMode.Fly) _movementMode = AutomaticMovementMode.Ride;
        }
        var toWaypoint = wp.Position - Player.Position;
        var horizontalDistance = new Vector2(toWaypoint.X, toWaypoint.Z).Length();
        if (_landing.Active || (Player.ExclusiveFlying && wp.InteractWithOID != 0 && horizontalDistance <= Math.Max(wp.Radius, 3))) {
            if (!EnsureGrounded()) return;
        }
        var needToGetCloser = toWaypoint.LengthSquared() > wp.Radius * wp.Radius;
        if (Player.ExclusiveFlying && wp.InteractWithOID != 0 && horizontalDistance <= Math.Max(wp.Radius + 5, 8))
            LogLandingDiagnostic("approaching-node");
        Pathfind = true;

        if (needToGetCloser) {
            _actionGate.Reset();
            if (_jumpRecovery.Active) {
                var jumpResult = _jumpRecovery.Update(Player.Position, Environment.TickCount64);
                if (jumpResult == JumpRecoveryResult.Pending) return;
                _stuck.Reset();
                if (jumpResult == JumpRecoveryResult.Failed) {
                    Service.Log.Information("Jump did not clear the obstacle; recovering navigation.");
                    RecoverNavigation();
                    return;
                }
                Service.Log.Information("Jump restored movement; continuing the current path.");
            }
            if (!PrepareMovement()) return;
            if (TryGroundShortcut(wp)) return;
            if (_detour.Active) {
                if (!Service.Navmesh.IsReady()) { SetState(State.WaitingForNavmesh); return; }
                SetState(State.Pathfinding);
                var detourPath = _detour.Tick(Environment.TickCount64,
                    p => Service.Navmesh.QueryMeshNearestPoint(p, 2, 5), Service.Navmesh.Pathfind);
                if (detourPath != null) {
                    Service.Navmesh.MoveTo(detourPath, NavigationFlying);
                    _navigationRequested = true;
                    _navigationRequestedAt = Environment.TickCount64;
                    Service.Log.Information($"Using alternative movement path to waypoint {CurrentWaypoint + 1}.");
                }
                else if (!_detour.Active) {
                    Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.NoDetourAvailable));
                    Finish();
                }
                return;
            }
            // skip current waypoint if target isn't there

            if (Service.Navmesh.IsRunning()) {
                SetState(State.WaitingForDestination);
                if (_stuck.Update(Player.Position, wp.Position, CurrentWaypoint, Environment.TickCount64, RouteDB.StuckTimeoutSeconds)) {
                    _jumpRecovery.Begin(Player.Position, Environment.TickCount64);
                    Service.Log.Information("Movement stuck; trying a jump while keeping navigation active.");
                    Player.Jump();
                }
                return;
            }

            _stuck.Reset();
            if (_navigationRequested && Service.Navmesh.IsReady() && !Service.Navmesh.PathfindInProgress()) {
                // Allow the IPC consumer time to start its asynchronous request.
                if (Environment.TickCount64 - _navigationRequestedAt >= 500)
                    RecoverNavigation();
                return;
            }
            if (_movementMode == AutomaticMovementMode.Walk)
                Player.Sprint();

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
            if (!Service.Navmesh.PathfindAndMoveTo(wp.Position, NavigationFlying)) {
                RecoverNavigation();
                return;
            }

            _navigationRequested = true;
            _navigationRequestedAt = Environment.TickCount64;
            return;
        }

        _groundShortcut.Cancel();
        _jumpRecovery.Reset();
        if (Player.ExclusiveFlying && wp.InteractWithOID != 0 && !EnsureGrounded()) return;
        _stuck.Reset();
        _recovery.ResetRetries();
        _detour.Reset();
        _navigationRequested = false;
        // force stop at destination to avoid a bug wherein you interact with the object and keep moving for a period of time
        if (Pathfind && Service.Navmesh.IsRunning())
            Service.Navmesh.Stop();

        if (wp.InteractWithOID != 0) {
            // Stop first, then allow landing and dismount animations to settle before interacting.
            var blocked = Player.IsJumping || Player.ExclusiveFlying || Player.IsCasting || Player.Mounting
                || Player.AnimationLock > 0 || (!Player.Mounted && !Player.Normal);
            if (!_actionGate.Ready(Player.Position, Environment.TickCount64, blocked)) {
                if (_landingDiagnosticsActive) LogLandingDiagnostic("waiting-action-readiness");
                SetState(State.WaitingForLanding);
                return;
            }
            if (Player.Mounted) {
                ResetLanding();
                SetState(State.Dismounting);
                if (_landingDiagnosticsActive) LogLandingDiagnostic("request-dismount");
                Player.Dismount();
                _actionGate.Reset();
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
                    _interact.Exec(() => {
                        if (_landingDiagnosticsActive) {
                            LogLandingDiagnostic("ready-to-interact", true);
                            ResetLandingDiagnostics();
                        }
                        SetState(State.Interacting);
                        Service.Log.Debug("Interacting...");
                        TargetSystem.Instance()->OpenObjectInteraction(interactObj);
                    });
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


        if (wp.WaitTimeET != default && wp.WaitTimeET != (Utils.EorzeanHour(), Utils.EorzeanMinute()).ToVec2()) return;

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

    private bool TryGroundShortcut(GatherRouteDB.Waypoint waypoint) {
        if (!RouteDB.GroundShortcuts || _movementMode == AutomaticMovementMode.Fly || Player.InclusiveFlying
            || _landing.Active || _detour.Active || _recovery.Retries > 0) {
            if (_groundShortcut.Active) {
                _groundShortcut.Cancel();
                Service.Navmesh.Stop();
                _navigationRequested = false;
            }
            return false;
        }
        var now = Environment.TickCount64;
        var distance = Player.DistanceTo(waypoint.Position);
        if (!_groundShortcut.Active) {
            if (_groundShortcut.Attempted || Service.Navmesh.PathfindInProgress() || now < _nextShortcutCheckAt) return false;
            _nextShortcutCheckAt = now + 500;
            if (!GroundShortcut.CanTraverse(Player.Position, waypoint.Position, GroundCollision.ClearBody, GroundCollision.FloorBelow)) return false;
            _groundShortcut.Begin(now, distance);
            Service.Navmesh.Stop();
            _navigationRequested = false;
            _nextShortcutMoveAt = now;
            Service.Log.Debug($"[GroundShortcut] trying direct movement to waypoint {CurrentWaypoint + 1}, distance={distance:F2}");
        }
        var action = _groundShortcut.Update(now, distance, !Player.Mounted && !Player.IsJumping);
        if (action == GroundShortcutAction.Fallback) {
            Service.Navmesh.Stop();
            _navigationRequested = false;
            Service.Log.Information($"[GroundShortcut] no progress or time limit; using vnavmesh for waypoint {CurrentWaypoint + 1}");
            return false;
        }
        if (action == GroundShortcutAction.Jump) {
            Player.Jump();
            Service.Log.Debug("[GroundShortcut] trying a jump while moving");
        }
        if (!Service.Navmesh.IsRunning() && now >= _nextShortcutMoveAt) {
            Service.Navmesh.MoveTo([waypoint.Position], false);
            _nextShortcutMoveAt = now + 1000;
        }
        SetState(State.Moving);
        return true;
    }

    private bool PrepareMovement() {
        var now = Environment.TickCount64;
        if (_movementMode != AutomaticMovementMode.Fly && Player.ExclusiveFlying && !EnsureGrounded()) return false;
        if (_movementMode == AutomaticMovementMode.Walk && Player.Mounted) {
            Service.Navmesh.Stop();
            _navigationRequested = false;
            ResetLanding();
            SetState(State.Dismounting);
            Player.Dismount();
            return false;
        }
        if (_movementMode != AutomaticMovementMode.Walk && !Player.Mounted) {
            if (_mountAttemptAt == 0) {
                _mountAttemptAt = now;
                SetState(State.Mounting);
            }
            if (now - _mountAttemptAt >= 5000) {
                _movementMode = AutomaticMovementMode.Walk;
                return true;
            }
            Player.Mount(RouteDB.MountId);
            // Keep ground navigation active while the mount cast completes.
            return true;
        }
        if (_movementMode == AutomaticMovementMode.Fly && !Player.InclusiveFlying) {
            if (_takeoffAttemptAt == 0) {
                _takeoffAttemptAt = now;
                // The ground path used during mounting must not survive takeoff.
                Service.Navmesh.Stop();
                _navigationRequested = false;
                _detour.Reset();
            }
            if (now - _takeoffAttemptAt >= 5000) {
                _movementMode = AutomaticMovementMode.Ride;
                return true;
            }
            SetState(State.Jumping);
            Player.Jump();
            return false;
        }
        return true;
    }

    private void StopLandingMovement() {
        if (_landing.Active)
            LogLandingDiagnostic($"stopping-{_landing.Phase}", true);
        _landingPath.Reset();
        Service.Navmesh.Stop();
    }

    private void ResetLanding() {
        _landingPath.Reset();
        _landing.Reset();
        if (_landingSavedTolerance.HasValue) {
            Service.Navmesh.SetTolerance(_landingSavedTolerance.Value);
            _landingSavedTolerance = null;
        }
    }

    private bool EnsureGrounded() {
        _stuck.Reset();
        _jumpRecovery.Reset();
        SetState(State.AdjustingPosition);
        var now = Environment.TickCount64;
        if (!_landing.Active && !Player.ExclusiveFlying) return true;
        if (!_landing.Active) {
            _landing.Begin(Player.Position, CurrentRoute!.Waypoints[CurrentWaypoint].Position, now,
                Math.Clamp(RouteDB.StuckMaxRetries + 3, 4, 12));
            Service.Navmesh.Stop();
            _navigationRequested = false;
            _detour.Reset();
            _landingSavedTolerance = Service.Navmesh.GetTolerance();
            Service.Navmesh.SetTolerance(0.1f);
            LogLandingDiagnostic("begin-descent", true);
        }
        if (_landing.Phase == LandingPhase.Calculating) {
            var queryResult = _landingPath.Poll(now, path => {
                if (_landing.PathApplied(now, Player.Position))
                    Service.Navmesh.MoveTo(path, true);
            });
            if (queryResult != null) {
                if (queryResult != "query-applied") {
                    ++_landingPathRejected;
                    _landing.PathFailed(now);
                }
                LogLandingDiagnostic(queryResult, true, queryResult == "query-faulted");
            }
        }
        var previousPhase = _landing.Phase;
        var previousAttempt = _landing.Attempt;
        var result = _landing.Tick(now, Player.Position, Player.ExclusiveFlying,
            Service.Navmesh.IsReady(), _landingPath.Pending || Service.Navmesh.IsRunning() || Service.Navmesh.PathfindInProgress(),
            query => {
                var floor = Service.Navmesh.QueryMeshPointOnFloor(query, false, 2);
                _landingDiagnosticFloor = floor;
                if (!floor.HasValue) ++_landingFloorMisses;
                return floor;
            },
            floor => {
                ++_landingPathRequests;
                var accepted = _landingPath.Begin(Player.Position, floor, now, Service.Navmesh.PathfindForLanding);
                _landingDiagnosticPathAccepted = accepted;
                if (!accepted) ++_landingPathRejected;
                return accepted;
            },
            floor => { Service.Navmesh.MoveTo([floor], true); }, StopLandingMovement);
        _landingDiagnosticFloor = _landing.Floor;
        if (result == LandingResult.Landed) {
            LogLandingDiagnostic("landed", true);
            Service.Navmesh.Stop();
            _navigationRequested = false;
            if (_movementMode == AutomaticMovementMode.Fly) _movementMode = AutomaticMovementMode.Ride;
            _wasFlyingForWaypoint = false;
            _actionGate.Reset();
            ResetLanding();
            return true;
        }
        if (result == LandingResult.Failed) {
            LogLandingDiagnostic($"failed-{_landing.Failure}", true, true);
            Service.ChatGui.PrintError(Localization.GetText(LocalizationKeys.Gathering.LandingFailed));
            Finish();
            return false;
        }
        var changed = previousPhase != _landing.Phase || previousAttempt != _landing.Attempt;
        LogLandingDiagnostic($"{_landing.Phase}-candidate-{_landing.Attempt}", changed);
        return false;
    }

    private void ResetLandingDiagnostics() {
        _landingDiagnosticsActive = false;
        _nextLandingDiagnosticAt = 0;
        _landingDiagnosticFloor = null;
        _landingDiagnosticPathAccepted = null;
        _landingFloorMisses = _landingPathRequests = _landingPathRejected = 0;
    }

    private void LogLandingDiagnostic(string phase, bool force = false, bool warning = false) {
        var now = Environment.TickCount64;
        if (!force && now < _nextLandingDiagnosticAt) return;
        _nextLandingDiagnosticAt = now + 2000;
        if (!_landingDiagnosticsActive) {
            _landingDiagnosticsActive = true;
            _landingDiagnosticsStartedAt = now;
            _landingDiagnosticOrigin = _landingDiagnosticPrevious = Player.Position;
        }
        try {
            var position = Player.Position;
            var waypoint = CurrentRoute != null && CurrentWaypoint >= 0 && CurrentWaypoint < CurrentRoute.Waypoints.Count
                ? CurrentRoute.Waypoints[CurrentWaypoint] : null;
            var target = waypoint?.Position;
            var delta = target.HasValue ? target.Value - position : Vector3.Zero;
            var elapsed = now - _landingDiagnosticsStartedAt;
            var remainingPath = Service.Navmesh.ListWaypoints();
            var pathPreview = remainingPath == null ? "unavailable" : string.Join("; ", remainingPath.Take(8));
            var floorDelta = _landingDiagnosticFloor.HasValue ? _landingDiagnosticFloor.Value.Y - position.Y : (float?)null;
            var log = $"[Landing] phase={phase}, route='{CurrentRoute?.Name}', step={CurrentWaypoint + 1}, mode={_movementMode}, "
                + $"elapsedMs={elapsed}, descentElapsedMs={(_landing.Active ? now - _landing.StartedAt : 0)}, "
                + $"position={position}, target={target}, horizontalDistance={new Vector2(delta.X, delta.Z).Length():F2}, targetDeltaY={delta.Y:F2}, "
                + $"radius={waypoint?.Radius}, arrivalRadius={waypoint?.Radius}, "
                + $"movementSinceLastLog={Vector3.Distance(position, _landingDiagnosticPrevious):F2}, heightChange={position.Y - _landingDiagnosticOrigin.Y:F2}, "
                + $"inFlight={Player.ExclusiveFlying}, mounted={Player.Mounted}, jumping={Player.IsJumping}, mounting={Player.Mounting}, casting={Player.IsCasting}, "
                + $"normal={Player.Normal}, animationLock={Player.AnimationLock:F2}, paused={Paused}, tasksBusy={Service.TaskManager.IsBusy}, state={CurrentState}, "
                + $"meshReady={Service.Navmesh.IsReady()}, pathRunning={Service.Navmesh.IsRunning()}, pathPending={Service.Navmesh.PathfindInProgress()}, ownedLandingQueryPending={_landingPath.Pending}, "
                + $"pathWaypoints={Service.Navmesh.NumWaypoints()}, movementAllowed={Service.Navmesh.GetMovementAllowed()}, tolerance={Service.Navmesh.GetTolerance():F2}, "
                + $"floor={_landingDiagnosticFloor}, floorDeltaY={floorDelta}, pathAccepted={_landingDiagnosticPathAccepted}, "
                + $"landingStage={_landing.Phase}, landingCandidate={_landing.Attempt}, landingCandidates={_landing.CandidateCount}, descentSteps={_landing.DescentSteps}, descentTarget={_landing.DescentTarget}, floorDistanceToTarget={(target.HasValue && _landingDiagnosticFloor.HasValue ? Vector3.Distance(target.Value, _landingDiagnosticFloor.Value) : (float?)null)}, queryElapsedMs={(_landing.Phase == LandingPhase.Calculating ? now - _landing.QueryStartedAt : 0)}, noProgressMs={(_landing.Active ? now - _landing.LastProgressAt : 0)}, remainingPath=[{pathPreview}], floorMisses={_landingFloorMisses}, pathRequests={_landingPathRequests}, pathRejected={_landingPathRejected}";
            _landingDiagnosticPrevious = position;
            if (warning) Service.Log.Warning(log);
            else Service.Log.Information(log);
        }
        catch (Exception ex) {
            // Diagnostics must not interrupt the existing navigation flow.
            Service.Log.Warning(ex, $"[Landing] Diagnostic snapshot failed, phase={phase}");
        }
    }

    private void RecoverNavigation() {
        _jumpRecovery.Reset();
        _navigationRequested = false;
        Service.Navmesh.Stop();
        _stuck.Reset();
        if (_recovery.TryRetry(RouteDB.StuckMaxRetries)) {
            Service.Log.Information($"Recalculating navigation to waypoint {CurrentWaypoint + 1}, retry {_recovery.Retries}/{RouteDB.StuckMaxRetries}.");
            SetState(State.Pathfinding);
            return;
        }

        var waypoint = CurrentRoute!.Waypoints[CurrentWaypoint];
        var fly = NavigationFlying;
        if (!_detour.Initialized)
            _detour.Begin(Player.Position, waypoint.Position, fly);
        else
            _detour.Resume(Player.Position);
        Service.ChatGui.Print(Localization.GetText(LocalizationKeys.Gathering.TryingDetour));
        SetState(State.Pathfinding);
    }

    private void SetState(State state) {
        if (state != State.WaitingForDestination)
            _stuck.Reset();
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
            _stuck.Reset();
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
