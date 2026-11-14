using clib.TaskSystem;
using System;
using System.Numerics;
using System.Threading.Tasks;
using visland.Helpers;

namespace visland.Gathering;

// Keep cancelled tasks until their completion callback: clib's finalizers can still stop vnavmesh.
// A replacement path must wait until those finalizers have run.
internal sealed class ClibGroundMovementBackend : IGroundMovementBackend {
    private GroundMoveTask? _task;
    private bool _disposed;
    private string? _startupFailure;
    public bool Busy => _task is { Completed: false };
    public string? Failure => _startupFailure ?? (_task is { Completed: true, Cancelled: false } task ? task.Failure : null);

    public bool Start(Vector3 target, float tolerance) {
        if (_disposed || Busy) return false;
        _startupFailure = null;
        try {
            var task = new GroundMoveTask(target, tolerance);
            _task = task;
            task.Run(() => task.Completed = true);
            return true;
        }
        catch (Exception ex) {
            _task?.Stop(unloading: true);
            _task = null;
            _startupFailure = "clib-ground-move-start-failed";
            Service.Log.Warning(ex, "Unable to start clib ground movement");
            return false;
        }
    }

    public void Cancel() => _task?.Stop();

    public void Dispose() {
        _disposed = true;
        _task?.Stop(unloading: true);
    }

    private sealed class GroundMoveTask(Vector3 target, float tolerance) : TaskBase {
        public bool Completed;
        public bool Cancelled { get; private set; }
        public string? Failure { get; private set; }
        private bool _started;
        private bool _cancelInvoked;

        protected override async Task Execute() {
            _started = true;
            if (Cancelled) return;
            try {
                // Visland owns mode selection, mounts, flight, landing and the sampling of path distance.
                // clib executes only this ground leg, without teleport/aethernet or mount side effects.
                var config = MovementConfig.Default.WithTolerance(tolerance).WithStrategy(PathingStrategy.Navmesh);
                var accepted = await TryMoveTo(target, config, allowTeleportIfFaster: false,
                    stopCondition: () => Cancelled || !Player.Available || Player.InclusiveFlying,
                    allowAethernet: false);
                if (!Cancelled && (!accepted || !Player.Available || Player.DistanceTo(target) > tolerance))
                    Failure = "clib-ground-move-incomplete";
            }
            catch (OperationCanceledException) when (Cancelled) { }
            catch (Exception ex) {
                Failure = "clib-ground-move-faulted";
                Service.Log.Warning(ex, "clib ground movement failed");
            }
        }

        public void Stop(bool unloading = false) {
            if (Completed) return;
            Cancelled = true;
            // Cancelling a framework task before it starts suppresses its completion callback.
            // Let a queued Execute return normally, so the replacement can observe cleanup completion.
            if (!_started && !unloading) return;
            if (_cancelInvoked) return;
            _cancelInvoked = true;
            try { Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
