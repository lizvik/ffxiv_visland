using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using visland.Helpers;

namespace visland.Pasture.RareAnimals;

internal static class AnimalCaptureTools {
    internal static uint ChoosePouch(RareAnimal animal, uint selected, int makeshift, int flawless) {
        if (makeshift > 0 && (selected == animal.ToolItem || selected == animal.ToolPouchId)) return animal.ToolPouchId;
        if (flawless > 0 && (selected == animal.FlawlessToolItem || selected == animal.FlawlessToolPouchId)) return animal.FlawlessToolPouchId;
        return makeshift > 0 ? animal.ToolPouchId : flawless > 0 ? animal.FlawlessToolPouchId : 0;
    }

    // Capturing tools live in the island pouch, not the character's normal inventory.
    public static unsafe int Count(uint pouchId) {
        if (!Player.Available || !Player.IsOnIsland) return 0;
        var pouch = AgentMJIPouch.Instance();
        return pouch == null || pouch->InventoryData == null ? 0 : Math.Max(0, pouch->GetPouchItemCount(pouchId));
    }
}
