using System;

namespace Spectrum;

internal enum VisualStyle
{
    None,
    Pulse,
    Glow
}

internal static class VisualStyles
{
    internal static readonly string[] Names = { "None", "Pulse", "Glow" };

    internal static string Resolve(string value)
    {
        foreach (var name in Names)
        {
            if (string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase))
                return name;
        }

        return "None";
    }

    internal static bool IsSupported(string mode) =>
        string.Equals(mode, "Spectrum", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "LED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, "Wave", StringComparison.OrdinalIgnoreCase);

    internal static VisualStyle Parse(string value) =>
        string.Equals(value, "Pulse", StringComparison.OrdinalIgnoreCase) ? VisualStyle.Pulse :
        string.Equals(value, "Glow", StringComparison.OrdinalIgnoreCase) ? VisualStyle.Glow :
        VisualStyle.None;
}
