using Dalamud.Interface.Windowing;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace visland.Helpers;

// window attached to the addon
public abstract class UIAttachedWindow : Window, IDisposable {
    private readonly string _addon;

    public UIAttachedWindow(string name, string addon, Vector2 initialSize) : base(name) {
        _addon = addon;
        RespectCloseHotkey = false; // don't steal esc focus
        ShowCloseButton = false; // opened/closed automatically
        Size = initialSize;
        SizeCondition = ImGuiCond.FirstUseEver;
        PositionCondition = ImGuiCond.Always; // updated every frame
        ForceMainWindow = true;
    }

    public virtual void Dispose() { }

    public override unsafe void PreOpenCheck() {
        IsOpen = GenericHelpers.TryGetAddonByName<AtkUnitBase>(_addon, out var addon) && GenericHelpers.IsAddonReady(addon);
        if (IsOpen) {
            Position = new Vector2(addon->X + addon->GetScaledWidth(true), addon->Y);
        }
    }
}
