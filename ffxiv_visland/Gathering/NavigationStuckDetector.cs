using System.Numerics;

namespace visland.Gathering;

// Progress means approaching a path point, not merely sliding along a wall.
internal sealed class NavigationStuckDetector {
    private bool _tracking;
    private Vector3 _anchor;
    private Vector3 _target;
    private int _waypoint;
    private long _lastProgress;
    private Vector3? _pathTarget;
    private float _bestDistance;
    private int? _remaining;

    public void Reset() => _tracking = false;

    public bool Update(Vector3 position, Vector3 target, int waypoint, long now, int timeoutSeconds,
        Vector3? pathTarget = null, int? remaining = null) {
        if (timeoutSeconds <= 0) { Reset(); return false; }
        if (!_tracking || _target != target || _waypoint != waypoint || now < _lastProgress) {
            _tracking = true;
            _anchor = position;
            _target = target;
            _waypoint = waypoint;
            _lastProgress = now;
            _pathTarget = pathTarget;
            _remaining = remaining;
            _bestDistance = pathTarget.HasValue ? Vector3.Distance(position, pathTarget.Value) : 0;
            return false;
        }
        var progressed = false;
        if (pathTarget.HasValue) {
            progressed = _remaining.HasValue && remaining.HasValue && remaining.Value < _remaining.Value;
            var distance = Vector3.Distance(position, pathTarget.Value);
            if (_pathTarget != pathTarget) {
                // A consumed point is progress. A replacement path alone is not.
                _bestDistance = distance;
            }
            else if (distance <= _bestDistance - 0.5f) {
                _bestDistance = distance;
                progressed = true;
            }
        }
        else if (Vector3.DistanceSquared(position, _anchor) >= 0.25f) {
            _anchor = position;
            progressed = true;
        }
        _pathTarget = pathTarget;
        _remaining = remaining;
        if (progressed) _lastProgress = now;
        if (now - _lastProgress < (long)timeoutSeconds * 1000)
            return false;
        Reset();
        return true;
    }
}
