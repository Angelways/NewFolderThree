using Dalamud.Configuration;
using Dalamud.Plugin;
using DeepDungeon.Fsd.Runtime;

namespace NewFolderThree;

[Serializable]
public sealed class Configuration : IPluginConfiguration, IFsdSettingsStore
{
    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public int Version { get; set; } = 1;
    public FsdSettings Fsd { get; set; } = new();
    public float MovementYSubtract { get; set; }
    public bool AutoYAxisAdjustment { get; set; }
    public bool MovementYDisableInParty { get; set; }
    public bool PotdSpeedEnabled { get; set; }
    public float PotdSpeedAddition { get; set; } = 0.5f;
    public bool PotdMaxAcceleration { get; set; }

    public void Initialize(IDalamudPluginInterface pluginInterface)
        => _pluginInterface = pluginInterface ?? throw new ArgumentNullException(nameof(pluginInterface));

    public FsdSettings Load() => Fsd ?? throw new InvalidOperationException("Standalone FSD settings are missing.");

    public void Refresh(FsdSettings settings)
    {
        if (!ReferenceEquals(settings, Fsd))
            throw new InvalidOperationException("Standalone FSD settings store received an unknown settings instance.");
    }

    public void Save(FsdSettings settings)
    {
        if (!ReferenceEquals(settings, Fsd))
            throw new InvalidOperationException("Standalone FSD settings store received an unknown settings instance.");
        (_pluginInterface ?? throw new InvalidOperationException("Standalone configuration is not initialized."))
            .SavePluginConfig(this);
    }

    public void SavePluginConfig()
        => (_pluginInterface ?? throw new InvalidOperationException("Standalone configuration is not initialized."))
            .SavePluginConfig(this);
}
