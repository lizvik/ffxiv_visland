using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using visland.Helpers;

namespace visland.Pasture;

unsafe class PastureWindow : UIAttachedWindow {
    private readonly PastureConfig _config;
    private readonly PastureDebug _debug = new();

    public PastureWindow() : base(Localization.GetLabel(LocalizationKeys.Windows.PastureTitle, "Pasture Automation"), "MJIAnimalManagement", new(400, 600)) {
        _config = Service.Config.Get<PastureConfig>();
    }

    public override void PreOpenCheck() {
        base.PreOpenCheck();
        var agent = AgentMJIAnimalManagement.Instance();
        IsOpen &= agent != null && !agent->UpdateNeeded;
    }

    public override void OnOpen() {
        if (_config.Collect != CollectStrategy.Manual) {
            var state = CalculateCollectResult();
            if (state == CollectResult.CanCollectSafely || _config.Collect == CollectStrategy.FullAuto && state == CollectResult.CanCollectWithOvercap) {
                CollectAll();
            }
        }
    }

    public override void Draw() {
        WindowName = Localization.GetLabel(LocalizationKeys.Windows.PastureTitle, "Pasture Automation");
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs) {
            using (var tab = ImRaii.TabItem(Localization.GetLabel(LocalizationKeys.Common.Main, "Main")))
                if (tab)
                    DrawMain();
            using (var tab = ImRaii.TabItem(Localization.GetLabel(LocalizationKeys.Common.Debug, "Debug")))
                if (tab)
                    _debug.Draw();
        }
    }

    private void DrawMain() {
        if (UICombo.Enum(Localization.GetLabel(LocalizationKeys.Common.AutoCollect, "Auto Collect"), ref _config.Collect))
            _config.NotifyModified();
        ImGui.Separator();

        var mji = MJIManager.Instance();
        var agent = AgentMJIAnimalManagement.Instance();
        if (mji == null || mji->PastureHandler == null || mji->IslandState.Pasture.EligibleForCare == 0 || agent == null) {
            ImGui.TextUnformatted(Localization.GetText(LocalizationKeys.Common.MammetsNotAvailable));
            return;
        }

        DrawGlobalOperations();
    }

    private void DrawGlobalOperations() {
        var res = CalculateCollectResult();
        if (res != CollectResult.NothingToCollect) {
            // if there's uncollected stuff - propose to collect everything
            using (ImRaii.Disabled(res == CollectResult.EverythingCapped)) {
                if (ImGui.Button(Localization.GetLabel(LocalizationKeys.Common.CollectAll, "Collect all")))
                    CollectAll();
                if (res != CollectResult.CanCollectSafely) {
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xff0000ff))
                        ImGui.TextV(res == CollectResult.EverythingCapped ? Localization.GetText(LocalizationKeys.Common.InventoryFull) : Localization.GetText(LocalizationKeys.Common.OvercapWarning));
                }
            }
        }
        else {
            // TODO: think about any other global operations?
            ImGui.TextV(Localization.GetText(LocalizationKeys.Common.NothingToCollect));
        }
    }

    private CollectResult CalculateCollectResult() {
        var mji = MJIManager.Instance();
        if (mji == null || mji->PastureHandler == null)
            return CollectResult.NothingToCollect;

        var haveNone = true;
        var anyOvercap = false;
        var allFull = true;
        foreach ((var itemId, var count) in mji->PastureHandler->AvailableMammetLeavings) {
            if (count <= 0)
                continue;
            haveNone = false;
            var inventory = Utils.NumItems(itemId);
            allFull &= inventory >= 999;
            anyOvercap |= inventory + count > 999;
        }

        return haveNone ? CollectResult.NothingToCollect : allFull ? CollectResult.EverythingCapped : anyOvercap ? CollectResult.CanCollectWithOvercap : CollectResult.CanCollectSafely;
    }

    private void CollectAll() {
        var mji = MJIManager.Instance();
        if (mji != null && mji->PastureHandler != null) {
            Service.Log.Info("Collecting everything from pasture");
            mji->PastureHandler->CollectLeavingsAll();
        }
    }
}
