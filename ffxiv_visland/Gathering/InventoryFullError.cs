using System;

namespace visland.Gathering;

internal static class InventoryFullError {
    // Deliberately match capacity errors, rather than every error containing "inventory".
    public static bool Matches(string message) {
        foreach (var phrase in new[] {
            "库存已满", "背包已满", "背包空间不足", "物品栏已满", "物品栏空间不足",
            "无法再持有", "无法持有更多", "不能持有更多", "持有数量已达到上限", "持有数量已达上限", "超过持有数量上限",
            "inventory is full", "inventory full", "inventory space is insufficient", "not enough space in your inventory",
            "insufficient inventory space", "cannot carry any more", "unable to carry any more", "cannot obtain any more"
        })
            if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
