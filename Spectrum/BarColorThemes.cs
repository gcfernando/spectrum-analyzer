using System.Drawing;

namespace Spectrum;

/// <summary>Named heat-map palettes selected by the App.config Theme key and usable with any visualization mode.</summary>
internal readonly struct BarColorTheme
{
    public Color Low { get; }
    public Color Mid { get; }
    public Color High { get; }
    public Color Peak { get; }
    public float IntensityCurve { get; }

    public BarColorTheme(Color low, Color mid, Color high, Color peak, float intensityCurve)
    {
        Low = low;
        Mid = mid;
        High = high;
        Peak = peak;
        IntensityCurve = intensityCurve;
    }
}

internal static class BarColorThemes
{
    internal static readonly string[] Names =
    {
        "ClassicSmooth", "Ice", "Sunset", "MonoCyan", "Synthwave", "Aurora",
        "Obsidian Gold", "Midnight Prism", "Emerald Noir", "Crimson Velvet"
    };

    // A gentler curve smooths the green-to-yellow-to-orange transition.
    public static readonly BarColorTheme ClassicSmooth = new(
        low: Color.FromArgb(31, 190, 112),
        mid: Color.FromArgb(222, 190, 82),
        high: Color.FromArgb(232, 112, 67),
        peak: Color.FromArgb(255, 206, 168),
        intensityCurve: 1.5f);

    // Cool gradient from deep blue through cyan to near-white.
    public static readonly BarColorTheme Ice = new(
        low: Color.FromArgb(48, 91, 190),
        mid: Color.FromArgb(30, 175, 205),
        high: Color.FromArgb(140, 222, 242),
        peak: Color.FromArgb(224, 246, 255),
        intensityCurve: 1.5f);

    // Warm amber and orange tones echo the title-bar accent.
    public static readonly BarColorTheme Sunset = new(
        low: Color.FromArgb(145, 79, 35),
        mid: Color.FromArgb(222, 126, 50),
        high: Color.FromArgb(238, 83, 57),
        peak: Color.FromArgb(255, 218, 170),
        intensityCurve: 1.5f);

    // Cyan intensity ramp from dim to bright.
    public static readonly BarColorTheme MonoCyan = new(
        low: Color.FromArgb(24, 94, 112),
        mid: Color.FromArgb(20, 157, 181),
        high: Color.FromArgb(67, 207, 220),
        peak: Color.FromArgb(206, 248, 247),
        intensityCurve: 1.4f);

    // Retro gradient from purple through magenta to cyan.
    public static readonly BarColorTheme Synthwave = new(
        low: Color.FromArgb(92, 55, 155),
        mid: Color.FromArgb(180, 65, 163),
        high: Color.FromArgb(232, 105, 190),
        peak: Color.FromArgb(148, 221, 250),
        intensityCurve: 1.5f);

    // Aurora-inspired gradient from emerald through cyan to pale lavender.
    public static readonly BarColorTheme Aurora = new(
        low: Color.FromArgb(34, 126, 91),
        mid: Color.FromArgb(28, 174, 151),
        high: Color.FromArgb(91, 199, 205),
        peak: Color.FromArgb(206, 213, 244),
        intensityCurve: 1.5f);

    // Obsidian-inspired gold ramp with a pale-gold peak.
    public static readonly BarColorTheme ObsidianGold = new(
        low: Color.FromArgb(92, 67, 18),
        mid: Color.FromArgb(191, 143, 22),
        high: Color.FromArgb(247, 201, 67),
        peak: Color.FromArgb(255, 245, 194),
        intensityCurve: 1.45f);

    // Saturated violet through cyan with a cool-white peak.
    public static readonly BarColorTheme MidnightPrism = new(
        low: Color.FromArgb(54, 31, 126),
        mid: Color.FromArgb(112, 64, 205),
        high: Color.FromArgb(35, 204, 244),
        peak: Color.FromArgb(232, 248, 255),
        intensityCurve: 1.45f);

    // Deep emerald through vivid jade with a mint-white peak.
    public static readonly BarColorTheme EmeraldNoir = new(
        low: Color.FromArgb(11, 91, 65),
        mid: Color.FromArgb(0, 171, 116),
        high: Color.FromArgb(57, 235, 157),
        peak: Color.FromArgb(218, 255, 235),
        intensityCurve: 1.45f);

    // Dark crimson through rose with a soft blush peak.
    public static readonly BarColorTheme CrimsonVelvet = new(
        low: Color.FromArgb(103, 21, 45),
        mid: Color.FromArgb(193, 31, 72),
        high: Color.FromArgb(244, 90, 118),
        peak: Color.FromArgb(255, 224, 230),
        intensityCurve: 1.45f);

    public static BarColorTheme Resolve(string name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ice" => Ice,
        "sunset" => Sunset,
        "monocyan" or "mono" or "monochromecyan" => MonoCyan,
        "synthwave" => Synthwave,
        "aurora" => Aurora,
        "obsidian gold" => ObsidianGold,
        "midnight prism" => MidnightPrism,
        "emerald noir" => EmeraldNoir,
        "crimson velvet" => CrimsonVelvet,
        _ => ClassicSmooth, // Use the classic palette for empty or unrecognized names.
    };
}
