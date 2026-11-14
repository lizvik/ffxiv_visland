using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using System.Linq;
using System.Numerics;
using visland.Export;
using visland.Farm;
using visland.Gathering;
using visland.Granary;
using visland.Helpers;
using visland.Pasture;
using visland.Workshop;

namespace visland;

public sealed class Plugin : IDalamudPlugin {
    public static string Name => "visland";
    public static string Repo => "https://puni.sh/api/repository/veyn";
    internal static string HelpMessage => "Opens the Gathering Menu\n" +
        $"/{Name} moveto <X> <Y> <Z> → move to raw coordinates\n" +
        $"/{Name} movedir <X> <Y> <Z> → move this many units over (relative to player facing)\n" +
        $"/{Name} stop → stop current route\n" +
        $"/{Name} pause → pause current route\n" +
        $"/{Name} resume → resume current route\n" +
        $"/{Name} exec <name> → run route by name continuously\n" +
        $"/{Name} execonce <name> → run route by name once\n" +
        $"/{Name} exectemp <base64 route> → run unsaved route continuously\n" +
        $"/{Name} exectemponce <base64 route> → run unsaved route once";

    internal static Plugin P = null!;

    private readonly WindowSystem _windowSystem = new("visland");

    public unsafe Plugin(IDalamudPluginInterface dalamud) {
        var dir = dalamud.ConfigDirectory;
        if (!dir.Exists)
            dir.Create();

        Service.Init(dalamud, this);

        P = this;
        _windowSystem.Add(new GatherWindow(), new WorkshopWindow(), new GranaryWindow(), new PastureWindow(), new FarmWindow(), new ExportWindow());

        Service.Interface.UiBuilder.Draw += OnDraw;
        Service.CommandManager.AddHandler("/visland", new CommandInfo(OnCommand) { HelpMessage = HelpMessage });
        Service.Interface.UiBuilder.OpenMainUi += OpenMainUi;
        Service.Interface.UiBuilder.OpenConfigUi += OpenMainUi;
    }

    public void Dispose() {
        Service.CommandManager.RemoveHandler("/visland");
        Service.Interface.UiBuilder.OpenMainUi -= OpenMainUi;
        Service.Interface.UiBuilder.OpenConfigUi -= OpenMainUi;
        Service.Interface.UiBuilder.Draw -= OnDraw;
        _windowSystem.Dispose();
        Service.Dispose();
    }

    private void OnDraw() => _windowSystem.Draw();

    private void OpenMainUi() => _windowSystem.Get<GatherWindow>()!.IsOpen = true;

    private void OnCommand(string command, string arguments) {
        try {
            arguments = arguments.Trim();
            var args = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) {
                _windowSystem.Get<GatherWindow>()!.IsOpen ^= true;
                return;
            }
            switch (args[0]) {
                case "moveto":
                case "movedir":
                    MoveToCommand(args, args[0] == "movedir");
                    break;
                case "stop":
                    Service.RouteExec.Finish();
                    break;
                case "pause":
                    Service.RouteExec.Paused = true;
                    break;
                case "resume":
                    Service.RouteExec.Paused = false;
                    break;
                case "exec":
                case "execonce":
                    ExecuteCommand(arguments[args[0].Length..].Trim(), args[0] == "execonce");
                    break;
                case "exectemp":
                case "exectemponce":
                    if (args.Length != 2)
                        throw new ArgumentException(Localization.FormatText(LocalizationKeys.Gathering.CommandUsage, $"/visland {args[0]} <base64 route>"));
                    ExecuteTempRoute(args[1], args[0] == "exectemponce");
                    break;
                default:
                    throw new ArgumentException(Localization.FormatText(LocalizationKeys.Gathering.CommandUsage, HelpMessage));
            }
        }
        catch (Exception ex) {
            Service.Log.Warning(ex, "Unable to execute visland command");
            Service.ChatGui.PrintError(Localization.FormatText(LocalizationKeys.Gathering.CommandFailed, ex.Message));
        }
    }

    internal void ExecuteTempRoute(string base64, bool once) {
        GatherRouteDB.Route? route;
        try {
            var (_, json) = Utils.FromCompressedBase64(base64);
            route = Newtonsoft.Json.JsonConvert.DeserializeObject<GatherRouteDB.Route>(json);
        }
        catch (Exception ex) {
            throw new ArgumentException(Localization.GetText(LocalizationKeys.Gathering.InvalidRouteData), ex);
        }
        ValidateCommandRoute(route);
        Service.RouteExec.Start(route!, 0, true, !once);
    }

    private static void ValidateCommandRoute(GatherRouteDB.Route? route) {
        if (route?.Waypoints is not { Count: > 0 })
            throw new ArgumentException(Localization.GetText(LocalizationKeys.Gathering.EmptyRoute));
        if (route.Waypoints.Any(waypoint => waypoint == null || !CommandArguments.IsFinite(waypoint.Position)
            || !float.IsFinite(waypoint.Radius) || waypoint.Radius < 0))
            throw new ArgumentException(Localization.GetText(LocalizationKeys.Gathering.InvalidRouteData));
    }

    internal void MoveToCommand(string[] args, bool relativeToPlayer) {
        if (!CommandArguments.TryParsePosition(args, out var offset))
            throw new ArgumentException(Localization.GetText(LocalizationKeys.Gathering.InvalidCoordinates));
        var originActor = relativeToPlayer ? Service.ObjectTable.LocalPlayer : null;
        var origin = originActor?.Position ?? new();
        var route = new GatherRouteDB.Route { Name = "Temporary", Waypoints = [] };
        route.Waypoints.Add(new() { Position = origin + offset, Radius = 0.5f, InteractWithName = "", InteractWithOID = 0 });
        Service.RouteExec.Start(route, 0, false, false);
    }

    internal void ExecuteCommand(string name, bool once) {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(Localization.GetText(LocalizationKeys.Gathering.RouteNameRequired));
        var route = Service.RouteExec.RouteDB.Routes.Find(r => r.Name == name);
        if (route == null)
            throw new ArgumentException(Localization.FormatText(LocalizationKeys.Gathering.RouteNotFound, name));
        ValidateCommandRoute(route);
        Service.RouteExec.Start(route, 0, true, !once);
    }
}
