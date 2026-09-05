using Dalamud.Interface.Windowing;
using Microsoft.Extensions.Logging;
using RavaCast.UI;
using RavaCast.Services;

namespace RavaCast.Services.Mediator;

public abstract class WindowMediatorSubscriberBase : Window, IMediatorSubscriber, IDisposable
{
    protected readonly ILogger _logger;
    private readonly PerformanceMonitor _performance;
    protected WindowMediatorSubscriberBase(ILogger logger, PluginMediator mediator, string name, PerformanceMonitor performance) : base(name)
    {
        _logger = logger; Mediator = mediator; _performance = performance;
        Mediator.Subscribe<UiToggleMessage>(this, msg => { if (msg.UiType == GetType()) Toggle(); });
    }
    public PluginMediator Mediator { get; }
    public override void Draw()
    {
        var themeScope = BeginThemeScope();
        try { using var chrome = RavaUiChrome.BeginScope(); DrawInternal(); }
        finally { themeScope?.Dispose(); }
    }
    protected virtual IDisposable? BeginThemeScope() => null;
    protected abstract void DrawInternal();
    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    protected virtual void Dispose(bool disposing) => Mediator.UnsubscribeAll(this);
}
