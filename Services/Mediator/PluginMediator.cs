using System.Collections.Concurrent;

namespace RavaCast.Services.Mediator;

public interface IMediatorSubscriber { PluginMediator Mediator { get; } }

public sealed class PluginMediator
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<MessageBase> _queue = new();
    private readonly Dictionary<Type, List<(IMediatorSubscriber Subscriber, Delegate Action)>> _subs = new();

    public void Publish<T>(T message) where T : MessageBase
    {
        if (message.KeepThreadContext) Execute(message);
        else _queue.Enqueue(message);
    }

    public void Pump(int maxMessages = 128)
    {
        for (var i = 0; i < maxMessages && _queue.TryDequeue(out var message); i++) Execute(message);
    }

    public void Subscribe<T>(IMediatorSubscriber subscriber, Action<T> action) where T : MessageBase
    {
        lock (_gate)
        {
            if (!_subs.TryGetValue(typeof(T), out var list)) _subs[typeof(T)] = list = [];
            list.Add((subscriber, action));
        }
    }

    public void UnsubscribeAll(IMediatorSubscriber subscriber)
    {
        lock (_gate)
            foreach (var list in _subs.Values) list.RemoveAll(x => ReferenceEquals(x.Subscriber, subscriber));
    }

    private void Execute(MessageBase message)
    {
        (IMediatorSubscriber Subscriber, Delegate Action)[] targets;
        lock (_gate)
        {
            if (!_subs.TryGetValue(message.GetType(), out var list) || list.Count == 0) return;
            targets = list.ToArray();
        }
        foreach (var target in targets)
        {
            try { target.Action.DynamicInvoke(message); }
            catch { }
        }
    }
}
