// Developer :: > Gehan Fernando
using System.Drawing;

namespace Spectrum;

// One bar palette: low/mid/high gradient stops, a peak-cap color, and the UI chrome that goes with it
internal readonly struct BarColorTheme
{
    public Color Low { get; }
    public Color Mid { get; }
    public Color High { get; }
    public Color Peak { get; }
    // Shapes how fast color ramps up with level; higher means colors stay cooler for longer
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

// Catalog of built-in themes plus name lookup
internal static class BarColorThemes
{
    // Display names for the theme picker; keep in sync with Resolve() below
    internal static readonly string[] Names =
    {
        "ClassicSmooth", "Ice", "Sunset", "MonoCyan", "Synthwave", "Aurora",
        "Obsidian Gold", "Midnight Prism", "Emerald Noir", "Crimson Velvet", "Studio",
        "Platinum Light", "High Contrast"
    };

    // Shared dark chrome used by most bar themes so only the bar colors change between them
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

    // Slightly teal-tinted chrome just for the Studio theme
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

    // Default look: green -> amber -> orange as the level climbs
    public static readonly BarColorTheme ClassicSmooth = new(
        low: Color.FromArgb(31, 190, 112),
        mid: Color.FromArgb(222, 190, 82),
        high: Color.FromArgb(232, 112, 67),
        peak: Color.FromArgb(255, 206, 168),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Cool blues fading to pale cyan
    public static readonly BarColorTheme Ice = new(
        low: Color.FromArgb(70, 139, 210),
        mid: Color.FromArgb(60, 181, 211),
        high: Color.FromArgb(139, 221, 239),
        peak: Color.FromArgb(211, 239, 248),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Warm copper to cream tones
    public static readonly BarColorTheme Sunset = new(
        low: Color.FromArgb(181, 112, 54),
        mid: Color.FromArgb(220, 166, 81),
        high: Color.FromArgb(238, 209, 140),
        peak: Color.FromArgb(248, 235, 205),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Single-hue cyan, just brightness changes
    public static readonly BarColorTheme MonoCyan = new(
        low: Color.FromArgb(48, 143, 164),
        mid: Color.FromArgb(80, 188, 208),
        high: Color.FromArgb(164, 223, 231),
        peak: Color.FromArgb(236, 250, 251),
        intensityCurve: 1.4f, ui: s_neutralUi);

    // Purple/magenta with a cool cyan-ish peak for contrast
    public static readonly BarColorTheme Synthwave = new(
        low: Color.FromArgb(133, 105, 204),
        mid: Color.FromArgb(181, 88, 178),
        high: Color.FromArgb(223, 121, 190),
        peak: Color.FromArgb(166, 215, 235),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Green into teal into soft lavender, northern-lights style
    public static readonly BarColorTheme Aurora = new(
        low: Color.FromArgb(54, 151, 108),
        mid: Color.FromArgb(67, 185, 188),
        high: Color.FromArgb(179, 207, 235),
        peak: Color.FromArgb(241, 236, 250),
        intensityCurve: 1.5f, ui: s_neutralUi);

    // Muted gold on dark, a bit more restrained than Sunset
    public static readonly BarColorTheme ObsidianGold = new(
        low: Color.FromArgb(173, 137, 53),
        mid: Color.FromArgb(211, 181, 100),
        high: Color.FromArgb(235, 217, 155),
        peak: Color.FromArgb(252, 243, 212),
        intensityCurve: 1.45f, ui: s_neutralUi);

    // Indigo and violet that jump to cyan at the top
    public static readonly BarColorTheme MidnightPrism = new(
        low: Color.FromArgb(109, 111, 209),
        mid: Color.FromArgb(163, 151, 219),
        high: Color.FromArgb(61, 194, 224),
        peak: Color.FromArgb(202, 232, 241),
        intensityCurve: 1.45f, ui: s_neutralUi);

    // Deep emerald drifting toward pale lime
    public static readonly BarColorTheme EmeraldNoir = new(
        low: Color.FromArgb(46, 156, 112),
        mid: Color.FromArgb(108, 201, 151),
        high: Color.FromArgb(181, 226, 178),
        peak: Color.FromArgb(240, 247, 217),
        intensityCurve: 1.45f, ui: s_neutralUi);

    // Dusty rose to peach
    public static readonly BarColorTheme CrimsonVelvet = new(
        low: Color.FromArgb(184, 101, 131),
        mid: Color.FromArgb(224, 145, 157),
        high: Color.FromArgb(237, 180, 170),
        peak: Color.FromArgb(250, 232, 218),
        intensityCurve: 1.45f, ui: s_neutralUi);

    // Calm blue-grey palette meant for long sessions; uses the studio chrome
    public static readonly BarColorTheme Studio = new(
        low: Color.FromArgb(48, 151, 176),
        mid: Color.FromArgb(126, 171, 218),
        high: Color.FromArgb(184, 202, 230),
        peak: Color.FromArgb(236, 244, 250),
        intensityCurve: 1.45f, ui: s_studioUi);

    // The only light theme, so the bar colors are dark and the peak is near-black to stay visible on white
    public static readonly BarColorTheme PlatinumLight = new(
        low: Color.FromArgb(35, 88, 149),
        mid: Color.FromArgb(117, 49, 135),
        high: Color.FromArgb(153, 67, 20),
        peak: Color.FromArgb(21, 32, 44),
        intensityCurve: 1.35f,
        ui: new VisualTheme(
            canvas: Color.FromArgb(239, 242, 246),
            headerStart: Color.FromArgb(250, 252, 255),
            headerEnd: Color.FromArgb(228, 234, 241),
            surface: Color.FromArgb(247, 249, 252),
            raisedSurface: Color.White,
            controlSurface: Color.White,
            visualizationSurface: Color.FromArgb(247, 249, 252),
            waveSurface: Color.White,
            divider: Color.FromArgb(111, 126, 143),
            frame: Color.FromArgb(89, 108, 128),
            grid: Color.FromArgb(217, 224, 233),
            majorGrid: Color.FromArgb(176, 190, 206),
            inactiveSignal: Color.FromArgb(206, 216, 227),
            primaryText: Color.FromArgb(21, 32, 44),
            secondaryText: Color.FromArgb(63, 77, 94),
            disabledText: Color.FromArgb(99, 112, 128),
            focus: Color.FromArgb(21, 83, 155),
            selection: Color.FromArgb(207, 225, 247),
            hover: Color.FromArgb(225, 236, 250)));

    // Pure black with saturated bars and white frames for accessibility
    public static readonly BarColorTheme HighContrast = new(
        low: Color.FromArgb(255, 221, 64),
        mid: Color.FromArgb(64, 235, 255),
        high: Color.FromArgb(255, 141, 225),
        peak: Color.White,
        intensityCurve: 1.35f,
        ui: new VisualTheme(
            canvas: Color.Black,
            headerStart: Color.Black,
            headerEnd: Color.Black,
            surface: Color.Black,
            raisedSurface: Color.FromArgb(14, 14, 14),
            controlSurface: Color.Black,
            visualizationSurface: Color.Black,
            waveSurface: Color.Black,
            divider: Color.FromArgb(160, 160, 160),
            frame: Color.White,
            grid: Color.FromArgb(56, 56, 56),
            majorGrid: Color.FromArgb(104, 104, 104),
            inactiveSignal: Color.FromArgb(42, 42, 42),
            primaryText: Color.White,
            secondaryText: Color.FromArgb(224, 224, 224),
            disabledText: Color.FromArgb(160, 160, 160),
            focus: Color.FromArgb(255, 221, 64),
            selection: Color.FromArgb(31, 45, 70),
            hover: Color.FromArgb(35, 35, 35)));

    // Forgiving lookup: trims, ignores case, accepts a few aliases, and falls back to the default theme
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
        "platinum light" => PlatinumLight,
        "high contrast" => HighContrast,
        _ => ClassicSmooth,
    };
}
