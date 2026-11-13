using Dalamud.Game.Text;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using System.Linq;
using visland.Helpers;

namespace visland.Gathering;

public unsafe class GatherDebug {

    public void Draw() {
        using var child = ImRaii.Child("child");
        if (!child) return;
        if (!Player.Available) return;

        if (Service.TargetManager.Target != null) {
            Utils.DrawSection("Target", ImGuiColors.ParsedGold);
            var t = Service.TargetManager.Target;
            if (GatheringPoint.GetRow(t.BaseId) is { } gp) {
                ImGui.Text($"IsNode: {gp}");
                if (gp.GatheringPointBase.IsValid)
                    ImGui.Text($"GatheringType: {gp.GatheringPointBase.Value.GatheringType.RowId}");
            }
            else
                ImGui.Text($"Not a GatheringPoint (BaseId={t.BaseId})");
        }
    }
}
