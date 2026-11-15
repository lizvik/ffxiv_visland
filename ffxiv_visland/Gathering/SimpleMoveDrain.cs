using System;
using System.Collections.Generic;
using System.Linq;

namespace visland.Gathering;

// Cancellation cleanup only: discard clib's late SimpleMove result before allowing another path.
internal sealed class SimpleMoveDrain(Func<bool> queryPending, Action stopPath) {
    private readonly List<Func<bool>> _cleanup = new();
    public bool Active { get; private set; }

    public void Begin(Func<bool> cleanupCompleted) {
        _cleanup.Add(cleanupCompleted);
        Active = true;
        stopPath();
    }

    public void Update() {
        if (!Active) return;
        stopPath();
        if (queryPending() || _cleanup.Any(done => !done())) return;
        _cleanup.Clear();
        Active = false;
    }
}
