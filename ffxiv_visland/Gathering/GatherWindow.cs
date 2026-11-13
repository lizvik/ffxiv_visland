using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
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
using static visland.Gathering.GatherRouteDB;

namespace visland.Gathering;

public class GatherWindow : Window {
    private readonly UITree _tree = new();
    private readonly List<System.Action> _postDraw = [];

    public GatherRouteDB RouteDB = null!;
    public GatherRouteExec Exec => Service.RouteExec;
    public GatherDebug _debug = null!;

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
    private string PlayTooltip => Exec.CurrentRoute == null ? L.Text("Start Route") : Exec.Paused ? L.Text("Resume Route") : L.Text("Pause Route");

    public GatherWindow() : base(L.Label("Gathering Automation"), ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse) {
        Size = new Vector2(800, 800);
        SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new();
    }

    public override void Draw() {
        WindowName = L.Label("Gathering Automation");
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs) {
            using (var tab = ImRaii.TabItem(L.Label("Routes")))
                if (tab) {
                    ImGui.TextWrapped(L.Text("Gathering routes are available only inside Island Sanctuary."));
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
            using (var tab = ImRaii.TabItem(L.Label("Settings")))
                if (tab)
                    DrawSettings();
            using (var tab = ImRaii.TabItem(L.Label("Debug")))
                if (tab)
                    _debug.Draw();
        }
    }

    private void DrawExecution() {
        ImGui.Text(L.Text("Status: "));
        ImGui.SameLine();

        if (Exec.CurrentRoute != null)
            Utils.FlashText($"{(Exec.Paused ? L.Text("PAUSED") : Exec.Waiting ? L.Text("WAITING") : L.Text("RUNNING"))}", new Vector4(1.0f, 1.0f, 1.0f, 1.0f), Exec.Paused ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), 2);
        ImGui.SameLine();

        if (Exec.CurrentRoute == null || Exec.CurrentWaypoint >= Exec.CurrentRoute.Waypoints.Count) {
            ImGui.Text(L.Text("Route not running"));
            return;
        }

        if (Exec.CurrentRoute != null) // Finish() call could've reset it
        {
            ImGui.SameLine();
            ImGui.Text(L.Format("{0}: Step #{1} {2}", Exec.CurrentRoute.Name, Exec.CurrentWaypoint + 1, Exec.CurrentRoute.Waypoints[Exec.CurrentWaypoint].Position));

            if (Exec.Waiting) {
                ImGui.SameLine();
                ImGui.Text(L.Format("waiting {0}ms", Exec.WaitUntil - Environment.TickCount64));
            }
        }

        ImGui.SameLine();
        ImGui.Text(L.Format("State: {0}", Exec.CurrentState));
    }

    private unsafe void DrawSidebar(Vector2 size) {
        using (ImRaii.Child("Sidebar", size, false)) {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus)) {
                RouteDB.Routes.Add(new() { Name = "Unnamed Route" });
                RouteDB.NotifyModified();
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Create a New Route"));
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileImport))
                TryImport(RouteDB);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(L.Text("Import Route from Clipboard"));

            ImGui.SameLine();
            RapidImport();

            ImGui.TextV(L.Text("Search: "));
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
        if (ImGui.Checkbox(L.Label("Enable Rapid Import"), ref RapidImportEnabled))
            ImGui.SetClipboardText("");

        ImGuiComponents.HelpMarker(L.Text("Import multiple presets with ease by simply copying them. Visland will read your clipboard and attempt to import whatever you copy. Your clipboard will be cleared upon enabling."));
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

        L.DrawSelector();
        ImGui.Separator();

        Utils.DrawSection(L.Text("Global Route Editing Options"), ImGuiColors.ParsedGold);
        if (ImGui.SliderFloat(L.Label("Default Waypoint Radius"), ref RouteDB.DefaultWaypointRadius, 0, 100))
            RouteDB.NotifyModified();
        if (ImGui.SliderFloat(L.Label("Default Interaction Radius"), ref RouteDB.DefaultInteractionRadius, 0, 100))
            RouteDB.NotifyModified();

        Utils.DrawSection(L.Text("Global Route Operation Options"), ImGuiColors.ParsedGold);
        ImGui.TextUnformatted(L.Text("Automatic navigation uses vnavmesh."));
        if (ImGui.Checkbox(L.Label("Automatically optimize gathering routes"), ref RouteDB.OptimizeRoutes))
            RouteDB.NotifyModified();
        ImGuiComponents.HelpMarker(L.Text("Optimizes independent gathering stops when starting a full route. Waits and route jumps keep their order. Saved routes are unchanged. Distance estimates use straight lines; vnavmesh handles obstacles during navigation."));

        ImGui.TextUnformatted(L.Text("Automatic movement"));
        var mountPreview = RouteDB.MountId == 0 ? L.Text("Mount roulette") : Mount.GetRow(RouteDB.MountId)?.Singular.ToString() ?? L.Text("Mount roulette");
        using (var mounts = ImRaii.Combo(L.Label("Mount"), mountPreview)) {
            if (mounts) {
                if (ImGui.Selectable(L.Text("Mount roulette"), RouteDB.MountId == 0)) {
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
        if (ImGui.Checkbox(L.Label("Allow flight"), ref RouteDB.AllowFlight))
            RouteDB.NotifyModified();
        if (ImGui.SliderFloat(L.Label("Mount distance (yalms)"), ref RouteDB.MountDistance, 5, 100)) {
            RouteDB.MountDistance = Math.Clamp(RouteDB.MountDistance, 5, 100);
            RouteDB.FlightDistance = Math.Max(RouteDB.FlightDistance, RouteDB.MountDistance);
            RouteDB.NotifyModified();
        }
        using (ImRaii.Disabled(!RouteDB.AllowFlight)) {
            if (ImGui.SliderFloat(L.Label("Flight distance (yalms)"), ref RouteDB.FlightDistance, RouteDB.MountDistance, 200)) {
                RouteDB.FlightDistance = Math.Clamp(RouteDB.FlightDistance, RouteDB.MountDistance, 200);
                RouteDB.NotifyModified();
            }
        }
        ImGuiComponents.HelpMarker(L.Text("Choose walking, riding or flying from the distance to each new target. Default thresholds: 20 and 40 yalms. Movement mode is fixed for that leg. Mounting or takeoff failures fall back to walking or ground riding. Legacy waypoint movement settings are ignored."));

        if (ImGui.SliderInt(L.Label("Stuck timeout (seconds)"), ref RouteDB.StuckTimeoutSeconds, 0, 300)) {
            RouteDB.StuckTimeoutSeconds = Math.Clamp(RouteDB.StuckTimeoutSeconds, 0, 300);
            RouteDB.NotifyModified();
        }
        ImGuiComponents.HelpMarker(L.Text("After this many seconds without movement, try jumping while navigation continues. If horizontal movement stays below 0.5 yalms for 3 seconds, recalculate the path and use detours after the retry limit. Default: 10 seconds; 0 disables detection. Pauses, gathering and mounting do not count."));

        if (ImGui.SliderInt(L.Label("Navigation retry limit"), ref RouteDB.StuckMaxRetries, 0, 20)) {
            RouteDB.StuckMaxRetries = Math.Clamp(RouteDB.StuckMaxRetries, 0, 20);
            RouteDB.NotifyModified();
        }
        ImGuiComponents.HelpMarker(L.Text("Maximum recalculations per waypoint after getting stuck or navigation failing to start. Default: 3. After retries fail, try a different movement path via nearby intermediate points to the same waypoint. The gathering route and target stay unchanged. Stop if no usable detour remains. 0 tries detours immediately."));

        if (ImGui.Checkbox(L.Label("Auto Enable Island Sanctuary Gather Mode"), ref RouteDB.GatherModeOnStart))
            RouteDB.NotifyModified();
        ImGuiComponents.HelpMarker(L.Text("Enables \"Gather Mode\" when on your Island Sanctuary automatically when commencing a route."));

        if (ImGui.Checkbox(L.Label("Stop when inventory is full"), ref RouteDB.StopOnInventoryFull))
            RouteDB.NotifyModified();
        ImGuiComponents.HelpMarker(L.Text("Stop only when every material currently unlocked for this gathering node is at its carrying limit. Materials locked by island progression or uncrafted tools are excluded. Continue if any available material has room. Unknown nodes do not trigger a stop."));

    }

    private void DrawEditor(Vector2 size) {
        if (selectedRouteIndex == -1) return;

        var routeSource = VisibleRoutes;
        if (routeSource.Count == 0) return;
        var route = selectedRouteIndex >= routeSource.Count ? routeSource.Last() : routeSource[selectedRouteIndex];

        using (ImRaii.Child("Editor", size)) {
            if (ImGuiComponents.IconButton(PlayIcon)) {
                if (Exec.CurrentRoute != null)
                    Exec.Paused = !Exec.Paused;
                if (Exec.CurrentRoute == null && route.Waypoints.Count > 0)
                    Exec.Start(route, 0, true, loop, route.Waypoints[0].Pathfind);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(PlayTooltip);
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, loop ? greenColor : redColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, loop ? greenColor : redColor);
            if (ImGuiComponents.IconButton(FontAwesomeIcon.SyncAlt))
                loop ^= true;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Loop Route"));
            ImGui.SameLine();

            if (Exec.CurrentRoute != null) {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Stop))
                    Exec.Finish();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Stop Route"));
                ImGui.SameLine();
            }

            var canDelete = !ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(canDelete)) {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash)) {
                    if (Exec.SourceRoute == route)
                        Exec.Finish();
                    RouteDB.Routes.Remove(route);
                    RefreshFilteredRoutes();
                    selectedRouteIndex = Math.Min(selectedRouteIndex, VisibleRoutes.Count - 1);
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(L.Text("Delete Route (Hold CTRL)"));
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileExport)) {
                ImGui.SetClipboardText(JsonConvert.SerializeObject(route));
            }
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip(L.Text("Export Route (\uE052 Base64)"));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.SetClipboardText(Utils.ToCompressedBase64(route));
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.EllipsisH))
                ImGui.OpenPopup("##MassEditing");
            DrawMassEditContextMenu(route);

            var name = route.Name;
            var group = route.Group;

            ImGui.TextV(L.Text("Name: "));
            ImGui.SameLine();
            if (ImGui.InputText("##name", ref name, 256)) {
                route.Name = name;
                RouteDB.NotifyModified();
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus)) {
                Exec.Finish();
                var player = Service.ObjectTable.LocalPlayer;
                if (player != null) {
                    route.Waypoints.Add(new() { Position = player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType });
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Add Waypoint: Current Position"));
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus)) {
                var target = Service.TargetManager.Target;
                if (target != null) {
                    route.Waypoints.Add(new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, InteractWithOID = target.BaseId, InteractWithName = target.Name.ToString().ToLower() });
                    RouteDB.NotifyModified();
                    Exec.Start(route, route.Waypoints.Count - 1, false, false);
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Add Waypoint: Interact with Target"));

            ImGui.TextV(L.Text("Group: "));
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
        ImGui.InputFloat(L.Label("Radius"), ref radius);
        ImGui.SameLine();
        if (ImGui.Button(L.Label("Apply All###Radius"))) {
            route?.Waypoints.ForEach(x => x.Radius = radius);
            RouteDB.NotifyModified();
        }

        UICombo.Enum(L.Label("Interaction type"), ref interaction);
        ImGui.SameLine();
        if (ImGui.Button(L.Label("Apply All###Interaction"))) {
            route?.Waypoints.ForEach(x => x.Interaction = interaction);
            RouteDB.NotifyModified();
        }
    }

    private void DrawWaypoint(Waypoint wp) {
        if (ImGui.IconButton(FontAwesomeIcon.MapMarker) && Player.Available) {
            wp.Position = Player.Position;
            wp.ZoneID = Service.ClientState.TerritoryType;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Set Position to Current"));
        ImGui.SameLine();
        if (ImGui.InputFloat3("Position", ref wp.Position))
            RouteDB.NotifyModified();

        if (ImGui.InputFloat(L.Label("Radius (yalms)"), ref wp.Radius))
            RouteDB.NotifyModified();

        if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus)) {
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
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Add/Remove target from waypoint"));
        ImGui.SameLine();
        if (ImGui.IconButton(FontAwesomeIcon.CommentDots)) {
            wp.showInteractions ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Toggle Interactions"));
        ImGui.SameLine();
        if (ImGui.IconButton(FontAwesomeIcon.Clock)) {
            wp.showWaits ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(L.Text("Toggle Waits"));

        if (wp.showInteractions) {
            if (UICombo.Enum(L.Label("Interaction Type"), ref wp.Interaction))
                RouteDB.NotifyModified();
            switch (wp.Interaction) {
                case InteractionType.None: break;
                case InteractionType.Standard: break;
                case InteractionType.StartRoute:
                    if (UICombo.String(L.Label("Route Name"), [.. RouteDB.Routes.Select(r => r.Name)], ref wp.RouteName))
                        RouteDB.NotifyModified();
                    break;

            }
        }

        if (wp.showWaits) {
            if (ImGui.InputFloat2("Eorzean Time Wait", ref wp.WaitTimeET))
                RouteDB.NotifyModified();
            if (ImGui.SliderInt(L.Label("Wait (ms)"), ref wp.WaitTimeMs, 0, 60000))
                RouteDB.NotifyModified();
            if (UICombo.Enum(L.Label("Wait for Condition"), ref wp.WaitForCondition))
                RouteDB.NotifyModified();
        }
    }

    private void ContextMenuGroup(string group) {
        var old = group;
        ImGui.TextV(L.Text("Name: "));
        ImGui.SameLine();
        if (ImGui.InputText("##groupname", ref group, 256)) {
            RouteDB.Routes.Where(r => r.Group == old).ToList().ForEach(r => r.Group = group);
            RouteDB.NotifyModified();
        }
    }

    private void ContextMenuWaypoint(Route r, int i) {
        if (ImGui.MenuItem(L.Label("Execute this step only")))
            Exec.Start(r, i, false, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem(L.Label("Execute route once starting from this step")))
            Exec.Start(r, i, true, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem(L.Label("Execute route starting from this step and then loop")))
            Exec.Start(r, i, true, true, r.Waypoints[i].Pathfind);

        var target = Service.TargetManager.Target;

        if (ImGui.MenuItem(L.Label(r.Waypoints[i].InteractWithOID != default ? "Swap to normal waypoint" : "Swap to interact waypoint"))) {
            _postDraw.Add(() => {
                r.Waypoints[i].InteractWithOID = r.Waypoints[i].InteractWithOID != default ? default : target?.BaseId ?? default;
                RouteDB.NotifyModified();
            });
        }

        if (ImGui.MenuItem(L.Label("Insert step above"))) {
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

        if (ImGui.MenuItem(L.Label("Insert step below"))) {
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

        if (ImGui.MenuItem(L.Label("Move up"))) {
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

        if (ImGui.MenuItem(L.Label("Move down"))) {
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

        if (ImGui.MenuItem(L.Label("Delete"))) {
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
