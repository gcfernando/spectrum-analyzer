using System.Configuration;

namespace Spectrum;

internal sealed class VisualizerPreferences : ApplicationSettingsBase
{
    private static readonly VisualizerPreferences s_default =
        (VisualizerPreferences)Synchronized(new VisualizerPreferences());

    public static VisualizerPreferences Default => s_default;

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string Mode
    {
        get => (string)this[nameof(Mode)];
        set => this[nameof(Mode)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("")]
    public string Theme
    {
        get => (string)this[nameof(Theme)];
        set => this[nameof(Theme)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("Fixed")]
    public string RotationMode
    {
        get => (string)this[nameof(RotationMode)];
        set => this[nameof(RotationMode)] = value;
    }

    [UserScopedSetting]
    [DefaultSettingValue("5")]
    public int RotationIntervalMinutes
    {
        get => (int)this[nameof(RotationIntervalMinutes)];
        set => this[nameof(RotationIntervalMinutes)] = value;
    }
}
