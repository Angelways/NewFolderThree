using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud;
using FFXIVClientStructs.Expansion;
using OmenTools;
using OmenTools.OmenService;

namespace NewFolderThree;

public sealed class Plugin : IDalamudPlugin, IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly WindowSystem _windows = new(ProductIdentity.InternalName);
    private readonly FsdApplication _application;
    private readonly YAxisAdjustController _yAxis;
    private readonly DeepDungeonSpeedController _potdSpeed;
    private readonly PotdMachinistActionBlocker _potdActionBlocker;
    private readonly CombatRecovery _combatRecovery;
    private readonly ReconnectController _reconnect;
    private readonly FsdWindow _window;
    private bool _disposed;

    public string Name => ProductIdentity.DisplayName;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IPluginLog pluginLog,
        IObjectTable objectTable,
        IPartyList partyList,
        ISigScanner sigScanner,
        IClientState clientState,
        ICondition conditions,
        ITargetManager targets,
        IGameInteropProvider interop,
        IPlayerState playerState,
        IGameGui gameGui,
        IDataManager gameData)
    {
        _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));
        _commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        _framework = framework ?? throw new ArgumentNullException(nameof(framework));

        FsdApplication? application = null;
        YAxisAdjustController? yAxis = null;
        DeepDungeonSpeedController? potdSpeed = null;
        PotdMachinistActionBlocker? potdActionBlocker = null;
        CombatRecovery? combatRecovery = null;
        ReconnectController? reconnect = null;
        var omenInitialized = false;
        var nativeInteropInitialized = false;
        var commandRegistered = false;
        var drawSubscribed = false;
        var configSubscribed = false;
        var frameworkSubscribed = false;

        try
        {
            // ItemSourceManager is unrelated to FSD and starts a raw background build that
            // cannot be cancelled during constructor rollback.
            DService.Init(
                pluginInterface,
                static () => new DServiceInitOptions()
                    .Disable<ItemSourceManager>()
                    .Disable<LogMessageManager>()
                    .Disable<TargetManager>()
                    .Disable<UseActionManager>());
            omenInitialized = true;
            nativeInteropInitialized = true;
            ExpansionInterop.Initialize(sigScanner.SearchBase);

            var savedConfiguration = pluginInterface.GetPluginConfig() as Configuration;
            var configuration = savedConfiguration ?? new Configuration();
            configuration.Initialize(pluginInterface);
            Directory.CreateDirectory(pluginInterface.GetPluginConfigDirectory());

            string hostVersion = typeof(Plugin).Assembly.GetName().Version?.ToString()
                ?? throw new InvalidOperationException("Standalone host version is unavailable.");
            var mapOptions = new DetailedMapHostOptions(
                catalogBaseUri: null,
                contributesAnonymousEvidence: false,
                deleteCatalogsWhenDisabled: false);

            // FsdApplication injects the shared Dalamud services required by FSD.
            var yAxisController = new YAxisAdjustController(configuration, objectTable, partyList, pluginLog);
            yAxis = yAxisController;
            _yAxis = yAxisController;
            potdSpeed = new DeepDungeonSpeedController(
                framework,
                conditions,
                objectTable,
                interop,
                sigScanner,
                pluginLog,
                () => configuration.PotdSpeedEnabled,
                () => configuration.PotdSpeedAddition,
                () => configuration.PotdMaxAcceleration);
            _potdSpeed = potdSpeed;
            potdActionBlocker = new PotdMachinistActionBlocker(
                commandManager, gameData, clientState, conditions, objectTable, interop, pluginLog);
            _potdActionBlocker = potdActionBlocker;
            application = FsdApplication.CreateStandalone(
                pluginInterface,
                configuration,
                ProductIdentity.HostIdentity,
                hostVersion,
                mapOptions,
                playerYOffsetProvider: () => yAxisController.ActualYOffset,
                movementSpeedProvider: _potdSpeed.GetNavigationMovementSpeed);
            _application = application;

            combatRecovery = new CombatRecovery(clientState, conditions, objectTable, targets, interop);
            _combatRecovery = combatRecovery;
            reconnect = new ReconnectController(_application, clientState, playerState, objectTable,
                conditions, gameGui, gameData, pluginLog);
            _reconnect = reconnect;
            _window = new FsdWindow(_application, _yAxis, _combatRecovery, _reconnect,
                configuration, _potdSpeed);
            _windows.AddWindow(_window);

            _commandManager.AddHandler(ProductIdentity.Command, new CommandInfo(OnCommand)
            {
                HelpMessage = $"Open {ProductIdentity.DisplayName} Deep Dungeon FSD."
            });
            commandRegistered = true;

            _pluginInterface.UiBuilder.Draw += Draw;
            drawSubscribed = true;
            _pluginInterface.UiBuilder.OpenConfigUi += OpenWindow;
            configSubscribed = true;
            _framework.Update += OnFrameworkUpdate;
            frameworkSubscribed = true;

            if (savedConfiguration == null)
                configuration.Save(configuration.Fsd);
        }
        catch (Exception startupError)
        {
            var rollbackErrors = new List<Exception>();
            RollBack(() => { if (frameworkSubscribed) _framework.Update -= OnFrameworkUpdate; }, rollbackErrors);
            RollBack(() => { if (configSubscribed) _pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow; }, rollbackErrors);
            RollBack(() => { if (drawSubscribed) _pluginInterface.UiBuilder.Draw -= Draw; }, rollbackErrors);
            RollBack(() => { if (commandRegistered) _commandManager.RemoveHandler(ProductIdentity.Command); }, rollbackErrors);
            RollBack(_windows.RemoveAllWindows, rollbackErrors);
            RollBack(() => reconnect?.Dispose(), rollbackErrors);
            RollBack(() => combatRecovery?.Dispose(), rollbackErrors);
            RollBack(() => application?.Dispose(), rollbackErrors);
            RollBack(() => potdActionBlocker?.Dispose(), rollbackErrors);
            RollBack(() => potdSpeed?.Dispose(), rollbackErrors);
            RollBack(() => yAxis?.Dispose(), rollbackErrors);
            RollBack(() => { if (nativeInteropInitialized) ExpansionInterop.Uninitialize(); }, rollbackErrors);
            RollBack(() => { if (omenInitialized) DService.Uninit(); }, rollbackErrors);

            if (rollbackErrors.Count == 0)
                throw;

            rollbackErrors.Insert(0, startupError);
            throw new AggregateException("Standalone FSD startup and rollback both failed.", rollbackErrors);
        }
    }

    private static void RollBack(Action action, List<Exception> errors)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private void Draw()
    {
        _windows.Draw();
        _yAxis.DrawOverlay();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        bool connected = _reconnect.Update();
        if (connected)
            _application.Update(framework);
        _potdActionBlocker.Update(_application.Settings.Rotation.Provider, _application.IsRunActive, connected);
        _yAxis.Update(connected ? _application.CurrentDeepDungeonState : default);
        _combatRecovery.Update(_application.IsRunActive, _application.CanRecoverCombat,
            _application.CanStartBossCombat, connected, _application.CurrentDeepDungeonState);
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length > 0 && args[0].Equals("fsd", StringComparison.OrdinalIgnoreCase))
            _application.HandleCommand(args[1..]);
        else
            _window.Toggle();
    }
    private void OpenWindow() => _window.IsOpen = true;

    public void Dispose()
    {
        if (_disposed)
            return;
        _framework.Update -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenWindow;
        _pluginInterface.UiBuilder.Draw -= Draw;
        _commandManager.RemoveHandler(ProductIdentity.Command);
        _windows.RemoveAllWindows();
        _reconnect.Dispose();
        _combatRecovery.Dispose();
        _potdActionBlocker.Dispose();
        _application.Dispose();
        _potdSpeed.Dispose();
        _yAxis.Dispose();
        ExpansionInterop.Uninitialize();
        DService.Uninit();
        _disposed = true;
    }
}
