using System;
using System.Numerics;

namespace visland.Gathering;

internal enum GroundShortcutAction { Continue, Jump, Fallback }

internal sealed class GroundShortcut {
    private long _started, _lastProgress;
    private float _bestDistance;
    private bool _jumped;
    public bool Active { get; private set; }
    public bool Attempted { get; private set; }
    public void Reset() { Active = Attempted = _jumped = false; }
    public void Cancel() { Active = false; }
    public void Begin(long now, float distance) {
        Active = Attempted = true;
        _started = _lastProgress = now;
        _bestDistance = distance;
        _jumped = false;
    }
    public GroundShortcutAction Update(long now, float distance, bool canJump) {
        if (distance < _bestDistance - 0.2f) { _bestDistance = distance; _lastProgress = now; }
        if (now - _started >= 6000) { Cancel(); return GroundShortcutAction.Fallback; }
        if (now - _lastProgress >= (_jumped ? 1500 : 1200)) {
            if (!_jumped && canJump) { _jumped = true; _lastProgress = now; return GroundShortcutAction.Jump; }
            Cancel();
            return GroundShortcutAction.Fallback;
        }
        return GroundShortcutAction.Continue;
    }

    public static bool CanTraverse(Vector3 from, Vector3 to, Func<Vector3, Vector3, bool> clearBody,
        Func<Vector3, Vector3?> floorBelow) {
        var horizontal = Vector2.Distance(new(from.X, from.Z), new(to.X, to.Z));
        if (horizontal < 0.5f || horizontal > 12 || to.Y - from.Y > 1 || from.Y - to.Y > 3) return false;
        if (!clearBody(from, to)) return false;
        var previousHeight = from.Y;
        var samples = Math.Max(2, (int)MathF.Ceiling(horizontal));
        for (var i = 1; i <= samples; ++i) {
            var sample = Vector3.Lerp(from, to, (float)i / samples);
            var ground = floorBelow(sample);
            if (!ground.HasValue || !float.IsFinite(ground.Value.Y)) return false;
            var step = ground.Value.Y - previousHeight;
            if (step > 0.8f || step < -2 || Math.Abs(ground.Value.Y - sample.Y) > 1.5f) return false;
            previousHeight = ground.Value.Y;
        }
        return true;
    }
}
