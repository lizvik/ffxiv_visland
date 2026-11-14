using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using visland.Helpers;

namespace visland.Workshop;

public static unsafe class WorkshopUtils {
    public static (long index, DateTime startTime) CurrentWeek() {
        var cycleData = CycleTime.GetRow(2)!;
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();
        var index = (now - cycleData.Value.FirstCycle) / cycleData.Value.Cycle;
        var startTime = cycleData.Value.FirstCycle + cycleData.Value.Cycle * index;
        return (index, DateTime.UnixEpoch.AddSeconds(startTime));
    }

    public static bool CurrentCycleIsEmpty() {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null)
            return false;
        foreach (ref var w in agent->Data->WorkshopSchedules)
            if (w.NumScheduleEntries != 0)
                return false;
        return true;
    }

    public static void ClearCurrentCycleSchedule() {
        Service.Log.Info($"Clearing current cycle schedule");
        Utils.SynthesizeEvent(&AgentMJICraftSchedule.Instance()->AgentInterface, 6, [new() { Type = AtkValueType.Int, Int = 0 }]);
    }

    // Craftworks use MJICraftworksObject.LevelReq, not the separate MJIRecipe unlock flags.
    public static bool IsRecipeUnlocked(MJICraftworksObject recipe) {
        var manager = MJIManager.Instance();
        return recipe.RowId != 0 && recipe.CraftingTime > 0 && manager != null
            && manager->IsPlayerInSanctuary && recipe.LevelReq <= manager->IslandState.CurrentRank;
    }

    public static bool IsRecipeUnlocked(uint craftObjectId)
        => MJICraftworksObject.GetRow(craftObjectId) is { } recipe && IsRecipeUnlocked(recipe);

    public static WorkshopSolver.Recs RecalculateRecipes(WorkshopSolver.Recs recommendations, bool nextWeek = false, bool notify = false) {
        var manager = MJIManager.Instance();
        if (manager == null || !manager->IsPlayerInSanctuary)
            throw new InvalidOperationException(Localization.GetText(LocalizationKeys.Workshop.RecipePlanningUnavailable));
        var popularity = new WorkshopSolver.Popularity();
        if (!manager->DemandDirty)
            popularity.Set(nextWeek ? manager->NextPopularity : manager->CurrentPopularity);
        var recipes = MJICraftworksObject.Get().Where(IsRecipeUnlocked).OrderBy(recipe => recipe.RowId)
            .Select(recipe => new WorkshopSchedulePlanner.Recipe(recipe.RowId, recipe.CraftingTime,
                recipe.Value * popularity.Multiplier(recipe.RowId), recipe.Theme[0].RowId, recipe.Theme[1].RowId)).ToArray();
        WorkshopSolver.Recs result;
        int rebuilt;
        try {
            result = recommendations.RebuildRecipes(recipes, out rebuilt);
        }
        catch (InvalidOperationException) {
            throw new InvalidOperationException(Localization.GetText(LocalizationKeys.Workshop.RecipePlanningUnavailable));
        }
        if (rebuilt > 0) {
            var message = Localization.FormatText(LocalizationKeys.Workshop.UnlockedSchedulesRebuilt, rebuilt);
            Service.Log.Info(message);
            if (notify)
                Service.ChatGui.Print(message, "visland");
        }
        return result;
    }

    public static bool ScheduleItemToWorkshop(uint objId, int startingHour, int cycle, int workshop) {
        if (workshop < 0 || workshop >= GetMaxWorkshops() || workshop >= 4 || cycle is < 0 or > 13 || startingHour is < 0 or > 23) {
            Service.Log.Warning($"Skipping invalid workshop schedule: workshop={workshop}, cycle={cycle}, hour={startingHour}");
            return false;
        }
        if (!IsRecipeUnlocked(objId)) {
            Service.Log.Warning($"Skipping unavailable workshop recipe {objId}");
            return false;
        }
        Service.Log.Info($"Adding schedule: {objId} @ {startingHour}/{cycle}/{workshop}");
        MJIManager.Instance()->ScheduleCraft((ushort)objId, (byte)((startingHour + 17) % 24), (byte)cycle, (byte)workshop);
        return true;
    }

    // this is what the game uses to refresh the ui after adding schedules
    public static void ResetCurrentCycleToRefreshUI() {
        Service.Log.Info($"Resetting current cycle");
        var agent = AgentMJICraftSchedule.Instance();
        agent->SetDisplayedCycle(agent->Data->CycleDisplayed);
        agent->Data->Flags1 |= AgentMJICraftSchedule.DataFlags1.MaterialsUpdated; // ensure material assignment addon is updated
    }

    public static void SetCurrentCycle(int cycle) {
        Service.Log.Info($"Setting cycle: {cycle}");
        AgentMJICraftSchedule.Instance()->SetDisplayedCycle(cycle);
    }

    public static void SetRestCycles(uint mask) {
        Service.Log.Info($"Setting rest: {mask:X}");
        var agent = AgentMJICraftSchedule.Instance();
        agent->Data->NewRestCycles = mask;
        Utils.SynthesizeEvent(&agent->AgentInterface, 5, [new() { Type = AtkValueType.Int, Int = 0 }]);
    }

    public static bool VoidSecondRestThisWeek() {
        const uint voidThisWeek = 0x2081u; // this=C1 (0x01), next=C1+C7 (0x41 << 7)
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || agent->Data == null)
            return false;
        if (agent->Data->RestCycles == voidThisWeek)
            return false;
        Service.Log.Info($"Voiding this week's second rest: 0x{agent->Data->RestCycles:X} → 0x{voidThisWeek:X}");
        SetRestCycles(voidThisWeek);
        return true;
    }

    public static void RequestDemandFavours() {
        Service.Log.Info("Fetching demand & favours");
        MJIManager.Instance()->RequestDemandFull();
        MJIManager.Instance()->RequestFavorData();
    }

    public static int GetMaxWorkshops() {
        var mji = MJIManager.Instance();
        return mji == null ? 0 : mji->IslandState.CurrentRank switch {
            < 3 => 0,
            < 6 => 1,
            < 8 => 2,
            < 14 => 3,
            _ => 4,
        };
    }

    public static List<int> GetCurrentRestCycles() {
        var restDays1 = MJIManager.Instance()->CraftworksRestDays[0];
        var restDays2 = MJIManager.Instance()->CraftworksRestDays[1];
        var restDays3 = MJIManager.Instance()->CraftworksRestDays[2];
        var restDays4 = MJIManager.Instance()->CraftworksRestDays[3];

        return [restDays1, restDays2, restDays3, restDays4];
    }

    public static int GetNextNonRestCycle(int cycle) {
        var restCycles = GetCurrentRestCycles();
        while (restCycles.Contains(cycle))
            cycle++;

        return cycle;
    }
}
