using Dalamud.IoC;
using clib;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using visland.Gathering;
using visland.Helpers;
using visland.IPC;

namespace visland;

public class Service {
    [PluginService] public static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Hook { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;

    public static Configuration Config { get; private set; } = null!;
    public static TaskManager TaskManager { get; private set; } = null!;
    public static NavmeshIPC Navmesh { get; private set; } = null!;
    public static VislandIPC Visland { get; private set; } = null!;
    public static GatherRouteExec RouteExec { get; private set; } = null!;

    private static bool _ecommonsInitialized;
    private static bool _clibInitialized;

    public static void Init(IDalamudPluginInterface pi, IDalamudPlugin plugin) {
        pi.Create<Service>();
        ECommonsMain.ReducedLogging = true;
        var minimumLogLevel = Log.MinimumLogLevel;
        try {
            ECommonsMain.Init(pi, plugin);
            _ecommonsInitialized = true;
        }
        finally {
            Log.MinimumLogLevel = minimumLogLevel;
        }
        try {
            // Movement tasks run individually; the global clib automation scheduler is unnecessary.
            CLibMain.Init(pi, plugin);
            _clibInitialized = true;
            Config = new();
            Config.Initialize(pi.ConfigFile);
            TaskManager = new TaskManager(new TaskManagerConfiguration {
                AbortOnTimeout = true,
                AbortOnError = true,
                TimeLimitMS = 20000,
                ShowDebug = false,
                OnTaskException = ReportTaskException,
                OnTaskTimeout = ReportTaskTimeout,
            });
            Navmesh = new NavmeshIPC();
            RouteExec = new GatherRouteExec();
            Visland = new VislandIPC();
        }
        catch (Exception ex) {
            Log.Error(ex, $"Error initialising {nameof(Service)}");
            Dispose();
            throw;
        }
    }

    private static void ReportTaskException(TaskManagerTask task, Exception exception, ref bool continueTask, ref bool? abort)
        => ChatGui.PrintError(exception.Message);

    private static void ReportTaskTimeout(TaskManagerTask task, ref long remainingTime)
        => ChatGui.PrintError(Localization.GetText(LocalizationKeys.Common.OperationTimedOut));

    public static void Dispose() {
        try {
            RouteExec?.Dispose();
            Navmesh?.Dispose();
            TaskManager?.Abort();
            TaskManager?.Dispose();
            Config?.Dispose();
        }
        finally {
            try {
                if (_clibInitialized) {
                    _clibInitialized = false;
                    CLibMain.Dispose();
                }
            }
            finally {
                if (_ecommonsInitialized) {
                    _ecommonsInitialized = false;
                    ECommonsMain.Dispose();
                }
            }
        }
    }
}
