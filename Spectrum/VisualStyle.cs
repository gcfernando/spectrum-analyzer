using System;

namespace Spectrum;

internal enum VisualStyle
{
    None,
    Pulse,
    Glow,
    Trail,
    Scanline,
    Precision
}

internal static class VisualStyles
{
    internal static readonly string[] Names = { "None", "Pulse", "Glow", "Trail", "Scanline", "Precision" };
    private static readonly string[] s_noStyles = { "None" };
    private static readonly string[] s_meterStyles = { "None", "Scanline", "Precision" };
    private static readonly string[] s_ledStyles = { "None", "Pulse", "Scanline", "Precision" };
    private static readonly string[] s_fullStyles = { "None", "Pulse", "Glow", "Trail", "Scanline", "Precision" };
    private static readonly string[] s_advancedStyles = { "None", "Scanline", "Precision" };

    internal static string Resolve(string value)
    {
        foreach (var name in Names)
        {
            if (string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return "None";
    }

    internal static bool IsSupported(string mode) => GetSupportedNames(mode).Length > 1;

    internal static bool IsSupported(string mode, string style)
    {
        var normalizedStyle = Resolve(style);
        if (normalizedStyle == "None")
            return true;

        foreach (var supportedStyle in GetSupportedNames(mode))
            if (string.Equals(supportedStyle, normalizedStyle, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    internal static string[] GetSupportedNames(string mode)
    {
        if (string.Equals(mode, "Spectrum", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Wave", StringComparison.OrdinalIgnoreCase))
            return s_fullStyles;

        if (string.Equals(mode, "LED", StringComparison.OrdinalIgnoreCase))
            return s_ledStyles;

        if (IsAdvancedMode(mode))
            return s_advancedStyles;

        if (string.Equals(mode, "Bricks", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Dots", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Lollipop", StringComparison.OrdinalIgnoreCase))
            return s_meterStyles;

        return s_noStyles;
    }

    internal static VisualStyle Parse(string value) =>
        string.Equals(value, "Pulse", StringComparison.OrdinalIgnoreCase) ? VisualStyle.Pulse :
        string.Equals(value, "Glow", StringComparison.OrdinalIgnoreCase) ? VisualStyle.Glow :
        string.Equals(value, "Trail", StringComparison.OrdinalIgnoreCase) ? VisualStyle.Trail :
        string.Equals(value, "Scanline", StringComparison.OrdinalIgnoreCase) ? VisualStyle.Scanline :
        string.Equals(value, "Precision", StringComparison.OrdinalIgnoreCase) ? VisualStyle.Precision :
        VisualStyle.None;

    private static bool IsAdvancedMode(string mode) =>
        string.Equals(mode, "Waterfall", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Radial Spectrum", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Contour", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Peak Trace", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Threshold Monitor", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Band Matrix", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Octave Spectrum", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Level Change", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Orbit History", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Octave Waterfall", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Level Change Map", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Frequency Ribbon", StringComparison.OrdinalIgnoreCase);
}
