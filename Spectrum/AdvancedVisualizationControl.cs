using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum.Dsp;

namespace Spectrum;

/// <summary>Rendering modes provided by <see cref="AdvancedVisualizationControl"/>.</summary>
public enum AdvancedVisualizationMode
{
    Waterfall = 0,
    RadialSpectrum = 1,
    Contour = 2,
    NoteMap = 3,
    PeakTrace = 4,
    ThresholdMonitor = 5,
    BandMatrix = 6,
    OctaveSpectrum = 7,
    SpectralFlux = 8,
    // Stable legacy identifiers normalize to retained views.
    OrbitHistory = 9,
    OctaveWaterfall = 10,
    TransientMap = 11,
    FrequencyRibbon = 12,
    StereoLevels = 13,
    Oscilloscope = 14,
    SpectralBalance = 15,
    StereoSpectrum = 16,
    DynamicsHistory = 17
}

/// <summary>A reusable 83-band renderer that accepts copied analyzer frames on the UI thread.</summary>
/// <remarks>Bytes use the fixed display range (0 = -72 dBFS, 255 = 0 dBFS). Note Map displays nearest-semitone estimates, not detected notes; Contour smoothing is visual interpolation only.</remarks>
public sealed class AdvancedVisualizationControl : Control
{
    public const int BandCount = 83;

    private const int MaximumHistoryFrames = 2048;
    private const int DefaultHistoryFrames = 160;
    private const int MinimumNoteOctave = 0;
    private const int NoteOctaveCount = 11;
    private const int OctaveCount = 10;
    private const int HistoryImageWidth = BandCount + OctaveCount;
    private static readonly double[] s_displayPower = BuildDisplayPower();
    private static readonly string[] s_levelLabels = BuildLevelLabels();
    private static readonly string[] s_matrixValueLabels = BuildMatrixValueLabels();
    private static readonly PointF[] s_radialDirections = BuildRadialDirections();
    private static readonly int[] s_radialLabelBands = { 0, 20, 40, 60 };
    private static readonly string[] s_octaveBandLabels = BuildOctaveLabels();
    private static readonly string[] s_octaveLowerLabels = { "20", "40", "80", "160", "320", "640", "1.28k", "2.56k", "5.12k", "10.24k" };
    private static readonly string[] s_referenceDbLabels = { "−72", "−60", "−48", "−36", "−24", "−12", "0 dBFS" };

    private static readonly string[] s_pitchNames =
    {
        "C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"
    };
    private static readonly string[] s_octaveLabels =
    {
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10"
    };

    private readonly byte[] _spectrum = new byte[BandCount];
    private readonly byte[] _previousSpectrum = new byte[BandCount];
    private byte[] _history;
    private readonly int[] _paletteArgb = new int[256];
    private int[] _historyPixels;
    private GCHandle _historyPixelsHandle;
    private Bitmap _historyImage;
    private long[] _historyTicks;
    private long _lastFrameTick;
    private readonly float[] _heldPeaks = new float[BandCount];
    private readonly float[] _peakHoldMs = new float[BandCount];
    private readonly int[] _octaveByBand = new int[BandCount];
    private readonly byte[] _octaveLevels = new byte[OctaveCount];
    private readonly double[] _octavePower = new double[OctaveCount];
    private readonly double[] _balance = new double[3];
    private readonly string[] _bandLabels = new string[BandCount];
    private readonly string[] _balanceLabels = { "0.0%", "0.0%", "0.0%" };
    private readonly int[] _balanceLabelTenths = new int[3];
    private readonly AudioVisualizationData _audio = new();
    private readonly string[] _audioLabels = { "—", "—", "—", "—" };
    private readonly double[] _audioDb = new double[4];
    private readonly int[] _audioLabelTenths = { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
    private int _captionAudioFrames;
    private int _captionAudioRate;
    private bool _captionStereo;
    private int _captionWaveCount;
    private int _captionWaveRate;
    private double _captionWaveRange;
    private string _audioCaption = "PCM samples not supplied";
    private string _waveCaption = "PCM samples not supplied";
    private string _historyCaption = "No displayed history";
    private string _radialCaption;
    private float _radialPenWidth;
    private double _waveRange = 1;
    private int _waveStart;
    private int _waveDisplayCount;
    private int _historySpanTenths = -1;
    private int _captionFrames;
    private readonly int[] _noteByBand = new int[BandCount];
    private readonly byte[] _noteLevels = new byte[12 * NoteOctaveCount];
    private readonly PointF[] _plotPoints = new PointF[BandCount];
    private readonly SolidBrush[] _levelBrushes = new SolidBrush[256];
    private readonly Pen[] _radialPens = new Pen[256];
    private readonly SolidBrush[] _matrixSurfaceBrushes = new SolidBrush[256];
    private readonly RectangleF[] _matrixCards = new RectangleF[BandCount];
    private readonly GraphicsPath[] _matrixCardPaths = new GraphicsPath[BandCount];
    private readonly SolidBrush _primaryTextBrush = new SolidBrush(Color.Empty);
    private Font _matrixFontSource;
    private Font _matrixFrequencyFont;
    private Font _matrixValueFont;
    private Size _matrixLayoutSize;
    private int _matrixColumns;
    private int _matrixHeaderHeight;
    private int _matrixPadding;
    private int _matrixFrequencyHeight;
    private int _matrixValueHeight;
    private float _matrixDpi;
    private float _matrixScale;
    private Font _analysisFontSource;
    private Font _analysisBodyFont;
    private Font _analysisTitleFont;
    private readonly Pen _rmsHistoryPen = new Pen(Color.Empty, 1.5f) { DashStyle = DashStyle.Dash };
    private readonly Pen _peakHistoryPen = new Pen(Color.Empty, 1.5f);
    private readonly string[] _channelSpectrumLabels = { "LEFT", "RIGHT" };
    private readonly string[] _dynamicsLabels = { "LEFT", "RIGHT" };
    private string _dynamicsCaption = "Observed PCM windows • RMS / sample peak • 20 s sample-time view";
    private readonly Pen _gridPen = new Pen(Color.Empty, 1f);
    private readonly Pen _majorGridPen = new Pen(Color.Empty, 1f);
    private readonly Pen _linePen = new Pen(Color.Empty, 1.5f);
    private readonly Pen _framePen = new Pen(Color.Empty, 1f);
    private readonly SolidBrush _contourFillBrush = new SolidBrush(Color.Empty);
    private readonly SolidBrush _thresholdBrush = new SolidBrush(Color.Empty);
    private readonly SolidBrush _surfaceBrush = new SolidBrush(Color.Empty);
    private readonly Pen _peakPen = new Pen(Color.Empty, 1.5f) { DashStyle = DashStyle.Dash };
    private readonly Pen _precisionPen = new Pen(Color.Empty, 1f);
    private readonly Pen _scanPen = new Pen(Color.Empty, 1f);
    private readonly SolidBrush _textBrush = new SolidBrush(Color.Empty);
    private readonly GraphicsPath _contourPath = new GraphicsPath();
    private readonly StringFormat _centeredText = new StringFormat
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    private int _historyFrames = DefaultHistoryFrames;
    private int _historyWriteIndex;
    private int _historyCount;
    private AdvancedVisualizationMode _mode = AdvancedVisualizationMode.Contour;
    private string _themeName = "ClassicSmooth";
    private BarColorTheme _theme;
    private BandPlan _bandPlan;
    private VisualStyle _style;

    public AdvancedVisualizationControl()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);

        _history = new byte[DefaultHistoryFrames * BandCount];
        _historyTicks = new long[DefaultHistoryFrames];
        for (var i = 0; i < _levelBrushes.Length; i++)
        {
            _levelBrushes[i] = new SolidBrush(Color.Black);
            _radialPens[i] = new Pen(Color.Black);
            _matrixSurfaceBrushes[i] = new SolidBrush(Color.Black);
        }

        _theme = BarColorThemes.Resolve(_themeName);
        ApplyThemeChrome(_theme);
        UpdatePalette();
        ReplaceHistoryImage(DefaultHistoryFrames);
        SetBandPlan(BandPlan.CreateLogarithmic(BandCount, 20, 20000, 48000));
    }

    /// <summary>The active visualization. Changing it does not add a sample to waterfall history.</summary>
    public AdvancedVisualizationMode Mode
    {
        get => _mode;
        set
        {
            ValidateMode(value, nameof(value));
            value = NormalizeMode(value);
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            if (value == AdvancedVisualizationMode.SpectralFlux)
                Array.Copy(_spectrum, _previousSpectrum, BandCount);
            if (IsMeasurementMode(value) && _style != VisualStyle.Precision)
                _style = VisualStyle.None;
            UpdateHistoryCaption();
            Invalidate();
        }
    }

    internal VisualStyle Style
    {
        get => _style;
        set
        {
            if (IsMeasurementMode(_mode) && value != VisualStyle.Precision)
                value = VisualStyle.None;
            if (_style == value)
                return;

            _style = value;
            Invalidate();
        }
    }

    /// <summary>Palette name understood by <see cref="BarColorThemes"/>, including Aurora; unknown names use ClassicSmooth.</summary>
    public string ThemeName
    {
        get => _themeName;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? "ClassicSmooth" : value.Trim();
            if (string.Equals(_themeName, name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _themeName = name;
            _theme = BarColorThemes.Resolve(name);
            ApplyThemeChrome(_theme);
            UpdatePalette();
            Invalidate();
        }
    }

    /// <summary>Maximum number of frames retained by Waterfall; changing it clears its history.</summary>
    public int HistoryFrames
    {
        get => _historyFrames;
        set
        {
            if (value < 1 || value > MaximumHistoryFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"History must be between 1 and {MaximumHistoryFrames} frames.");
            }

            if (_historyFrames == value)
            {
                return;
            }

            var history = new byte[value * BandCount];
            var ticks = new long[value];
            ReplaceHistoryImage(value);
            _history = history;
            _historyTicks = ticks;
            _historyFrames = value;
            _historyWriteIndex = 0;
            _historyCount = 0;
            RebuildHistoryImage();
            Invalidate();
        }
    }

    /// <summary>Copies up to 83 display values, zero-pads short frames, ignores extra values, and appends one Waterfall row.</summary>
    /// <remarks>Call on the UI thread; the caller retains ownership of <paramref name="values"/>.</remarks>
    public void SetSpectrum(byte[] values)
    {
        CopyAndAppendFrame(values);
        Invalidate();
    }

    internal void SetBandPlan(BandPlan plan)
    {
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));
        if (plan.Count != BandCount)
            throw new ArgumentException($"The visualization requires {BandCount} frequency bands.", nameof(plan));
        if (ReferenceEquals(_bandPlan, plan))
            return;

        _bandPlan = plan;
        BuildNoteMap(plan);
        for (var band = 0; band < BandCount; band++)
        {
            _octaveByBand[band] = Math.Max(0, Math.Min(OctaveCount - 1,
                (int)Math.Floor(Math.Log(plan[band].CenterHz / 20, 2))));
            var hz = plan[band].CenterHz;
            _bandLabels[band] = hz >= 1000 ? (hz / 1000).ToString("0.##") + " kHz" : hz.ToString("0.#") + " Hz";
        }
        _radialCaption = "Clockwise: " + _bandLabels[0] + " → " + _bandLabels[BandCount - 1] + " • origin at 12 o'clock";
        ResetHistory();
        // Existing channel bands belonged to the previous frequency partition.
        _audio.SetChannelSpectrum(null, null);
        UpdateAnalysisLabels();
        Invalidate();
    }

    /// <summary>Copies a frame and updates the active named palette and mode.</summary>
    /// <remarks>Call on the UI thread; the caller retains ownership of <paramref name="values"/>.</remarks>
    public void SetSpectrum(byte[] values, string themeName, AdvancedVisualizationMode mode)
    {
        ValidateMode(mode, nameof(mode));
        CopyAndAppendFrame(values);

        var name = string.IsNullOrWhiteSpace(themeName) ? "ClassicSmooth" : themeName.Trim();
        if (!string.Equals(_themeName, name, StringComparison.OrdinalIgnoreCase))
        {
            _themeName = name;
            _theme = BarColorThemes.Resolve(name);
            ApplyThemeChrome(_theme);
            UpdatePalette();
        }

        _mode = NormalizeMode(mode);
        if (IsMeasurementMode(_mode) && _style != VisualStyle.Precision) _style = VisualStyle.None;
        UpdateHistoryCaption();
        Invalidate();
    }

    /// <summary>Copies a frame and updates the palette and mode using this assembly's internal theme type.</summary>
    /// <param name="values">Analyzer display-level frame; this control copies the values it uses.</param>
    /// <param name="theme">Palette already resolved by the application.</param>
    /// <param name="mode">A defined advanced visualization name (case-insensitive), including stable legacy aliases.</param>
    internal void SetSpectrum(byte[] values, BarColorTheme theme, string mode)
    {
        if (!TryParseMode(mode, out var parsedMode))
        {
            throw new ArgumentException(
                "Mode must name a supported advanced visualization.", nameof(mode));
        }

        CopyAndAppendFrame(values);
        _theme = theme;
        _themeName = "Custom";
        ApplyThemeChrome(_theme);
        UpdatePalette();
        _mode = NormalizeMode(parsedMode);
        if (IsMeasurementMode(_mode) && _style != VisualStyle.Precision) _style = VisualStyle.None;
        UpdateHistoryCaption();
        Invalidate();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_levelBrushes[0] != null)
        {
            UpdatePalette();
            Invalidate();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateHistoryCaption();
        Invalidate();
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        _textBrush.Color = ForeColor;
        Invalidate();
    }

    internal void ApplyTheme(BarColorTheme theme)
    {
        _theme = theme;
        _themeName = "Custom";
        ApplyThemeChrome(theme);
        UpdatePalette();
        Invalidate();
    }

    private void ApplyThemeChrome(BarColorTheme theme)
    {
        var ui = theme.Ui;
        BackColor = ui.VisualizationSurface;
        ForeColor = ui.SecondaryText;
        _gridPen.Color = Color.FromArgb(88, ui.Grid);
        _majorGridPen.Color = Color.FromArgb(142, ui.MajorGrid);
        _framePen.Color = Color.FromArgb(180, ui.Frame);
        _linePen.Color = theme.High;
        _contourFillBrush.Color = Color.FromArgb(62, theme.Mid);
        _textBrush.Color = ui.SecondaryText;
        _primaryTextBrush.Color = ui.PrimaryText;
        _rmsHistoryPen.Color = theme.Low;
        _peakHistoryPen.Color = ui.PrimaryText;
        _surfaceBrush.Color = ui.RaisedSurface;
        _peakPen.Color = ui.PrimaryText;
        _precisionPen.Color = Color.FromArgb(180, ui.MajorGrid);
        _scanPen.Color = Color.FromArgb(72, theme.High);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(ClientRectangle);
            graphics.Clear(BackColor);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var width = ClientSize.Width;
            var height = ClientSize.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            switch (_mode)
            {
                case AdvancedVisualizationMode.Waterfall:
                    PaintWaterfall(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.RadialSpectrum:
                    PaintRadialSpectrum(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.Contour:
                    PaintContour(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.NoteMap:
                    PaintNoteMap(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.PeakTrace:
                    PaintPeakTrace(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.ThresholdMonitor:
                    PaintThresholdMonitor(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.BandMatrix:
                    PaintBandMatrix(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.OctaveSpectrum:
                    PaintOctaveSpectrum(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.SpectralFlux:
                    PaintSpectralFlux(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.OctaveWaterfall:
                    PaintOctaveWaterfall(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.StereoLevels:
                    PaintStereoLevels(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.Oscilloscope:
                    PaintOscilloscope(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.SpectralBalance:
                    PaintSpectralBalance(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.StereoSpectrum:
                    PaintStereoSpectrum(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.DynamicsHistory:
                    PaintDynamicsHistory(graphics, width, height);
                    break;
            }

            PaintStyleOverlay(graphics, width, height);
            if (_mode == AdvancedVisualizationMode.PeakTrace)
            {
                GetCurvePlotBounds(width, height, out var top, out var baseline);
                DrawSpectrumLine(graphics, width, top, baseline, true, _theme.Ui.PrimaryText, 1.3f);
            }

            if (width > 2 && height > 2)
            {
                graphics.DrawRectangle(_framePen, 0, 0, width - 1, height - 1);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            for (var i = 0; i < _levelBrushes.Length; i++)
            {
                _levelBrushes[i]?.Dispose();
                _radialPens[i]?.Dispose();
                _matrixSurfaceBrushes[i]?.Dispose();
            }
            foreach (var path in _matrixCardPaths) path?.Dispose();
            _matrixFrequencyFont?.Dispose();
            _matrixValueFont?.Dispose();
            _primaryTextBrush.Dispose();
            _analysisBodyFont?.Dispose();
            _analysisTitleFont?.Dispose();
            _rmsHistoryPen.Dispose();
            _peakHistoryPen.Dispose();

            _gridPen.Dispose();
            _majorGridPen.Dispose();
            _linePen.Dispose();
            _framePen.Dispose();
            _contourFillBrush.Dispose();
            _thresholdBrush.Dispose();
            _surfaceBrush.Dispose();
            _peakPen.Dispose();
            _precisionPen.Dispose();
            _scanPen.Dispose();
            _historyImage?.Dispose();
            if (_historyPixelsHandle.IsAllocated) _historyPixelsHandle.Free();
            _textBrush.Dispose();
            _contourPath.Dispose();
            _centeredText.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PaintWaterfall(Graphics graphics, int width, int height)
    {
        PaintHistoryImage(graphics, width, height, false);
    }

    private void PaintRadialSpectrum(Graphics graphics, int width, int height)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var centerX = width * 0.5f;
        var labeled = width >= 240 && height >= 140;
        var centerY = labeled ? (32 + height - 20) * 0.5f : height * 0.5f;
        var radius = labeled ? Math.Max(1f, Math.Min(width * 0.5f - 45, (height - 52) * 0.5f - 12))
            : Math.Max(1f, Math.Min(width, height) * 0.42f);
        var innerRadius = radius * 0.20f;
        var span = radius - innerRadius;
        var radialGuides = _style == VisualStyle.Precision ? 6 : 2;
        for (var guide = 0; guide <= radialGuides; guide++)
        {
            var r = innerRadius + span * guide / radialGuides;
            graphics.DrawEllipse(_majorGridPen, centerX - r, centerY - r, 2 * r, 2 * r);
        }
        var penWidth = Math.Max(1f, Math.Min(5f, (float)(2 * Math.PI * radius / BandCount) * 0.65f));
        if (_radialPenWidth != penWidth)
        {
            _radialPenWidth = penWidth;
            foreach (var pen in _radialPens) pen.Width = penWidth;
        }
        _peakPen.DashStyle = DashStyle.Solid;
        for (var band = 0; band < BandCount; band++)
        {
            var direction = s_radialDirections[band];
            var end = innerRadius + span * _spectrum[band] / 255f;
            if (_spectrum[band] > 0)
            {
                var pen = _radialPens[_spectrum[band]];
                graphics.DrawLine(pen, centerX + direction.X * innerRadius, centerY + direction.Y * innerRadius,
                    centerX + direction.X * end, centerY + direction.Y * end);
            }
            if (_heldPeaks[band] > 0.5f)
            {
                var r = innerRadius + span * _heldPeaks[band] / 255f;
                var halfTick = Math.Max(1f, penWidth * 0.65f);
                graphics.DrawLine(_peakPen, centerX + direction.X * r - direction.Y * halfTick,
                    centerY + direction.Y * r + direction.X * halfTick,
                    centerX + direction.X * r + direction.Y * halfTick,
                    centerY + direction.Y * r - direction.X * halfTick);
            }
        }
        if (labeled)
        {
            graphics.DrawString("Inner −72 / outer 0 dBFS", Font, _textBrush, 4, height - 18);
            DrawCaption(graphics, width, _radialCaption);
            foreach (var band in s_radialLabelBands)
            {
                var direction = s_radialDirections[band];
                var x = centerX + direction.X * (radius + 10);
                var y = centerY + direction.Y * (radius + 10);
                graphics.DrawString(_bandLabels[band], Font, _textBrush,
                    new RectangleF(x - 35, y - 8, 70, 16), _centeredText);
            }
        }
    }

    private void PaintContour(Graphics graphics, int width, int height)
    {
        if (width < 2 || height < 2)
        {
            return;
        }

        var previousSmoothingMode = graphics.SmoothingMode;
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            GetCurvePlotBounds(width, height, out var top, out var baseline);
            var plotHeight = Math.Max(1f, baseline - top);
            var guides = _style == VisualStyle.Precision ? 5 : 3;
            for (var guide = 1; guide <= guides; guide++)
            {
                var y = baseline - (plotHeight * guide / (guides + 1));
                graphics.DrawLine(_majorGridPen, 0, y, width, y);
            }

            for (var band = 20; band < BandCount; band += 20)
            {
                var x = (float)band * width / BandCount;
                graphics.DrawLine(_majorGridPen, x, top, x, baseline);
            }

            for (var band = 0; band < BandCount; band++)
            {
                var x = (band + 0.5f) * width / BandCount;
                var y = baseline - ((_spectrum[band] / 255f) * plotHeight);
                _plotPoints[band] = new PointF(x, y);
            }

            _contourPath.Reset();
            AppendPlotLines();
            _contourPath.AddLine(width - 1f, baseline, 0f, baseline);
            _contourPath.CloseFigure();

            _contourFillBrush.Color = Color.FromArgb(36, _theme.Low);
            graphics.FillPath(_contourFillBrush, _contourPath);

            _linePen.Width = Math.Max(1f, Math.Min(2.5f, height / 100f));
            _linePen.Color = _theme.Low;
            _contourPath.Reset();
            AppendPlotLines();
            graphics.DrawPath(_linePen, _contourPath);
            graphics.DrawLine(_gridPen, 0, baseline, width, baseline);
            DrawCaption(graphics, width, "Logarithmic frequency bands • −72 … 0 dBFS");
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothingMode;
        }
    }

    private void PaintNoteMap(Graphics graphics, int width, int height)
    {
        const int leftMargin = 32;
        const int rightMargin = 4;
        const int topMargin = 4;
        const int bottomMargin = 20;

        var gridWidth = Math.Max(0, width - leftMargin - rightMargin);
        var gridHeight = Math.Max(0, height - topMargin - bottomMargin);
        if (gridWidth == 0 || gridHeight == 0)
        {
            return;
        }

        Array.Clear(_noteLevels, 0, _noteLevels.Length);
        for (var band = 0; band < BandCount; band++)
        {
            var cell = _noteByBand[band];
            if (_spectrum[band] > _noteLevels[cell])
            {
                _noteLevels[cell] = _spectrum[band];
            }
        }

        var cellWidth = (float)gridWidth / NoteOctaveCount;
        var cellHeight = (float)gridHeight / 12;
        for (var octave = 0; octave < NoteOctaveCount; octave++)
        {
            var x = leftMargin + (octave * cellWidth);
            for (var pitch = 0; pitch < 12; pitch++)
            {
                var y = topMargin + (pitch * cellHeight);
                graphics.FillRectangle(_levelBrushes[_noteLevels[(octave * 12) + pitch]],
                    x, y, cellWidth, cellHeight);
                graphics.DrawRectangle(_majorGridPen, x, y, cellWidth, cellHeight);

                var pitchBounds = new RectangleF(0, y, leftMargin - 2f, cellHeight);
                graphics.DrawString(s_pitchNames[pitch], Font, _textBrush, pitchBounds, _centeredText);
            }

            var octaveBounds = new RectangleF(x, topMargin + gridHeight, cellWidth, bottomMargin);
            graphics.DrawString(s_octaveLabels[octave], Font, _textBrush,
                octaveBounds, _centeredText);
        }
    }

    private void PaintPeakTrace(Graphics graphics, int width, int height)
    {
        DrawContourGrid(graphics, width, height, out var top, out var baseline);
        DrawSpectrumLine(graphics, width, top, baseline, false, _theme.Low, 2f);
        DrawCaption(graphics, width, "Current spectrum • dashed 300 ms peak hold • −72 … 0 dBFS");
    }

    private void PaintThresholdMonitor(Graphics graphics, int width, int height)
    {
        const byte threshold = 170;
        var cellWidth = width / (float)BandCount;
        var top = width >= 180 && height >= 80 ? 22f : 0f;
        var plotHeight = Math.Max(1f, height - top);
        var thresholdY = (int)Math.Round(top + plotHeight * (1f - (threshold / 255f)));
        if (_style == VisualStyle.Precision)
            for (var guide = 1; guide < 6; guide++)
                graphics.DrawLine(_precisionPen, 0, top + plotHeight * guide / 6f, width, top + plotHeight * guide / 6f);
        for (var band = 0; band < BandCount; band++)
        {
            var level = _spectrum[band];
            if (level == 0)
                continue;
            var color = level >= threshold ? _theme.High : level >= 96 ? _theme.Mid : _theme.Low;
            var cellHeight = Math.Max(1f, plotHeight * level / 255f);
            _thresholdBrush.Color = color;
            graphics.FillRectangle(_thresholdBrush, band * cellWidth, height - cellHeight, Math.Max(0.5f, cellWidth - 0.5f), cellHeight);
        }

        graphics.DrawLine(_majorGridPen, 0, thresholdY, width, thresholdY);
        DrawCaption(graphics, width, "Threshold −24 dBFS • quantized display levels");
    }

    private void PaintBandMatrix(Graphics graphics, int width, int height)
    {
        EnsureMatrixLayout(graphics, width, height);
        var scale = _matrixScale;
        if (_matrixHeaderHeight > 0)
        {
            graphics.DrawString("BAND MATRIX", _matrixValueFont, _primaryTextBrush, _matrixPadding, 3 * scale);
            graphics.DrawString("83 log bands • left → right, then down", _matrixFrequencyFont, _textBrush,
                new RectangleF(_matrixPadding, 3 * scale + _matrixValueHeight, width - 2 * _matrixPadding, _matrixFrequencyHeight), _centeredText);
            if (width >= 620 * scale)
                graphics.DrawString("DISPLAY RANGE • −72 … 0 dBFS", _matrixFrequencyFont, _textBrush,
                    new RectangleF(width - 285 * scale, 3 * scale, 277 * scale, 23 * scale), _centeredText);
            else graphics.DrawString("dBFS", _matrixFrequencyFont, _textBrush, width - 45 * scale, 3 * scale);
        }
        var firstCard = _matrixCards[0];
        var fullLabels = firstCard.Width >= 44 * scale && firstCard.Height >= _matrixFrequencyHeight + _matrixValueHeight + 8 * scale;
        var frequencyOnly = !fullLabels && firstCard.Width >= 42 * scale && firstCard.Height >= _matrixFrequencyHeight + 9 * scale;
        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var band = 0; band < BandCount; band++)
        {
            var rect = _matrixCards[band];
            var level = _spectrum[band];
            var path = _matrixCardPaths[band];
            var brush = fullLabels || frequencyOnly || level == 0 ? _matrixSurfaceBrushes[level] : _levelBrushes[level];
            if (path != null)
            {
                graphics.FillPath(brush, path);
                graphics.DrawPath(_gridPen, path);
            }
            else graphics.FillRectangle(brush, rect);
            if (!fullLabels && !frequencyOnly) continue;

            var innerX = rect.X + 5 * scale;
            var innerWidth = Math.Max(1, rect.Width - 10 * scale);
            graphics.DrawString(_bandLabels[band], _matrixFrequencyFont, _textBrush,
                new RectangleF(innerX, rect.Y + 2 * scale, innerWidth, _matrixFrequencyHeight), _centeredText);
            if (fullLabels)
                graphics.DrawString(s_matrixValueLabels[level], _matrixValueFont,
                    level == 0 ? _textBrush : _primaryTextBrush,
                    new RectangleF(innerX, rect.Y + 2 * scale + _matrixFrequencyHeight, innerWidth, _matrixValueHeight), _centeredText);

            var meterY = rect.Bottom - 5 * scale;
            graphics.FillRectangle(_surfaceBrush, innerX, meterY, innerWidth, 3 * scale);
            if (level > 0)
                graphics.FillRectangle(_levelBrushes[level], innerX, meterY, Math.Max(scale, innerWidth * level / 255f), 3 * scale);
            if (_style == VisualStyle.Precision)
                for (var reference = 1; reference < 3; reference++)
                    graphics.DrawLine(_precisionPen, innerX + innerWidth * reference / 3, meterY - scale,
                        innerX + innerWidth * reference / 3, meterY + 4 * scale);
        }
        graphics.SmoothingMode = previousSmoothing;
    }

    private void EnsureMatrixLayout(Graphics graphics, int width, int height)
    {
        var source = Font;
        var fontChanged = !ReferenceEquals(source, _matrixFontSource);
        if (fontChanged)
        {
            _matrixFrequencyFont?.Dispose();
            _matrixValueFont?.Dispose();
            _matrixFontSource = source;
            _matrixFrequencyFont = new Font(source.FontFamily, Math.Max(6.5f, source.SizeInPoints - 1), FontStyle.Regular);
            _matrixValueFont = new Font(source.FontFamily, Math.Max(7.5f, source.SizeInPoints + 0.25f), FontStyle.Bold);
        }
        var dpi = graphics.DpiY;
        var metricsChanged = fontChanged || _matrixDpi != dpi;
        if (!metricsChanged && _matrixLayoutSize.Width == width && _matrixLayoutSize.Height == height) return;
        _matrixDpi = dpi;
        _matrixScale = dpi / 96f;
        if (metricsChanged)
        {
            _matrixFrequencyHeight = (int)Math.Ceiling(_matrixFrequencyFont.GetHeight(graphics));
            _matrixValueHeight = (int)Math.Ceiling(_matrixValueFont.GetHeight(graphics));
        }
        var scale = _matrixScale;
        _matrixLayoutSize = new Size(width, height);
        _matrixHeaderHeight = width >= 360 * scale && height >= 120 * scale ? _matrixFrequencyHeight + _matrixValueHeight + (int)Math.Ceiling(8 * scale) : 0;
        _matrixPadding = (int)Math.Ceiling((width < 200 * scale || height < 80 * scale ? 2 : 8) * scale);
        var gap = (width < 200 * scale || height < 80 * scale ? 1f : 4f) * scale;
        if (width < 40 || height < 24) { _matrixPadding = 0; gap = 0; }
        var availableWidth = Math.Max(1, width - 2 * _matrixPadding);
        var availableHeight = Math.Max(1, height - _matrixHeaderHeight - 2 * _matrixPadding);
        var bestScore = float.MinValue;
        _matrixColumns = 1;
        for (var columns = 1; columns <= BandCount; columns++)
        {
            var rows = (BandCount + columns - 1) / columns;
            var cardWidth = (availableWidth - gap * (columns - 1)) / columns;
            var cardHeight = (availableHeight - gap * (rows - 1)) / rows;
            if (cardWidth <= 0 || cardHeight <= 0) continue;
            var score = Math.Min(cardWidth / (56 * Math.Max(scale, _matrixValueHeight / 14f)), cardHeight / (_matrixFrequencyHeight + _matrixValueHeight + 8 * scale))
                - (rows * columns - BandCount) * 0.001f;
            if (score <= bestScore) continue;
            bestScore = score;
            _matrixColumns = columns;
        }
        var rowCount = (BandCount + _matrixColumns - 1) / _matrixColumns;
        var w = Math.Max(0.01f, (availableWidth - gap * (_matrixColumns - 1)) / _matrixColumns);
        var h = Math.Max(0.01f, (availableHeight - gap * (rowCount - 1)) / rowCount);
        for (var band = 0; band < BandCount; band++)
        {
            _matrixCardPaths[band]?.Dispose();
            _matrixCardPaths[band] = null;
            var rect = new RectangleF(_matrixPadding + (band % _matrixColumns) * (w + gap),
                _matrixHeaderHeight + _matrixPadding + (band / _matrixColumns) * (h + gap), w, h);
            _matrixCards[band] = rect;
            if (w < 16 * scale || h < 16 * scale) continue;
            var path = new GraphicsPath();
            var diameter = 6 * scale;
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            _matrixCardPaths[band] = path;
        }
    }

    private void PaintOctaveSpectrum(Graphics graphics, int width, int height)
    {
        var top = height >= 80 ? 24f : 2f;
        var baseline = Math.Max(top + 1, height - (height >= 80 ? 23f : 2f));
        var plotHeight = Math.Max(1, baseline - top);
        var groupWidth = width / (float)OctaveCount;
        for (var group = 0; group < OctaveCount; group++)
        {
            var level = _octaveLevels[group];
            var barHeight = plotHeight * level / 255f;
            var x = group * groupWidth;
            if (level > 0)
                graphics.FillRectangle(_levelBrushes[level], x + 2, baseline - Math.Max(1, barHeight),
                    Math.Max(1, groupWidth - 4), Math.Max(1, barHeight));
            graphics.DrawRectangle(_majorGridPen, x + 1, top, Math.Max(1, groupWidth - 2), plotHeight);
            if (_style == VisualStyle.Precision)
                for (var guide = 1; guide < 6; guide++)
                    graphics.DrawLine(_precisionPen, x + 1, baseline - plotHeight * guide / 6f,
                        x + groupWidth - 1, baseline - plotHeight * guide / 6f);
            if (groupWidth >= 35 && height >= 80)
                graphics.DrawString(groupWidth >= 100 ? s_octaveBandLabels[group] : s_octaveLowerLabels[group], Font, _textBrush,
                    new RectangleF(x, baseline + 2, groupWidth, 20), _centeredText);
        }
        DrawCaption(graphics, width, width >= 1000 ? "Summed display power • octave groups by band center, Hz" :
            "Approx. octave groups by band center • lower edges, Hz");
    }

    private void PaintSpectralFlux(Graphics graphics, int width, int height)
    {
        var center = height * 0.5f;
        var span = Math.Max(1, height * 0.5f - (height >= 80 ? 24 : 2));
        graphics.DrawLine(_majorGridPen, 0, center, width, center);
        if (_style == VisualStyle.Precision)
            for (var guide = 1; guide < 6; guide++)
            {
                graphics.DrawLine(_precisionPen, 0, center - span * guide / 6f, width, center - span * guide / 6f);
                graphics.DrawLine(_precisionPen, 0, center + span * guide / 6f, width, center + span * guide / 6f);
            }
        var columnWidth = width / (float)BandCount;
        for (var band = 0; band < BandCount; band++)
        {
            var difference = _spectrum[band] - _previousSpectrum[band];
            if (difference == 0) continue;
            var extent = span * Math.Abs(difference) / 255f;
            _thresholdBrush.Color = difference > 0 ? _theme.High : _theme.Low;
            var y = difference > 0 ? center - extent : center;
            graphics.FillRectangle(_thresholdBrush, band * columnWidth, y,
                Math.Max(0.5f, columnWidth - 1), Math.Max(1, extent));
        }
        DrawCaption(graphics, width, "Display-level change • rise above center, fall below • full scale ±72 dB");
    }


    private void PaintOctaveWaterfall(Graphics graphics, int width, int height)
    {
        PaintHistoryImage(graphics, width, height, true);
    }


    private void PaintStyleOverlay(Graphics graphics, int width, int height)
    {
        if (IsMeasurementMode(_mode)) return;
        if (_style == VisualStyle.Scanline &&
            (_mode == AdvancedVisualizationMode.Contour || _mode == AdvancedVisualizationMode.PeakTrace ||
             _mode == AdvancedVisualizationMode.ThresholdMonitor))
        {
            GetCurvePlotBounds(width, height, out var top, out var baseline);
            if (_mode == AdvancedVisualizationMode.ThresholdMonitor)
            {
                top = width >= 180 && height >= 80 ? 22 : 0;
                baseline = height;
            }
            var y = baseline - (baseline - top) * GetAverageLevel();
            graphics.DrawLine(_scanPen, 0, y, width, y);
        }
    }

    private float GetAverageLevel()
    {
        var total = 0;
        for (var band = 0; band < BandCount; band++)
            total += _spectrum[band];

        return total / (BandCount * 255f);
    }

    private void DrawContourGrid(Graphics graphics, int width, int height, out float top, out float baseline)
        {
            GetCurvePlotBounds(width, height, out top, out baseline);
            var guides = _style == VisualStyle.Precision ? 5 : 3;
            for (var guide = 1; guide <= guides; guide++)
            {
                var y = baseline - ((baseline - top) * guide / (guides + 1));
                graphics.DrawLine(_majorGridPen, 0, y, width, y);
            }
        }

    private static void GetCurvePlotBounds(int width, int height, out float top, out float baseline)
    {
        top = width >= 180 && height >= 80 ? 22f : Math.Max(2f, height * 0.06f);
        baseline = Math.Max(top + 1f, height - Math.Max(2f, height * 0.08f));
    }

    private void DrawSpectrumLine(Graphics graphics, int width, float top, float baseline, bool peaks, Color color, float thickness)
    {
        var plotHeight = Math.Max(1f, baseline - top);
        for (var band = 0; band < BandCount; band++)
        {
            var level = peaks ? _heldPeaks[band] : _spectrum[band];
            _plotPoints[band] = new PointF((band + 0.5f) * width / BandCount,
                baseline - level / 255f * plotHeight);
        }
        _contourPath.Reset();
        AppendPlotLines();
        if (peaks)
        {
            _peakPen.Color = color;
            _peakPen.Width = thickness;
            _peakPen.DashStyle = DashStyle.Dash;
            graphics.DrawPath(_peakPen, _contourPath);
        }
        else
        {
            _linePen.Color = color;
            _linePen.Width = thickness;
            graphics.DrawPath(_linePen, _contourPath);
        }
    }

    private void AppendPlotLines()
    {
        // Framework AddLines(PointF[]) marshals the entire array on each paint. Reuse the points
        // and scalar path API so rendering does not allocate temporary managed point objects.
        for (var band = 1; band < BandCount; band++)
            _contourPath.AddLine(_plotPoints[band - 1].X, _plotPoints[band - 1].Y,
                _plotPoints[band].X, _plotPoints[band].Y);
    }

    private int GetHistoryFrame(int age)
        {
            if (age < 0 || age >= _historyCount)
                return -1;

            var frame = _historyWriteIndex - 1 - age;
            if (frame < 0)
                frame += _historyFrames;
            return frame;
        }

    internal static byte GetGroupLevel(byte[] values, int group, int groupCount, int offset = 0)
        {
            var start = offset + ((group * BandCount) / groupCount);
            var end = offset + (((group + 1) * BandCount) / groupCount);
            var power = 0.0;
            for (var index = start; index < end; index++)
                power += LevelScale.DisplayByteToRelativePower(values[index]);

            return LevelScale.ToDisplayByte(LevelScale.PowerToDb(power));
        }

    public void SetAudioSamples(float[] samples, int frames, int channels, int sampleRate)
    {
        _audio.Update(samples, frames, channels, sampleRate);
        UpdateAudioLabels();
        UpdateAnalysisLabels();
        Invalidate();
    }

    internal void SetAudioData(AudioVisualizationData data)
    {
        _audio.CopyFrom(data);
        UpdateAudioLabels();
        UpdateAnalysisLabels();
        Invalidate();
    }

    internal void SynchronizeSpectrum(byte[] values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        ResetHistory();
        SetSpectrum(values);
        Array.Copy(_spectrum, _previousSpectrum, BandCount);
    }

    private void UpdateAudioLabels()
    {
        if (!_audio.HasSamples)
        {
            _audioCaption = _waveCaption = "PCM samples not supplied";
            _captionAudioFrames = _captionWaveCount = 0;
            _waveDisplayCount = 0;
            return;
        }
        _audioDb[0] = AudioVisualizationData.AmplitudeToDb(_audio.LeftRms);
        _audioDb[1] = AudioVisualizationData.AmplitudeToDb(_audio.LeftPeak);
        _audioDb[2] = AudioVisualizationData.AmplitudeToDb(_audio.RightRms);
        _audioDb[3] = AudioVisualizationData.AmplitudeToDb(_audio.RightPeak);
        for (var i = 0; i < _audioDb.Length; i++)
        {
            var silent = (i == 0 ? _audio.LeftRms : i == 1 ? _audio.LeftPeak : i == 2 ? _audio.RightRms : _audio.RightPeak) == 0;
            var tenths = silent ? int.MinValue : (int)Math.Round(_audioDb[i] * 10);
            if (_audioLabelTenths[i] == tenths) continue;
            _audioLabelTenths[i] = tenths;
            _audioLabels[i] = silent ? "−∞ dBFS" : _audioDb[i].ToString("0.0") + " dBFS";
        }
        if (_captionAudioFrames != _audio.WindowFrames || _captionAudioRate != _audio.SampleRate || _captionStereo != _audio.HasStereo)
        {
            _captionAudioFrames = _audio.WindowFrames;
            _captionAudioRate = _audio.SampleRate;
            _captionStereo = _audio.HasStereo;
            _audioCaption = (_audio.HasStereo ? "Front L/R" : "Mono") + " • sample peak/RMS, FS amplitude = 1 • " +
                (1000d * _audio.WindowFrames / _audio.SampleRate).ToString("0.00") + " ms window";
        }
        _waveDisplayCount = Math.Max(1, _audio.WaveCount / 2);
        _waveStart = 0;
        for (var i = 1; i <= _audio.WaveCount - _waveDisplayCount; i++)
            if (_audio.Wave[i - 1] <= 0 && _audio.Wave[i] > 0) { _waveStart = i; break; }
        var peak = 0d;
        for (var i = 0; i < _waveDisplayCount; i++)
            peak = Math.Max(peak, Math.Abs((double)_audio.Wave[_waveStart + i]));
        _waveRange = Math.Pow(2, Math.Ceiling(Math.Log(Math.Max(1d / 1024d, peak), 2)));
        if (_captionWaveCount != _waveDisplayCount || _captionWaveRate != _audio.SampleRate || _captionWaveRange != _waveRange)
        {
            _captionWaveCount = _waveDisplayCount;
            _captionWaveRate = _audio.SampleRate;
            _captionWaveRange = _waveRange;
            _waveCaption = "Left waveform • " + (1000d * Math.Max(0, _waveDisplayCount - 1) / _audio.SampleRate).ToString("0.00") +
                " ms • auto range ±" + _waveRange.ToString("G4") + " FS";
        }
    }

    private void EnsureAnalysisFonts()
    {
        if (ReferenceEquals(Font, _analysisFontSource)) return;
        _analysisFontSource = Font;
        _analysisBodyFont?.Dispose();
        _analysisTitleFont?.Dispose();
        _analysisBodyFont = new Font(Font.FontFamily, Math.Max(7f, Font.SizeInPoints - 0.5f));
        _analysisTitleFont = new Font(Font.FontFamily, Math.Max(8f, Font.SizeInPoints + 0.5f), FontStyle.Bold);
    }

    private void UpdateAnalysisLabels()
    {
        for (var channel = 0; channel < 2; channel++)
        {
            var bands = channel == 0 ? _audio.LeftSpectrum : _audio.RightSpectrum;
            var strongest = 0;
            for (var band = 1; band < BandCount; band++) if (bands[band] > bands[strongest]) strongest = band;
            var identity = channel == 0 ? _audio.Channels >= 2 ? "LEFT" : "MONO" : "RIGHT";
            _channelSpectrumLabels[channel] = identity + (!_audio.HasChannelSpectrum || (channel == 1 && !_audio.HasStereo)
                ? "  •  unavailable" : bands[strongest] == 0 ? "  •  no band above display floor"
                    : "  •  strongest band " + _bandLabels[strongest] + "  ·  " + s_levelLabels[bands[strongest]]);
            if (_audio.ObservationCount == 0) continue;
            var latest = _audio.ObservationAt(_audio.ObservationCount - 1);
            var crest = latest.CrestDb(channel);
            _dynamicsLabels[channel] = (channel == 0 ? _audio.Channels >= 2 ? "LEFT" : "MONO" : "RIGHT") +
                "  •  RMS " + (_audio.HasSamples ? _audioLabels[channel * 2] : "unavailable") +
                "  •  sample peak " + (_audio.HasSamples ? _audioLabels[channel * 2 + 1] : "unavailable") +
                "  •  crest " + (double.IsNaN(crest) ? "—" : crest.ToString("0.0") + " dB");
        }
        _dynamicsCaption = "DYNAMICS HISTORY  •  PCM window " +
            (_audio.HasSamples ? (1000d * _audio.WindowFrames / _audio.SampleRate).ToString("0.00") + " ms" : "unavailable") +
            "  •  RMS dashed / sample peak solid";
    }

    private void PaintStereoSpectrum(Graphics graphics, int width, int height)
    {
        EnsureAnalysisFonts();
        var scale = graphics.DpiY / 96f;
        var bodyHeight = _analysisBodyFont.GetHeight(graphics);
        var titleHeight = _analysisTitleFont.GetHeight(graphics);
        var labeled = width >= 340 * scale && height >= 130 * scale;
        var compactIdentifiers = !labeled && width >= 180 * scale && height >= 80 * scale;
        var header = labeled ? bodyHeight + 10 * scale : 0;
        var footer = labeled ? bodyHeight + 7 * scale : 0;
        var left = labeled ? 48 * scale : 2 * scale;
        var right = Math.Max(left + 1, width - (labeled ? 10 : 2) * scale);
        if (labeled)
            graphics.DrawString(width >= 620 * scale
                ? "STEREO SPECTRUM  •  front L/R  •  full-scale-sine band power, −72 … 0 dBFS"
                : "STEREO SPECTRUM  •  L/R  •  sine ref  •  −72 … 0 dBFS",
                _analysisBodyFont, _textBrush, 8 * scale, 4 * scale);
        var panelHeight = Math.Max(1, (height - header - footer) / 2);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var channel = 0; channel < 2; channel++)
        {
            var panelY = header + channel * panelHeight;
            var top = panelY + (labeled ? titleHeight + 8 * scale : compactIdentifiers ? bodyHeight + 6 * scale : 2 * scale);
            var bottom = Math.Max(top + 1, panelY + panelHeight - 7 * scale);
            graphics.FillRectangle(_surfaceBrush, left, top, right - left, bottom - top);
            if (labeled)
                graphics.DrawString(_channelSpectrumLabels[channel], _analysisTitleFont, _primaryTextBrush, left, panelY + 2 * scale);
            else if (compactIdentifiers)
                graphics.DrawString(channel == 0 ? _audio.Channels >= 2 ? "LEFT" : "MONO" : "RIGHT",
                    _analysisBodyFont, _primaryTextBrush, left, panelY + 2 * scale);
            DrawAnalysisGrid(graphics, left, right, top, bottom, labeled, scale);
            if (!_audio.HasChannelSpectrum || (channel == 1 && !_audio.HasStereo))
            {
                if (labeled)
                    graphics.DrawString(channel == 1 && _audio.HasSamples && !_audio.HasStereo ? "RIGHT unavailable — mono input" : "Frequency data unavailable",
                        _analysisBodyFont, _textBrush, left + 6 * scale, top + 4 * scale);
                continue;
            }
            var bands = channel == 0 ? _audio.LeftSpectrum : _audio.RightSpectrum;
            _linePen.Color = channel == 0 ? _theme.Low : _theme.High;
            _linePen.Width = 1.8f * scale;
            var previous = new PointF(left, bottom - (bottom - top) * bands[0] / 255f);
            for (var band = 0; band < BandCount; band++)
            {
                var x = left + (right - left) * band / (BandCount - 1);
                var y = bottom - (bottom - top) * bands[band] / 255f;
                if (bands[band] > 0)
                    graphics.FillRectangle(_levelBrushes[bands[band]], x, y,
                        Math.Max(scale, (right - left) / (BandCount - 1) * 0.55f), Math.Max(scale, bottom - y));
                var point = new PointF(x, y);
                if (band > 0 && (bands[band] > 0 || bands[band - 1] > 0)) graphics.DrawLine(_linePen, previous, point);
                previous = point;
            }
        }
        if (labeled)
            for (var index = 0; index < 5; index++)
            {
                var band = index == 4 ? 82 : index * 20;
                var x = left + (right - left) * band / 82;
                graphics.DrawString(_bandLabels[band], _analysisBodyFont, _textBrush,
                    new RectangleF(Math.Max(0, Math.Min(width - 80 * scale, x - 40 * scale)), height - footer, 80 * scale, bodyHeight + 2 * scale), _centeredText);
            }
    }

    private void DrawAnalysisGrid(Graphics graphics, float left, float right, float top, float bottom, bool labeled, float scale)
    {
        var guides = _style == VisualStyle.Precision ? 6 : 3;
        for (var guide = 0; guide <= guides; guide++)
        {
            var y = bottom - (bottom - top) * guide / guides;
            graphics.DrawLine(_gridPen, left, y, right, y);
            if (labeled && (guide == 0 || guide == guides || guide * 2 == guides))
                graphics.DrawString(s_referenceDbLabels[guide * 6 / guides], _analysisBodyFont, _textBrush, 3 * scale, y - _analysisBodyFont.GetHeight(graphics) / 2);
        }
    }

    private void PaintDynamicsHistory(Graphics graphics, int width, int height)
    {
        EnsureAnalysisFonts();
        var scale = graphics.DpiY / 96f;
        var bodyHeight = _analysisBodyFont.GetHeight(graphics);
        var titleHeight = _analysisTitleFont.GetHeight(graphics);
        var labeled = width >= 480 * scale && height >= 150 * scale;
        var compactIdentifiers = !labeled && width >= 180 * scale && height >= 80 * scale;
        var header = labeled ? bodyHeight + 10 * scale : 0;
        var footer = labeled ? bodyHeight + 8 * scale : 0;
        var left = labeled ? 48 * scale : 2 * scale;
        var right = Math.Max(left + 1, width - 10 * scale);
        if (labeled) graphics.DrawString(_dynamicsCaption, _analysisBodyFont, _textBrush, 8 * scale, 4 * scale);
        if (_audio.ObservationCount == 0 || _audio.SampleRate < 1)
        {
            DrawUnavailable(graphics, width, height);
            return;
        }
        var oldestFrame = _audio.EndFrame - 20L * _audio.SampleRate;
        var channels = _audio.Channels >= 2 ? 2 : 1;
        var panelHeight = Math.Max(1, (height - header - footer) / channels);
        _peakHistoryPen.Width = 1.5f * scale;
        _rmsHistoryPen.Width = 1.5f * scale;
        for (var channel = 0; channel < channels; channel++)
        {
            var panelY = header + channel * panelHeight;
            var top = panelY + (labeled ? titleHeight + 8 * scale : compactIdentifiers ? bodyHeight + 6 * scale : 2 * scale);
            var bottom = Math.Max(top + 1, panelY + panelHeight - 7 * scale);
            graphics.FillRectangle(_surfaceBrush, left, top, right - left, bottom - top);
            if (labeled) graphics.DrawString(_dynamicsLabels[channel], _analysisTitleFont, _primaryTextBrush, left, panelY + 2 * scale);
            else if (compactIdentifiers)
                graphics.DrawString(channel == 0 ? channels == 2 ? "LEFT" : "MONO" : "RIGHT",
                    _analysisBodyFont, _primaryTextBrush, left, panelY + 2 * scale);
            DrawAnalysisGrid(graphics, left, right, top, bottom, labeled, scale);
            for (var index = 0; index < _audio.ObservationCount; index++)
            {
                var entry = _audio.ObservationAt(index);
                if (entry.EndFrame <= oldestFrame || !entry.Available || (channel == 1 && !entry.Stereo)) continue;
                var x1 = Math.Max(left, left + (right - left) * (entry.StartFrame - oldestFrame) / (20d * _audio.SampleRate));
                var x2 = Math.Min(right, left + (right - left) * (entry.EndFrame - oldestFrame) / (20d * _audio.SampleRate));
                var rms = channel == 0 ? entry.LeftRms : entry.RightRms;
                var peak = channel == 0 ? entry.LeftPeak : entry.RightPeak;
                var rmsY = bottom - (bottom - top) * (float)LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(rms));
                var peakY = bottom - (bottom - top) * (float)LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(peak));
                if (entry.Discontinuity) graphics.DrawLine(_precisionPen, (float)x1, top, (float)x1, bottom);
                if (index > 0 && !entry.Discontinuity)
                {
                    var prior = _audio.ObservationAt(index - 1);
                    var priorRms = channel == 0 ? prior.LeftRms : prior.RightRms;
                    var priorPeak = channel == 0 ? prior.LeftPeak : prior.RightPeak;
                    graphics.DrawLine(_rmsHistoryPen, (float)x1, bottom - (bottom - top) * (float)LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(priorRms)), (float)x1, rmsY);
                    graphics.DrawLine(_peakHistoryPen, (float)x1, bottom - (bottom - top) * (float)LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(priorPeak)), (float)x1, peakY);
                }
                graphics.DrawLine(_rmsHistoryPen, (float)x1, rmsY, (float)x2, rmsY);
                graphics.DrawLine(_peakHistoryPen, (float)x1, peakY, (float)x2, peakY);
            }
        }
        if (labeled)
            for (var tick = 0; tick <= 4; tick++)
                graphics.DrawString(tick == 4 ? "LATEST" : tick == 0 ? "−20 s" : tick == 1 ? "−15 s" : tick == 2 ? "−10 s" : "−5 s",
                    _analysisBodyFont, _textBrush, new RectangleF(Math.Max(0, Math.Min(width - 64 * scale, left + (right - left) * tick / 4 - 32 * scale)), height - footer, 64 * scale, bodyHeight + 2 * scale), _centeredText);
    }

    private void PaintStereoLevels(Graphics graphics, int width, int height)
    {
        if (!_audio.HasSamples) { DrawUnavailable(graphics, width, height); return; }
        var labeled = width >= 260 && height >= 140;
        var channels = _audio.HasStereo ? 2 : 1;
        var cardWidth = width / (float)channels;
        var top = labeled ? 58f : 3f;
        var bottom = Math.Max(top + 1, height - (labeled ? 42f : 3f));
        var plotHeight = bottom - top;
        _peakPen.DashStyle = DashStyle.Solid;
        for (var channel = 0; channel < channels; channel++)
        {
            var x = channel * cardWidth + 6;
            var innerWidth = Math.Max(4, cardWidth - 12);
            graphics.FillRectangle(_surfaceBrush, x, top, innerWidth, plotHeight);
            var guides = _style == VisualStyle.Precision ? 6 : 2;
            for (var guide = 0; guide <= guides; guide++)
            {
                var y = bottom - plotHeight * guide / guides;
                graphics.DrawLine(_majorGridPen, x, y, x + innerWidth, y);
                if (labeled && width >= 520)
                    graphics.DrawString(s_referenceDbLabels[guide * 6 / guides], Font, _textBrush, x + 2, y - 8);
            }
            for (var measure = 0; measure < 2; measure++)
            {
                var index = channel * 2 + measure;
                var normalized = (float)LevelScale.Normalize(_audioDb[index]);
                var barWidth = Math.Max(2, Math.Min(34, innerWidth * 0.23f));
                var barX = x + innerWidth * (measure == 0 ? 0.25f : 0.65f) - barWidth / 2;
                var fillHeight = plotHeight * normalized;
                _thresholdBrush.Color = channel == 0 ? _theme.Low : _theme.High;
                if (normalized > 0)
                {
                    graphics.FillRectangle(_thresholdBrush, barX, bottom - fillHeight, barWidth, fillHeight);
                    graphics.DrawLine(_peakPen, barX - 1, bottom - fillHeight, barX + barWidth + 1, bottom - fillHeight);
                }
                if (labeled)
                {
                    graphics.DrawString(measure == 0 ? "RMS" : "Sample peak", Font, _textBrush,
                        new RectangleF(barX - 32, bottom + 2, barWidth + 64, 16), _centeredText);
                    graphics.DrawString(_audioLabels[index], Font, _textBrush,
                        new RectangleF(barX - 32, bottom + 18, barWidth + 64, 16), _centeredText);
                }
            }
            if (labeled)
                graphics.DrawString(_audio.HasStereo ? channel == 0 ? "LEFT" : "RIGHT" : "MONO", Font, _textBrush,
                    new RectangleF(x, 29, innerWidth, 20), _centeredText);
        }
        DrawCaption(graphics, width, _audioCaption);
    }

    private void PaintOscilloscope(Graphics graphics, int width, int height)
    {
        if (!_audio.HasSamples || _waveDisplayCount < 1) { DrawUnavailable(graphics, width, height); return; }
        var top = height >= 80 ? 25f : 2f;
        var bottom = Math.Max(top + 1, height - 4f);
        var center = (top + bottom) / 2;
        var halfHeight = (bottom - top) / 2;
        graphics.DrawLine(_majorGridPen, 0, center, width, center);
        var guides = _style == VisualStyle.Precision ? 8 : 4;
        for (var i = 1; i < guides; i++)
            graphics.DrawLine(_gridPen, width * i / (float)guides, top, width * i / (float)guides, bottom);
        _linePen.Width = 1.4f;
        _linePen.Color = _theme.Low;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_waveDisplayCount <= width)
        {
            var previous = new PointF(0, center - (float)(_audio.Wave[_waveStart] / _waveRange) * halfHeight);
            if (_waveDisplayCount == 1)
                graphics.DrawLine(_linePen, 0, previous.Y, width - 1, previous.Y);
            for (var i = 1; i < _waveDisplayCount; i++)
            {
                var point = new PointF(i * (width - 1f) / Math.Max(1, _waveDisplayCount - 1),
                    center - (float)(_audio.Wave[_waveStart + i] / _waveRange) * halfHeight);
                graphics.DrawLine(_linePen, previous, point);
                previous = point;
            }
        }
        else
        {
            // Preserve sample extrema when several real samples share a pixel column.
            var previousEnd = new PointF(0, center - (float)(_audio.Wave[_waveStart] / _waveRange) * halfHeight);
            for (var x = 0; x < width; x++)
            {
                var start = x * _waveDisplayCount / width;
                var end = (x + 1) * _waveDisplayCount / width;
                float minimum = float.MaxValue, maximum = float.MinValue;
                for (var sample = start; sample < end; sample++)
                {
                    var value = _audio.Wave[_waveStart + sample];
                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                }
                graphics.DrawLine(_linePen, x, center - (float)(maximum / _waveRange) * halfHeight,
                    x, center - (float)(minimum / _waveRange) * halfHeight);
                var first = new PointF(x, center - (float)(_audio.Wave[_waveStart + start] / _waveRange) * halfHeight);
                if (x > 0) graphics.DrawLine(_linePen, previousEnd, first);
                previousEnd = new PointF(x, center - (float)(_audio.Wave[_waveStart + end - 1] / _waveRange) * halfHeight);
            }
        }
        DrawCaption(graphics, width, _waveCaption);
    }

    private void PaintSpectralBalance(Graphics graphics, int width, int height)
    {
        var top = height >= 100 ? 30f : 2f;
        var rowHeight = Math.Max(1, (height - top - 3) / 3);
        for (var group = 0; group < 3; group++)
        {
            var x = width >= 300 ? 170f : 2f;
            var y = top + group * rowHeight;
            var available = Math.Max(1, width - x - (width >= 300 ? 70 : 2));
            graphics.FillRectangle(_surfaceBrush, x, y + 3, available, Math.Max(1, rowHeight - 6));
            if (_style == VisualStyle.Precision)
                for (var tick = 1; tick < 4; tick++)
                    graphics.DrawLine(_precisionPen, x + available * tick / 4, y + 3, x + available * tick / 4, y + rowHeight - 3);
            _thresholdBrush.Color = group == 0 ? _theme.Low : group == 1 ? _theme.Mid : _theme.High;
            if (_balance[group] > 0)
                graphics.FillRectangle(_thresholdBrush, x, y + 3, (float)(_balance[group] * available), Math.Max(1, rowHeight - 6));
            if (width >= 300)
            {
                graphics.DrawString(group == 0 ? "Bass: centers <250 Hz" : group == 1 ? "Mid: 250 Hz–4 kHz" : "High: centers ≥4 kHz",
                    Font, _textBrush, new RectangleF(4, y, x - 8, rowHeight), _centeredText);
                graphics.DrawString(_balanceLabels[group], Font, _textBrush,
                    new RectangleF(x + available + 3, y, 65, rowHeight), _centeredText);
            }
        }
        DrawCaption(graphics, width, "Relative power shares • estimated from displayed frequency bands");
    }

    private void DrawCaption(Graphics graphics, int width, string text)
    {
        if (width < 180 || ClientSize.Height < 80) return;
        _thresholdBrush.Color = BackColor;
        graphics.FillRectangle(_thresholdBrush, 2, 2, width - 4, 19);
        graphics.DrawString(text, Font, _textBrush, new RectangleF(4, 3, width - 8, 17), _centeredText);
    }

    private void DrawUnavailable(Graphics graphics, int width, int height)
    {
        graphics.DrawLine(_majorGridPen, 0, height / 2f, width, height / 2f);
        graphics.DrawString("PCM samples not supplied", Font, _textBrush,
            new RectangleF(4, Math.Max(0, height / 2f - 20), Math.Max(1, width - 8), 20), _centeredText);
    }

    private void ReplaceHistoryImage(int frames)
    {
        var pixels = new int[HistoryImageWidth * frames];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        Bitmap image;
        try { image = new Bitmap(HistoryImageWidth, frames, HistoryImageWidth * sizeof(int), PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject()); }
        catch { handle.Free(); throw; }
        _historyImage?.Dispose();
        if (_historyPixelsHandle.IsAllocated) _historyPixelsHandle.Free();
        _historyPixels = pixels;
        _historyPixelsHandle = handle;
        _historyImage = image;
    }

    private void PaintHistoryImage(Graphics graphics, int width, int height, bool octaves)
    {
        if (_historyCount == 0) return;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        var plotTop = width >= 180 && height >= 80 ? 22 : 0;
        var labels = octaves && width >= 350 && height >= 100;
        var plotHeight = Math.Max(1, height - plotTop - (labels ? 20 : 0));
        var slots = Math.Min(_historyFrames, plotHeight);
        var rows = Math.Min(_historyCount, slots);
        var start = (_historyWriteIndex - rows + _historyFrames) % _historyFrames;
        var firstRows = Math.Min(rows, _historyFrames - start);
        var blankRows = slots - rows;
        var top = plotTop + blankRows * plotHeight / slots;
        var split = plotTop + (blankRows + firstRows) * plotHeight / slots;
        var sourceX = octaves ? BandCount : 0;
        var sourceWidth = octaves ? OctaveCount : BandCount;
        graphics.DrawImage(_historyImage, new Rectangle(0, top, width, split - top),
            new Rectangle(sourceX, start, sourceWidth, firstRows), GraphicsUnit.Pixel);
        if (rows > firstRows)
            graphics.DrawImage(_historyImage, new Rectangle(0, split, width, plotTop + plotHeight - split),
                new Rectangle(sourceX, 0, sourceWidth, rows - firstRows), GraphicsUnit.Pixel);
        if (labels)
        {
            for (var group = 0; group < OctaveCount; group++)
                graphics.DrawString(width >= 1000 ? s_octaveBandLabels[group] : s_octaveLowerLabels[group], Font, _textBrush,
                    new RectangleF(group * width / (float)OctaveCount, height - 18, width / (float)OctaveCount, 16), _centeredText);
        }
        if (_style == VisualStyle.Precision)
        {
            var columns = octaves ? OctaveCount : 8;
            for (var column = 1; column < columns; column++)
                graphics.DrawLine(_precisionPen, width * column / (float)columns, 0, width * column / (float)columns, height);
        }
        DrawCaption(graphics, width, _historyCaption);
    }

    private void WriteHistoryImageRow(int row, int offset)
    {
        var imageOffset = row * HistoryImageWidth;
        Array.Clear(_octavePower, 0, _octavePower.Length);
        for (var band = 0; band < BandCount; band++)
        {
            var level = _history[offset + band];
            _historyPixels[imageOffset + band] = _paletteArgb[level];
            _octavePower[_octaveByBand[band]] += s_displayPower[level];
        }
        for (var group = 0; group < OctaveCount; group++)
            _historyPixels[imageOffset + BandCount + group] = _paletteArgb[
                LevelScale.ToDisplayByte(LevelScale.PowerToDb(_octavePower[group]))];
    }

    private void RebuildHistoryImage()
    {
        if (_historyPixels == null || _history == null) return;
        for (var row = 0; row < _history.Length / BandCount; row++)
            WriteHistoryImageRow(row, row * BandCount);
    }

    private void ResetHistory()
    {
        _historyWriteIndex = _historyCount = 0;
        _lastFrameTick = 0;
        _historySpanTenths = -1;
        _historyCaption = "No displayed history";
        Array.Clear(_history, 0, _history.Length);
        Array.Clear(_spectrum, 0, BandCount);
        Array.Clear(_previousSpectrum, 0, BandCount);
        Array.Clear(_heldPeaks, 0, BandCount);
        Array.Clear(_peakHoldMs, 0, BandCount);
        UpdateAggregates();
        RebuildHistoryImage();
    }

    private void UpdateHistoryCaption()
    {
        if (_historyTicks == null || _historyCount == 0) return;
        var top = ClientSize.Width >= 180 && ClientSize.Height >= 80 ? 22 : 0;
        var bottom = _mode == AdvancedVisualizationMode.OctaveWaterfall && ClientSize.Width >= 350 && ClientSize.Height >= 100 ? 20 : 0;
        var frames = Math.Min(_historyCount, Math.Max(1, ClientSize.Height - top - bottom));
        var oldest = GetHistoryFrame(frames - 1);
        var newest = GetHistoryFrame(0);
        var tenths = (int)((_historyTicks[newest] - _historyTicks[oldest]) * 10 / Stopwatch.Frequency);
        if (tenths == _historySpanTenths && frames == _captionFrames) return;
        _historySpanTenths = tenths;
        _captionFrames = frames;
        _historyCaption = (tenths / 10d).ToString("0.0") + " s • " + frames + " displayed frames • newest at bottom";
    }

    private void UpdateAggregates()
    {
        Array.Clear(_octavePower, 0, _octavePower.Length);
        Array.Clear(_balance, 0, _balance.Length);
        for (var band = 0; band < BandCount; band++)
        {
            var power = s_displayPower[_spectrum[band]];
            _octavePower[_octaveByBand[band]] += power;
            var hz = _bandPlan[band].CenterHz;
            _balance[hz < 250 ? 0 : hz < 4000 ? 1 : 2] += power;
        }
        for (var group = 0; group < OctaveCount; group++)
            _octaveLevels[group] = LevelScale.ToDisplayByte(LevelScale.PowerToDb(_octavePower[group]));
        var total = _balance[0] + _balance[1] + _balance[2];
        for (var group = 0; group < 3; group++)
        {
            _balance[group] = total > 0 ? _balance[group] / total : 0;
            var tenths = (int)Math.Round(_balance[group] * 1000);
            if (_balanceLabelTenths[group] == tenths) continue;
            _balanceLabelTenths[group] = tenths;
            _balanceLabels[group] = (_balance[group] * 100).ToString("0.0") + "%";
        }
    }

    internal static void UpdatePeakEnvelope(float[] peaks, float[] holds, byte[] levels, float elapsedMs)
    {
        var elapsed = Math.Max(0, elapsedMs);
        for (var band = 0; band < BandCount; band++)
        {
            if (levels[band] >= peaks[band])
            {
                peaks[band] = levels[band];
                holds[band] = 300;
                continue;
            }
            // A frame may span both hold and release. Consume its remaining elapsed time
            // immediately so slow frames or a UI pause do not extend the measured hold.
            var decayMs = Math.Max(0, elapsed - holds[band]);
            holds[band] = Math.Max(0, holds[band] - elapsed);
            peaks[band] = Math.Max(levels[band], peaks[band] - 160 * decayMs / 1000);
        }
    }

    private static AdvancedVisualizationMode NormalizeMode(AdvancedVisualizationMode mode) => mode switch
    {
        AdvancedVisualizationMode.OrbitHistory or AdvancedVisualizationMode.FrequencyRibbon => AdvancedVisualizationMode.PeakTrace,
        AdvancedVisualizationMode.TransientMap => AdvancedVisualizationMode.SpectralFlux,
        _ => mode
    };

    private static bool IsMeasurementMode(AdvancedVisualizationMode mode) =>
        mode is AdvancedVisualizationMode.StereoLevels or AdvancedVisualizationMode.Oscilloscope or AdvancedVisualizationMode.SpectralBalance
            or AdvancedVisualizationMode.StereoSpectrum or AdvancedVisualizationMode.DynamicsHistory;

    private static double[] BuildDisplayPower()
    {
        var values = new double[256];
        for (var i = 0; i < values.Length; i++) values[i] = LevelScale.DisplayByteToRelativePower((byte)i);
        return values;
    }

    private static string[] BuildLevelLabels()
    {
        var labels = new string[256];
        labels[0] = "— dBFS";
        for (var i = 1; i < labels.Length; i++)
            labels[i] = (LevelScale.FloorDb + 72d * i / 255).ToString("0.0") + " dBFS";
        return labels;
    }

    private static string[] BuildMatrixValueLabels()
    {
        var labels = new string[256];
        labels[0] = "FLOOR";
        for (var i = 1; i < labels.Length; i++)
            labels[i] = (LevelScale.FloorDb + 72d * i / 255).ToString("0.0");
        return labels;
    }

    private static string[] BuildOctaveLabels()
    {
        var labels = new string[OctaveCount];
        for (var i = 0; i < labels.Length; i++)
        {
            var lower = 20 << i;
            var upper = lower * 2;
            labels[i] = lower >= 1000 ? (lower / 1000d).ToString("0.##") + "–" + (upper / 1000d).ToString("0.##") + "k" :
                lower + "–" + upper;
        }
        return labels;
    }

    private static PointF[] BuildRadialDirections()
    {
        var directions = new PointF[BandCount];
        for (var band = 0; band < directions.Length; band++)
        {
            var angle = 2 * Math.PI * band / BandCount - Math.PI / 2;
            directions[band] = new PointF((float)Math.Cos(angle), (float)Math.Sin(angle));
        }
        return directions;
    }

    private void BuildNoteMap(BandPlan plan)
    {
        for (var band = 0; band < BandCount; band++)
        {
            var frequency = plan[band].CenterHz;
            var midiNote = (int)Math.Round(69.0 + (12.0 * Math.Log(frequency / 440.0, 2.0)));
            var octave = (midiNote / 12) - 1;
            var pitchClass = midiNote % 12;
            if (pitchClass < 0)
            {
                pitchClass += 12;
            }

            octave = Math.Max(MinimumNoteOctave, Math.Min(NoteOctaveCount - 1, octave));
            _noteByBand[band] = (octave * 12) + pitchClass;
        }
    }

    private void CopyAndAppendFrame(byte[] values)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var copyCount = Math.Min(values.Length, BandCount);
        Array.Copy(_spectrum, _previousSpectrum, _spectrum.Length);
        Array.Clear(_spectrum, 0, _spectrum.Length);
        Array.Copy(values, 0, _spectrum, 0, copyCount);
        var tick = Stopwatch.GetTimestamp();
        var elapsedMs = _lastFrameTick == 0 ? 0f : (float)Math.Max(0, (tick - _lastFrameTick) * 1000d / Stopwatch.Frequency);
        _lastFrameTick = tick;
        UpdatePeakEnvelope(_heldPeaks, _peakHoldMs, _spectrum, elapsedMs);
        UpdateAggregates();

        var historyOffset = _historyWriteIndex * BandCount;
        Array.Copy(_spectrum, 0, _history, historyOffset, BandCount);
        _historyTicks[_historyWriteIndex] = tick;
        WriteHistoryImageRow(_historyWriteIndex, historyOffset);
        _historyWriteIndex++;
        if (_historyWriteIndex == _historyFrames)
        {
            _historyWriteIndex = 0;
        }

        if (_historyCount < _historyFrames)
        {
            _historyCount++;
        }
        UpdateHistoryCaption();
    }

    private static bool TryParseMode(string mode, out AdvancedVisualizationMode parsedMode)
    {
        if (string.Equals(mode, "Waterfall", StringComparison.OrdinalIgnoreCase))
        {
            parsedMode = AdvancedVisualizationMode.Waterfall;
            return true;
        }

        if (string.Equals(mode, "RadialSpectrum", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Radial Spectrum", StringComparison.OrdinalIgnoreCase))
        {
            parsedMode = AdvancedVisualizationMode.RadialSpectrum;
            return true;
        }

        if (string.Equals(mode, "Contour", StringComparison.OrdinalIgnoreCase))
        {
            parsedMode = AdvancedVisualizationMode.Contour;
            return true;
        }

        if (string.Equals(mode, "NoteMap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "Note Map", StringComparison.OrdinalIgnoreCase))
        {
            parsedMode = AdvancedVisualizationMode.NoteMap;
            return true;
        }

        if (string.Equals(mode, "Level Change", StringComparison.OrdinalIgnoreCase))
        {
            parsedMode = AdvancedVisualizationMode.SpectralFlux;
            return true;
        }
        if (Enum.TryParse(mode?.Replace(" ", string.Empty), true, out AdvancedVisualizationMode parsed) &&
            Enum.IsDefined(typeof(AdvancedVisualizationMode), parsed))
        {
            parsedMode = NormalizeMode(parsed);
            return true;
        }

        parsedMode = default;
        return false;
    }

    private static void ValidateMode(AdvancedVisualizationMode mode, string parameterName)
    {
        if (!Enum.IsDefined(typeof(AdvancedVisualizationMode), mode))
        {
            throw new ArgumentOutOfRangeException(parameterName, mode, "Unknown visualization mode.");
        }
    }

    private void UpdatePalette()
    {
        for (var level = 0; level < _levelBrushes.Length; level++)
        {
            Color color;
            if (level == 0)
            {
                color = BackColor;
            }
            else
            {
                // The analyzer gates zero separately. Every non-zero display level therefore starts
                // at the contrast-validated Low token instead of fading into the canvas.
                var position = (level - 1) / 254f;
                if (position < (1f / 3f))
                {
                    color = Blend(_theme.Low, _theme.Mid, position * 3f);
                }
                else if (position < (2f / 3f))
                {
                    color = Blend(_theme.Mid, _theme.High, (position - (1f / 3f)) * 3f);
                }
                else
                {
                    color = Blend(_theme.High, _theme.Peak, (position - (2f / 3f)) * 3f);
                }
            }

            _levelBrushes[level].Color = color;
            if (_radialPens[level] != null) _radialPens[level].Color = color;
            if (_matrixSurfaceBrushes[level] != null)
                _matrixSurfaceBrushes[level].Color = Blend(_theme.Ui.RaisedSurface, color, level == 0 ? 0 : 0.025f + level / 255f * 0.09f);
            _paletteArgb[level] = color.ToArgb();
        }
        RebuildHistoryImage();
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Max(0f, Math.Min(1f, amount));
        return Color.FromArgb(
            from.A + (int)((to.A - from.A) * amount),
            from.R + (int)((to.R - from.R) * amount),
            from.G + (int)((to.G - from.G) * amount),
            from.B + (int)((to.B - from.B) * amount));
    }
}
