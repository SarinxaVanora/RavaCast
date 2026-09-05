using Dalamud.Utility;

namespace RavaCast.Infrastructure;

public static class LinuxSmoothMode
{
    public static bool IsActive { get; } = SafeIsWine();
    private static bool SafeIsWine()
    {
        try { return Util.IsWine(); }
        catch { return false; }
    }
}
