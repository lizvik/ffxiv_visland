using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using visland.Gathering;
using visland.Helpers;
using R = visland.Helpers.LocalizationKeys.RareAnimals;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace visland.Pasture.RareAnimals;

internal sealed unsafe class RareAnimalCapture : IDisposable {
    private RareAnimal? _animal;
    private GatherRouteDB.Route? _route;
    private Vector3 _destination;
    private long _started, _nextActionAt, _lostSince;
    private int _initialCount, _attempts, _approaches, _modeSwitchAttempts;
    private bool _modeMenuOpened;
    private bool _toolMenuOpened;
    private int _toolSwitchAttempts;
    private long _pausedAt;
    private readonly DismountController _dismount = new(() => new ClibDismountTask());
    private readonly GatheringActionGate _readyGate = new();
    public bool Active => _animal != null;
    public string StatusKey { get; private set; } = R.Idle;
    public RareAnimal? Selected => _animal;

    public RareAnimalCapture() => Service.Framework.Update += Update;

    public static IGameObject? Find(RareAnimal animal, AnimalPastureSnapshot snapshot, bool targetableOnly = true) => Service.ObjectTable
        .Where(o => (!targetableOnly || o.IsTargetable) && !snapshot.OwnedEntities.Contains(o.EntityId)
            && o.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)
        .Where(o => o.BaseId == animal.BaseId || string.Equals(o.Name.ToString(), animal.Name, StringComparison.OrdinalIgnoreCase))
        .OrderBy(o => Vector3.DistanceSquared(o.Position, Player.Position)).FirstOrDefault();

    public bool Start(RareAnimal animal) {
        Service.Log.Information($"Rare animal capture requested: animal={animal.Id}, name={animal.Name}");
        try { return StartCore(animal); }
        catch (Exception ex) {
            Service.Log.Warning(ex, "Rare animal capture could not start");
            Stop(R.CaptureError);
            return Reject(R.CaptureError);
        }
    }

    private bool Reject(string status) {
        // Keep the running capture's status when a second capture is requested.
        if (!Active) StatusKey = status;
        var reason = Localization.GetText(status);
        Service.ChatGui.PrintError(reason);
        Service.Log.Warning($"Rare animal capture rejected: {status}: {reason}");
        return false;
    }

    private bool StartCore(RareAnimal animal) {
        if (Active || Service.RouteExec.CurrentRoute != null || Service.TaskManager.IsBusy) return Reject(R.Busy);
        var snapshot = AnimalPastureSnapshot.Read();
        if (!snapshot.Known) return Reject(R.IslandOnly);
        var makeshift = AnimalCaptureTools.Count(animal.ToolPouchId);
        var flawless = AnimalCaptureTools.Count(animal.FlawlessToolPouchId);
        Service.Log.Information($"Rare animal capture checks: pasture={snapshot.Total}/{snapshot.Capacity}, tools={makeshift}/{flawless}, weather={snapshot.Weather}, ET={Utils.EorzeanHour()}");
        if (makeshift + flawless == 0) return Reject(R.ToolsMissing);
        var live = Find(animal, snapshot, targetableOnly: false);
        if (live == null && !animal.Definition.Available(Utils.EorzeanHour(), snapshot.Weather)) {
            return Reject(R.UnavailableWindow);
        }
        if (!Service.Navmesh.IsReady()) return Reject(R.NavmeshRequired);
        Vector3? destination = live?.Position;
        if (destination == null && animal.Definition.CaveApproach is { } caveApproach) {
            // The cave lies beneath outdoor polygons, so constrain the query to the verified cave floor.
            destination = Service.Navmesh.QueryMeshNearestPoint(caveApproach, 3, 2);
            if (!destination.HasValue) return Reject(R.CaveNavigationUnavailable);
        }
        else if (destination == null) {
            var map = TerritoryTypeMap();
            if (map == null) return Reject(R.NoFloor);
            var world = RareAnimalLocations.MapToWorld(new(animal.Definition.X, animal.Definition.Y), map.Value);
            destination = Service.Navmesh.QueryMeshPointOnFloor(new(world.X, 1000, world.Y), false, 2);
        }
        if (!destination.HasValue) return Reject(R.NoFloor);
        _animal = animal;
        _initialCount = snapshot.Counts.GetValueOrDefault(animal.Id);
        _started = Environment.TickCount64;
        _pausedAt = 0;
        _dismount.Reset();
        _readyGate.Reset();
        _nextActionAt = _lostSince = 0;
        _attempts = _approaches = _modeSwitchAttempts = 0;
        _modeMenuOpened = false;
        _toolMenuOpened = false;
        _toolSwitchAttempts = 0;
        _destination = destination.Value;
        Service.RouteExec.Paused = false;
        StatusKey = R.SelectingTool;
        Service.Log.Information($"Rare animal capture started: animal={animal.Id}, destination={destination.Value}, liveTarget={live != null}");
        return true;
    }

    internal static Lumina.Excel.Sheets.Map? TerritoryTypeMap()
        => Lumina.Excel.Sheets.TerritoryType.GetRow(RareAnimalCatalog.Territory)?.Map.Value;

    private void Approach(Vector3 destination) {
        _readyGate.Reset();
        _destination = destination;
        _route = new() { Name = $"Rare animal: {_animal!.Name}" };
        _route.Waypoints.Add(new() {
            Position = destination, Radius = 2.5f, ZoneID = RareAnimalCatalog.Territory,
            Interaction = GatherRouteDB.InteractionType.None,
        });
        ++_approaches;
        Service.RouteExec.Start(_route, 0, false, false);
        if (!ReferenceEquals(Service.RouteExec.CurrentRoute, _route)) {
            Stop(R.CaptureError);
            Reject(R.CaptureError);
            return;
        }
        StatusKey = R.Moving;
    }

    private void Update(IFramework framework) {
        try { UpdateCapture(); }
        catch (Exception ex) {
            Service.Log.Warning(ex, "Rare animal capture failed");
            Stop(R.CaptureError);
        }
    }

    private void UpdateCapture() {
        if (_animal == null) return;
        var now = Environment.TickCount64;
        if (Service.RouteExec.Paused) {
            _dismount.Reset();
            if (_pausedAt == 0) _pausedAt = now;
            return;
        }
        if (_pausedAt != 0) { _started += now - _pausedAt; _pausedAt = 0; }
        var snapshot = AnimalPastureSnapshot.Read();
        if (!snapshot.Known) { Stop(R.IslandOnly); return; }
        if (snapshot.Counts.GetValueOrDefault(_animal.Id) > _initialCount) { Stop(R.Success); return; }
        var agent = AgentMJIAnimalManagement.Instance();
        // Full pastures can still capture animals. Let the player handle the game's replacement dialog.
        if (agent != null && agent->IsAgentActive() && agent->DuringCapture) { Stop(R.PastureSelectionRequired); return; }
        if (now - _started >= 180000) { Stop(R.TimedOut); return; }
        if (Service.TaskManager.IsBusy) { Stop(R.Busy); return; }
        if (Service.RouteExec.CurrentRoute is { } route) {
            if (!ReferenceEquals(route, _route)) Stop(R.Cancelled);
            return;
        }
        if (_route != null) {
            _route = null;
            if (Player.DistanceTo(_destination) > 4) { Stop(R.Cancelled); return; }
        }
        var awaitingNavigation = _approaches == 0;
        if (!awaitingNavigation && (Player.Mounted || _dismount.Active)) {
            var ready = !Service.Navmesh.IsDrainingSimpleMove && !Player.IsCasting && !Player.Mounting && !Player.IsJumping;
            var result = _dismount.Update(now, Player.Mounted, ready, Service.RouteExec.RouteDB.StuckMaxRetries,
                Player.ExclusiveFlying ? 60000 : 8000);
            if (result == DismountResult.Failed) { Stop(R.Failed); return; }
            StatusKey = R.Moving;
            return;
        }
        if (!_readyGate.Ready(Player.Position, now, (!awaitingNavigation && Player.InclusiveFlying) || Player.IsCasting || Player.IsJumping
            || Player.Mounting || Player.AnimationLock > 0 || AddonUtils.IsOccupied()) || now < _nextActionAt) return;
        var manager = MJIManager.Instance();
        if (manager == null) { Stop(R.IslandOnly); return; }
        if (!IslandModeMenu.IsCaptureMode()) {
            _toolMenuOpened = false;
            _toolSwitchAttempts = 0;
            StatusKey = R.SelectingTool;
            if (_modeSwitchAttempts >= 10) {
                Stop(R.ModeSwitchFailed);
                Service.ChatGui.PrintError(Localization.GetText(R.ModeSwitchFailed));
                return;
            }
            ++_modeSwitchAttempts;
            Service.Log.Information($"Rare animal capture mode switch: attempt={_modeSwitchAttempts}, currentMode={manager->CurrentMode}, menuRequested={_modeMenuOpened}");
            if (!_modeMenuOpened && GenericHelpers.TryGetAddonByName<AtkUnitBase>("MJIHud", out var hud)
                && GenericHelpers.IsAddonReady(hud)) {
                Callback.Fire(hud, false, 11, 0);
                _modeMenuOpened = true;
                Service.Log.Information("Rare animal capture: requested island mode menu");
            }
            else if (_modeMenuOpened && GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextIconMenu", out var menu)
                && GenericHelpers.IsAddonReady(menu)) {
                if (IslandModeMenu.SelectCapture(menu))
                    Service.Log.Information("Rare animal capture: clicked Capture in island mode menu; waiting for mode confirmation");
            }
            else if (_modeMenuOpened) _modeMenuOpened = false;
            _nextActionAt = now + 1000;
            return;
        }
        if (_modeSwitchAttempts > 0) {
            Service.Log.Information($"Rare animal capture: Capture mode confirmed, currentMode={manager->CurrentMode}");
            _modeSwitchAttempts = 0;
        }
        _modeMenuOpened = false;
        if (!EnsureToolSelected(manager, now)) return;
        if (awaitingNavigation) {
            Approach(_destination);
            return;
        }
        var live = Find(_animal, snapshot);
        if (live == null) {
            if (_lostSince == 0) _lostSince = now;
            if (now - _lostSince >= 5000) Stop(R.NotFound);
            return;
        }
        _lostSince = 0;
        if (Player.DistanceTo(live.Position) > 3.5f) {
            if (_approaches >= 4) { Stop(R.NotFound); return; }
            Approach(live.Position);
            return;
        }
        if (_attempts >= 10) { Stop(R.Failed); return; }
        Service.TargetManager.Target = live;
        TargetSystem.Instance()->OpenObjectInteraction((NativeObject*)live.Address);
        ++_attempts;
        _nextActionAt = now + 3000;
        StatusKey = R.Capturing;
    }

    private bool EnsureToolSelected(MJIManager* manager, long now) {
        var animal = _animal!;
        var selected = manager->CurrentModeItem;
        var desired = AnimalCaptureTools.ChoosePouch(animal, selected,
            AnimalCaptureTools.Count(animal.ToolPouchId), AnimalCaptureTools.Count(animal.FlawlessToolPouchId));
        if (desired == 0) { Stop(R.ToolsMissing); return false; }
        var selectedPouch = selected == animal.ToolItem ? animal.ToolPouchId
            : selected == animal.FlawlessToolItem ? animal.FlawlessToolPouchId : selected;
        if (selectedPouch == desired) {
            if (_toolSwitchAttempts > 0)
                Service.Log.Information($"Rare animal capture: tool confirmed, selected={selected}, pouch={desired}");
            _toolSwitchAttempts = 0;
            _toolMenuOpened = false;
            return true;
        }
        StatusKey = R.SwitchingTool;
        if (_toolSwitchAttempts >= 10) {
            Stop(R.ToolSwitchFailed);
            Service.ChatGui.PrintError(Localization.GetText(R.ToolSwitchFailed));
            return false;
        }
        ++_toolSwitchAttempts;
        Service.Log.Information($"Rare animal capture tool switch: attempt={_toolSwitchAttempts}, selected={selected}, desiredPouch={desired}");
        if (!_toolMenuOpened && GenericHelpers.TryGetAddonByName<AtkUnitBase>("MJIHud", out var hud)
            && GenericHelpers.IsAddonReady(hud)) {
            // MJIHud's tool button emits callback 12. Callback 11 always opens the mode menu.
            Callback.Fire(hud, false, 12);
            _toolMenuOpened = true;
            Service.Log.Information("Rare animal capture: requested capture tool menu");
        }
        else if (_toolMenuOpened && GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextIconMenu", out var toolMenu)
            && GenericHelpers.IsAddonReady(toolMenu)) IslandModeMenu.SelectTool(toolMenu, desired);
        else if (_toolMenuOpened) _toolMenuOpened = false;
        _nextActionAt = now + 1000;
        _lostSince = 0;
        return false;
    }

    public void Stop() => Stop(R.Cancelled);
    private void Stop(string status) {
        if (Active) Service.Log.Information($"Rare animal capture stopped: animal={_animal!.Id}, reason={status}, attempts={_attempts}");
        _dismount.Reset();
        _readyGate.Reset();
        // A different task may have replaced our temporary route; never stop that task.
        if (_route != null && ReferenceEquals(Service.RouteExec.CurrentRoute, _route)) Service.RouteExec.Finish();
        _animal = null;
        _route = null;
        _modeMenuOpened = false;
        _toolMenuOpened = false;
        StatusKey = status;
    }

    public void Dispose() {
        Service.Framework.Update -= Update;
        Stop();
    }
}
