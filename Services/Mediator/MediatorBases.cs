using Microsoft.Extensions.Logging;

namespace RavaCast.Services.Mediator;

public abstract class MediatorSubscriberBase : IMediatorSubscriber
{
    protected MediatorSubscriberBase(ILogger logger, PluginMediator mediator) { Logger = logger; Mediator = mediator; }
    public PluginMediator Mediator { get; }
    protected ILogger Logger { get; }
    protected void UnsubscribeAll() => Mediator.UnsubscribeAll(this);
}

public abstract class DisposableMediatorSubscriberBase : MediatorSubscriberBase, IDisposable
{
    protected DisposableMediatorSubscriberBase(ILogger logger, PluginMediator mediator) : base(logger, mediator) { }
    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
    protected virtual void Dispose(bool disposing) => UnsubscribeAll();
}
