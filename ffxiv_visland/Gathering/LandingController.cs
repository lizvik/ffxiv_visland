using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;

namespace visland.Gathering;

internal enum LandingResult { Pending, Landed, Failed }
internal enum LandingPhase { Idle, Selecting, Calculating, Approaching, Descending, Landed, Failed }

internal sealed class LandingController {
    private readonly Queue<Vector3> _candidates = new();
    private readonly List<Vector3> _samples = new();
    private Vector3 _origin, _target;
    private bool _prepared;
    private int _maximumAttempts;
    public int CandidateCount { get; private set; }
    public int DescentSteps { get; private set; }
    public Vector3? DescentTarget { get; private set; }
    private Vector3 _anchor;
    private long _lastProgress, _nextCommandAt, _queryStartedAt;
    public long LastProgressAt => _lastProgress;
    public long QueryStartedAt => _queryStartedAt;
    public bool Active => Phase is LandingPhase.Selecting or LandingPhase.Calculating or LandingPhase.Approaching or LandingPhase.Descending;
    public LandingPhase Phase { get; private set; }
    public long StartedAt { get; private set; }
    public Vector3? Floor { get; private set; }
    public int Attempt { get; private set; }
    public string Failure { get; private set; } = "";

    public void Reset() { Phase = LandingPhase.Idle; Floor = null; _candidates.Clear(); _samples.Clear(); _prepared = false; CandidateCount = 0; DescentSteps = 0; DescentTarget = null; Attempt = 0; Failure = ""; }

    public void Begin(Vector3 origin, Vector3 target, long now, int maximumCandidates) {
        Reset();
        StartedAt = _lastProgress = now;
        _nextCommandAt = now;
        _anchor = origin;
        _origin = origin;
        _target = target;
        _maximumAttempts = Math.Clamp(maximumCandidates, 1, 12);
        _samples.Add(target);
        // Sample a complete ring at each radius; query order must not decide landing priority.
        foreach (var radius in new[] { 2f, 4f, 6f })
            for (var direction = 0; direction < 8; ++direction) {
                var angle = direction * MathF.PI / 4;
                _samples.Add(target + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius));
            }
        _samples.Add(origin);
        Phase = LandingPhase.Selecting;
    }

    public LandingResult Tick(long now, Vector3 position, bool inFlight, bool meshReady, bool navigationBusy,
        Func<Vector3, Vector3?> findFloor, Func<Vector3, bool> pathfind, Action<Vector3> descend, Action stop) {
        if (!inFlight) { Phase = LandingPhase.Landed; return LandingResult.Landed; }
        if (now - StartedAt >= 60000) return Fail("total-timeout");
        if (!meshReady) { _lastProgress = now; _anchor = position; return LandingResult.Pending; }
        if (Vector3.DistanceSquared(position, _anchor) >= 0.25f) { _anchor = position; _lastProgress = now; }
        if (((Phase is LandingPhase.Approaching or LandingPhase.Descending) && now - _lastProgress >= 10000)
            || (Phase == LandingPhase.Calculating && now - _queryStartedAt >= 10000)) {
            stop();
            Floor = null;
            Phase = LandingPhase.Selecting;
            _nextCommandAt = Math.Max(_nextCommandAt, now + 1000);
        }
        if (now < _nextCommandAt) return LandingResult.Pending;
        if (Phase == LandingPhase.Selecting) {
            if (navigationBusy) {
                // An older SimpleMove query can start moving after Path.Stop. Reclaim ownership,
                // rate limited, rather than waiting forever for a blocked old path to finish.
                stop();
                _nextCommandAt = now + 1000;
                return LandingResult.Pending;
            }
            if (!_prepared) PrepareCandidates(position, findFloor);
            if (Attempt >= _maximumAttempts || !_candidates.TryDequeue(out var candidate)) return Fail("candidates-exhausted");
            ++Attempt;
            DescentSteps = 0;
            DescentTarget = null;
            _nextCommandAt = now + 1000;
            Vector3? floor = candidate;
            Floor = floor;
            _lastProgress = now;
            _anchor = position;
            _queryStartedAt = now;
            Phase = LandingPhase.Calculating;
            if (!pathfind(floor.Value)) PathFailed(now);
            return LandingResult.Pending;
        }
        if (Phase == LandingPhase.Approaching && Floor.HasValue) {
            var delta = Floor.Value - position;
            if (new Vector2(delta.X, delta.Z).LengthSquared() <= 0.5625f && delta.Y >= -1.5f && delta.Y <= 0.25f) {
                // Path arrival tolerance is not proof of contact with the terrain.
                stop();
                IssueDescent(now, position, descend);
                Phase = LandingPhase.Descending;
                _lastProgress = now;
                _anchor = position;
            }
            else if (!navigationBusy) {
                Floor = null;
                Phase = LandingPhase.Selecting;
                _nextCommandAt = now + 1000;
            }
        }
        else if (Phase == LandingPhase.Descending && !navigationBusy) {
            if (DescentSteps >= 12) {
                stop();
                Floor = null;
                Phase = LandingPhase.Selecting;
                _nextCommandAt = now + 1000;
            }
            else {
                // A completed short path is not proof of landing. Keep applying downward input
                // from the current position until the game's InFlight flag actually clears.
                IssueDescent(now, position, descend);
            }
        }
        return LandingResult.Pending;
    }

    private void IssueDescent(long now, Vector3 position, Action<Vector3> descend) {
        DescentTarget = position - new Vector3(0, 0.3f, 0);
        ++DescentSteps;
        _nextCommandAt = now + 1000;
        descend(DescentTarget.Value);
        // Do not refresh the no-progress timer here: only actual movement can do that.
    }

    private void PrepareCandidates(Vector3 position, Func<Vector3, Vector3?> findFloor) {
        var projected = new List<Vector3>();
        foreach (var sample in _samples) {
            var query = sample;
            query.Y = Math.Max(Math.Max(_origin.Y, _target.Y), position.Y) + 2;
            var floor = findFloor(query);
            if (floor.HasValue && float.IsFinite(floor.Value.X) && float.IsFinite(floor.Value.Y) && float.IsFinite(floor.Value.Z))
                projected.Add(floor.Value);
        }
        // Rank the actual projected ground positions, not the requested sample locations.
        // For near-equal ground positions retain the one closest to the gathering target.
        var unique = new List<Vector3>();
        foreach (var floor in projected.OrderBy(p => Vector3.DistanceSquared(p, _target))
            .ThenBy(p => Vector3.DistanceSquared(p, _origin))) {
            if (unique.Exists(p => Vector3.DistanceSquared(p, floor) < 0.25f)) continue;
            unique.Add(floor);
            _candidates.Enqueue(floor);
        }
        CandidateCount = unique.Count;
        _prepared = true;
    }

    public bool PathApplied(long now, Vector3 position) {
        if (Phase != LandingPhase.Calculating || now - _queryStartedAt >= 10000 || now - StartedAt >= 60000)
            return false;
        Phase = LandingPhase.Approaching;
        _anchor = position;
        _lastProgress = now;
        _nextCommandAt = now + 1000;
        return true;
    }

    public void PathFailed(long now) {
        if (Phase != LandingPhase.Calculating) return;
        Floor = null;
        Phase = LandingPhase.Selecting;
        _nextCommandAt = now + 1000;
    }

    private LandingResult Fail(string reason) { Phase = LandingPhase.Failed; Failure = reason; return LandingResult.Failed; }
}
