// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace Spectrum;

[Description("Vertical spectrum-style progress bar")]
[Category("VerticalProgressBar")]
[RefreshProperties(RefreshProperties.All)]
public sealed class VerticalProgressBar : ProgressBar
{
    private static readonly object s_lock = new();
    private static volatile System.Windows.Forms.Timer s_timer;

    private Color _inactiveBrickColor = Color.FromArgb(42, 58, 70);
    private Color _inactiveDotColor = Color.FromArgb(52, 58, 70);
    private Color _inactiveSpectrumColor = Color.FromArgb(30, 58, 70);
    private Color _waveBackgroundColor = Color.FromArgb(24, 58, 70);
    private static readonly List<WeakReference<VerticalProgressBar>> s_instances = new(128);

    private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();
    private static long s_lastTicks;
    private static int s_idleTicks;
    private const int SleepAfterIdleTicks = 12;

    private volatile int _targetValue;
    private float _displayValue;

    private float _peakValue;
    private float _peakHoldLeftMs;
    private float _visualPeakLevel;
    private bool _visualPeakValid;
    private bool _visualPeakReleasing;
    private readonly int _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
    private int _timerWakePending;
    private readonly float[] _trailLevels = new float[8];
    private readonly float[] _trailLife = new float[8];
    private int _trailWrite;
    private float _trailSampleElapsed;
    private bool _trailActive;
    private int _lastTrailQ = int.MinValue;

    private int _lastDisplayQ = int.MinValue;
    private int _lastPeakQ = int.MinValue;
    private Color _lastDisplayColor = Color.Empty;
    private Color _lastPeakColor = Color.Empty;
    private VisualizationMode _lastModeQ = (VisualizationMode)(-1);

    private readonly List<Rectangle> _brickRects = new(256);
    private readonly List<int> _brickCentres = new(256);
    private readonly List<float> _brickT = new(256);
    private readonly List<Color> _brickHeatColors = new(256);

    private int _cachedWidth = -1;

    // hysteresis so the top brick doesn't flicker near a boundary (flips 2 px off center)
    private const int BrickHysteresisPx = 2;
    private int _litBricks;
    private bool _litBricksValid;
    private int _cachedHeight = -1;
    private bool _colorsDirty = true;

    private SolidBrush _backgroundBrush;
    private Bitmap _inactiveMeterSurface;
    private Bitmap _activeMeterSurface;
    private bool _meterSurfacesDirty = true;
    private bool _meterSurfacesHighQuality;
    private VisualizationMode _meterSurfacesMode;
    private float[] _surfaceGridlines = Array.Empty<float>();
    private float[] _surfaceVerticalGridlines = Array.Empty<float>();
    private Rectangle _cachedPlotBounds;
    private Rectangle _surfacePlotBounds;
    internal Rectangle RenderedActiveBounds { get; private set; }
    internal Rectangle RenderedPeakBounds { get; private set; }

    // everything here is device pixels; bitmap DPI metadata must never shift the layout
    private float RenderDpiY => DeviceDpi;
    private int MarkerThickness => (int)Math.Min(int.MaxValue / 4,
        Math.Max(1, Math.Ceiling(Math.Max(1, PeakLineThickness) * (double)RenderDpiY / 96)));
    internal int PeakGapPixels => Math.Max(1, (int)Math.Ceiling(2f * RenderDpiY / 96f));

    // level extent: zero at Bottom, full scale at Top, peak headroom sits above Top
    internal Rectangle PlotBounds
    {
        get
        {
            var bounds = ClientRectangle;
            var pad = BrickPadding;
            var width = Math.Max(0, bounds.Width - 2 * pad);
            var height = Math.Max(0, bounds.Height - 2 * pad);
            var mode = GetVisualizationModeCached();
            var reserve = mode is VisualizationMode.Spectrum or VisualizationMode.Bricks or VisualizationMode.Led
                ? MarkerThickness + PeakGapPixels : 0;
            // a tiny control keeps its level mapping but hides a marker that can't fit
            if (height <= reserve) reserve = 0;
            return new Rectangle(bounds.X + pad, bounds.Y + pad + reserve, width, height - reserve);
        }
    }
    private readonly SolidBrush _workBrush = new(Color.Black);
    private readonly SolidBrush _brickShadeBrush = new(Color.FromArgb(48, 0, 0, 0));
    private readonly SolidBrush _glowOuterBrush = new(Color.FromArgb(24, Color.Cyan));
    private readonly SolidBrush _glowMiddleBrush = new(Color.FromArgb(66, Color.Cyan));
    private readonly SolidBrush _glowCoreBrush = new(Color.FromArgb(224, Color.Cyan));
    private SolidBrush _brickHighlightBrush;
    private readonly Pen _borderPen = new(Color.FromArgb(92, 167, 177, 188), 1f);
    private readonly Pen _innerEdgePen = new(Color.FromArgb(24, 255, 255, 255), 1f);
    private readonly Pen _highlightPen = new(Color.FromArgb(170, 255, 255, 255), 1f);

    private readonly SolidBrush _peakBrush = new(Color.White);
    private readonly Pen _peakOutlinePen = new(Color.Black, 1f);

    private readonly Pen _gridlinePen = new(Color.FromArgb(26, 205, 218, 231), 1f);

    private readonly Pen _wavePen = new(Color.Lime, 2f);
    private Color _cachedWaveColor = Color.Empty;
    private float _cachedWaveWidth = -1f;

    private readonly Point[] _wavePoints = new Point[96];

    private enum VisualizationMode
    {
        Bricks,
        Led,
        Dots,
        Center,
        Mirror,
        Wave,
        Spectrum,
        Lollipop
    }

    private object _cachedTagObject;
    private string _cachedTagText;
    private VisualizationMode _cachedMode = VisualizationMode.Bricks;

    private VisualizationMode GetVisualizationModeCached()
    {
        var tagObj = Tag;
        if (!ReferenceEquals(tagObj, _cachedTagObject))
        {
            _cachedTagObject = tagObj;
            _cachedTagText = tagObj?.ToString();
            _cachedMode = ParseVisualizationMode(_cachedTagText);
        }
        return _cachedMode;
    }

    private static VisualizationMode ParseVisualizationMode(string tagText)
    {
        if (string.IsNullOrWhiteSpace(tagText))
            return VisualizationMode.Bricks;

        var pipeIndex = tagText.IndexOf('|');
        if (pipeIndex >= 0)
            tagText = tagText.Substring(0, pipeIndex);

        return tagText.Trim().ToLowerInvariant() switch
        {
            "bricks"          => VisualizationMode.Bricks,
            "led"             => VisualizationMode.Led,
            "dots"            => VisualizationMode.Dots,
            "center"          => VisualizationMode.Center,
            "mirror"          => VisualizationMode.Mirror,
            "wave"            => VisualizationMode.Wave,
            "spectrum"        => VisualizationMode.Spectrum,
            "lollipop"        => VisualizationMode.Lollipop,
            _                 => VisualizationMode.Bricks,
        };
    }

    private static bool ModeSnapsDown(VisualizationMode mode)
        => mode is VisualizationMode.Center or VisualizationMode.Mirror;

    private static bool ModeHasPeakMarker(VisualizationMode mode)
        => mode is not VisualizationMode.Center and not VisualizationMode.Mirror;

    private VisualStyle _style;

    internal VisualStyle VisualStyle
    {
        get => _style;
        set
        {
            if (_style == value)
                return;

            _style = value;
            Array.Clear(_trailLife, 0, _trailLife.Length);
            _trailActive = false;
            _lastTrailQ = int.MinValue;
            Invalidate();
        }
    }

    [Category("Appearance")]
    public int BrickPadding
    {
        get => _brickPadding;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (_brickPadding == value) return;
            _brickPadding = value;
            InvalidateBrickGeometry();
        }
    }
    private int _brickPadding = 2;

    [Category("Appearance")]
    public int BrickHeight
    {
        get => _brickHeight;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
            if (_brickHeight == value) return;
            _brickHeight = value;
            InvalidateBrickGeometry();
        }
    }
    private int _brickHeight = 6;

    [Category("Appearance")]
    public int BrickGap
    {
        get => _brickGap;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (_brickGap == value) return;
            _brickGap = value;
            InvalidateBrickGeometry();
        }
    }
    private int _brickGap = 2;

    [Category("Appearance")]
    public bool BrickHighlight
    {
        get => _brickHighlight;
        set
        {
            if (_brickHighlight == value) return;
            _brickHighlight = value;
            BuildBrickHighlightBrush();
            Invalidate();
        }
    }
    private bool _brickHighlight = true;

    private int _highlightAlpha = 55;

    [Category("Appearance")]
    public int HighlightAlpha
    {
        get => _highlightAlpha;
        set
        {
            _highlightAlpha = Clamp(value, 0, 255);
            BuildBrickHighlightBrush();
            Invalidate();
        }
    }

    [Category("Appearance")]
    public bool HighQualityRendering
    {
        get => _highQualityRendering;
        set
        {
            if (_highQualityRendering == value) return;
            _highQualityRendering = value;
            _meterSurfacesDirty = true;
            Invalidate();
        }
    }
    private bool _highQualityRendering;

    private bool _heatmapEnabled = true;
    private Color _heatLowColor = Color.FromArgb(0, 255, 80);
    private Color _heatMidColor = Color.FromArgb(255, 235, 0);
    private Color _heatHighColor = Color.FromArgb(255, 140, 0);
    private Color _heatPeakColor = Color.Red;
    private float _heatIntensityCurve = 1.6f;

    private bool _topEmphasisEnabled = true;
    private float _topEmphasisStart = 0.85f;
    private float _topEmphasisStrength = 0.45f;

    [Category("Heatmap")]
    public bool HeatmapEnabled
    {
        get => _heatmapEnabled;
        set { _heatmapEnabled = value; _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public Color HeatLowColor
    {
        get => _heatLowColor;
        set { _heatLowColor = value; _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public Color HeatMidColor
    {
        get => _heatMidColor;
        set { _heatMidColor = value; _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public Color HeatHighColor
    {
        get => _heatHighColor;
        set { _heatHighColor = value; _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public Color HeatPeakColor
    {
        get => _heatPeakColor;
        set { _heatPeakColor = value; _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public float HeatIntensityCurve
    {
        get => _heatIntensityCurve;
        set { _heatIntensityCurve = Math.Max(0.15f, value); _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public bool TopEmphasisEnabled
    {
        get => _topEmphasisEnabled;
        set { _topEmphasisEnabled = value; _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public float TopEmphasisStart
    {
        get => _topEmphasisStart;
        set { _topEmphasisStart = Clamp01(value); _colorsDirty = true; Invalidate(); }
    }

    [Category("Heatmap")]
    public float TopEmphasisStrength
    {
        get => _topEmphasisStrength;
        set { _topEmphasisStrength = Clamp01(value); _colorsDirty = true; Invalidate(); }
    }

    [Category("Peak Hold")]
    public bool PeakHoldEnabled
    {
        get => _peakHoldEnabled;
        set
        {
            _peakHoldEnabled = value;
            if (!value) _visualPeakValid = _visualPeakReleasing = false;
            Invalidate();
        }
    }
    private bool _peakHoldEnabled = true;

    [Category("Peak Hold")]
    public int PeakHoldMilliseconds { get; set; } = 140;

    [Category("Peak Hold")]
    public float PeakDecayPerTick { get; set; } = 1.2f;

    [Category("Peak Hold")]
    public int PeakLineThickness
    {
        get => _peakLineThickness;
        set
        {
            if (_peakLineThickness == value) return;
            _peakLineThickness = value;
            InvalidateBrickGeometry();
        }
    }
    private int _peakLineThickness = 2;

    [Category("Peak Hold")]
    public Color PeakLineColor
    {
        get => _peakLineColor;
        set { _peakLineColor = value; Invalidate(); }
    }
    private Color _peakLineColor = Color.White;

    // peak marker takes the heat-map color from the peak's own height
    [Category("Peak Hold")]
    public bool PeakColorMatchesHeat
    {
        get => _peakColorMatchesHeat;
        set { _peakColorMatchesHeat = value; Invalidate(); }
    }
    private bool _peakColorMatchesHeat = true;

    // shared reference ticks line up across bars and only show through above the fill
    [Category("Appearance")]
    public IReadOnlyList<float> GridlineLevels
    {
        get => _gridlineLevels;
        set { _gridlineLevels = value; _meterSurfacesDirty = true; Invalidate(); }
    }
    private IReadOnlyList<float> _gridlineLevels;

    // grid x positions are local device pixels, not band indices
    internal IReadOnlyList<float> VerticalGridlinePositions
    {
        get => _verticalGridlinePositions;
        set { _verticalGridlinePositions = value; _meterSurfacesDirty = true; Invalidate(); }
    }
    private IReadOnlyList<float> _verticalGridlinePositions;

    [Category("Appearance")]
    public Color GridlineColor
    {
        get => _gridlinePen.Color;
        set { _gridlinePen.Color = value; _meterSurfacesDirty = true; Invalidate(); }
    }

    private int _animationFps = 60;

    [Category("Performance")]
    public int AnimationFps
    {
        get => _animationFps;
        set
        {
            _animationFps = Clamp(value, 15, 240);
            EnsureTimerConfigured();
        }
    }

    [Category("Performance")]
    public int ResponseTimeMs { get; set; } = 45;

    [Category("Performance")]
    public bool UseAsymmetricBallistics { get; set; } = false;

    private int _releaseTimeMs = 0;

    [Category("Performance")]
    public int ReleaseTimeMs
    {
        get => _releaseTimeMs;
        set => _releaseTimeMs = Clamp(value, 0, 5000);
    }

    [Category("Performance")]
    public int SnapUpThreshold { get; set; } = 6;

    private const float SilentSnapEpsilon = 0.06f;

    public VerticalProgressBar()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);

        DoubleBuffered = true;

        _backgroundBrush = new SolidBrush(BackColor);
        BuildBrickHighlightBrush();

        _targetValue = base.Value;
        _displayValue = base.Value;

        _peakValue = _displayValue;
        _peakHoldLeftMs = 0;

        RegisterInstance();
        UpdateStyles();
    }

    internal void ApplyThemeChrome(VisualTheme theme)
    {
        BackColor = theme.VisualizationSurface;
        ForeColor = theme.PrimaryText;
        _inactiveBrickColor = Color.FromArgb(120, theme.InactiveSignal);
        _inactiveDotColor = Color.FromArgb(155, theme.InactiveSignal);
        _inactiveSpectrumColor = Color.FromArgb(96, theme.InactiveSignal);
        _waveBackgroundColor = Color.FromArgb(72, theme.InactiveSignal);
        _borderPen.Color = theme.Frame;
        _innerEdgePen.Color = Color.FromArgb(80, theme.Divider);
        _highlightPen.Color = Color.FromArgb(122, theme.PrimaryText);
        _gridlinePen.Color = Color.FromArgb(94, theme.Grid);
        _meterSurfacesDirty = true;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnregisterInstance();

            _backgroundBrush?.Dispose();
            _inactiveMeterSurface?.Dispose();
            _activeMeterSurface?.Dispose();
            _brickHighlightBrush?.Dispose();
            _borderPen?.Dispose();
            _innerEdgePen?.Dispose();
            _highlightPen?.Dispose();
            _peakBrush?.Dispose();
            _peakOutlinePen?.Dispose();
            _gridlinePen?.Dispose();
            _workBrush?.Dispose();
            _brickShadeBrush?.Dispose();
            _glowOuterBrush?.Dispose();
            _glowMiddleBrush?.Dispose();
            _glowCoreBrush?.Dispose();
            _wavePen?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _backgroundBrush?.Dispose();
        _backgroundBrush = new SolidBrush(BackColor);
        _meterSurfacesDirty = true;
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        _colorsDirty = true;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _cachedWidth = -1;
        _cachedHeight = -1;
        _colorsDirty = true;

        _lastDisplayQ = int.MinValue;
        _lastPeakQ = int.MinValue;
        _lastModeQ = (VisualizationMode)(-1);

        Invalidate();
    }

    public new int Value
    {
        get => base.Value;
        set
        {
            var v = Clamp(value, Minimum, Maximum);
            base.Value = v;
            _targetValue = v;
            EnsureTimerConfigured();
        }
    }

    public void SetTargetValueUI(int value)
    {
        if (IsDisposed) return;
        _targetValue = Clamp(value, Minimum, Maximum);
        var t = s_timer;
        if (t != null && t.Enabled) return;
        EnsureTimerConfigured();
    }

    public void SetTargetValueThreadSafe(int value)
    {
        if (IsDisposed) return;
        _targetValue = Clamp(value, Minimum, Maximum);
        if (Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
        {
            EnsureTimerConfigured();
            return;
        }
        // no handle yet, so the timer or OnHandleCreated will pick the target up later
        if (!IsHandleCreated || Interlocked.Exchange(ref _timerWakePending, 1) != 0) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _timerWakePending, 0);
                if (!IsDisposed && !Disposing) EnsureTimerConfigured();
            }));
        }
        catch (InvalidOperationException) when (!IsHandleCreated || IsDisposed || Disposing)
        {
            Interlocked.Exchange(ref _timerWakePending, 0);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Interlocked.Exchange(ref _timerWakePending, 0);
        EnsureTimerConfigured();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        Array.Clear(_trailLife, 0, _trailLife.Length);
        _trailActive = false;
        _visualPeakValid = _visualPeakReleasing = false;
        if (Visible)
        {
            _displayValue = _targetValue;
            _peakValue = _displayValue;
            _peakHoldLeftMs = 0;
            _lastDisplayQ = _lastPeakQ = _lastTrailQ = int.MinValue;
            EnsureTimerConfigured();
            Invalidate();
            return;
        }
        lock (s_lock)
        {
            foreach (var reference in s_instances)
                if (reference.TryGetTarget(out var control) && !control.IsDisposed && control.Visible) return;
            s_timer?.Stop();
            s_lastTicks = 0;
            s_idleTicks = 0;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        ConfigureGraphicsQuality(e.Graphics);
        RenderedActiveBounds = Rectangle.Empty;
        RenderedPeakBounds = Rectangle.Empty;
        var plot = PlotBounds;
        var innerX = plot.X;
        var innerW = plot.Width;
        var topInner = plot.Top;
        var bottomInner = plot.Bottom;

        if (innerW <= 0 || bottomInner <= topInner)
        {
            DrawMeterBackground(e.Graphics, bounds);
            return;
        }

        var innerH = Math.Max(1, bottomInner - topInner);

        float range = Math.Max(1, Maximum - Minimum);
        var fillPercent = Clamp01((_displayValue - Minimum) / range);

        var filledH = (int)Math.Round(innerH * fillPercent);

        var mode = GetVisualizationModeCached();
        if (mode != VisualizationMode.Spectrum)
            UpdateLitBricks(bounds, bottomInner, innerH, filledH);

        var activeTop = GetActiveTop(mode, plot, filledH);
        var cached = (mode == VisualizationMode.Spectrum || mode == VisualizationMode.Bricks ||
            mode == VisualizationMode.Led) && DrawCachedMeter(e.Graphics, bounds, mode);
        GraphicsState levelClip;
        if (!cached)
        {
            DrawMeterBackground(e.Graphics, bounds);
            levelClip = e.Graphics.Save();
            e.Graphics.SetClip(plot, CombineMode.Intersect);
            switch (mode)
            {
            case VisualizationMode.Bricks:
                DrawMode_Bricks(e.Graphics, bounds);
                break;
            case VisualizationMode.Led:
                DrawMode_Led(e.Graphics, bounds);
                break;
            case VisualizationMode.Dots:
                DrawMode_Dots(e.Graphics, bounds);
                break;
            case VisualizationMode.Center:
                DrawMode_CenterBricks(e.Graphics, bounds, topInner, bottomInner, innerH, fillPercent);
                break;
            case VisualizationMode.Mirror:
                DrawMode_MirrorBricks(e.Graphics, bounds, topInner, bottomInner, innerH, fillPercent);
                break;
            case VisualizationMode.Wave:
                DrawMode_Wave(e.Graphics, innerX, innerW, topInner, bottomInner, innerH, fillPercent);
                break;
            case VisualizationMode.Lollipop:
                DrawMode_Lollipop(e.Graphics, innerX, innerW, topInner, bottomInner, innerH, fillPercent);
                break;
            default:
                DrawMode_Spectrum(e.Graphics, bounds);
                break;
            }
            e.Graphics.Restore(levelClip);
        }

        // cached bodies already respect the plot clip, so the usual 83-band path skips
            // saving graphics state unless an effect or antialiased edge needs containment.
        var clipEffects = VisualStyle is not VisualStyle.None and not VisualStyle.Pulse || HighQualityRendering;
        levelClip = clipEffects ? e.Graphics.Save() : null;
        if (clipEffects) e.Graphics.SetClip(plot, CombineMode.Intersect);
        if (VisualStyle == VisualStyle.Glow)
            DrawStyleGlow(e.Graphics, innerX, innerW, topInner, bottomInner, filledH, fillPercent);
        else if (VisualStyle == VisualStyle.Trail)
            DrawStyleTrail(e.Graphics, innerX, innerW, topInner, bottomInner, innerH, range);
        else if (VisualStyle == VisualStyle.Scanline)
            DrawStyleScanline(e.Graphics, innerX, innerW, topInner, innerH, fillPercent);
        else if (VisualStyle == VisualStyle.Precision)
            DrawPrecisionGrid(e.Graphics, innerX, innerW, topInner, bottomInner, innerH);

        var topEdge = mode == VisualizationMode.Center || mode == VisualizationMode.Mirror
            ? topInner + (innerH / 2)
            : activeTop;
        if (mode != VisualizationMode.Led && mode != VisualizationMode.Dots && mode != VisualizationMode.Lollipop &&
            filledH > 1 && topEdge >= topInner && topEdge <= bottomInner)
        {
            e.Graphics.DrawLine(_highlightPen, innerX, topEdge, innerX + innerW - 1, topEdge);
            if (HighQualityRendering)
                activeTop = Math.Max(topInner, Math.Min(activeTop, topEdge - 1));
        }
        activeTop = GetPaintedActiveTop(mode, plot, filledH, fillPercent, range);
        if (activeTop < bottomInner)
            RenderedActiveBounds = Rectangle.FromLTRB(innerX, activeTop, innerX + innerW, bottomInner);
        if (clipEffects) e.Graphics.Restore(levelClip);

        // peaks go last so styles and edge highlights can't hide them
        // at the floor the marker is hidden, otherwise silence shows a row of markers
        if (PeakHoldEnabled && ModeHasPeakMarker(mode) &&
            (_peakValue > Minimum + 0.5f || _visualPeakValid && _visualPeakLevel > Minimum + 0.5f))
        {
            var peakPercent = Clamp01((Clamp(_peakValue, Minimum, Maximum) - Minimum) / range);
            _peakBrush.Color = GetPeakColor(peakPercent);
            _peakOutlinePen.Color = BackColor;

            if (mode == VisualizationMode.Dots)
                DrawPeakMarker_Dot(e.Graphics, bounds, innerX, innerW, topInner, bottomInner, innerH, range);
            else if (mode == VisualizationMode.Lollipop)
                DrawPeakMarker_Lollipop(e.Graphics, innerX, innerW, topInner, bottomInner, innerH, range);
            else
                DrawPeakMarker_Line(e.Graphics, innerX, innerW, topInner, bottomInner, innerH, range);
        }
    }

    private void DrawMeterBackground(Graphics graphics, Rectangle bounds)
    {
        graphics.FillRectangle(_backgroundBrush, bounds);
        graphics.DrawRectangle(_borderPen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        if (bounds.Width > 3 && bounds.Height > 3)
            graphics.DrawRectangle(_innerEdgePen, bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
        var plot = PlotBounds;
        if (plot.Width > 0 && plot.Height > 0)
            DrawGridlines(graphics, plot.X, plot.Width, plot.Top, plot.Bottom, plot.Height);
    }

    private bool DrawCachedMeter(Graphics graphics, Rectangle bounds, VisualizationMode mode)
    {
        // cap optional cache memory even if someone sizes the control absurdly
        if ((long)bounds.Width * bounds.Height > 1024 * 1024) return false;
        var plot = PlotBounds;
        if (_surfacePlotBounds != plot)
        {
            _surfacePlotBounds = plot;
            _meterSurfacesDirty = true;
        }
        if (mode != VisualizationMode.Spectrum)
        {
            EnsureBrickGeometry(bounds);
            if (_colorsDirty)
            {
                RebuildBrickHeatColors();
                _meterSurfacesDirty = true;
            }
        }
        var levels = GridlineLevels;
        var count = levels?.Count ?? 0;
        if (_surfaceGridlines.Length != count)
        {
            _surfaceGridlines = new float[count];
            _meterSurfacesDirty = true;
        }
        for (var index = 0; index < count; index++)
        {
            if (_surfaceGridlines[index].Equals(levels[index])) continue;
            _surfaceGridlines[index] = levels[index];
            _meterSurfacesDirty = true;
        }
        var verticals = VerticalGridlinePositions;
        count = verticals?.Count ?? 0;
        if (_surfaceVerticalGridlines.Length != count)
        {
            _surfaceVerticalGridlines = new float[count];
            _meterSurfacesDirty = true;
        }
        for (var index = 0; index < count; index++)
        {
            if (_surfaceVerticalGridlines[index].Equals(verticals[index])) continue;
            _surfaceVerticalGridlines[index] = verticals[index];
            _meterSurfacesDirty = true;
        }
        if (_inactiveMeterSurface == null || _inactiveMeterSurface.Size != bounds.Size ||
            _meterSurfacesMode != mode || _meterSurfacesHighQuality != HighQualityRendering ||
            _meterSurfacesDirty)
        {
            _inactiveMeterSurface?.Dispose();
            _activeMeterSurface?.Dispose();
            _activeMeterSurface = null;
            _inactiveMeterSurface = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            var lit = _litBricks;
            try
            {
                PaintMeterSurface(_inactiveMeterSurface, bounds, mode, 0);
                if (mode != VisualizationMode.Spectrum)
                {
                    _activeMeterSurface = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                    PaintMeterSurface(_activeMeterSurface, bounds, mode, _brickRects.Count);
                }
            }
            finally { _litBricks = lit; }
            _meterSurfacesMode = mode;
            _meterSurfacesHighQuality = HighQualityRendering;
            _meterSurfacesDirty = false;
        }
        graphics.DrawImageUnscaled(_inactiveMeterSurface, 0, 0);
        if (mode == VisualizationMode.Spectrum)
            DrawMode_Spectrum(graphics, bounds);
        else if (_litBricks > 0)
        {
            var top = Math.Max(plot.Top, _brickRects[Math.Min(_litBricks, _brickRects.Count) - 1].Top -
                (mode == VisualizationMode.Led && HighQualityRendering ? 1 : 0));
            var activeBounds = new Rectangle(0, top, bounds.Width, bounds.Height - top);
            graphics.DrawImage(_activeMeterSurface, activeBounds, activeBounds, GraphicsUnit.Pixel);
        }
        return true;
    }

    private void PaintMeterSurface(Bitmap surface, Rectangle bounds, VisualizationMode mode, int lit)
    {
        using var graphics = Graphics.FromImage(surface);
        ConfigureGraphicsQuality(graphics);
        DrawMeterBackground(graphics, bounds);
        graphics.SetClip(PlotBounds, CombineMode.Intersect);
        _litBricks = lit;
        if (mode == VisualizationMode.Bricks) DrawMode_Bricks(graphics, bounds);
        else if (mode == VisualizationMode.Led) DrawMode_Led(graphics, bounds);
    }

    private void ConfigureGraphicsQuality(Graphics g)
    {
        var smoothing = HighQualityRendering ? SmoothingMode.AntiAlias : SmoothingMode.None;
        var pixelOffset = HighQualityRendering ? PixelOffsetMode.HighQuality : PixelOffsetMode.None;
        var compositing = HighQualityRendering ? CompositingQuality.HighQuality : CompositingQuality.HighSpeed;
        var interpolation = HighQualityRendering ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        // the quality setters validate and box enums, so skip them when the context is already set up
        if (g.SmoothingMode != smoothing) g.SmoothingMode = smoothing;
        if (g.PixelOffsetMode != pixelOffset) g.PixelOffsetMode = pixelOffset;
        if (g.CompositingQuality != compositing) g.CompositingQuality = compositing;
        if (g.InterpolationMode != interpolation) g.InterpolationMode = interpolation;
    }

    private void DrawMode_Bricks(Graphics g, Rectangle bounds)
    {
        EnsureBrickGeometry(bounds);
        if (_colorsDirty) RebuildBrickHeatColors();

        for (var i = 0; i < _brickRects.Count; i++)
        {
            var brickRect = _brickRects[i];
            var isActive = i < _litBricks;

            if (isActive)
            {
                _workBrush.Color = HeatmapEnabled
                    ? _brickHeatColors[i]
                    : (ForeColor.IsEmpty ? Color.LimeGreen : ForeColor);

                g.FillRectangle(_workBrush, brickRect);

                if (brickRect.Height >= 3)
                    g.FillRectangle(_brickShadeBrush, new Rectangle(brickRect.X, brickRect.Y, brickRect.Width, 2));

                if (BrickHighlight && _brickHighlightBrush != null && brickRect.Height >= 5)
                    g.FillRectangle(_brickHighlightBrush, brickRect.X + 1, brickRect.Y + 3, Math.Max(1, brickRect.Width - 2), 1);
            }
            else
            {
                _workBrush.Color = _inactiveBrickColor;
                g.FillRectangle(_workBrush, brickRect);
            }
        }
    }

    private void DrawMode_Dots(Graphics g, Rectangle bounds)
    {
        EnsureBrickGeometry(bounds);
        if (_colorsDirty) RebuildBrickHeatColors();

        for (var i = 0; i < _brickRects.Count; i++)
        {
            var rect = _brickRects[i];
            var isActive = i < _litBricks;

            _workBrush.Color = isActive
                ? (HeatmapEnabled ? _brickHeatColors[i] : (ForeColor.IsEmpty ? Color.LimeGreen : ForeColor))
                : _inactiveDotColor;

            var dotSize = Math.Min(Math.Min(rect.Width, rect.Height), GetDotSizeFromBrick(rect) + (isActive ? 1 : 0));
            var x = rect.X + ((rect.Width - dotSize) / 2);
            var y = rect.Y + ((rect.Height - dotSize) / 2);
            g.FillEllipse(_workBrush, x, y, dotSize, dotSize);
        }
    }

    private void DrawMode_Led(Graphics g, Rectangle bounds)
    {
        EnsureBrickGeometry(bounds);
        if (_colorsDirty) RebuildBrickHeatColors();

        for (var i = 0; i < _brickRects.Count; i++)
        {
            var rect = _brickRects[i];
            var cellSize = Math.Min(rect.Width, rect.Height);
            var x = rect.X + ((rect.Width - cellSize) / 2);
            var y = rect.Y + ((rect.Height - cellSize) / 2);

            _workBrush.Color = i < _litBricks
                ? (HeatmapEnabled ? _brickHeatColors[i] : (ForeColor.IsEmpty ? Color.LimeGreen : ForeColor))
                : _inactiveDotColor;

            g.FillEllipse(_workBrush, x, y, cellSize, cellSize);
        }
    }

    private static int GetDotSizeFromBrick(Rectangle brickRect)
    {
        var dotSize = Math.Min(brickRect.Width, brickRect.Height) - 1;
        return dotSize < 1 ? 1 : dotSize;
    }

    private int GetActiveTop(VisualizationMode mode, Rectangle plot, int filledH)
    {
        if (mode is VisualizationMode.Bricks or VisualizationMode.Led or VisualizationMode.Dots)
        {
            if (_litBricks <= 0 || _brickRects.Count == 0) return plot.Bottom;
            var rect = _brickRects[Math.Min(_litBricks, _brickRects.Count) - 1];
            if (mode == VisualizationMode.Bricks) return rect.Top;
            var size = mode == VisualizationMode.Led ? Math.Min(rect.Width, rect.Height) :
                Math.Min(Math.Min(rect.Width, rect.Height), GetDotSizeFromBrick(rect) + 1);
            return Math.Max(plot.Top, rect.Y + (rect.Height - size) / 2 - (HighQualityRendering ? 1 : 0));
        }
        if (filledH <= 0) return plot.Bottom;
        if (mode == VisualizationMode.Wave) return Math.Max(plot.Top, plot.Bottom - filledH - 2);
        if (mode == VisualizationMode.Lollipop)
            return Math.Max(plot.Top, plot.Bottom - filledH - Math.Min(Math.Min(plot.Width, plot.Height + 1), 9) / 2 -
                (HighQualityRendering ? 1 : 0));
        return plot.Bottom - filledH;
    }

    private int GetStyledActiveTop(int activeTop, Rectangle plot, int filledH, float fillPercent, float range)
    {
        if (VisualStyle == VisualStyle.Glow && filledH > 0)
            activeTop = Math.Min(activeTop, Math.Max(plot.Top, plot.Bottom - filledH - (HighQualityRendering ? 1 : 0)));
        else if (VisualStyle == VisualStyle.Trail)
        {
            for (var i = 0; i < _trailLife.Length; i++)
            {
                if (_trailLife[i] <= 0 || _trailLevels[i] <= Minimum + 0.5f) continue;
                var y = plot.Bottom - (int)Math.Round(Clamp01((_trailLevels[i] - Minimum) / range) * plot.Height);
                if (y < plot.Bottom) activeTop = Math.Min(activeTop, Math.Max(plot.Top, y));
            }
        }
        else if (VisualStyle == VisualStyle.Scanline)
        {
            var y = plot.Top + (int)Math.Round((1f - fillPercent) * Math.Max(0, plot.Height - 1));
            activeTop = Math.Min(activeTop, Math.Max(plot.Top, y - (HighQualityRendering ? 1 : 0)));
        }
        return activeTop;
    }

    private int GetPaintedActiveTop(VisualizationMode mode, Rectangle plot, int filledH, float fillPercent, float range)
    {
        var activeTop = GetActiveTop(mode, plot, filledH);
        var topEdge = mode is VisualizationMode.Center or VisualizationMode.Mirror
            ? plot.Top + plot.Height / 2 : activeTop;
        if (mode is not VisualizationMode.Led and not VisualizationMode.Dots and not VisualizationMode.Lollipop &&
            filledH > 1 && topEdge >= plot.Top && topEdge <= plot.Bottom && HighQualityRendering)
            activeTop = Math.Max(plot.Top, Math.Min(activeTop, topEdge - 1));
        return GetStyledActiveTop(activeTop, plot, filledH, fillPercent, range);
    }

    private int ConstrainVisualPeak(VisualizationMode mode, Rectangle plot, int activeTop, float range)
    {
        var peakPercent = Clamp01((Clamp(_peakValue, Minimum, Maximum) - Minimum) / range);
        var desiredAnchor = Math.Min(plot.Bottom - (int)Math.Round(plot.Height * peakPercent) +
            GetSegmentedPeakOffset(mode, plot), activeTop);
        var desiredLevel = Minimum + (plot.Bottom - desiredAnchor) * range / plot.Height;
        if (!_visualPeakValid)
        {
            _visualPeakLevel = desiredLevel;
            _visualPeakValid = true;
        }
        // resolve constraints before comparing render keys and before painting; once both levels
        // hit the same pixel there's nothing left to draw, so painting isn't needed to settle
        _visualPeakLevel = Math.Max(_visualPeakLevel, desiredLevel);
        var anchor = Math.Min(desiredAnchor,
            plot.Bottom - (int)Math.Round(plot.Height * Clamp01((_visualPeakLevel - Minimum) / range)));
        _visualPeakReleasing = anchor < desiredAnchor;
        if (!_visualPeakReleasing) _visualPeakLevel = desiredLevel;
        return anchor;
    }

    private int GetSegmentedPeakOffset(VisualizationMode mode, Rectangle plot)
    {
        if (mode is not VisualizationMode.Bricks and not VisualizationMode.Led) return 0;
        EnsureBrickGeometry(ClientRectangle);
        if (_brickRects.Count == 0) return 0;
        var highest = _brickRects[_brickRects.Count - 1];
        var top = highest.Top;
        if (mode == VisualizationMode.Led)
            top += (highest.Height - Math.Min(highest.Width, highest.Height)) / 2;
        if (HighQualityRendering) top--;
        // shift only peak pixels, not measured levels: a segmented fill may stop short of 0 dB
        // because a whole cell has to fit, and the fixed offset keeps the decay smooth
        return Math.Max(plot.Top, top) - plot.Top;
    }

    private void DrawMode_CenterBricks(Graphics g, Rectangle bounds, int topInner, int bottomInner, int innerH, float fillPercent)
    {
        EnsureBrickGeometry(bounds);
        if (_colorsDirty) RebuildBrickHeatColors();

        var centerY = topInner + (innerH / 2);
        var halfFillH = (int)Math.Round(innerH * fillPercent / 2f);

        var activeTop = centerY - halfFillH;
        var activeBottom = centerY + halfFillH;

        for (var i = 0; i < _brickRects.Count; i++)
        {
            var brickRect = _brickRects[i];
            var cy = brickRect.Top + (brickRect.Height / 2);
            var isActive = cy >= activeTop && cy <= activeBottom;

            if (isActive)
            {
                _workBrush.Color = HeatmapEnabled
                    ? _brickHeatColors[i]
                    : (ForeColor.IsEmpty ? Color.LimeGreen : ForeColor);

                g.FillRectangle(_workBrush, brickRect);

                if (brickRect.Height >= 3)
                    g.FillRectangle(_brickShadeBrush, new Rectangle(brickRect.X, brickRect.Y, brickRect.Width, 2));

                if (BrickHighlight && _brickHighlightBrush != null && brickRect.Height >= 5)
                    g.FillRectangle(_brickHighlightBrush, brickRect.X + 1, brickRect.Y + 3, Math.Max(1, brickRect.Width - 2), 1);
            }
            else
            {
                _workBrush.Color = _inactiveBrickColor;
                g.FillRectangle(_workBrush, brickRect);
            }
        }
    }

    private void DrawMode_MirrorBricks(Graphics g, Rectangle bounds, int topInner, int bottomInner, int innerH, float fillPercent)
    {
        EnsureBrickGeometry(bounds);
        if (_colorsDirty) RebuildBrickHeatColors();

        var halfFillH = (int)Math.Round(innerH * fillPercent / 2f);

        var topZoneBottom = topInner + halfFillH;
        var bottomZoneTop = bottomInner - halfFillH;

        for (var i = 0; i < _brickRects.Count; i++)
        {
            var brickRect = _brickRects[i];
            var cy = brickRect.Top + (brickRect.Height / 2);

            var isActive = (halfFillH > 0) && (cy <= topZoneBottom || cy >= bottomZoneTop);

            if (isActive)
            {
                _workBrush.Color = HeatmapEnabled
                    ? _brickHeatColors[i]
                    : (ForeColor.IsEmpty ? Color.LimeGreen : ForeColor);

                g.FillRectangle(_workBrush, brickRect);

                if (brickRect.Height >= 3)
                    g.FillRectangle(_brickShadeBrush, new Rectangle(brickRect.X, brickRect.Y, brickRect.Width, 2));

                if (BrickHighlight && _brickHighlightBrush != null && brickRect.Height >= 5)
                    g.FillRectangle(_brickHighlightBrush, brickRect.X + 1, brickRect.Y + 3, Math.Max(1, brickRect.Width - 2), 1);
            }
            else
            {
                _workBrush.Color = _inactiveBrickColor;
                g.FillRectangle(_workBrush, brickRect);
            }
        }
    }

    private void DrawMode_Wave(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int innerH, float fillPercent)
    {
        _workBrush.Color = _waveBackgroundColor;
        g.FillRectangle(_workBrush, new Rectangle(innerX, topInner, innerW, innerH));

        var filledH = (int)Math.Round(innerH * fillPercent);
        if (filledH <= 0) return;

        var fillRect = new Rectangle(innerX, bottomInner - filledH, innerW, filledH);

        var baseColor = GetLevelColor(fillPercent);
        _workBrush.Color = Color.FromArgb(120, baseColor.R, baseColor.G, baseColor.B);
        g.FillRectangle(_workBrush, fillRect);

        var amp = Math.Max(1f, innerW * 0.22f * (0.25f + (0.75f * fillPercent)));
        var phase = fillPercent * (float)Math.PI;

        var points = Math.Min(_wavePoints.Length, Math.Max(12, fillRect.Height / 4));
        var midX = innerX + (innerW / 2);

        var stepY = points <= 1 ? 1 : (float)fillRect.Height / (points - 1);
        for (var i = 0; i < points; i++)
        {
            var y = fillRect.Y + (int)Math.Round(i * stepY);
            var yy01 = fillRect.Height <= 1 ? 0f : (float)i / (points - 1);

            var localAmp = amp * (0.35f + (0.65f * (1f - yy01)));

            var x = midX + (int)Math.Round((float)Math.Sin(phase + (yy01 * 6.0f)) * localAmp);
            if (x < innerX) x = innerX;
            if (x > innerX + innerW - 1) x = innerX + innerW - 1;

            _wavePoints[i] = new Point(x, y);
        }

        EnsureWavePenUpToDate(fillPercent, 2.2f);

        for (var i = 1; i < points; i++)
            g.DrawLine(_wavePen, _wavePoints[i - 1], _wavePoints[i]);
    }

    private void EnsureWavePenUpToDate(float level01, float width)
    {
        var c = GetLevelColor(level01);

        if (_cachedWaveColor != c || Math.Abs(_cachedWaveWidth - width) > 0.001f)
        {
            _cachedWaveColor = c;
            _cachedWaveWidth = width;
            _wavePen.Color = c;
            _wavePen.Width = width;

            _wavePen.StartCap = LineCap.Square;
            _wavePen.EndCap = LineCap.Square;
            _wavePen.LineJoin = LineJoin.Miter;
        }
    }

    private void DrawStyleGlow(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int filledH, float fillPercent)
    {
        if (filledH <= 0)
            return;

        var fillTop = Math.Max(topInner, bottomInner - filledH);
        var fillHeight = bottomInner - fillTop;
        if (fillHeight <= 0)
            return;

        // emphasis stays on the active edge so Glow doesn't turn into a second filled bar
        var color = GetLevelColor(fillPercent);
        _glowOuterBrush.Color = Color.FromArgb(36, color);
        _glowMiddleBrush.Color = Color.FromArgb(76, color);
        _glowCoreBrush.Color = Color.FromArgb(180, color);

        g.FillRectangle(_glowOuterBrush, innerX, fillTop, innerW, Math.Min(3, fillHeight));
        if (fillHeight > 1)
            g.FillRectangle(_glowMiddleBrush, innerX + 1, fillTop, Math.Max(1, innerW - 2), Math.Min(2, fillHeight));
        g.FillRectangle(_glowCoreBrush, innerX + 2, fillTop, Math.Max(1, innerW - 4), 1);
    }

    private void DrawMode_Lollipop(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int innerH, float fillPercent)
    {
        var levelY = bottomInner - (int)Math.Round(innerH * fillPercent);
        levelY = Clamp(levelY, topInner, bottomInner);
        var centerX = innerX + (innerW / 2);
        var stemWidth = Math.Min(innerW, 2);
        var stemX = centerX - (stemWidth / 2);
        var color = GetLevelColor(fillPercent);

        _workBrush.Color = Color.FromArgb(78, color);
        g.FillRectangle(_workBrush, stemX, levelY, stemWidth, Math.Max(1, bottomInner - levelY + 1));

        _workBrush.Color = Color.FromArgb(230, color);
        var markerSize = Math.Min(Math.Min(innerW, innerH + 1), 9);
        markerSize = Math.Max(1, markerSize);
        var markerX = centerX - (markerSize / 2);
        var markerY = Clamp(levelY - (markerSize / 2), topInner, bottomInner - markerSize + 1);
        g.FillEllipse(_workBrush, markerX, markerY, markerSize, markerSize);
    }

    private void DrawMode_Spectrum(Graphics g, Rectangle bounds)
    {
        var plot = PlotBounds;
        var innerX = plot.X;
        var innerW = plot.Width;
        var topInner = plot.Top;
        var bottomInner = plot.Bottom;
        if (innerW <= 0 || bottomInner <= topInner)
            return;

        var innerH = Math.Max(1, bottomInner - topInner);
        var fillPercent = Clamp01((_displayValue - Minimum) / Math.Max(1, Maximum - Minimum));
        var filledH = (int)Math.Round(innerH * fillPercent);
        if (filledH <= 0)
            return;

        var fillTop = bottomInner - filledH;
        _workBrush.Color = GetLevelColor(fillPercent);
        g.FillRectangle(_workBrush, innerX, fillTop, innerW, filledH);
    }

    private void DrawGridlines(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int innerH)
    {
        var levels = GridlineLevels;
        for (var i = 0; i < (levels?.Count ?? 0); i++)
        {
            if (float.IsNaN(levels[i]) || float.IsInfinity(levels[i])) continue;
            var t = Clamp01(levels[i]);
            var y = bottomInner - (int)Math.Round(t * innerH);
            if (y < topInner || y > bottomInner) continue;

            g.DrawLine(_gridlinePen, innerX, y, innerX + innerW, y);
        }
        var positions = VerticalGridlinePositions;
        for (var i = 0; i < (positions?.Count ?? 0); i++)
        {
            var x = positions[i];
            if (float.IsNaN(x) || float.IsInfinity(x) || x < 0 || x > ClientSize.Width) continue;
            x = Math.Min(x, ClientSize.Width - 1);
            g.DrawLine(_gridlinePen, x, topInner, x, bottomInner);
        }
    }

    private void DrawStyleTrail(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int innerH, float range)
    {
        for (var i = 0; i < _trailLife.Length; i++)
        {
            if (_trailLife[i] <= 0 || _trailLevels[i] <= Minimum + 0.5f) continue;
            var percent = Clamp01((_trailLevels[i] - Minimum) / range);
            var y = bottomInner - (int)Math.Round(percent * innerH);
            if (y < topInner || y >= bottomInner) continue;
            _workBrush.Color = Color.FromArgb((int)(100 * _trailLife[i] / 0.4f), GetLevelColor(percent));
            g.FillRectangle(_workBrush, innerX, y, innerW, 1);
        }
    }

    private void DrawStyleScanline(Graphics g, int innerX, int innerW, int topInner, int innerH, float fillPercent)
    {
        var y = topInner + (int)Math.Round((1f - fillPercent) * Math.Max(0, innerH - 1));
        using var pen = new Pen(Color.FromArgb(80, HeatHighColor), 1f);
        g.DrawLine(pen, innerX, y, innerX + innerW - 1, y);
    }

    private void DrawPrecisionGrid(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int innerH)
    {
        var levels = GridlineLevels;
        if (levels == null)
            return;

        using var pen = new Pen(Color.FromArgb(160, _gridlinePen.Color), 1f);
        foreach (var level in levels)
        {
            var y = bottomInner - (int)Math.Round(Clamp01(level) * innerH);
            if (y >= topInner && y <= bottomInner)
                g.DrawLine(pen, innerX, y, innerX + innerW, y);
        }
    }

    // lighten the fill color at the peak so the marker pops against the fill below
    private Color GetPeakColor(float peakPercent)
    {
        if (!PeakColorMatchesHeat) return PeakLineColor;

        var heat = GetLevelColor(peakPercent);
        return LerpRgb(heat, Color.White, 0.25f);
    }

    private void DrawPeakMarker_Line(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int innerH, float range)
    {
        var activeTop = RenderedActiveBounds.IsEmpty ? bottomInner : RenderedActiveBounds.Top;
        var thickness = MarkerThickness;
        // clearance can jump up instantly but releases at the same elapsed-time fall rate,
        // so expiring trails or switching cells can't make the line drop suddenly
        var anchor = ConstrainVisualPeak(GetVisualizationModeCached(), PlotBounds, activeTop, range);
        var bottom = anchor - PeakGapPixels;
        var top = bottom - thickness;
        if (innerW <= 0 || top < BrickPadding || bottom > bottomInner) return;
        var peakRect = new Rectangle(innerX, top, innerW, thickness);
        RenderedPeakBounds = peakRect;
        // whole-pixel fills keep the marker bounds exact even with the high-quality styles
        var smoothing = g.SmoothingMode;
        var offset = g.PixelOffsetMode;
        if (smoothing != SmoothingMode.None) g.SmoothingMode = SmoothingMode.None;
        if (offset != PixelOffsetMode.None) g.PixelOffsetMode = PixelOffsetMode.None;
        g.FillRectangle(_peakBrush, peakRect);
        if (smoothing != SmoothingMode.None) g.SmoothingMode = smoothing;
        if (offset != PixelOffsetMode.None) g.PixelOffsetMode = offset;
    }

    private void DrawPeakMarker_Dot(Graphics g, Rectangle bounds, int innerX, int innerW, int topInner, int bottomInner, int innerH, float range)
    {
        EnsureBrickGeometry(bounds);
        if (_brickRects.Count == 0) return;

        var pv = Clamp(_peakValue, Minimum, Maximum);
        var peakPercent = Clamp01((pv - Minimum) / range);
        var peakY = bottomInner - (int)Math.Round(innerH * peakPercent);

        var dotSize = GetDotSizeFromBrick(_brickRects[0]);
        var x = innerX + ((innerW - dotSize) / 2);
        var activeTop = RenderedActiveBounds.IsEmpty ? bottomInner : RenderedActiveBounds.Top;
        var fringe = HighQualityRendering ? 1 : 0;
        var y = Math.Min(peakY, activeTop) - PeakGapPixels - dotSize - fringe;
        if (y - fringe < BrickPadding) return;
        RenderedPeakBounds = new Rectangle(x - fringe, y - fringe, dotSize + 2 * fringe, dotSize + 2 * fringe);
        g.FillEllipse(_peakBrush, x, y, dotSize, dotSize);
        if (dotSize >= 4)
            g.DrawEllipse(_peakOutlinePen, x, y, dotSize - 1, dotSize - 1);
    }

    private void DrawPeakMarker_Lollipop(Graphics g, int innerX, int innerW, int topInner, int bottomInner, int innerH, float range)
    {
        var pv = Clamp(_peakValue, Minimum, Maximum);
        var peakPercent = Clamp01((pv - Minimum) / range);
        var peakY = bottomInner - (int)Math.Round(innerH * peakPercent);
        peakY = Clamp(peakY, topInner, bottomInner);

        var tickWidth = Math.Min(innerW, 8);
        var tickHeight = MarkerThickness;
        var tickX = innerX + ((innerW - tickWidth) / 2);
        var activeTop = RenderedActiveBounds.IsEmpty ? bottomInner : RenderedActiveBounds.Top;
        var tickY = Math.Min(peakY, activeTop) - PeakGapPixels - tickHeight;
        if (tickY < BrickPadding) return;
        RenderedPeakBounds = new Rectangle(tickX, tickY, tickWidth, tickHeight);
        g.FillRectangle(_peakBrush, tickX, tickY, tickWidth, tickHeight);
    }

    private void EnsureBrickGeometry(Rectangle bounds)
    {
        var plot = PlotBounds;
        if (bounds.Width == _cachedWidth && bounds.Height == _cachedHeight && plot == _cachedPlotBounds)
            return;

        _cachedWidth = bounds.Width;
        _cachedHeight = bounds.Height;
        _cachedPlotBounds = plot;
        _meterSurfacesDirty = true;
        _litBricksValid = false; // geometry changed, so the hysteresis state is stale

        _brickRects.Clear();
        _brickCentres.Clear();
        _brickT.Clear();
        _brickHeatColors.Clear();

        var innerX = plot.X;
        var innerW = plot.Width;
        var top = plot.Top;
        var bottom = plot.Bottom;

        if (innerW <= 0 || bottom <= top) return;

        var brickHeight = Math.Max(1, BrickHeight);
        var gap = Math.Max(0, BrickGap);

        var y = (long)bottom - brickHeight;
        var innerH = Math.Max(1f, bottom - top);

        while (y >= top)
        {
            var rect = new Rectangle(innerX, (int)y, innerW, brickHeight);
            _brickRects.Add(rect);
            _brickCentres.Add(rect.Top + (rect.Height / 2));

            var centerY = rect.Top + (rect.Height * 0.5f);
            var t = (bottom - centerY) / innerH; // 0 at the bottom, 1 at the top
            _brickT.Add(Clamp01(t));

            y -= (long)brickHeight + gap;
        }

        for (var i = 0; i < _brickRects.Count; i++)
            _brickHeatColors.Add(Color.Empty);

        _colorsDirty = true;
    }

    private void InvalidateBrickGeometry()
    {
        _meterSurfacesDirty = true;
        _cachedWidth = _cachedHeight = -1;
        _litBricksValid = false;
        _colorsDirty = true;
        _lastDisplayQ = _lastPeakQ = int.MinValue;
        Invalidate();
    }

    private void RebuildBrickHeatColors()
    {
        _colorsDirty = false;

        if (!HeatmapEnabled || _brickRects.Count == 0)
            return;

        var curve = Math.Max(0.15f, HeatIntensityCurve);

        for (var i = 0; i < _brickRects.Count; i++)
        {
            var t = _brickT[i];

            t = (float)Math.Pow(t, 1.0f / curve);
            var c = HeatmapColorHsv(t, HeatLowColor, HeatMidColor, HeatHighColor, HeatPeakColor);

            if (TopEmphasisEnabled && t >= TopEmphasisStart)
            {
                var u = (t - TopEmphasisStart) / Math.Max(0.0001f, 1f - TopEmphasisStart);
                var strength = TopEmphasisStrength * (0.25f + (0.75f * u));
                c = LerpRgb(c, HeatPeakColor, strength);
            }

            _brickHeatColors[i] = c;
        }
    }

    private void BuildBrickHighlightBrush()
    {
        _meterSurfacesDirty = true;
        _brickHighlightBrush?.Dispose();
        if (!BrickHighlight || HighlightAlpha <= 0)
        {
            _brickHighlightBrush = null;
            return;
        }
        _brickHighlightBrush = new SolidBrush(Color.FromArgb(HighlightAlpha, 255, 255, 255));
    }

    private Color GetLevelColor(float level01)
    {
        // same intensity curve as the fill, so markers and Wave colors match the heat-map at that height
        var t = level01;
        if (HeatmapEnabled)
        {
            var curve = Math.Max(0.15f, HeatIntensityCurve);
            t = (float)Math.Pow(Clamp01(level01), 1.0f / curve);
        }

        var c = HeatmapEnabled
            ? HeatmapColorHsv(t, HeatLowColor, HeatMidColor, HeatHighColor, HeatPeakColor)
            : (ForeColor.IsEmpty ? Color.LimeGreen : ForeColor);

        if (HeatmapEnabled && TopEmphasisEnabled && t >= TopEmphasisStart)
        {
            var u = (t - TopEmphasisStart) / Math.Max(0.0001f, 1f - TopEmphasisStart);
            var strength = TopEmphasisStrength * (0.25f + (0.75f * u));
            c = LerpRgb(c, HeatPeakColor, strength);
        }

        if (VisualStyle == VisualStyle.Pulse)
        {
            c = LerpRgb(c, Color.White, 0.08f + (0.20f * Clamp01(level01)));
        }

        return c;
    }

    // bricks are ordered bottom-up; safe to call from the animation tick or OnPaint
    private void UpdateLitBricks(Rectangle bounds, int bottomInner, int innerH, int filledH)
    {
        EnsureBrickGeometry(bounds);
        var topLimit = bottomInner - filledH;

        _litBricks = _litBricksValid
            ? BarBallistics.StepLitBricks(_litBricks, _brickCentres, topLimit, BrickHysteresisPx)
            : BarBallistics.StepLitBricks(0, _brickCentres, topLimit, 0);
        _litBricksValid = true;
    }

    // stop the timer once bar and peak have settled
    private bool IsSettled()
        // a held peak on a stable bar doesn't animate; keep its hold time for the next drop
        => _displayValue == _targetValue && _peakValue <= _displayValue &&
            !_trailActive && !_visualPeakReleasing;

    private void AnimateStep(float dtSeconds, float intervalMs)
    {
        var mode    = GetVisualizationModeCached();
        var target  = (float)_targetValue;
        if (_visualPeakValid)
            _visualPeakLevel = Math.Max(Minimum,
                _visualPeakLevel - Math.Max(0.01f, PeakDecayPerTick) * 60f * dtSeconds);

        var releaseMs = UseAsymmetricBallistics && ReleaseTimeMs > 0
            ? ReleaseTimeMs
            : ResponseTimeMs;

        var previousDisplay = _displayValue;
        _displayValue = BarBallistics.StepLevel(
            _displayValue, target, dtSeconds, Maximum - Minimum, ResponseTimeMs, releaseMs, ModeSnapsDown(mode));
        UpdateTrail(dtSeconds, previousDisplay);

        if (target <= Minimum && _displayValue <= (Minimum + SilentSnapEpsilon))
            _displayValue = Minimum;

        if (!ModeHasPeakMarker(mode))
        {
            _peakValue = _displayValue;
            _peakHoldLeftMs = 0;
            _visualPeakValid = _visualPeakReleasing = false;
        }
        else
        {
            UpdatePeakHold(intervalMs);

            if (_targetValue <= Minimum && _displayValue <= Minimum + SilentSnapEpsilon && _peakValue <= Minimum + 0.25f)
            {
                _peakValue = Minimum;
                _peakHoldLeftMs = 0;
            }
        }
        if (mode is VisualizationMode.Dots or VisualizationMode.Lollipop ||
            _peakValue <= Minimum + 0.5f && _visualPeakLevel <= Minimum + 0.5f)
            _visualPeakValid = _visualPeakReleasing = false;
    }

    private void UpdateTrail(float elapsed, float previousDisplay)
    {
        _trailActive = false;
        if (VisualStyle != VisualStyle.Trail) return;
        _trailSampleElapsed += elapsed;
        for (var i = 0; i < _trailLife.Length; i++)
        {
            _trailLife[i] = Math.Max(0, _trailLife[i] - elapsed);
            _trailActive |= _trailLife[i] > 0;
        }
        if (elapsed <= 0.4f && Math.Abs(previousDisplay - _displayValue) > 0.05f &&
            previousDisplay > Minimum + 0.5f && (_trailSampleElapsed >= 0.04f || !_trailActive))
        {
            _trailLevels[_trailWrite] = previousDisplay;
            _trailLife[_trailWrite] = 0.4f;
            _trailWrite = (_trailWrite + 1) % _trailLife.Length;
            _trailSampleElapsed = 0;
            _trailActive = true;
        }
    }

    private void UpdatePeakHold(float intervalMs)
    {
        if (!PeakHoldEnabled)
        {
            _peakValue = _displayValue;
            _peakHoldLeftMs = 0;
            return;
        }

        // turn the per-60-Hz decay into a rate so it doesn't depend on refresh rate
        var decayPerSecond = Math.Max(0.01f, PeakDecayPerTick) * 60f;

        // catch real transients even if the smoothed attack hasn't gotten there yet
        var peakInput = Math.Max(_displayValue, Clamp(_targetValue, Minimum, Maximum));
        BarBallistics.StepPeak(
            ref _peakValue, ref _peakHoldLeftMs, peakInput, intervalMs, PeakHoldMilliseconds, decayPerSecond, Minimum);
    }

    // only repaint on a visible change; 1/1024 quantization avoids sub-brick repaints
    private bool ComputeShouldInvalidate(VisualizationMode mode)
    {
        var dq = ComputeRenderKey(mode, out var pq, out var filledH);
        var range = Math.Max(1, Maximum - Minimum);
        var displayColor = mode is VisualizationMode.Spectrum or VisualizationMode.Wave or VisualizationMode.Lollipop
            ? GetLevelColor(Clamp01((_displayValue - Minimum) / range)) : Color.Empty;
        var peakColor = pq >= 0 ? GetPeakColor(Clamp01((_peakValue - Minimum) / range)) : Color.Empty;
        var trailKey = 0;
        if (VisualStyle == VisualStyle.Trail)
            for (var i = 0; i < _trailLife.Length; i++)
                unchecked { trailKey = trailKey * 31 + (int)(_trailLife[i] * 100) + (int)_trailLevels[i]; }

        var changed =
            dq != _lastDisplayQ ||
            pq != _lastPeakQ ||
            displayColor != _lastDisplayColor ||
            peakColor != _lastPeakColor ||
            mode != _lastModeQ || trailKey != _lastTrailQ;

        _lastDisplayQ = dq;
        _lastPeakQ = pq;
        _lastDisplayColor = displayColor;
        _lastPeakColor = peakColor;
        _lastModeQ = mode;
        _lastTrailQ = trailKey;

        return changed;
    }

    private int ComputeRenderKey(VisualizationMode mode, out int peakKey, out int filledH)
    {
        peakKey = -1;
        filledH = 0;

        var bounds = ClientRectangle;
        var plot = PlotBounds;
        var topInner = plot.Top;
        var bottomInner = plot.Bottom;
        if (plot.Width <= 0 || bottomInner <= topInner)
        {
            _visualPeakReleasing = false;
            return 0;
        }

        var innerH = Math.Max(1, bottomInner - topInner);
        float range = Math.Max(1, Maximum - Minimum);
        var fillPercent = Clamp01((_displayValue - Minimum) / range);
        filledH = (int)Math.Round(innerH * fillPercent);

        if (mode is VisualizationMode.Bricks or VisualizationMode.Led or VisualizationMode.Dots)
            UpdateLitBricks(bounds, bottomInner, innerH, filledH);
        if (PeakHoldEnabled && ModeHasPeakMarker(mode) &&
            (_peakValue > Minimum + 0.5f || _visualPeakValid && _visualPeakLevel > Minimum + 0.5f))
        {
            if (mode is VisualizationMode.Dots or VisualizationMode.Lollipop)
            {
                var peakPercent = Clamp01((Clamp(_peakValue, Minimum, Maximum) - Minimum) / range);
                peakKey = mode == VisualizationMode.Lollipop
                    ? (int)Math.Round(peakPercent * 1024f) : (int)Math.Round(innerH * peakPercent);
            }
            else
            {
                var activeTop = GetPaintedActiveTop(mode, plot, filledH, fillPercent, range);
                peakKey = bottomInner - ConstrainVisualPeak(mode, plot, activeTop, range);
            }
        }
        else
            _visualPeakValid = _visualPeakReleasing = false;

        switch (mode)
        {
            case VisualizationMode.Lollipop:
                // heat-colored markers can change color between pixel rows
                return (int)Math.Round(fillPercent * 1024f);

            case VisualizationMode.Bricks:
            case VisualizationMode.Led:
            case VisualizationMode.Dots:
            {
                return _litBricks;
            }

            case VisualizationMode.Spectrum:
            case VisualizationMode.Center:
            case VisualizationMode.Mirror:
                return mode == VisualizationMode.Spectrum
                    ? filledH
                    : (int)Math.Round(innerH * fillPercent / 2f);

            default:
                return filledH;
        }
    }

    private void RegisterInstance()
    {
        lock (s_lock)
        {
            s_instances.Add(new WeakReference<VerticalProgressBar>(this));
            EnsureTimerConfigured_NoLock();
        }
    }

    private void UnregisterInstance()
    {
        lock (s_lock)
        {
            for (var i = s_instances.Count - 1; i >= 0; i--)
            {
                if (!s_instances[i].TryGetTarget(out var ctrl) || ctrl == this || ctrl.IsDisposed)
                    s_instances.RemoveAt(i);
            }

            if (s_instances.Count == 0 && s_timer != null)
            {
                s_timer.Stop();
                s_timer.Dispose();
                s_timer = null;
                s_lastTicks = 0;
                s_idleTicks = 0;
            }
        }
    }

    private void EnsureTimerConfigured()
    {
        lock (s_lock) EnsureTimerConfigured_NoLock();
    }

    private void EnsureTimerConfigured_NoLock()
    {
        if (!Visible || IsDisposed || Disposing) return;
        if (s_timer == null)
        {
            s_timer = new System.Windows.Forms.Timer();
            s_timer.Tick += (s, e) => TickAll();
            s_lastTicks = 0;
            s_idleTicks = 0;
        }

        var fps = Math.Max(15, AnimationFps);
        // ask for slightly under the frame period, WinForms rounds timer intervals up to the next system tick
        var interval = Math.Max(4, (int)Math.Floor(1000.0 / fps) - 1);

        if (s_timer.Interval != interval)
            s_timer.Interval = interval;

        if (!s_timer.Enabled)
            s_timer.Start();
    }

    private static void TickAll()
    {
        lock (s_lock)
        {
            if (s_instances.Count == 0) return;

            var now = s_stopwatch.ElapsedTicks;
            var last = s_lastTicks == 0 ? now : s_lastTicks;
            s_lastTicks = now;

            var dt = (float)((now - last) / (double)Stopwatch.Frequency);
            if (dt <= 0) dt = 1f / 60f;
            var intervalMs = dt * 1000f;

            var anyInvalidated = false;
            var anyAnimating = false;

            for (var i = s_instances.Count - 1; i >= 0; i--)
            {
                if (!s_instances[i].TryGetTarget(out var ctrl) || ctrl.IsDisposed)
                {
                    s_instances.RemoveAt(i);
                    continue;
                }
                if (!ctrl.Visible) continue;

                ctrl.AnimateStep(dt, intervalMs);
                anyAnimating |= !ctrl.IsSettled();

                var mode = ctrl.GetVisualizationModeCached();
                if (ctrl.ComputeShouldInvalidate(mode))
                {
                    ctrl.Invalidate();
                    anyInvalidated = true;
                }
            }

            // Keep ticking while ballistics move, even if no frame changes, to preserve elapsed-time accuracy.
            if (!anyInvalidated && !anyAnimating)
            {
                s_idleTicks++;
                if (s_idleTicks >= SleepAfterIdleTicks && s_timer != null)
                {
                    s_timer.Stop();
                    s_idleTicks = 0;
                    s_lastTicks = 0;
                }
            }
            else
            {
                s_idleTicks = 0;
            }

            if (s_instances.Count == 0 && s_timer != null)
            {
                s_timer.Stop();
                s_timer.Dispose();
                s_timer = null;
                s_lastTicks = 0;
                s_idleTicks = 0;
            }
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x04;
            return cp;
        }
    }

    private static Color HeatmapColorHsv(float t, Color low, Color mid, Color high, Color peak)
    {
        t = Clamp01(t);

        if (t <= 0.55f)
        {
            var u = t / 0.55f;
            return LerpHsv(low, mid, u);
        }
        if (t <= 0.82f)
        {
            var u = (t - 0.55f) / (0.82f - 0.55f);
            return LerpHsv(mid, high, u);
        }

        var u2 = (t - 0.82f) / (1f - 0.82f);
        return LerpHsv(high, peak, u2);
    }

    private static Color LerpRgb(Color a, Color b, float t)
    {
        t = Clamp01(t);
        var r = a.R + (int)((b.R - a.R) * t);
        var g = a.G + (int)((b.G - a.G) * t);
        var bl = a.B + (int)((b.B - a.B) * t);
        var al = a.A + (int)((b.A - a.A) * t);
        return Color.FromArgb(Clamp(al, 0, 255), Clamp(r, 0, 255), Clamp(g, 0, 255), Clamp(bl, 0, 255));
    }

    private static Color LerpHsv(Color a, Color b, float t)
    {
        t = Clamp01(t);

        RgbToHsv(a, out var ah, out var asat, out var av);
        RgbToHsv(b, out var bh, out var bsat, out var bv);

        var dh = bh - ah;
        if (dh > 180f) dh -= 360f;
        if (dh < -180f) dh += 360f;

        var h = ah + (dh * t);
        if (h < 0) h += 360f;
        if (h >= 360f) h -= 360f;

        var s = asat + ((bsat - asat) * t);
        var v = av + ((bv - av) * t);

        var alpha = a.A + (int)((b.A - a.A) * t);
        var rgb = HsvToRgb(h, s, v);
        return Color.FromArgb(Clamp(alpha, 0, 255), rgb.R, rgb.G, rgb.B);
    }

    private static void RgbToHsv(Color c, out float h, out float s, out float v)
    {
        var r = c.R / 255f;
        var g = c.G / 255f;
        var b = c.B / 255f;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        h = delta < 0.00001f
            ? 0f
            : max == r ? 60f * ((g - b) / delta % 6f)
            : max == g ? 60f * (((b - r) / delta) + 2f)
            : 60f * (((r - g) / delta) + 4f);

        if (h < 0) h += 360f;

        s = max <= 0 ? 0 : (delta / max);
        v = max;
    }

    private static Color HsvToRgb(float h, float s, float v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60f % 2) - 1));
        var m = v - c;

        float r1, g1, b1;
        if (h < 60) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        var rr = (int)Math.Round((r1 + m) * 255);
        var gg = (int)Math.Round((g1 + m) * 255);
        var bb = (int)Math.Round((b1 + m) * 255);

        return Color.FromArgb(Clamp(rr, 0, 255), Clamp(gg, 0, 255), Clamp(bb, 0, 255));
    }

    private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
