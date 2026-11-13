using System;
using System.Collections.Generic;

namespace visland.Gathering;

internal static class NodeInventoryPolicy {
    public static bool AllAvailableMaterialsFull(IEnumerable<uint> materials, Func<uint, bool> unlocked,
        Func<uint, int> count, Func<uint, uint> capacity) {
        var any = false;
        foreach (var item in materials) {
            if (!unlocked(item)) continue;
            any = true;
            var cap = capacity(item);
            if (cap == 0 || count(item) < cap) return false;
        }
        // Unknown nodes and nodes with no unlocked materials must never imply a full inventory.
        return any;
    }
}
