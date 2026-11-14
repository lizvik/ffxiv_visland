using System.Collections.Generic;

namespace visland;

public enum CollectResult {
    NothingToCollect,
    CanCollectSafely,
    CanCollectWithOvercap,
    EverythingCapped
}

internal static class CollectionCapacity {
    public static CollectResult Classify(IEnumerable<(int inventory, int amount)> resources, int capacity = 999) {
        var any = false;
        var allFull = true;
        var anyOvercap = false;
        foreach (var (inventory, amount) in resources) {
            if (amount <= 0) continue;
            any = true;
            allFull &= inventory >= capacity;
            anyOvercap |= (long)inventory + amount > capacity;
        }
        return !any ? CollectResult.NothingToCollect : allFull ? CollectResult.EverythingCapped
            : anyOvercap ? CollectResult.CanCollectWithOvercap : CollectResult.CanCollectSafely;
    }
}
