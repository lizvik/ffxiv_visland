using Dalamud.Game.ClientState.Conditions;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;

namespace visland.Gathering;

public class GatherRouteDB : Configuration.Node {
    public enum Movement {
        Normal = 0,
        MountFly = 1,
        MountNoFly = 2,
    }

    public enum InteractionType {
        None = 0,
        Standard = 1,
        StartRoute = 9,
    }

    public class Waypoint {
        public Vector3 Position;
        public uint ZoneID;
        public float Radius;
        public Movement Movement;
        public bool Pathfind = true;
        public uint InteractWithOID;
        public string InteractWithName = "";
        public Vector3 InteractWithPosition;

        public bool showInteractions;
        public InteractionType Interaction = InteractionType.Standard;
        public string RouteName = "";

        public bool showWaits;
        public ConditionFlag WaitForCondition;
        public int WaitTimeMs;
        public Vector2 WaitTimeET;

        public bool NeedsMount => Movement is Movement.MountFly or Movement.MountNoFly;
        public bool IsPhantom;
    }

    public class Route {
        public string Name = "";
        public string Group = "";
        public List<Waypoint> Waypoints = [];
    }

    public List<Route> Routes = [];
    public float DefaultWaypointRadius = 3;
    public float DefaultInteractionRadius = 2;
    public bool GatherModeOnStart = true;
    public bool StopOnInventoryFull;

    public bool GroundShortcuts = true;
    public uint MountId;
    public float MountDistance = 20;
    public bool AllowFlight = true;
    public int StuckTimeoutSeconds = 10;
    public int StuckMaxRetries = 3;

    public override void Deserialize(JObject j, JsonSerializer ser) {
        Routes.Clear();
        if (j["Routes"] is JArray ja) {
            foreach (var jr in ja) {
                var jn = jr["Name"]?.Value<string>();
                if (jn == null || jr["Waypoints"] is not JArray jw)
                    continue;

                Routes.Add(new Route {
                    Name = jn,
                    Group = jr["Group"]?.Value<string>() ?? "",
                    Waypoints = LoadFromJSONWaypoints(jw),
                });
            }
        }
        StuckMaxRetries = Math.Clamp((int?)j["StuckMaxRetries"] ?? 3, 0, 20);
        StuckTimeoutSeconds = Math.Clamp((int?)j["StuckTimeoutSeconds"] ?? 10, 0, 300);
        MountId = (uint?)j["MountId"] ?? 0;
        MountDistance = Math.Clamp((float?)j["MountDistance"] ?? 20, 5, 100);
        AllowFlight = (bool?)j["AllowFlight"] ?? true;
        GroundShortcuts = (bool?)j["GroundShortcuts"] ?? true;
        StopOnInventoryFull = (bool?)j["StopOnInventoryFull"] ?? (bool?)j["DisableOnErrors"] ?? false;
        GatherModeOnStart = (bool?)j["GatherModeOnStart"] ?? true;
        DefaultWaypointRadius = (float?)j["DefaultWaypointRadius"] ?? 3;
        DefaultInteractionRadius = (float?)j["DefaultInteractionRadius"] ?? 2;
        // FlightDistance is obsolete: clib chooses ground travel or flight for mounted legs.
        // OptimizeRoutes is obsolete: waypoint order is always preserved.
        // Intentionally ignore obsolete extras (Food, GlobalFood, ExtractMateria, RepairGear,
        // RepairPercent, Desynth, AutoRetainerIntegration) and legacy keys: Manual, GlobalManual, WasFlyingInManual,
        // LandDistance, PathFindCancellationTime, EmoteID, ActionID, ItemID, MobID, QuestID, ChatCommand, etc.
    }

    public override JObject Serialize(JsonSerializer ser) {
        JArray res = [];
        foreach (var r in Routes) {
            res.Add(new JObject {
                { "Name", r.Name },
                { "Group", r.Group },
                { "Waypoints", SaveToJSONWaypoints(r.Waypoints) },
            });
        }
        return new JObject {
            { "Routes", res },
            { "GroundShortcuts", GroundShortcuts },
            { "MountId", MountId },
            { "MountDistance", MountDistance },
            { "AllowFlight", AllowFlight },
            { "StuckTimeoutSeconds", StuckTimeoutSeconds },
            { "StuckMaxRetries", StuckMaxRetries },
            { "StopOnInventoryFull", StopOnInventoryFull },
            { "GatherModeOnStart", GatherModeOnStart },
            { "DefaultWaypointRadius", DefaultWaypointRadius },
            { "DefaultInteractionRadius", DefaultInteractionRadius },
        };
    }

    public static JArray SaveToJSONWaypoints(List<Waypoint> waypoints) {
        JArray jw = [];
        foreach (var wp in waypoints) {
            if (wp.IsPhantom) continue;
            jw.Add(new JObject {
                { "X", wp.Position.X },
                { "Y", wp.Position.Y },
                { "Z", wp.Position.Z },
                { "ZoneID", wp.ZoneID },
                { "Radius", wp.Radius },
                { "InteractWithName", wp.InteractWithName },
                { "Movement", wp.Movement.ToString() },
                { "InteractWithOID", wp.InteractWithOID },
                { "iX", wp.InteractWithPosition.X },
                { "iY", wp.InteractWithPosition.Y },
                { "iZ", wp.InteractWithPosition.Z },
                { "showInteractions", wp.showInteractions },
                { "Interaction", wp.Interaction.ToString() },
                { "showWaits", wp.showWaits },
                { "WaitTimeMs", wp.WaitTimeMs },
                { "WaitTimeET", new JObject {
                    { "X", wp.WaitTimeET.X },
                    { "Y", wp.WaitTimeET.Y },
                }},
                { "WaitForCondition", wp.WaitForCondition.ToString() },
                { "Pathfind", wp.Pathfind },
                { "RouteName", wp.RouteName },
            });
        }
        return jw;
    }

    public static List<Waypoint> LoadFromJSONWaypoints(JArray j) {
        List<Waypoint> res = [];
        try {
            foreach (var jwe in j) {
                if (jwe is not JObject o)
                    continue;

                // Parse known InteractionType values; map obsolete experimental values to None/Standard safely.
                var interactionRaw = o["Interaction"]?.Value<string>();
                var interaction = InteractionType.Standard;
                if (!string.IsNullOrEmpty(interactionRaw) && (!Enum.TryParse(interactionRaw, out interaction) || !Enum.IsDefined(interaction)))
                    interaction = InteractionType.None;

                res.Add(new Waypoint {
                    Position = new Vector3(
                        o["X"]?.Value<float>() ?? 0,
                        o["Y"]?.Value<float>() ?? 0,
                        o["Z"]?.Value<float>() ?? 0
                    ),
                    ZoneID = o["ZoneID"]?.Value<uint>() ?? 0,
                    Radius = o["Radius"]?.Value<float>() ?? 0,
                    InteractWithName = o["InteractWithName"]?.Value<string>() ?? "",
                    Movement = Enum.TryParse<Movement>(o["Movement"]?.Value<string>(), out var movement) ? movement : Movement.Normal,
                    InteractWithOID = o["InteractWithOID"]?.Value<uint>() ?? 0,
                    InteractWithPosition = new Vector3(
                        o["iX"]?.Value<float>() ?? 0,
                        o["iY"]?.Value<float>() ?? 0,
                        o["iZ"]?.Value<float>() ?? 0
                    ),
                    showInteractions = o["showInteractions"]?.Value<bool>() ?? false,
                    Interaction = interaction,
                    showWaits = o["showWaits"]?.Value<bool>() ?? false,
                    WaitTimeMs = o["WaitTimeMs"]?.Value<int>() ?? 0,
                    WaitTimeET = new Vector2(
                        o["WaitTimeET"]?["X"]?.Value<float>() ?? 0,
                        o["WaitTimeET"]?["Y"]?.Value<float>() ?? 0
                    ),
                    WaitForCondition = Enum.TryParse<ConditionFlag>(o["WaitForCondition"]?.Value<string>(), out var condition) ? condition : ConditionFlag.None,
                    Pathfind = o["Pathfind"]?.Value<bool>() ?? false,
                    RouteName = o["RouteName"]?.Value<string>() ?? "",
                });
            }
        }
        catch (Exception) {
            Service.Log.Error("Failed to load waypoints from JSON.");
        }
        return res;
    }

    public static List<string> GetGroups(GatherRouteDB gatherRouteDB, bool sort = false) {
        List<string> groups = ["Ungrouped"];
        for (var g = 0; g < gatherRouteDB.Routes.Count; g++) {
            if (string.IsNullOrEmpty(gatherRouteDB.Routes[g].Group))
                gatherRouteDB.Routes[g].Group = "Ungrouped";
            if (!groups.Contains(gatherRouteDB.Routes[g].Group))
                groups.Add(gatherRouteDB.Routes[g].Group);
        }
        if (sort)
            groups = [.. groups.OrderBy(i => i == "Ungrouped").ThenBy(i => i)];
        return groups;
    }

    public static void TryImport(GatherRouteDB routeDB) {
        try {
            var data = ImGui.GetClipboardText();
            (var isBase64, var json) = Utils.FromCompressedBase64(data);
            Route? import = null;
            if (isBase64)
                import = JsonConvert.DeserializeObject<Route>(json);
            else if (Utils.IsJson(data))
                import = JsonConvert.DeserializeObject<Route>(data);
            if (import == null)
                return;

            // Unknown / obsolete waypoint keys are ignored by Newtonsoft (MissingMemberHandling.Ignore).
            if (import.Waypoints.Any(x => x.Pathfind && !Service.Navmesh.IsEnabled))
                Service.ChatGui.Print($"[{Service.Interface.InternalName}] Imported route uses pathfinding, but vnavmesh is not installed. It's located on the same repo as {Service.Interface.InternalName} ({Plugin.Repo}).");

            routeDB.Routes.Add(import);
            routeDB.NotifyModified();
        }
        catch (JsonReaderException ex) {
            Service.ChatGui.PrintError($"Failed to import route: {ex.Message}");
            Service.Log.Error(ex, "Failed to import route");
        }
    }
}

public static class WaypointExtensions {
    public static bool TryGetNextWaypoint(this GatherRouteDB.Waypoint waypoint, GatherRouteDB.Route route, bool loop, out GatherRouteDB.Waypoint? nextWaypoint) {
        var index = route.Waypoints.IndexOf(waypoint);
        if (index >= 0 && index < route.Waypoints.Count - 1) {
            nextWaypoint = route.Waypoints[index + 1];
            return true;
        }
        if (loop) {
            nextWaypoint = route.Waypoints.First();
            return true;
        }
        nextWaypoint = null;
        return false;
    }

    public static void AddWaypointsAfter(this GatherRouteDB.Waypoint waypoint, GatherRouteDB.Route route, List<GatherRouteDB.Waypoint> waypoints) {
        var index = route.Waypoints.IndexOf(waypoint);
        route.Waypoints.InsertRange(index + 1, waypoints);
    }

    public static bool IsLast(this GatherRouteDB.Waypoint waypoint, GatherRouteDB.Route route) => waypoint.Equals(route.Waypoints.Last());
}
