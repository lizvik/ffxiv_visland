using clib.TaskSystem;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using visland.Helpers;

namespace visland.Gathering;

// The path distance selects walking or mounting; clib chooses ground travel or flight for mounted legs.
internal sealed class ClibMovementBackend(
    Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>?> underwaterQuery,
    Action<List<Vector3>> underwaterFollow, Action stopPath, Func<bool> isRunning) : IMovementBackend {
    private MoveTask? _task;
    private bool _disposed;
    private string? _startupFailure;
    public bool Busy => _task is { Completed: false };
    public string? Failure => _startupFailure ?? (_task is { Completed: true, Cancelled: false } task ? task.Failure : null);

    public bool Start(Vector3 target, float tolerance, MovementSettings settings) {
        if (_disposed || Busy) return false;
        _startupFailure = null;
        try {
            var underwater = new PathMovement(target, tolerance, underwaterQuery, underwaterFollow, stopPath, isRunning);
            var task = new MoveTask(target, tolerance, settings, underwater);
            _task = task;
            task.Run(() => task.Completed = true);
            return true;
        }
        catch (Exception ex) {
            _task?.Stop(unloading: true);
            _task = null;
            _startupFailure = "clib-move-start-failed";
            Service.Log.Warning(ex, "Unable to start clib movement");
            return false;
        }
    }

    public void Cancel() => _task?.Stop();

    public void Dispose() {
        _disposed = true;
        _task?.Stop(unloading: true);
    }

    private sealed class MoveTask(Vector3 target, float tolerance, MovementSettings settings, PathMovement underwater) : TaskBase {
        public bool Completed;
        public bool Cancelled { get; private set; }
        public string? Failure { get; private set; }
        private bool _started;
        private bool _cancelInvoked;
        private readonly MovementPathPlan _plan = new();

        protected override async Task Execute() {
            _started = true;
            if (Cancelled) return;
            try {
                while (!Cancelled && Player.Available) {
                    var now = Environment.TickCount64;
                    if (_plan.Started && Vector3.DistanceSquared(Player.Position, _plan.Origin) > 9)
                        _plan.Reset();
                    if (!_plan.Started)
                        _plan.Begin(Player.Position, target, now, Player.InclusiveFlying,
                            settings.AllowFlight && !Player.InclusiveFlying, Service.Navmesh.PathfindCancelable);
                    _plan.Tick(now, Service.Navmesh.PathfindCancelable);
                    if (_plan.Ready) break;
                    await NextFrame();
                }
                if (Cancelled || !Player.Available || Player.DistanceTo(target) <= tolerance) return;
                var useMount = settings.ShouldMount(_plan.Distance);
                if (!useMount && Player.Mounted) await Dismount();
                if (Cancelled) return;
                if (useMount) await MountPreferred();
                if (Cancelled) return;
                if (Player.IsDiving) {
                    if (useMount) await Mount();
                    if (Cancelled) return;
                    underwater.Begin(Player.Position, Environment.TickCount64);
                    while (!Cancelled && underwater.Active) {
                        underwater.Update(Player.Position, Environment.TickCount64, Player.Available && Player.IsDiving);
                        if (underwater.Active) await NextFrame();
                    }
                    Failure = underwater.Failure;
                    return;
                }
                var config = (useMount
                    ? settings.AllowFlight ? MovementConfig.Everything : MovementConfig.GroundMove
                    : MovementConfig.Default)
                    .WithTolerance(tolerance).WithStrategy(PathingStrategy.Navmesh);
                var accepted = await TryMoveTo(target, config, allowTeleportIfFaster: false,
                    stopCondition: () => Cancelled || !Player.Available || Player.IsDiving,
                    allowAethernet: false);
                if (!Cancelled && (!accepted || !Player.Available || Player.DistanceTo(target) > tolerance))
                    Failure = "clib-move-incomplete";
            }
            catch (OperationCanceledException) when (Cancelled) { }
            catch (Exception ex) {
                Failure = "clib-move-faulted";
                Service.Log.Warning(ex, "clib movement failed");
            }
            finally {
                _plan.Reset();
                underwater.Cancel();
                Service.Navmesh.DrainSimpleMove(() => Completed);
            }
        }

        private async Task MountPreferred() {
            if (settings.MountId == 0 || Player.Mounted || !Player.IsMountUnlocked(settings.MountId)) return;
            var deadline = Environment.TickCount64 + 5000;
            var nextAttemptAt = 0L;
            while (!Cancelled && Player.Available && !Player.Mounted && Environment.TickCount64 < deadline) {
                var now = Environment.TickCount64;
                if (now >= nextAttemptAt && !Player.IsCasting && !Player.Mounting && !Player.IsJumping
                    && Player.AnimationLock <= 0) {
                    Player.Mount(settings.MountId);
                    nextAttemptAt = now + 500;
                }
                await NextFrame();
            }
            // clib's Mount() retains an existing mount and supplies roulette if this attempt failed.
        }

        public void Stop(bool unloading = false) {
            if (Completed || (Cancelled && !unloading)) return;
            Cancelled = true;
            _plan.Reset();
            underwater.Cancel();
            if (_started) Service.Navmesh.DrainSimpleMove(() => Completed);
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
