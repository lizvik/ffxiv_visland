using System;
using System.Numerics;

namespace visland.Gathering;

internal sealed class GatheringActionGate {
    private bool _observing;
    private Vector3 _anchor;
    private long _stableSince;
    public void Reset() => _observing = false;

    public bool Ready(Vector3 position, long now, bool blocked) {
        if (blocked) { Reset(); return false; }
        if (!_observing || now < _stableSince || Vector3.DistanceSquared(position, _anchor) > 0.0025f) {
            _observing = true;
            _anchor = position;
            _stableSince = now;
            return false;
        }
        return now - _stableSince >= 350;
    }

    // Match the reported game message as a fallback for a condition flag that already cleared.
    public static bool IsTransientJumpError(string message)
        => message.Contains("跳跃中无法进行该操作", StringComparison.Ordinal);
}
