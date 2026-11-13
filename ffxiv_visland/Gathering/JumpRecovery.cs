using System.Numerics;

namespace visland.Gathering;

internal enum JumpRecoveryResult { Pending, Moved, Failed }

internal sealed class JumpRecovery {
    private Vector3 _origin;
    private long _started;
    public bool Active { get; private set; }
    public void Begin(Vector3 position, long now) { _origin = position; _started = now; Active = true; }
    public void Reset() => Active = false;
    public JumpRecoveryResult Update(Vector3 position, long now) {
        // Jump height alone is not evidence that the obstacle was cleared.
        var horizontal = new Vector2(position.X - _origin.X, position.Z - _origin.Z);
        if (horizontal.LengthSquared() >= 0.25f) { Reset(); return JumpRecoveryResult.Moved; }
        if (now - _started >= 3000) { Reset(); return JumpRecoveryResult.Failed; }
        return JumpRecoveryResult.Pending;
    }
}
