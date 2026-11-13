using System.Collections.Generic;
using System.Linq;

namespace visland.Gathering;

internal static class IslandGatheringPolicy {
    // Zero is the legacy value for routes recorded without territory metadata.
    public static bool CanRun(bool playerAvailable, bool onIsland, uint territory, IEnumerable<uint> waypointTerritories)
        => playerAvailable && onIsland && territory != 0 && waypointTerritories.All(zone => zone == 0 || zone == territory);
}
