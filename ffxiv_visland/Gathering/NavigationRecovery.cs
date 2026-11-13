using System.Collections.Generic;
using System;

namespace visland.Gathering;

internal sealed class NavigationRecovery {
    public int Retries { get; private set; }
    public void ResetRetries() => Retries = 0;
    public void Reset() => ResetRetries();
    public bool TryRetry(int maximum) {
        if (Retries >= Math.Max(0, maximum)) return false;
        ++Retries;
        return true;
    }

}
