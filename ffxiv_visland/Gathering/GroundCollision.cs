using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace visland.Gathering;

internal static class GroundCollision {
    public static bool ClearBody(Vector3 from, Vector3 to) {
        var delta = to - from;
        if (delta.LengthSquared() < 0.01f) return true;
        var direction = Vector3.Normalize(delta);
        var side = Vector3.Cross(Vector3.UnitY, direction);
        if (side.LengthSquared() > 0.01f) side = Vector3.Normalize(side) * 0.35f;
        // Three chest-height rays reject walls while allowing low steps to be tried with a jump.
        foreach (var offset in new[] { Vector3.Zero, side, -side }) {
            var start = from + new Vector3(0, 1.1f, 0) + offset;
            if (BGCollisionModule.RaycastMaterialFilter(start, direction, out _, delta.Length())) return false;
        }
        return true;
    }

    public static Vector3? FloorBelow(Vector3 point) {
        var start = point + new Vector3(0, 2, 0);
        return BGCollisionModule.RaycastMaterialFilter(start, -Vector3.UnitY, out var hit, 5)
            ? hit.Point : null;
    }
}
