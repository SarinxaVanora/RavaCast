using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RavaCast.Configuration;
using RavaCast.Infrastructure;
using RavaCast.Services;
using RavaCast.Services.Mediator;
using RavaCast.Services.Mesh;
using RavaCast.Services.RavaCast;
using RavaCast.Services.RavaCast.Rendering;
using RavaCast.Services.RavaCast.WorldRender;
using RavaCast.UI;

namespace RavaCast;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commands;
    private readonly IFramework _framework;
    private readonly WindowSystem _windows = new("RavaCast");
    private readonly PluginMediator _mediator = new();
    private readonly PerformanceMonitor _performance = new();
    private readonly RavaMesh _mesh;
    private readonly RavaCastConfigService _config;
    private readonly RavaCastBackendInstallerService _backendInstaller;
    private readonly RavaCastBrowserSurface _surface;
    private readonly RavaCastWorldImageRenderer _worldRenderer;
    private readonly RavaCastService _cast;
    private readonly RavaCastRenderer _renderer;
    private readonly RavaCastUi _ui;
    private bool _loaded;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework, IObjectTable objects,
        IClientState clientState, IGameGui gameGui, IPluginLog pluginLog)
    {
        _pluginInterface = pluginInterface;
        _commands = commands;
        _framework = framework;

        _config = new RavaCastConfigService(pluginInterface);
        var world = new GameWorldService(objects, framework, clientState);
        var uiShared = new UiSharedService();
        _mesh = new RavaMesh(new PluginLogger<RavaMesh>(pluginLog), _mediator, world, framework);
        _backendInstaller = new RavaCastBackendInstallerService(new PluginLogger<RavaCastBackendInstallerService>(pluginLog), pluginInterface);
        var backend = new RavaCastWebView2D3DTextureBackend(new PluginLogger<RavaCastWebView2D3DTextureBackend>(pluginLog), pluginInterface, _backendInstaller, _config);
        _surface = new RavaCastBrowserSurface(new PluginLogger<RavaCastBrowserSurface>(pluginLog), backend);
        _worldRenderer = new RavaCastWorldImageRenderer(new PluginLogger<RavaCastWorldImageRenderer>(pluginLog));
        _cast = new RavaCastService(new PluginLogger<RavaCastService>(pluginLog), _mediator, _mesh, world, objects, clientState, framework, _config, _surface, _performance);
        _renderer = new RavaCastRenderer(new PluginLogger<RavaCastRenderer>(pluginLog), pluginInterface.UiBuilder, gameGui, _cast, _surface, objects, _worldRenderer);
        _ui = new RavaCastUi(new PluginLogger<RavaCastUi>(pluginLog), _mediator, uiShared, _cast, _surface, _config, _backendInstaller, _performance);
        _windows.AddWindow(_ui);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        _pluginInterface.UiBuilder.Draw += Draw;
        _pluginInterface.UiBuilder.OpenMainUi += OpenMain;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenMain;
        _framework.Update += Pump;
        _commands.AddHandler("/ravacast", new CommandInfo((_, _) => _mediator.Publish(new UiToggleMessage(typeof(RavaCastUi)))) { HelpMessage = "Open RavaCast." });
        _mesh.Start();
        await _cast.StartAsync(cancellationToken).ConfigureAwait(false);
        await _renderer.StartAsync(cancellationToken).ConfigureAwait(false);
        _loaded = true;
    }

    private void Draw() => _windows.Draw();
    private void OpenMain() => _ui.IsOpen = true;

    private void Pump(IFramework _) => _mediator.Pump();

    public async ValueTask DisposeAsync()
    {
        if (_loaded)
        {
            _commands.RemoveHandler("/ravacast");
            _framework.Update -= Pump;
            _pluginInterface.UiBuilder.Draw -= Draw;
            _pluginInterface.UiBuilder.OpenMainUi -= OpenMain;
            _pluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
            await _renderer.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _cast.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _loaded = false;
        }

        _ui.Dispose();
        _renderer.Dispose();
        _cast.Dispose();
        _surface.Dispose();
        _worldRenderer.Dispose();
        _backendInstaller.Dispose();
        await _mesh.DisposeAsync().ConfigureAwait(false);
    }
}
