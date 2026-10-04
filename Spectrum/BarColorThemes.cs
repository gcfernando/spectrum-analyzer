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
    public VisualTheme Ui { get; }

    public BarColorTheme(Color low, Color mid, Color high, Color peak, float intensityCurve, VisualTheme ui)
    {
        Low = low;
        Mid = mid;
        High = high;
        Peak = peak;
        IntensityCurve = intensityCurve;
        Ui = ui;
    }
}

internal static class BarColorThemes
{
    internal static readonly string[] Names =
    {
        "ClassicSmooth", "Ice", "Sunset", "MonoCyan", "Synthwave", "Aurora",
        "Obsidian Gold", "Midnight Prism", "Emerald Noir", "Crimson Velvet", "Studio"
    };

    private static readonly VisualTheme s_neutralUi = new(
        canvas: Color.FromArgb(12, 16, 21),
        headerStart: Color.FromArgb(35, 43, 52),
        headerEnd: Color.FromArgb(22, 28, 35),
        surface: Color.FromArgb(17, 22, 28),
        raisedSurface: Color.FromArgb(24, 31, 39),
        controlSurface: Color.FromArgb(28, 36, 45),
        visualizationSurface: Color.FromArgb(18, 24, 31),
        waveSurface: Color.FromArgb(12, 18, 24),
        divider: Color.FromArgb(73, 87, 101),
        frame: Color.FromArgb(98, 117, 134),
        grid: Color.FromArgb(45, 59, 72),
        majorGrid: Color.FromArgb(72, 88, 103),
        inactiveSignal: Color.FromArgb(42, 58, 70),
        primaryText: Color.FromArgb(239, 244, 248),
        secondaryText: Color.FromArgb(183, 196, 207),
        disabledText: Color.FromArgb(127, 140, 151),
        focus: Color.FromArgb(101, 196, 232),
        selection: Color.FromArgb(48, 76, 98),
        hover: Color.FromArgb(40, 57, 72));

    private static readonly VisualTheme s_studioUi = new(
        canvas: Color.FromArgb(11, 16, 21),
        headerStart: Color.FromArgb(29, 42, 51),
        headerEnd: Color.FromArgb(18, 28, 35),
        surface: Color.FromArgb(15, 23, 30),
        raisedSurface: Color.FromArgb(22, 32, 40),
        controlSurface: Color.FromArgb(25, 38, 47),
        visualizationSurface: Color.FromArgb(15, 25, 33),
        waveSurface: Color.FromArgb(10, 19, 26),
        divider: Color.FromArgb(65, 91, 105),
        frame: Color.FromArgb(92, 124, 140),
        grid: Color.FromArgb(40, 62, 74),
        majorGrid: Color.FromArgb(66, 94, 108),
        inactiveSignal: Color.FromArgb(38, 61, 72),
        primaryText: Color.FromArgb(237, 245, 248),
        secondaryText: Color.FromArgb(177, 199, 208),
        disabledText: Color.FromArgb(119, 140, 150),
        focus: Color.FromArgb(92, 201, 220),
        selection: Color.FromArgb(38, 79, 94),
        hover: Color.FromArgb(33, 61, 73));

    public static readonly BarColorTheme ClassicSmooth = new(
        low: Color.FromArgb(31, 190, 112),
        mid: Color.FromArgb(222, 190, 82),
        high: Color.FromArgb(232, 112, 67),
        peak: Color.FromArgb(255, 206, 168),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Cool gradient from deep blue through cyan to near-white.
    public static readonly BarColorTheme Ice = new(
        low: Color.FromArgb(70, 139, 210),
        mid: Color.FromArgb(60, 181, 211),
        high: Color.FromArgb(139, 221, 239),
        peak: Color.FromArgb(211, 239, 248),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Warm amber and orange tones echo the title-bar accent.
    public static readonly BarColorTheme Sunset = new(
        low: Color.FromArgb(181, 112, 54),
        mid: Color.FromArgb(220, 143, 64),
        high: Color.FromArgb(229, 107, 72),
        peak: Color.FromArgb(247, 208, 160),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Cyan intensity ramp from dim to bright.
    public static readonly BarColorTheme MonoCyan = new(
        low: Color.FromArgb(48, 143, 164),
        mid: Color.FromArgb(45, 177, 196),
        high: Color.FromArgb(105, 211, 222),
        peak: Color.FromArgb(204, 238, 239),
        intensityCurve: 1.4f, ui: s_neutralUi);

    // Retro gradient from purple through magenta to cyan.
    public static readonly BarColorTheme Synthwave = new(
        low: Color.FromArgb(133, 105, 204),
        mid: Color.FromArgb(181, 88, 178),
        high: Color.FromArgb(223, 121, 190),
        peak: Color.FromArgb(166, 215, 235),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Aurora-inspired gradient from emerald through cyan to pale lavender.
    public static readonly BarColorTheme Aurora = new(
        low: Color.FromArgb(54, 151, 108),
        mid: Color.FromArgb(48, 181, 156),
        high: Color.FromArgb(108, 202, 202),
        peak: Color.FromArgb(196, 214, 234),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Obsidian-inspired gold ramp with a pale-gold peak.
    public static readonly BarColorTheme ObsidianGold = new(
        low: Color.FromArgb(173, 137, 53),
        mid: Color.FromArgb(204, 162, 50),
        high: Color.FromArgb(235, 195, 86),
        peak: Color.FromArgb(245, 232, 177),
        intensityCurve: 1.45f, ui: s_neutralUi);

    // Saturated violet through cyan with a cool-white peak.
    public static readonly BarColorTheme MidnightPrism = new(
        low: Color.FromArgb(109, 111, 209),
        mid: Color.FromArgb(126, 91, 207),
        high: Color.FromArgb(61, 194, 224),
        peak: Color.FromArgb(202, 232, 241),
        intensityCurve: 1.45f, ui: s_neutralUi);

    // Deep emerald through vivid jade with a mint-white peak.
    public static readonly BarColorTheme EmeraldNoir = new(
        low: Color.FromArgb(46, 156, 112),
        mid: Color.FromArgb(33, 184, 130),
        high: Color.FromArgb(88, 219, 159),
        peak: Color.FromArgb(196, 237, 213),
        intensityCurve: 1.45f, ui: s_neutralUi);

    // Dark crimson through rose with a soft blush peak.
    public static readonly BarColorTheme CrimsonVelvet = new(
        low: Color.FromArgb(192, 85, 111),
        mid: Color.FromArgb(211, 69, 103),
        high: Color.FromArgb(235, 112, 134),
        peak: Color.FromArgb(244, 203, 211),
        intensityCurve: 1.45f, ui: s_neutralUi);

    public static readonly BarColorTheme Studio = new(
        low: Color.FromArgb(48, 151, 176),
        mid: Color.FromArgb(43, 181, 197),
        high: Color.FromArgb(91, 202, 218),
        peak: Color.FromArgb(177, 224, 230),
        intensityCurve: 1.45f, ui: s_studioUi);

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
        "studio" => Studio,
        _ => ClassicSmooth, // Use the classic palette for empty or unrecognized names.
    };
}
