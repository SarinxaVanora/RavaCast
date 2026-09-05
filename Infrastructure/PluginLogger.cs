using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace RavaCast.Infrastructure;

public sealed class PluginLogger<T> : ILogger<T>
{
    private readonly IPluginLog _log;
    private readonly string _name = typeof(T).Name;

    public PluginLogger(IPluginLog log) => _log = log;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = $"[{_name}] {formatter(state, exception)}";
        if (exception is not null) message += Environment.NewLine + exception;
        if (logLevel >= LogLevel.Error) _log.Error(message);
        else if (logLevel == LogLevel.Warning) _log.Warning(message);
        else _log.Information(message);
    }
}
