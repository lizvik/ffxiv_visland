using ECommons.ImGuiMethods;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using visland.Helpers;
using visland.IPC;
using visland.Pasture.RareAnimals;
using static visland.Gathering.GatherRouteDB;

namespace visland.Gathering;

public class GatherWindow : Window {
    private readonly UITree _tree = new();
    private readonly RareAnimalPage _rareAnimals = new();
    private readonly List<System.Action> _postDraw = [];

    public GatherRouteDB RouteDB = null!;
    public GatherRouteExec Exec => Service.RouteExec;

    private int selectedRouteIndex = -1;
    private static bool loop;

    private Vector4 greenColor = new Vector4(0x5C, 0xB8, 0x5C, 0xFF) / 0xFF;
    private Vector4 redColor = new Vector4(0xD9, 0x53, 0x4F, 0xFF) / 0xFF;

    private string searchString = string.Empty;
    private readonly List<Route> _filteredRoutes = [];
    private bool _searchChanged;
    private readonly HashSet<string> _openGroups = [];
    private HashSet<string> _preSearchOpenGroups = [];
    private List<Route> VisibleRoutes => searchString.Length > 0 ? _filteredRoutes : RouteDB.Routes;
    private FontAwesomeIcon PlayIcon => Exec.CurrentRoute != null && !Exec.Paused ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
    private string PlayTooltip => Exec.CurrentRoute == null ? Localization.GetText(LocalizationKeys.Gathering.StartRoute) : Exec.Paused ? Localization.GetText(LocalizationKeys.Gathering.ResumeRoute) : Localization.GetText(LocalizationKeys.Gathering.PauseRoute);

    public GatherWindow() : base(Localization.GetLabel(LocalizationKeys.Windows.GatheringTitle, "Gathering Automation"), ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse) {
        Size = new Vector2(800, 800);
        SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

    }

    public override void Draw() {
        WindowName = Localization.GetLabel(LocalizationKeys.Windows.GatheringTitle, "Gathering Automation");
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs) {
            using (var tab = ImRaii.TabItem(Localization.GetLabel(LocalizationKeys.Gathering.Routes, "Routes")))
                if (tab) {
                    ImGui.TextWrapped(Localization.GetText(LocalizationKeys.Gathering.IslandOnlyHelp));
                    DrawExecution();
                    ImGui.Separator();
                    ImGui.Spacing();

                    var cra = ImGui.GetContentRegionAvail();
                    var sidebar = cra with { X = cra.X * 0.40f };
                    var editor = cra with { X = cra.X * 0.60f };

                    DrawSidebar(sidebar);
                    ImGui.SameLine();
                    DrawEditor(editor);

                    foreach (var a in _postDraw)
                        a();
                    _postDraw.Clear();
                }
            using (var tab = ImRaii.TabItem(Localization.GetLabel(LocalizationKeys.RareAnimals.Title)))
                if (tab)
                    _rareAnimals.Draw();
            using (var tab = ImRaii.TabItem(Localization.GetLabel(LocalizationKeys.Common.Settings, "Settings")))
                if (tab)
                    DrawSettings();
        }
    }

    private void DrawExecution() {
        ImGui.Text(Localization.GetText(LocalizationKeys.Gathering.Status));
        ImGui.SameLine();

        if (Exec.CurrentRoute != null)
            Utils.FlashText($"{(Exec.Paused ? Localization.GetText(LocalizationKeys.Gathering.StatusPaused) : Exec.Waiting ? Localization.GetText(LocalizationKeys.Gathering.StatusWaiting) : Localization.GetText(LocalizationKeys.Gathering.StatusRunning))}", new Vector4(1.0f, 1.0f, 1.0f, 1.0f), Exec.Paused ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), 2);
        ImGui.SameLine();

        if (Exec.CurrentRoute == null || Exec.CurrentWaypoint >= Exec.CurrentRoute.Waypoints.Count) {
            ImGui.Text(Localization.GetText(LocalizationKeys.Gathering.RouteNotRunning));
            return;
        }

        if (Exec.CurrentRoute != null) // Finish() call could've reset it
        {
            ImGui.SameLine();
            ImGui.Text(Localization.FormatText(LocalizationKeys.Gathering.RouteStepFormat, Exec.CurrentRoute.Name, Exec.CurrentWaypoint + 1, Exec.CurrentRoute.Waypoints[Exec.CurrentWaypoint].Position));

            if (Exec.Waiting) {
                ImGui.SameLine();
                ImGui.Text(Localization.FormatText(LocalizationKeys.Gathering.WaitMillisecondsFormat, Exec.WaitUntil - Environment.TickCount64));
            }
        }

        ImGui.SameLine();
        ImGui.Text(Localization.FormatText(LocalizationKeys.Gathering.StateFormat, Exec.CurrentState));
    }

    private unsafe void DrawSidebar(Vector2 size) {
        using (ImRaii.Child("Sidebar", size, false)) {
            if (ImGuiEx.IconButton(FontAwesomeIcon.Plus)) {
                RouteDB.Routes.Add(new() { Name = "Unnamed Route" });
                RouteDB.NotifyModified();
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.CreateANewRoute));
            ImGui.SameLine();

            if (ImGuiEx.IconButton(FontAwesomeIcon.FileImport))
                TryImport(RouteDB);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.ImportRouteFromClipboard));

            ImGui.SameLine();
            RapidImport();

            ImGuiEx.TextV(Localization.GetText(LocalizationKeys.Gathering.Search));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            var wasSearching = searchString.Length > 0;
            if (ImGui.InputText("###RouteSearch", ref searchString, 500)) {
                if (!wasSearching) // restored when the search is cleared
                    _preSearchOpenGroups = [.. _openGroups];
                _searchChanged = true;
                RefreshFilteredRoutes();
            }

            ImGui.Separator();

            using (ImRaii.Child("routes")) {
                var searching = searchString.Length > 0;
                var matchCounts = VisibleRoutes.GroupBy(r => r.Group).ToDictionary(g => g.Key, g => g.Count());
                if (!searching)
                    _openGroups.Clear();

                var groups = GetGroups(RouteDB, true);
                foreach (var group in groups) {
                    var matches = matchCounts.GetValueOrDefault(group);
                    if (_searchChanged) // open groups with hits, or restore the pre-search layout
                        ImGui.SetNextItemOpen(searching ? matches > 0 : _preSearchOpenGroups.Contains(group), ImGuiCond.Always);
                    var groupLabel = searching ? $"{group} ({matches})" : group;
                    var groupColor = searching && matches == 0 ? ImGui.GetColorU32(ImGuiCol.TextDisabled) : 0xffffffff;
                    foreach (var _ in _tree.Node($"{groupLabel}###{groups.IndexOf(group)}", color: groupColor, contextMenu: () => ContextMenuGroup(group))) {
                        if (!searching)
                            _openGroups.Add(group);
                        for (var i = 0; i < VisibleRoutes.Count; i++) {
                            var route = VisibleRoutes[i];
                            var routeGroup = string.IsNullOrEmpty(route.Group) ? "None" : route.Group;
                            if (routeGroup == group) {
                                var routeLabel = $"{route.Name} ({route.Waypoints.Count} steps)";
                                if (searching) { // label drawn by hand so the match can be colored
                                    var textPos = ImGui.GetCursorScreenPos();
                                    if (ImGui.Selectable($"###{i}", i == selectedRouteIndex, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetTextLineHeight())))
                                        selectedRouteIndex = i;
                                    DrawHighlightedLabel(textPos, routeLabel, route.Name.Length);
                                }
                                else if (ImGui.Selectable($"{routeLabel}###{i}", i == selectedRouteIndex))
                                    selectedRouteIndex = i;
                                //if (ImRaii.ContextPopup($"{route.Name}{i}"))
                                //{
                                //    selectedRouteIndex = i;
                                //    ContextMenuRoute(routeSource[i]);
                                //}
                            }
                        }
                    }
                }
                _searchChanged = false;
            }
        }
    }

    private void RefreshFilteredRoutes() {
        _filteredRoutes.Clear();
        if (searchString.Length == 0)
            return;

        _filteredRoutes.AddRange(RouteDB.Routes.Where(route => route.Name.Contains(searchString, StringComparison.CurrentCultureIgnoreCase) || route.Group.Contains(searchString, StringComparison.CurrentCultureIgnoreCase)));
    }

    // only the first searchableLength chars get matched, so the "(x steps)" suffix never lights up
    private void DrawHighlightedLabel(Vector2 pos, string label, int searchableLength) {
        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        var matchColor = ImGui.GetColorU32(ImGuiColors.ParsedGold);
        var underlineY = pos.Y + ImGui.GetTextLineHeight();

        void Segment(string text, bool match) {
            if (text.Length == 0) return;
            var width = ImGui.CalcTextSize(text).X;
            drawList.AddText(pos, match ? matchColor : textColor, text);
            if (match)
                drawList.AddLine(new Vector2(pos.X, underlineY), new Vector2(pos.X + width, underlineY), matchColor);
            pos.X += width;
        }

        var cursor = 0;
        var searchable = label[..searchableLength];
        for (var idx = searchable.IndexOf(searchString, StringComparison.OrdinalIgnoreCase); idx >= 0; idx = searchable.IndexOf(searchString, cursor, StringComparison.OrdinalIgnoreCase)) {
            Segment(label[cursor..idx], false);
            Segment(label.Substring(idx, searchString.Length), true);
            cursor = idx + searchString.Length;
        }
        Segment(label[cursor..], false);
    }

    internal static bool RapidImportEnabled = false;
    private void RapidImport() {
        if (ImGui.Checkbox(Localization.GetLabel(LocalizationKeys.Gathering.EnableRapidImport, "Enable Rapid Import"), ref RapidImportEnabled))
            ImGui.SetClipboardText("");

        ImGuiEx.HelpMarker(Localization.GetText(LocalizationKeys.Gathering.RapidImportHelp));
        if (RapidImportEnabled) {
            try {
                var text = ImGui.GetClipboardText();
                if (text != "") {
                    TryImport(RouteDB);
                    ImGui.SetClipboardText("");
                }
            }
            catch (Exception e) {
                Service.Log.Error(e, "Rapid import failed");
            }
        }
    }

    private void DrawSettings() {
        using var child = ImRaii.Child("GatherSettings");
        if (!child) return;

        Localization.DrawLanguageSelector();
        ImGui.Separator();

        Utils.DrawSection(Localization.GetText(LocalizationKeys.Gathering.EditingOptionsHeading), ImGuiColors.ParsedGold);
        if (ImGui.SliderFloat(Localization.GetLabel(LocalizationKeys.Gathering.DefaultWaypointRadius, "Default Waypoint Radius"), ref RouteDB.DefaultWaypointRadius, 0, 100))
            RouteDB.NotifyModified();
        if (ImGui.SliderFloat(Localization.GetLabel(LocalizationKeys.Gathering.DefaultInteractionRadius, "Default Interaction Radius"), ref RouteDB.DefaultInteractionRadius, 0, 100))
            RouteDB.NotifyModified();

        Utils.DrawSection(Localization.GetText(LocalizationKeys.Gathering.OperationOptionsHeading), ImGuiColors.ParsedGold);

        ImGui.TextUnformatted(Localization.GetText(LocalizationKeys.Gathering.AutomaticMovementHeading));
        var mountPreview = RouteDB.MountId == 0 ? Localization.GetText(LocalizationKeys.Gathering.MountRoulette)
            : Mount.GetRow(RouteDB.MountId)?.Singular.ToString() ?? Localization.GetText(LocalizationKeys.Gathering.MountRoulette);
        using (var mounts = ImRaii.Combo(Localization.GetLabel(LocalizationKeys.Gathering.Mount, "Mount"), mountPreview)) {
            if (mounts) {
                if (ImGui.Selectable(Localization.GetText(LocalizationKeys.Gathering.MountRoulette), RouteDB.MountId == 0)) {
                    RouteDB.MountId = 0;
                    RouteDB.NotifyModified();
                }
                foreach (var mount in Mount.Get().Where(m => Player.IsMountUnlocked(m.RowId) && !string.IsNullOrEmpty(m.Singular.ToString()))) {
                    if (ImGui.Selectable(mount.Singular.ToString() + $"##{mount.RowId}", RouteDB.MountId == mount.RowId)) {
                        RouteDB.MountId = mount.RowId;
                        RouteDB.NotifyModified();
                    }
                }
            }
        }
        if (ImGui.SliderFloat(Localization.GetLabel(LocalizationKeys.Gathering.MountDistance, "Mount distance (yalms)"), ref RouteDB.MountDistance, 5, 100)) {
            RouteDB.MountDistance = Math.Clamp(RouteDB.MountDistance, 5, 100);
            RouteDB.NotifyModified();
        }
        if (ImGui.Checkbox(Localization.GetLabel(LocalizationKeys.Gathering.AllowFlight, "Allow flight"), ref RouteDB.AllowFlight))
            RouteDB.NotifyModified();
        ImGuiEx.HelpMarker(Localization.GetText(LocalizationKeys.Gathering.AutomaticMovementHelp));

        if (ImGui.Checkbox(Localization.GetLabel(LocalizationKeys.Gathering.GroundShortcuts, "Try short ground shortcuts"), ref RouteDB.GroundShortcuts))
            RouteDB.NotifyModified();
        ImGuiEx.HelpMarker(Localization.GetText(LocalizationKeys.Gathering.GroundShortcutsHelp));

        if (ImGui.SliderInt(Localization.GetLabel(LocalizationKeys.Gathering.StuckTimeoutSeconds, "Stuck timeout (seconds)"), ref RouteDB.StuckTimeoutSeconds, 0, 300)) {
            RouteDB.StuckTimeoutSeconds = Math.Clamp(RouteDB.StuckTimeoutSeconds, 0, 300);
            RouteDB.NotifyModified();
        }
        ImGuiEx.HelpMarker(Localization.GetText(LocalizationKeys.Gathering.StuckDetectionHelp));

        if (ImGui.SliderInt(Localization.GetLabel(LocalizationKeys.Gathering.NavigationRetryLimit, "Navigation retry limit"), ref RouteDB.StuckMaxRetries, 0, 20)) {
            RouteDB.StuckMaxRetries = Math.Clamp(RouteDB.StuckMaxRetries, 0, 20);
            RouteDB.NotifyModified();
        }
        ImGuiEx.HelpMarker(Localization.GetText(LocalizationKeys.Gathering.NavigationRetryHelp));

        if (ImGui.Checkbox(Localization.GetLabel(LocalizationKeys.Gathering.AutoEnableIslandSanctuaryGatherMode, "Auto Enable Island Sanctuary Gather Mode"), ref RouteDB.GatherModeOnStart))
            RouteDB.NotifyModified();
        ImGuiEx.HelpMarker(Localization.GetText(LocalizationKeys.Gathering.GatherModeHelp));

        if (ImGui.Checkbox(Localization.GetLabel(LocalizationKeys.Gathering.StopOnInventoryFull, "Stop when inventory is full"), ref RouteDB.StopOnInventoryFull))
            RouteDB.NotifyModified();
        ImGuiEx.HelpMarker(Localization.GetText(LocalizationKeys.Gathering.StopOnInventoryFullHelp));

    }

    private void DrawEditor(Vector2 size) {
        if (selectedRouteIndex == -1) return;

        var routeSource = VisibleRoutes;
        if (routeSource.Count == 0) return;
        var route = selectedRouteIndex >= routeSource.Count ? routeSource.Last() : routeSource[selectedRouteIndex];

        using (ImRaii.Child("Editor", size)) {
            if (ImGuiEx.IconButton(PlayIcon)) {
                if (Exec.CurrentRoute != null)
                    Exec.Paused = !Exec.Paused;
                if (Exec.CurrentRoute == null && route.Waypoints.Count > 0)
                    Exec.Start(route, 0, true, loop, route.Waypoints[0].Pathfind);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(PlayTooltip);
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, loop ? greenColor : redColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, loop ? greenColor : redColor);
            if (ImGuiEx.IconButton(FontAwesomeIcon.SyncAlt))
                loop ^= true;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.LoopRoute));
            ImGui.SameLine();

            if (Exec.CurrentRoute != null) {
                if (ImGuiEx.IconButton(FontAwesomeIcon.Stop)) {
                    if (Service.RareAnimalCapture.Active) Service.RareAnimalCapture.Stop();
                    Exec.Finish();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.StopRoute));
                ImGui.SameLine();
            }

            var canDelete = !ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(canDelete)) {
                if (ImGuiEx.IconButton(FontAwesomeIcon.Trash)) {
                    if (Exec.SourceRoute == route)
                        Exec.Finish();
                    RouteDB.Routes.Remove(route);
                    RefreshFilteredRoutes();
                    selectedRouteIndex = Math.Min(selectedRouteIndex, VisibleRoutes.Count - 1);
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.DeleteRouteHoldCtrl));
            ImGui.SameLine();

            if (ImGuiEx.IconButton(FontAwesomeIcon.FileExport)) {
                ImGui.SetClipboardText(JsonConvert.SerializeObject(route));
            }
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.ExportRoute));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.SetClipboardText(Utils.ToCompressedBase64(route));
            }

            ImGui.SameLine();
            if (ImGuiEx.IconButton(FontAwesomeIcon.EllipsisH))
                ImGui.OpenPopup("##MassEditing");
            DrawMassEditContextMenu(route);

            var name = route.Name;
            var group = route.Group;

            ImGuiEx.TextV(Localization.GetText(LocalizationKeys.Gathering.Name));
            ImGui.SameLine();
            if (ImGui.InputText("##name", ref name, 256)) {
                route.Name = name;
                RouteDB.NotifyModified();
            }
            ImGui.SameLine();
            if (ImGuiEx.IconButton(FontAwesomeIcon.Plus)) {
                Exec.Finish();
                var player = Service.ObjectTable.LocalPlayer;
                if (player != null) {
                    route.Waypoints.Add(new() { Position = player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType });
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.AddWaypointCurrentPosition));
            ImGui.SameLine();
            if (ImGuiEx.IconButton(FontAwesomeIcon.UserPlus)) {
                var target = Service.TargetManager.Target;
                if (target != null) {
                    route.Waypoints.Add(new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, InteractWithOID = target.BaseId, InteractWithName = target.Name.ToString().ToLower() });
                    RouteDB.NotifyModified();
                    Exec.Start(route, route.Waypoints.Count - 1, false, false);
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.AddWaypointInteractWithTarget));

            ImGuiEx.TextV(Localization.GetText(LocalizationKeys.Gathering.Group));
            ImGui.SameLine();
            if (ImGui.InputText("##group", ref group, 256)) {
                route.Group = group;
                RouteDB.NotifyModified();
            }

            using (ImRaii.Child("waypoints")) {
                for (var i = 0; i < route.Waypoints.Count; ++i) {
                    var wp = route.Waypoints[i];
                    foreach (var wn in _tree.Node($"#{i + 1}: [x: {wp.Position.X:f0}, y: {wp.Position.Y:f0}, z: {wp.Position.Z:f0}] {(wp.InteractWithOID != 0 ? $" @ {wp.InteractWithName} ({wp.InteractWithOID:X})" : "")}###{i}", color: wp.IsPhantom ? ImGuiColors.HealerGreen.ToHex() : 0xffffffff, contextMenu: () => ContextMenuWaypoint(route, i)))
                        DrawWaypoint(wp);
                }
            }
        }
    }

    private float radius;
    private InteractionType interaction;
    private void DrawMassEditContextMenu(Route route) {
        using var popup = ImRaii.Popup("##MassEditing");
        if (!popup) return;

        Utils.DrawSection("Mass Editing", ImGuiColors.ParsedGold);
        ImGui.InputFloat(Localization.GetLabel(LocalizationKeys.Gathering.Radius, "Radius"), ref radius);
        ImGui.SameLine();
        if (ImGui.Button(Localization.GetLabel(LocalizationKeys.Common.ApplyAll, "Radius"))) {
            route?.Waypoints.ForEach(x => x.Radius = radius);
            RouteDB.NotifyModified();
        }

        UICombo.Enum(Localization.GetLabel(LocalizationKeys.Gathering.InteractionTypeLabel, "Interaction type"), ref interaction);
        ImGui.SameLine();
        if (ImGui.Button(Localization.GetLabel(LocalizationKeys.Common.ApplyAll, "Interaction"))) {
            route?.Waypoints.ForEach(x => x.Interaction = interaction);
            RouteDB.NotifyModified();
        }
    }

    private void DrawWaypoint(Waypoint wp) {
        if (ImGuiEx.IconButton(FontAwesomeIcon.MapMarker) && Player.Available) {
            wp.Position = Player.Position;
            wp.ZoneID = Service.ClientState.TerritoryType;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.SetPositionToCurrent));
        ImGui.SameLine();
        if (ImGui.InputFloat3("Position", ref wp.Position))
            RouteDB.NotifyModified();

        if (ImGui.InputFloat(Localization.GetLabel(LocalizationKeys.Gathering.RadiusYalms, "Radius (yalms)"), ref wp.Radius))
            RouteDB.NotifyModified();

        if (ImGuiEx.IconButton(FontAwesomeIcon.UserPlus)) {
            if (wp.InteractWithOID == default) {
                var target = Service.TargetManager.Target;
                if (target != null) {
                    wp.Position = target.Position;
                    wp.Radius = RouteDB.DefaultInteractionRadius;
                    wp.InteractWithName = target.Name.ToString().ToLower();
                    wp.InteractWithOID = target.BaseId;
                    RouteDB.NotifyModified();
                }
            }
            else
                wp.InteractWithOID = default;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.AddRemoveTargetFromWaypoint));
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.CommentDots)) {
            wp.showInteractions ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.ToggleInteractions));
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Clock)) {
            wp.showWaits ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.GetText(LocalizationKeys.Gathering.ToggleWaits));

        if (wp.showInteractions) {
            if (UICombo.Enum(Localization.GetLabel(LocalizationKeys.Gathering.InteractionType, "Interaction Type"), ref wp.Interaction))
                RouteDB.NotifyModified();
            switch (wp.Interaction) {
                case InteractionType.None: break;
                case InteractionType.Standard: break;
                case InteractionType.StartRoute:
                    if (UICombo.String(Localization.GetLabel(LocalizationKeys.Gathering.RouteName, "Route Name"), [.. RouteDB.Routes.Select(r => r.Name)], ref wp.RouteName))
                        RouteDB.NotifyModified();
                    break;

            }
        }

        if (wp.showWaits) {
            if (ImGui.InputFloat2("Eorzean Time Wait", ref wp.WaitTimeET))
                RouteDB.NotifyModified();
            if (ImGui.SliderInt(Localization.GetLabel(LocalizationKeys.Gathering.WaitMs, "Wait (ms)"), ref wp.WaitTimeMs, 0, 60000))
                RouteDB.NotifyModified();
            if (UICombo.Enum(Localization.GetLabel(LocalizationKeys.Gathering.WaitForCondition, "Wait for Condition"), ref wp.WaitForCondition))
                RouteDB.NotifyModified();
        }
    }

    private void ContextMenuGroup(string group) {
        var old = group;
        ImGuiEx.TextV(Localization.GetText(LocalizationKeys.Gathering.Name));
        ImGui.SameLine();
        if (ImGui.InputText("##groupname", ref group, 256)) {
            RouteDB.Routes.Where(r => r.Group == old).ToList().ForEach(r => r.Group = group);
            RouteDB.NotifyModified();
        }
    }

    private void ContextMenuWaypoint(Route r, int i) {
        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Gathering.ExecuteSingleStep, "Execute this step only")))
            Exec.Start(r, i, false, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Gathering.ExecuteFromHereOnce, "Execute route once starting from this step")))
            Exec.Start(r, i, true, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Gathering.ExecuteFromHereLoop, "Execute route starting from this step and then loop")))
            Exec.Start(r, i, true, true, r.Waypoints[i].Pathfind);

        var target = Service.TargetManager.Target;

        if (ImGui.MenuItem(Localization.GetLabel(r.Waypoints[i].InteractWithOID != default ? LocalizationKeys.Gathering.SwapToNormalWaypoint : LocalizationKeys.Gathering.SwapToInteractWaypoint, r.Waypoints[i].InteractWithOID != default ? "Swap to normal waypoint" : "Swap to interact waypoint"))) {
            _postDraw.Add(() => {
                r.Waypoints[i].InteractWithOID = r.Waypoints[i].InteractWithOID != default ? default : target?.BaseId ?? default;
                RouteDB.NotifyModified();
            });
        }

        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Gathering.InsertStepAbove, "Insert step above"))) {
            _postDraw.Add(() => {
                if (i > 0 && i < r.Waypoints.Count) {
                    if (Exec.SourceRoute == r)
                        Exec.Finish();
                    if (Service.ObjectTable.LocalPlayer != null) {
                        r.Waypoints.Insert(i, new() { Position = Player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) {
            _postDraw.Add(() => {
                if (i > 0 && i < r.Waypoints.Count) {
                    if (Exec.SourceRoute == r)
                        Exec.Finish();
                    if (target != null) {
                        r.Waypoints.Insert(i, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, InteractWithOID = target.BaseId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Gathering.InsertStepBelow, "Insert step below"))) {
            _postDraw.Add(() => {
                if (i > 0 && i < r.Waypoints.Count) {
                    if (Exec.SourceRoute == r)
                        Exec.Finish();
                    if (Service.ObjectTable.LocalPlayer != null) {
                        r.Waypoints.Insert(i + 1, new() { Position = Player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) {
            _postDraw.Add(() => {
                if (i > 0 && i < r.Waypoints.Count) {
                    if (Exec.SourceRoute == r)
                        Exec.Finish();
                    if (target != null) {
                        r.Waypoints.Insert(i + 1, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, InteractWithOID = target.BaseId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Common.MoveUp, "Move up"))) {
            _postDraw.Add(() => {
                if (i > 0 && i < r.Waypoints.Count) {
                    if (Exec.SourceRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i - 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Common.MoveDown, "Move down"))) {
            _postDraw.Add(() => {
                if (i + 1 < r.Waypoints.Count) {
                    if (Exec.SourceRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i + 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem(Localization.GetLabel(LocalizationKeys.Common.Delete, "Delete"))) {
            _postDraw.Add(() => {
                if (i < r.Waypoints.Count) {
                    if (Exec.SourceRoute == r)
                        Exec.Finish();
                    r.Waypoints.RemoveAt(i);
                    RouteDB.NotifyModified();
                }
            });
        }
    }
}
