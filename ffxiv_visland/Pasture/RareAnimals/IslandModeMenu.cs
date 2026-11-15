using ECommons.Automation;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using visland.Helpers;

namespace visland.Pasture.RareAnimals;

internal static class IslandModeMenu {
    public static unsafe bool IsCaptureMode() {
        // MJIHudMode row IDs differ from runtime CurrentMode values on the Chinese client.
        // Confirm the displayed HUD icon instead of comparing the runtime value to a sheet row ID.
        var mode = MJIHudMode.GetRow(RareAnimalCatalog.CaptureModeRow);
        return mode.HasValue && GenericHelpers.TryGetAddonByName<AtkUnitBase>("MJIHud", out var hud)
            && GenericHelpers.IsAddonReady(hud) && HasModeIcon(hud, mode.Value.Icon);
    }

    internal static unsafe bool HasModeIcon(AtkUnitBase* hud, uint icon)
        => hud != null && hud->AtkValues != null && hud->AtkValuesCount > 2
            && hud->AtkValues[2].Type == AtkValueType.UInt && hud->AtkValues[2].UInt == icon;

    internal static unsafe int FindEntry(AtkUnitBase* menu, uint icon, uint itemId = 0) {
        // Read the active menu data, not the generic list or inactive alternate radio buttons.
        // AtkValues[4] is the active count; each entry starts at 11 with a stride of 8.
        if (menu == null || menu->AtkValues == null || menu->AtkValuesCount < 5 || menu->AtkValues[4].Type != AtkValueType.UInt) return -1;
        var count = (int)menu->AtkValues[4].UInt;
        if (count is < 1 or > 10 || menu->AtkValuesCount < 11 + count * 8) return -1;
        var index = -1;
        for (var i = 0; i < count; ++i) {
            var value = menu->AtkValues[11 + i * 8];
            if (value.Type != AtkValueType.UInt || value.UInt != icon) continue;
            var item = menu->AtkValues[12 + i * 8];
            if (item.Type != AtkValueType.UInt || item.UInt != itemId) continue;
            if (index >= 0) return -1;
            index = i;
        }
        return index;
    }

    public static unsafe bool SelectCapture(AtkUnitBase* menu) {
        var mode = MJIHudMode.GetRow(RareAnimalCatalog.CaptureModeRow);
        if (!mode.HasValue) return false;
        var index = FindEntry(menu, mode.Value.Icon);
        Service.Log.Information($"Rare animal capture mode menu: index={index}, targetIcon={mode.Value.Icon}");
        if (index < 0) return false;
        // The callback previously reached Capture (runtime mode 5); only confirmation was wrong.
        // Use the actual menu index and icon, then wait for the HUD icon to confirm the change.
        Callback.Fire(menu, false, 0, index, (int)mode.Value.Icon, 0, 0);
        return true;
    }

    public static unsafe bool SelectTool(AtkUnitBase* menu, uint pouchId) {
        var pouch = MJIItemPouch.GetRow(pouchId);
        if (!pouch.HasValue || !pouch.Value.Item.IsValid) return false;
        var item = pouch.Value.Item.Value;
        var index = FindEntry(menu, item.Icon, item.RowId);
        Service.Log.Information($"Rare animal capture tool menu: index={index}, pouch={pouchId}, item={item.RowId}");
        if (index < 0) return false;
        Callback.Fire(menu, false, 0, index, (int)item.Icon, (int)item.RowId, 0);
        return true;
    }
}
