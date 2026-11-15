using clib.TaskSystem;
using System;
using System.Threading.Tasks;
using visland.Helpers;

namespace visland.Gathering;

internal sealed class ClibDismountTask : TaskBase, IDismountTask {
    public bool Completed { get; private set; }
    public Exception? Fault { get; private set; }
    private bool _started, _cancelled;

    public void Start() => Run(() => Completed = true);

    protected override async Task Execute() {
        _started = true;
        if (_cancelled) return;
        try {
            if (!Player.Available)
                throw new InvalidOperationException("The player is unavailable for dismounting.");
            await Dismount();
        }
        catch (OperationCanceledException) when (_cancelled) { }
        catch (Exception ex) {
            Fault = ex;
            Service.Log.Warning(ex, "clib landing/dismount task failed");
        }
        finally { Service.Navmesh.DrainSimpleMove(() => Completed); }
    }

    public void Stop() {
        if (Completed || _cancelled) return;
        _cancelled = true;
        // Path.Stop does not cancel a SimpleMove query. Quarantine late results after cancellation.
        if (_started) Service.Navmesh.DrainSimpleMove(() => Completed);
        try { Cancel(); }
        catch (ObjectDisposedException) { }
        // Cancelling a queued framework invocation suppresses its completion callback.
        if (!_started) Completed = true;
    }

    public void Dispose() => Stop();
}
