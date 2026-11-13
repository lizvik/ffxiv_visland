namespace visland.Gathering;

internal enum AutomaticMovementMode { Walk, Ride, Fly }

internal static class AutomaticMovementPolicy {
    public static AutomaticMovementMode Select(float distance, float mountDistance, float flightDistance, bool allowFlight) {
        if (distance < mountDistance) return AutomaticMovementMode.Walk;
        return allowFlight && distance >= System.Math.Max(mountDistance, flightDistance)
            ? AutomaticMovementMode.Fly : AutomaticMovementMode.Ride;
    }
}
