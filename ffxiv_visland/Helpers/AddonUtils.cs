using Dalamud.Game.ClientState.Conditions;

namespace visland.Helpers;

public static class AddonUtils {
    public static bool IsOccupied()
        => Service.Condition[ConditionFlag.OccupiedInQuestEvent] ||
        Service.Condition[ConditionFlag.OccupiedInEvent] ||
        Service.Condition[ConditionFlag.OccupiedSummoningBell] ||
        Service.Condition[ConditionFlag.Occupied39] ||
        Service.Condition[ConditionFlag.Crafting] ||
        Service.Condition[ConditionFlag.PreparingToCraft] ||
        Service.Condition[ConditionFlag.ExecutingGatheringAction];
}
