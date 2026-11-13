using System.Numerics;

namespace visland.Gathering;

// Uses monotonic time and cumulative displacement to ignore small position jitter.
internal sealed class NavigationStuckDetector {
    private bool _tracking;
    private Vector3 _anchor;
    private Vector3 _target;
    private int _waypoint;
    private long _lastProgress;

    public void Reset() => _tracking = false;

    public bool Update(Vector3 position, Vector3 target, int waypoint, long now, int timeoutSeconds) {
        if (timeoutSeconds <= 0) { Reset(); return false; }
        if (!_tracking || _target != target || _waypoint != waypoint || now < _lastProgress
            || Vector3.DistanceSquared(position, _anchor) >= 0.25f) {
            _tracking = true;
            _anchor = position;
            _target = target;
            _waypoint = waypoint;
            _lastProgress = now;
            return false;
        }
        if (now - _lastProgress < (long)timeoutSeconds * 1000)
            return false;
        Reset();
        return true;
    }
}
