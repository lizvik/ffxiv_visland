using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using visland.Helpers;

namespace visland.Pasture.RareAnimals;

internal static class AnimalCaptureTools {
    // Capturing tools live in the island pouch, not the character's normal inventory.
    public static unsafe int Count(uint pouchId) {
        if (!Player.Available || !Player.IsOnIsland) return 0;
        var pouch = AgentMJIPouch.Instance();
        return pouch == null || pouch->InventoryData == null ? 0 : Math.Max(0, pouch->GetPouchItemCount(pouchId));
    }
}
