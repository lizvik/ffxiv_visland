using System.Numerics;
using Lumina.Excel.Sheets;

namespace visland.Pasture.RareAnimals;

internal static class RareAnimalLocations {
    // Inverse of MapUtil.WorldToMap; returned components are world X/Z.
    internal static Vector2 MapToWorld(Vector2 location, Map map) {
        var origin = new Vector2(1f + 2048f / map.SizeFactor);
        return (location - origin) * 50f - new Vector2(map.OffsetX, map.OffsetY);
    }
}
