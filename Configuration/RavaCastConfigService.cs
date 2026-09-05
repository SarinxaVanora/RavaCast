using Dalamud.Configuration;
using Dalamud.Plugin;

namespace RavaCast.Configuration;

[Serializable]
public sealed class RavaCastConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public float RavaCastDefaultVolume { get; set; } = 0.50f;
    public bool RavaCastDisableHardwareAcceleration { get; set; } = true;
    public bool RavaCastBrowserDarkMode { get; set; } = true;
    public List<RavaCastSavedScreenPlacement> RavaCastSavedScreenPlacements { get; set; } = [];
    public string RavaCastSelectedScreenPlacementName { get; set; } = string.Empty;
}

public sealed class RavaCastConfigService
{
    private readonly IDalamudPluginInterface _pluginInterface;
    public RavaCastConfiguration Current { get; }

    public RavaCastConfigService(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
        Current = pluginInterface.GetPluginConfig() as RavaCastConfiguration ?? new RavaCastConfiguration();
    }

    public void Save() => _pluginInterface.SavePluginConfig(Current);
}
