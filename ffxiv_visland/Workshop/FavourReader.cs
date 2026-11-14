using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using ECommons.Automation.NeoTaskManager;
using Lumina.Data;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using visland.Helpers;

namespace visland.Workshop;

internal unsafe class FavourReader(List<string> botNames) {
    public WorkshopSolver.FavourState ReadFavourState(bool nextWeek) {
        var mji = RequireManager();
        var favour = mji->FavorState;
        if (favour == null || favour->UpdateState != 2)
            throw new InvalidOperationException("Favour data is not available yet");
        var state = new WorkshopSolver.FavourState();
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i) {
            state.CraftObjectIds[i] = favour->CraftObjectIds[i + offset];
            state.CompletedCounts[i] = favour->NumDelivered[i + offset] + favour->NumScheduled[i + offset];
        }
        if (!mji->DemandDirty)
            state.Popularity.Set(nextWeek ? mji->NextPopularity : mji->CurrentPopularity);
        if (state.CraftObjectIds.Any(id => id == 0))
            throw new Exception("Favour craft IDs not available yet");
        return state;
    }

    public string CreateFavourRequestCommand(bool nextWeek) {
        var state = RequireManager()->FavorState;
        if (state == null)
            throw new InvalidOperationException("Favour data is not available yet");
        if (state->UpdateState != 2)
            throw new InvalidOperationException($"Favour data not available: {state->UpdateState}");

        var res = "/favors";
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i) {
            var id = state->CraftObjectIds[offset + i];
            var name = MJICraftworksObject.GetRow(id)?.WithLanguage(Language.English).Item.Value.Name ?? string.Empty;
            if (!name.IsEmpty && id < botNames.Count)
                res += $" favor{i + 1}:{botNames[(int)id].Replace("\'", "")}";
        }
        return res;
    }

    private static MJIManager* RequireManager() {
        var manager = MJIManager.Instance();
        if (manager == null || !manager->IsPlayerInSanctuary)
            throw new InvalidOperationException("Favour data requires being on your island");
        return manager;
    }

    public void EnsureDemandFavoursAvailable(TaskManager tasks) {
        var manager = RequireManager();
        if (manager->DemandDirty || manager->FavorState == null || manager->FavorState->UpdateState != 2) {
            WorkshopUtils.RequestDemandFavours();
            tasks.Enqueue(() => {
                var current = RequireManager();
                return !current->DemandDirty && current->FavorState != null && current->FavorState->UpdateState == 2;
            }, "Wait for workshop demand and favours");
        }
    }

    public List<WorkshopSolver.WorkshopRec> SolveRecOverrides(bool nextWeek)
        => new WorkshopSolverFavourSheet(ReadFavourState(nextWeek)).Recs;
}
