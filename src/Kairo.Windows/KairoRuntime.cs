using Kairo.Core.Abstractions;
using Kairo.Core.Agent;
using Kairo.Core.AI;
using Kairo.Core.AI.Jev;
using Kairo.Core.AI.OpenRouter;
using Kairo.Core.AI.Planning;
using Kairo.Core.AI.Vision;
using Kairo.Core.Files;
using Kairo.Core.History;
using Kairo.Core.Perception;
using Kairo.Core.Security;
using Kairo.Core.Settings;
using Kairo.Core.Telemetry;
using Kairo.Windows.Apps;
using Kairo.Windows.Automation;
using Kairo.Windows.Browser;
using Kairo.Windows.Capture;
using Kairo.Windows.Input;
using Kairo.Windows.Security;
using Kairo.Windows.Windows;

namespace Kairo.Windows;

/// <summary>Options for building the runtime (tests replace models or paths).</summary>
public sealed class KairoRuntimeOptions
{
    public KairoPaths? Paths { get; init; }
    public IChatModel? ChatModelOverride { get; init; }
    public IDecisionModel? DecisionModelOverride { get; init; }
    public OpenRouterOptions OpenRouter { get; init; } = new();
    /// <summary>Full path of Kairo.BrowserHost.exe for pipe client verification (null = no check).</summary>
    public string? BrowserHostPath { get; init; }
    public bool StartBrowserBridge { get; init; } = true;
    public string? BrowserPipeName { get; init; }
}

/// <summary>Composition root: wires Core and the Windows implementations into a working agent.</summary>
public sealed class KairoRuntime : IAsyncDisposable
{
    public const string ApiKeySecretName = "openrouter-api-key";

    public KairoRuntime(IUserInteraction interaction, KairoRuntimeOptions? options = null)
    {
        options ??= new KairoRuntimeOptions();
        Paths = options.Paths ?? KairoPaths.ForCurrentUser();
        Log = new KairoLogger(Paths.LogDirectory);
        Settings = new SettingsStore(Paths, Log);
        Secrets = new DpapiSecretStore(Paths.SecretsDirectory, Log);
        Usage = new UsageTracker(Log);
        ControlSwitch = new ComputerControlSwitch();

        Http = new OpenRouterHttp(options.OpenRouter, () => Secrets.GetSecret(ApiKeySecretName), Log);
        OpenRouter = new OpenRouterClient(Http, Usage, Log);
        Jev = new JevClient(Http, Usage, Log);
        ConnectionTester = new ConnectionTester(OpenRouter, Jev);
        IChatModel chat = options.ChatModelOverride ?? OpenRouter;
        IDecisionModel decisions = options.DecisionModelOverride ?? Jev;

        Windows = new WindowService(Log);
        Input = new InputSimulator();
        Uia = new UiaCore(Log);
        Bridge = new BrowserBridgeServer(Log, options.BrowserHostPath, options.BrowserPipeName);
        UiaProvider = new UiaPerceptionProvider(Uia, Log);
        DomProvider = new BrowserPerceptionProvider(Bridge, Log) { Enabled = Settings.Current.Control.UseBrowserExtension };
        ChangeMonitor = new UiaChangeMonitor(Uia, Log);
        Perception = new PerceptionService([DomProvider, UiaProvider], ChangeMonitor, Usage, Log);
        Bridge.EventReceived += OnBrowserEvent;

        Capture = new ScreenCaptureService(Log);
        Clipboard = new ClipboardService();
        Launcher = new AppLauncher(Windows, Log);
        Executor = new WindowsActionExecutor(
            new UiaElementActions(Uia, Windows, Input, Log),
            new BrowserElementActions(Bridge, Windows, Input),
            Windows, Input, Launcher, Clipboard, Log);

        Files = new FileOperations(new FileContentReader(), new ShellRecycleBin());
        History = new TaskHistoryStore(Paths.HistoryDirectory, Secrets, Log);
        Permissions = new PermissionManager(decisions, () => Settings.Current, Log);
        Runner = new AgentRunner(new AgentServices
        {
            Planner = new Planner(chat, Log),
            Resolver = new TargetResolver(decisions, Log),
            Verifier = new Verifier(Perception, decisions, Log),
            Perception = Perception,
            Executor = Executor,
            Permissions = Permissions,
            Interaction = interaction,
            Windows = Windows,
            Files = Files,
            Settings = () => Settings.Current,
            FileAccessFactory = CreateFileAccessPolicy,
            Usage = Usage,
            Log = Log,
            ControlSwitch = ControlSwitch,
            Vision = new VisionAnalyzer(chat, Log),
            Capture = Capture,
        });
        Tasks = new TaskManager(Runner, History, () => Settings.Current, Log);

        Settings.Changed += (_, s) =>
        {
            DomProvider.Enabled = s.Control.UseBrowserExtension;
            Perception.DefaultRequest = new PerceptionRequest { MaxElements = s.Control.MaxSnapshotElements };
        };
        Perception.DefaultRequest = new PerceptionRequest { MaxElements = Settings.Current.Control.MaxSnapshotElements };

        if (options.StartBrowserBridge) { Bridge.Start(); }
        Log.Info("runtime", $"Kairo runtime started (version {typeof(KairoRuntime).Assembly.GetName().Version})");
    }

    public KairoPaths Paths { get; }
    public KairoLogger Log { get; }
    public SettingsStore Settings { get; }
    public DpapiSecretStore Secrets { get; }
    public UsageTracker Usage { get; }
    public ComputerControlSwitch ControlSwitch { get; }
    public OpenRouterHttp Http { get; }
    public OpenRouterClient OpenRouter { get; }
    public JevClient Jev { get; }
    public ConnectionTester ConnectionTester { get; }
    public WindowService Windows { get; }
    public InputSimulator Input { get; }
    public UiaCore Uia { get; }
    public BrowserBridgeServer Bridge { get; }
    public UiaPerceptionProvider UiaProvider { get; }
    public BrowserPerceptionProvider DomProvider { get; }
    public UiaChangeMonitor ChangeMonitor { get; }
    public PerceptionService Perception { get; }
    public ScreenCaptureService Capture { get; }
    public ClipboardService Clipboard { get; }
    public AppLauncher Launcher { get; }
    public WindowsActionExecutor Executor { get; }
    public FileOperations Files { get; }
    public TaskHistoryStore History { get; }
    public PermissionManager Permissions { get; }
    public AgentRunner Runner { get; }
    public TaskManager Tasks { get; }

    public bool HasApiKey => Secrets.HasSecret(ApiKeySecretName);

    public FileAccessPolicy CreateFileAccessPolicy()
    {
        var s = Settings.Current.Security;
        return FileAccessPolicy.CreateDefault(s.AllowedReadRoots, s.AllowedWriteRoots, Paths.LocalDirectory);
    }

    private void OnBrowserEvent(object? sender, BrowserEvent e)
    {
        // DOM or navigation changes in the watched browser window invalidate its snapshot.
        var target = Tasks.Current is { State: var state } current && !state.IsFinal() ? current.TargetWindow : null;
        if (target is null || !target.IsWebBrowser || Bridge.ConnectionFor(target) != e.Connection) { return; }
        var kind = e.Event is "navigationCompleted" or "tabActivated" or "tabRemoved" ? UiChangeKind.Navigation : UiChangeKind.Structure;
        Perception.Invalidate(target.Handle);
        ChangeMonitor.RaiseExternal(target.Handle, kind);
    }

    public async ValueTask DisposeAsync()
    {
        Tasks.CancelCurrent("Kairo wird beendet.");
        ChangeMonitor.Dispose();
        await Bridge.DisposeAsync().ConfigureAwait(false);
        Http.Dispose();
        Log.Info("runtime", "Kairo runtime stopped");
        Log.Dispose();
    }
}
