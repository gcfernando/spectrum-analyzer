using System.Drawing;

namespace Spectrum;

/// <summary>Semantic colors shared by application chrome and visualization renderers.</summary>
internal readonly struct VisualTheme
{
    public Color Canvas { get; }
    public Color HeaderStart { get; }
    public Color HeaderEnd { get; }
    public Color Surface { get; }
    public Color RaisedSurface { get; }
    public Color ControlSurface { get; }
    public Color VisualizationSurface { get; }
    public Color WaveSurface { get; }
    public Color Divider { get; }
    public Color Frame { get; }
    public Color Grid { get; }
    public Color MajorGrid { get; }
    public Color InactiveSignal { get; }
    public Color PrimaryText { get; }
    public Color SecondaryText { get; }
    public Color DisabledText { get; }
    public Color Focus { get; }
    public Color Selection { get; }
    public Color Hover { get; }

    public VisualTheme(
        Color canvas, Color headerStart, Color headerEnd, Color surface, Color raisedSurface,
        Color controlSurface, Color visualizationSurface, Color waveSurface, Color divider,
        Color frame, Color grid, Color majorGrid, Color inactiveSignal, Color primaryText,
        Color secondaryText, Color disabledText, Color focus, Color selection, Color hover)
    {
        Canvas = canvas;
        HeaderStart = headerStart;
        HeaderEnd = headerEnd;
        Surface = surface;
        RaisedSurface = raisedSurface;
        ControlSurface = controlSurface;
        VisualizationSurface = visualizationSurface;
        WaveSurface = waveSurface;
        Divider = divider;
        Frame = frame;
        Grid = grid;
        MajorGrid = majorGrid;
        InactiveSignal = inactiveSignal;
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        DisabledText = disabledText;
        Focus = focus;
        Selection = selection;
        Hover = hover;
    }
}
