// Developer :: > Gehan Fernando
using System;
using System.Configuration;
using System.IO;

namespace Spectrum;

// Persisted user settings for mode/theme/style plus the random-rotation options
internal sealed class VisualizerPreferences : ApplicationSettingsBase
{
    // Synchronized wrapper so saves from different threads don't trip over each other
    private static readonly VisualizerPreferences s_default =
        (VisualizerPreferences)Synchronized(new VisualizerPreferences());

    public static VisualizerPreferences Default => s_default;

    // Empty defaults mean "nothing saved yet", the snapshot loader fills in real fallbacks
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
    [DefaultSettingValue("")]
    public string Style
    {
        get => (string)this[nameof(Style)];
        set => this[nameof(Style)] = value;
    }

    // Either "Fixed" or "Random"; random cycles modes on a timer
    [UserScopedSetting]
    [DefaultSettingValue("Fixed")]
    public string RotationMode
    {
        get => (string)this[nameof(RotationMode)];
        set => this[nameof(RotationMode)] = value;
    }

    // Minutes between random switches; clamped when loaded
    [UserScopedSetting]
    [DefaultSettingValue("5")]
    public int RotationIntervalMinutes
    {
        get => (int)this[nameof(RotationIntervalMinutes)];
        set => this[nameof(RotationIntervalMinutes)] = value;
    }
}

// Validated, read-only copy of the settings the UI starts with; any read error is carried in Error
internal sealed class VisualPreferenceSnapshot
{
    // Built via Load() only, so there's one place that decides the fallbacks
    private VisualPreferenceSnapshot(string mode, string style, string theme,
        bool randomEnabled, int intervalMinutes, Exception error)
    {
        Mode = mode;
        Style = style;
        Theme = theme;
        RandomEnabled = randomEnabled;
        IntervalMinutes = intervalMinutes;
        Error = error;
    }

    internal string Mode { get; }
    internal string Style { get; }
    internal string Theme { get; }
    internal bool RandomEnabled { get; }
    internal int IntervalMinutes { get; }
    // Non-null when settings couldn't be read, so the caller can warn the user
    internal Exception Error { get; }

    // Both sources are passed in as delegates so this stays testable without touching real config files
    internal static VisualPreferenceSnapshot Load(
        Func<string, string> configuration, Func<VisualizerPreferences> userFactory)
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));
        if (userFactory == null)
            throw new ArgumentNullException(nameof(userFactory));

        // App-level defaults come first; user-saved values override them below
        string mode;
        string style;
        string theme;
        try
        {
            mode = configuration("Mode");
            style = configuration("Style");
            theme = configuration("Theme");
        }
        // If the config itself is broken, fall all the way back to hardcoded defaults
        catch (Exception exception) when (IsPreferenceReadError(exception))
        {
            return new VisualPreferenceSnapshot("Spectrum", "None", "ClassicSmooth", false, 5, exception);
        }

        mode = string.IsNullOrWhiteSpace(mode) ? "Spectrum" : mode;
        style = string.IsNullOrWhiteSpace(style) ? "None" : style;
        theme = string.IsNullOrWhiteSpace(theme) ? "ClassicSmooth" : theme;
        try
        {
            var preferences = userFactory();
            var savedMode = preferences.Mode;
            var savedStyle = preferences.Style;
            var savedTheme = preferences.Theme;
            var randomEnabled = string.Equals(preferences.RotationMode, "Random", StringComparison.OrdinalIgnoreCase);
            // Keep the interval sane so a hand-edited value can't make rotation spin or stall
            var intervalMinutes = Math.Max(1, Math.Min(240, preferences.RotationIntervalMinutes));

            return new VisualPreferenceSnapshot(
                string.IsNullOrWhiteSpace(savedMode) ? mode : savedMode,
                string.IsNullOrWhiteSpace(savedStyle) ? style : savedStyle,
                string.IsNullOrWhiteSpace(savedTheme) ? theme : savedTheme,
                randomEnabled, intervalMinutes, null);
        }
        catch (Exception exception) when (IsPreferenceReadError(exception))
        {
            return new VisualPreferenceSnapshot(mode, style, theme, false, 5, exception);
        }
    }

    // Only the "file is missing/corrupt/locked" style failures are swallowed; real bugs still surface
    private static bool IsPreferenceReadError(Exception exception) =>
        exception is ConfigurationErrorsException || exception is IOException || exception is UnauthorizedAccessException;
}
