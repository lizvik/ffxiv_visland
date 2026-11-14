using System;
using System.Collections.Generic;

namespace visland.Workshop;

// Scores a complete day using recipe value/popularity and adjacent efficiency bonuses.
internal static class WorkshopSchedulePlanner {
    internal readonly record struct Recipe(uint Id, int Duration, float Value, uint Theme1, uint Theme2);

    internal static WorkshopSolver.WorkshopRec Solve(IReadOnlyList<Recipe> recipes) {
        var count = recipes.Count;
        var scores = new double[25, count];
        var previous = new int[25, count];
        for (var hour = 0; hour <= 24; ++hour)
            for (var index = 0; index < count; ++index) {
                scores[hour, index] = double.NegativeInfinity;
                previous[hour, index] = -1;
            }

        for (var index = 0; index < count; ++index) {
            var recipe = recipes[index];
            if (recipe.Duration is > 0 and <= 24)
                scores[recipe.Duration, index] = recipe.Value;
        }
        for (var hour = 1; hour < 24; ++hour)
            for (var last = 0; last < count; ++last) {
                if (double.IsNegativeInfinity(scores[hour, last]))
                    continue;
                for (var next = 0; next < count; ++next) {
                    var recipe = recipes[next];
                    var end = hour + recipe.Duration;
                    if (recipe.Duration <= 0 || end > 24)
                        continue;
                    var linked = IsLinked(recipes[last], recipe);
                    var score = scores[hour, last] + recipe.Value * (linked ? 2 : 1);
                    if (score <= scores[end, next])
                        continue;
                    scores[end, next] = score;
                    previous[end, next] = last;
                }
            }

        var best = -1;
        for (var index = 0; index < count; ++index)
            if (!double.IsNegativeInfinity(scores[24, index]) && (best < 0 || scores[24, index] > scores[24, best]))
                best = index;
        if (best < 0)
            throw new InvalidOperationException("No unlocked recipe combination can fill 24 hours.");

        var result = new WorkshopSolver.WorkshopRec();
        for (var hour = 24; best >= 0;) {
            var recipe = recipes[best];
            var start = hour - recipe.Duration;
            result.Add(start, recipe.Id);
            best = previous[hour, best];
            hour = start;
        }
        result.Slots.Reverse();
        return result;
    }

    internal static bool IsComplete(WorkshopSolver.WorkshopRec schedule, IReadOnlyDictionary<uint, Recipe> recipes) {
        var hour = 0;
        foreach (var slot in schedule.Slots) {
            if (slot.Slot != hour || !recipes.TryGetValue(slot.CraftObjectId, out var recipe))
                return false;
            hour += recipe.Duration;
        }
        return hour == 24;
    }

    private static bool IsLinked(Recipe left, Recipe right)
        => left.Id != right.Id && (left.Theme1 != 0 && (left.Theme1 == right.Theme1 || left.Theme1 == right.Theme2)
            || left.Theme2 != 0 && (left.Theme2 == right.Theme1 || left.Theme2 == right.Theme2));
}
