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

    public static BarColorTheme Resolve(string name) => (name ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "ice" => Ice,
        "sunset" => Sunset,
        "monocyan" or "mono" or "monochromecyan" => MonoCyan,
        "synthwave" => Synthwave,
        "aurora" => Aurora,
        _ => ClassicSmooth, // Use the classic palette for empty or unrecognized names.
    };
}
