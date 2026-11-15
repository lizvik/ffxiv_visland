using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using visland.Helpers;

namespace visland.Gathering;

internal static unsafe class CompatModule {
    public static void EnsureCompatibility(GatherRouteDB routeDB, bool ensureGatherMode = true) {
        if (ensureGatherMode && routeDB.GatherModeOnStart && Player.IsOnIsland) {
            if (MJIManager.Instance()->CurrentMode != 1) {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("MJIHud", out var hud) && GenericHelpers.IsAddonReady(hud))
                    Callback.Fire(hud, false, 11, 0);
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextIconMenu", out var menu) && GenericHelpers.IsAddonReady(menu))
                    Callback.Fire(menu, false, 0, 1, 82042, 0, 0);
            }
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextIconMenu", out var menuToClose) && GenericHelpers.IsAddonReady(menuToClose))
                Callback.Fire(menuToClose, false, -1);
        }
        OverrideAFK.ResetTimers();
    }
}
