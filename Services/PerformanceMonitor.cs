namespace RavaCast.Services;

public sealed class PerformanceMonitor
{
    public bool Enabled => false;
    public void LogDiagnosticPerformance(object owner, string operation, Action action) => action();
    public T LogDiagnosticPerformance<T>(object owner, string operation, Func<T> func) => func();
    public void LogPerformance(object owner, string operation, Action action) => action();
}
