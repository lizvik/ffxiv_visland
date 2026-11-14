using System.Globalization;
using System.Numerics;

namespace visland.Helpers;

internal static class CommandArguments {
    public static bool TryParsePosition(string[] arguments, out Vector3 position) {
        position = default;
        if (arguments.Length != 4
            || !float.TryParse(arguments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(arguments[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(arguments[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;
        position = new(x, y, z);
        return IsFinite(position);
    }

    public static bool IsFinite(Vector3 position)
        => float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
}
