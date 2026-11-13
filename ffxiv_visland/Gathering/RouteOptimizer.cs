using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace visland.Gathering;

internal static class RouteOptimizer {
    // Optimize only independent runs. Fixed steps and movement changes remain barriers.
    public static List<T> Optimize<T>(IReadOnlyList<T> source, Vector3 origin, Func<T, Vector3> position,
        Func<T, bool> canReorder, Func<T, int> movement, bool loop) {
        var result = source.ToList();
        for (var start = 0; start < result.Count;) {
            if (!canReorder(result[start])) { ++start; continue; }
            var end = start + 1;
            while (end < result.Count && canReorder(result[end]) && movement(result[end]) == movement(result[start])) ++end;
            // Bound work on unusually large imported routes.
            if (end - start > 1 && end - start <= 256) {
                var from = start == 0 ? origin : position(result[start - 1]);
                Vector3? to = end < result.Count ? position(result[end]) : loop ? position(result[0]) : null;
                var original = result.GetRange(start, end - start);
                var remaining = original.ToList();
                var candidate = new List<T>();
                var current = from;
                while (remaining.Count > 0) {
                    var best = 0;
                    for (var i = 1; i < remaining.Count; ++i)
                        if (Vector3.DistanceSquared(current, position(remaining[i])) < Vector3.DistanceSquared(current, position(remaining[best]))) best = i;
                    candidate.Add(remaining[best]);
                    current = position(remaining[best]);
                    remaining.RemoveAt(best);
                }
                for (var pass = 0; pass < 4; ++pass) {
                    var changed = false;
                    for (var i = 0; i < candidate.Count - 1; ++i)
                        for (var j = i + 1; j < candidate.Count; ++j) {
                            var before = i == 0 ? from : position(candidate[i - 1]);
                            Vector3? after = j + 1 < candidate.Count ? position(candidate[j + 1]) : to;
                            var oldCost = Vector3.Distance(before, position(candidate[i])) + (after.HasValue ? Vector3.Distance(position(candidate[j]), after.Value) : 0);
                            var newCost = Vector3.Distance(before, position(candidate[j])) + (after.HasValue ? Vector3.Distance(position(candidate[i]), after.Value) : 0);
                            if (newCost + 0.001f < oldCost) { candidate.Reverse(i, j - i + 1); changed = true; }
                        }
                    if (!changed) break;
                }
                if (Length(candidate, from, position, to) + 0.001f < Length(original, from, position, to))
                    for (var i = 0; i < candidate.Count; ++i) result[start + i] = candidate[i];
            }
            start = end;
        }
        Vector3? originalEnd = loop && source.Count > 0 ? position(source[0]) : null;
        Vector3? resultEnd = loop && result.Count > 0 ? position(result[0]) : null;
        return Length(result, origin, position, resultEnd) <= Length(source, origin, position, originalEnd) ? result : source.ToList();
    }

    internal static float Length<T>(IReadOnlyList<T> points, Vector3 origin, Func<T, Vector3> position, Vector3? end = null) {
        float length = 0;
        foreach (var point in points) { var next = position(point); length += Vector3.Distance(origin, next); origin = next; }
        return length + (end.HasValue ? Vector3.Distance(origin, end.Value) : 0);
    }
}
