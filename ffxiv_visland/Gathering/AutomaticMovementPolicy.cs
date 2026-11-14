namespace visland.Gathering;

internal enum AutomaticMovementMode { Walk, Ride, Fly, Dive, DiveMounted }

internal static class AutomaticMovementPolicy {
    // vnavmesh's fly flag selects volume navigation, which is also needed underwater.
    public static bool UsesVolume(AutomaticMovementMode mode, bool mounted, bool inFlight, bool diving)
        => diving || mode == AutomaticMovementMode.Fly && mounted && inFlight;

    public static AutomaticMovementMode Select(float distance, float mountDistance, float flightDistance, bool allowFlight, bool diving = false) {
        if (diving) return distance >= mountDistance ? AutomaticMovementMode.DiveMounted : AutomaticMovementMode.Dive;
        if (distance < mountDistance) return AutomaticMovementMode.Walk;
        return allowFlight && distance >= System.Math.Max(mountDistance, flightDistance)
            ? AutomaticMovementMode.Fly : AutomaticMovementMode.Ride;
    }
}
