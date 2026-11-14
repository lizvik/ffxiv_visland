using Dalamud.Game.ClientState.Conditions;
using ECommons.Throttlers;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;

namespace visland.Helpers;

public static unsafe class Player {
    [MemberNotNullWhen(true, nameof(Object))]
    public static bool Available => Service.ObjectTable.LocalPlayer is { IsDead: false };
    public static IGameObject? Object => Service.ObjectTable.LocalPlayer;
    public static Vector3 Position => Object?.Position ?? default;
    public static ulong CID => Service.PlayerState.ContentId;
    public static uint Job => Service.PlayerState.ClassJob.RowId;
    public static uint Territory => Service.ClientState.TerritoryType;
    public static bool Mounted => Service.Condition[ConditionFlag.Mounted];
    public static bool Mounting => Service.Condition[ConditionFlag.Mounting];
    public static bool IsJumping => Service.Condition[ConditionFlag.Jumping] || Service.Condition[ConditionFlag.Jumping61];
    public static bool IsCasting => Service.Condition[ConditionFlag.Casting];
    public static IEnumerable<IStatus> Status => Service.ObjectTable.LocalPlayer?.StatusList is { } list ? list : [];
    public static bool Normal => Service.Condition[ConditionFlag.NormalConditions];
    public static bool ExclusiveFlying => Service.Condition[ConditionFlag.InFlight];
    public static bool IsDiving => Service.Condition[ConditionFlag.Diving];
    public static bool InclusiveFlying => ExclusiveFlying || IsDiving;
    public static bool InWater => Service.Condition[ConditionFlag.Swimming] || Service.Condition[ConditionFlag.Diving];
    public static float SprintRemainingTime => Status.FirstOrDefault(s => s.StatusId == 50)?.RemainingTime ?? 0;
    public static bool StellarSprinting => Status.Any(x => x.StatusId == 4398);
    public static float AnimationLock => ActionManager.Instance()->AnimationLock;
    public static bool InGatheringAnimation => Service.Condition[ConditionFlag.ExecutingGatheringAction];
    public static uint Gp => Service.ObjectTable.LocalPlayer?.CurrentGp ?? 0;
    public static uint MaxGp => Service.ObjectTable.LocalPlayer?.MaxGp ?? 0;
    public static int Gathering => PlayerState.Instance()->Attributes[72];
    public static int Perception => PlayerState.Instance()->Attributes[73];
    public static bool IsOnIsland => MJIManager.Instance() != null && MJIManager.Instance()->IsPlayerInSanctuary;

    public static float DistanceTo(Vector3 pos) => Object == null ? float.MaxValue : Vector3.Distance(Object.Position, pos);

    public static bool IsMountUnlocked(uint id) => id != 0 && PlayerState.Instance()->IsMountUnlocked(id);
    public static void Mount(uint id = 0) {
        if (IsMountUnlocked(id))
            ExecuteActionSafe(ActionType.Mount, id);
        else
            ExecuteActionSafe(ActionType.GeneralAction, 24);
    }
    public static void Dismount() => ExecuteActionSafe(ActionType.GeneralAction, 23);
    public static void Jump() => ExecuteActionSafe(ActionType.GeneralAction, 2);

    public static void Sprint() {
        if (!Available || Mounted || Mounting || IsCasting || IsJumping || ExclusiveFlying
            || InGatheringAnimation || AnimationLock > 0 || SprintRemainingTime > 0 || StellarSprinting)
            return;
        // Isle Sprint upgrades the permanent jogging effect after its 20-second sprint expires.
        // Check the action's actual availability rather than treating buff duration as cooldown.
        var onIsland = IsOnIsland;
        // Isle Sprint is also considered while swimming/diving; the game determines availability.
        if (InWater && !onIsland) return;
        var actionType = onIsland ? ActionType.Action : ActionType.GeneralAction;
        var actionId = onIsland ? 31314u : 4u;
        if (_sprint.Throttle("sprint", 1000)) {
            var manager = ActionManager.Instance();
            if (manager != null && manager->GetActionStatus(actionType, actionId) == 0)
                manager->UseAction(actionType, actionId);
        }
    }

    public static bool SwitchJob(uint classJobId) {
        if (Job == classJobId) return true;
        var gearsets = RaptureGearsetModule.Instance();
        foreach (ref var gs in gearsets->Entries) {
            if (!gearsets->IsValidGearset(gs.Id)) continue;
            if (gs.ClassJob == classJobId) {
                Service.Log.Debug($"Switching from {Job} to {classJobId} (gs: {gs.Id}/{gs.NameString})");
                return gearsets->EquipGearset(gs.Id) == 0;
            }
        }
        return false;
    }

    public static int JobLevel(uint classJobId) => Service.PlayerState.GetClassJobLevel(ClassJob.GetRow(classJobId)!.Value);

    private static void ExecuteActionSafe(ActionType type, uint id, uint extraParam = 0) {
        if (_action.Throttle("action", 500))
            ActionManager.Instance()->UseAction(type, id, extraParam: extraParam);
    }

    private static readonly EzThrottler<string> _action = new();
    private static readonly EzThrottler<string> _sprint = new();
}
