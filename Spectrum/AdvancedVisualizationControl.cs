// Developer :: > Gehan Fernando
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum.Dsp;

namespace Spectrum;

// every view the control can draw; the numbers are persisted so don't renumber them
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
    // old ids kept alive so saved settings still map onto a view that exists
    OrbitHistory = 9,
    OctaveWaterfall = 10,
    TransientMap = 11,
    FrequencyRibbon = 12,
    StereoLevels = 13,
    Oscilloscope = 14,
    SpectralBalance = 15,
    StereoSpectrum = 16,
    DynamicsHistory = 17,
    SpectralHistory = 18,
    AdvancedSpectralBalance = 19,
    Overview = 20,
    StereoCompare = 21
}

// 83-band renderer fed copied analyzer frames on the UI thread; painting only shows data, it never measures anything
public sealed class AdvancedVisualizationControl : Control
{
    public const int BandCount = 83;

    private const int MaximumHistoryFrames = 2048;
    private const int DefaultHistoryFrames = 160;
    private const int MinimumNoteOctave = 0;
    private const int NoteOctaveCount = 11;
    private const int OctaveCount = 10;
    private const int HistoryImageWidth = BandCount + OctaveCount;
    private const int PerspectiveSlices = 12;
    private const int PerspectiveHistoryFrames = 64;
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
    private bool _fluxBaselineValid, _fluxComparisonAvailable;
    private bool _fluxAudioBound, _fluxAudioAvailable, _fluxAudioFrameNew;
    private object _fluxSourceEpoch;
    private long _fluxEndFrame;
    private int _fluxSampleRate, _fluxChannels;
    internal bool LevelChangeHasComparison => _fluxComparisonAvailable;
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
    private readonly float[] _sevenLevels = new float[SevenBandAnalyzer.BandCount];
    private readonly float[] _sevenPeaks = new float[SevenBandAnalyzer.BandCount];
    private readonly float[] _sevenPeakHoldMs = new float[SevenBandAnalyzer.BandCount];
    private readonly bool[] _sevenAvailable = new bool[SevenBandAnalyzer.BandCount];
    private readonly string[] _sevenLabels = new string[SevenBandAnalyzer.BandCount];
    private readonly int[] _sevenLabelTenths = new int[SevenBandAnalyzer.BandCount];
    private long _sevenLastTick;
    private readonly System.Windows.Forms.Timer _sevenTimer;
    private Form _sevenHost;
    private bool _sevenHasData;
    private readonly int[] _balanceLabelTenths = new int[3];
    private readonly AudioVisualizationData _audio = new();
    private readonly string[] _audioLabels = { "—", "—", "—", "—" };
    private readonly string[] _audioShortLabels = { "—", "—", "—", "—" };
    private readonly string[] _waveTimeLabels = new string[5];
    private readonly string[] _waveAmplitudeLabels = new string[5];
    private readonly double[] _audioDb = new double[4];
    private readonly int[] _audioLabelTenths = { int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue };
    private int _captionAudioFrames;
    private int _captionAudioRate;
    private bool _captionStereo;
    private int _captionWaveCount;
    private int _captionWaveRate;
    private bool _captionWaveStereo;
    private double _captionWaveRange;
    private string _audioCaption = "PCM samples not supplied";
    private string _waveCaption = "PCM samples not supplied";
    private string _historyCaption = "No displayed history";
    private string _radialCaption;
    private float _radialPenWidth;
    private Font _radialLayoutFont;
    private Size _radialLayoutSize;
    private float _radialLayoutDpi;
    private PointF _radialCenter;
    internal PointF RadialCenter => _radialCenter;
    internal float RadialOuterRadius => _radialRadius;
    internal RectangleF GetMatrixCardBounds(int band) => _matrixCards[band];
    internal RectangleF StereoComparePlotBounds { get; private set; }
    private float _radialRadius;
    private bool _radialLabelsVisible;
    private readonly RectangleF[] _radialLabelBounds = new RectangleF[s_radialLabelBands.Length];
    private RectangleF _radialCaptionBounds;
    private RectangleF _radialLegendBounds;
    private string _radialLayoutCaption;
    private string _radialLayoutLegend;
    private double _waveRange = 1;
    private int _waveStart;
    private int _waveDisplayCount;
    private int _historySpanTenths = -1;
    private string _historyOldestLabel = "Oldest received";
    private int _captionFrames;
    private AdvancedVisualizationMode _captionHistoryMode = (AdvancedVisualizationMode)(-1);
    private readonly int[] _noteByBand = new int[BandCount];
    private readonly byte[] _noteLevels = new byte[12 * NoteOctaveCount];
    private readonly PointF[] _plotPoints = new PointF[BandCount];
    private readonly RectangleF[] _stereoSpectrumPlotBounds = new RectangleF[2];
    private readonly SolidBrush[] _levelBrushes = new SolidBrush[256];
    private readonly Pen[] _radialPens = new Pen[256];
    private readonly Pen[] _historyTracePens = new Pen[PerspectiveSlices];
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
    private float _analysisFontSize;
    private Font _analysisTitleFont;
    private readonly Pen _rmsHistoryPen = new Pen(Color.Empty, 1.5f) { DashStyle = DashStyle.Dash };
    private readonly Pen _peakHistoryPen = new Pen(Color.Empty, 1.5f);
    private readonly GraphicsPath _dynamicsRmsPath = new GraphicsPath();
    private readonly GraphicsPath _dynamicsPeakPath = new GraphicsPath();
    private readonly GraphicsPath _dynamicsBreakPath = new GraphicsPath();
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
    private readonly StringFormat _singleLineText = new StringFormat
    {
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
    private bool _showGuides;
    private bool _displayFrozen;
    private bool _resumeSpectrum;
    private int _deltaRangeDb = 12;
    private string _deltaCaption = "Display-level change • ±12 dB view • contrast caps exceed range • quantized levels";
    private string _deltaRiseLabel = "+12 dB · RISE";
    private string _deltaFallLabel = "−12 dB · FALL";
    private Color _analyticalLow, _analyticalHigh, _riseColor, _fallColor;
    private Color _thresholdBelowColor, _thresholdAboveColor, _leftChannelColor, _rightChannelColor;
    private readonly Color[] _balanceColors = new Color[3];
    private int _thresholdDb = -24;
    private byte _thresholdByte = 170;
    private string _thresholdCaption = "Threshold −24 dBFS • filled caps = at/above • quantized levels";
    private string _thresholdBelowLabel = "Below −24 dBFS";
    private string _thresholdAboveLabel = "At/above −24 dBFS · capped";
    private bool _showOctaveValues;
    internal int ThresholdDb
    {
        get => _thresholdDb;
        set
        {
            if (value != -12 && value != -18 && value != -24 && value != -36 && value != -48)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_thresholdDb == value) return;
            _thresholdDb = value;
            _thresholdByte = LevelScale.ToDisplayByte(value);
            _thresholdCaption = "Threshold " + value + " dBFS • filled caps = at/above • quantized levels";
            _thresholdBelowLabel = "Below " + value + " dBFS";
            _thresholdAboveLabel = "At/above " + value + " dBFS · capped";
            Invalidate();
        }
    }
    internal bool ShowOctaveValues
    {
        get => _showOctaveValues;
        set { if (_showOctaveValues == value) return; _showOctaveValues = value; Invalidate(); }
    }
    internal RectangleF OctaveSpectrumPlotBounds { get; private set; }
    internal RectangleF WaveformPlotBounds { get; private set; }
    internal RectangleF RadialLeftKeyBounds { get; private set; }
    internal RectangleF RadialRightReadoutBounds { get; private set; }
    private string _radialStrongest = "No band above display floor";
    private int _radialStrongestBand = -1, _radialStrongestLevel = -1;
    internal int DeltaRangeDb
    {
        get => _deltaRangeDb;
        set
        {
            if (value != 6 && value != 12 && value != 24 && value != 72) throw new ArgumentOutOfRangeException(nameof(value));
            if (_deltaRangeDb == value) return;
            _deltaRangeDb = value;
            _deltaCaption = "Display-level change • ±" + value + " dB view • contrast caps exceed range • quantized levels";
            _deltaRiseLabel = "+" + value + " dB · RISE";
            _deltaFallLabel = "−" + value + " dB · FALL";
            Invalidate();
        }
    }
    private float _effectStrength = 0.6f;
    internal float EffectStrength
    {
        get => _effectStrength;
        set { _effectStrength = Math.Max(0, Math.Min(1, float.IsNaN(value) ? 0.6f : value)); Invalidate(); }
    }
    private int _selectedMatrixBand = -1;
    private string _selectedMatrixCaption;
    internal bool ShowGuides
    {
        get => _showGuides;
        set { if (_showGuides == value) return; _showGuides = value; Invalidate(); }
    }
    private bool GuidesVisible => ShowGuides || _style == VisualStyle.Precision;
    internal bool DisplayFrozen
    {
        get => _displayFrozen;
        set
        {
            if (_displayFrozen == value) return;
            _displayFrozen = value;
            _lastFrameTick = _sevenLastTick = 0;
            if (!value) _resumeSpectrum = true;
            UpdateSevenBandTimer();
        }
    }
    private float _paintFontHeight;
    private Font _historyMetricsFont;
    private float _historyMetricsDpi;
    private float _historyFrequencyLabelWidth;
    private float _historyDbLabelWidth;

    public AdvancedVisualizationControl()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;

        _history = new byte[DefaultHistoryFrames * BandCount];
        _historyTicks = new long[DefaultHistoryFrames];
        for (var i = 0; i < _levelBrushes.Length; i++)
        {
            _levelBrushes[i] = new SolidBrush(Color.Black);
            _radialPens[i] = new Pen(Color.Black);
            _matrixSurfaceBrushes[i] = new SolidBrush(Color.Black);
        }
        for (var i = 0; i < _historyTracePens.Length; i++)
            _historyTracePens[i] = new Pen(Color.Black, i == 0 ? 1.8f : 1f);

        // 16ms timer so the bars and peak caps fall smoothly even when analyzer frames arrive slower
        _sevenTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _sevenTimer.Tick += (s, e) =>
        {
            if (DisplayFrozen || !Visible || Mode != AdvancedVisualizationMode.AdvancedSpectralBalance || !_sevenHasData)
            {
                UpdateSevenBandTimer();
                return;
            }
            UpdateSevenBandBallistics();
            Invalidate();
        };

        _theme = BarColorThemes.Resolve(_themeName);
        ApplyThemeChrome(_theme);
        UpdatePalette();
        ReplaceHistoryImage(DefaultHistoryFrames);
        SetBandPlan(BandPlan.CreateLogarithmic(BandCount, 20, 20000, 48000));
    }

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
                InvalidateFluxBaseline();
            if (IsMeasurementMode(value) && _style != VisualStyle.Precision)
                _style = VisualStyle.None;
            UpdateHistoryCaption();
            UpdateFluxAccessibility();
            UpdateSevenBandTimer();
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

    // unknown palette names quietly fall back to ClassicSmooth
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

    // resizing the history wipes it, old frames wouldn't line up anymore
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

    // copies the frame (pads short, trims long) so the caller can reuse its buffer
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
        _selectedMatrixBand = -1;
        _selectedMatrixCaption = null;
        BuildNoteMap(plan);
        for (var band = 0; band < BandCount; band++)
        {
            _octaveByBand[band] = Math.Max(0, Math.Min(OctaveCount - 1,
                (int)Math.Floor(Math.Log(plan[band].CenterHz / 20, 2))));
            var hz = plan[band].CenterHz;
            _bandLabels[band] = hz >= 1000 ? (hz / 1000).ToString("0.##") + " kHz" : hz.ToString("0.#") + " Hz";
        }
        _radialCaption = "Clockwise: " + _bandLabels[0] + " → " + _bandLabels[BandCount - 1] + " • origin at 12 o'clock";
        _radialLayoutFont = null;
        _historyMetricsFont = null;
        ResetHistory();
        // channel bands were built for the old frequency layout, so drop them
        _audio.SetChannelSpectrum(null, null);
        UpdateAnalysisLabels();
        Invalidate();
    }

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
        UpdateFluxAccessibility();
        if (IsMeasurementMode(_mode) && _style != VisualStyle.Precision) _style = VisualStyle.None;
        UpdateHistoryCaption();
        UpdateSevenBandTimer();
        Invalidate();
    }

    // same as above but takes an already-resolved theme and the mode as a string (legacy aliases work too)
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
        UpdateFluxAccessibility();
        if (IsMeasurementMode(_mode) && _style != VisualStyle.Precision) _style = VisualStyle.None;
        UpdateHistoryCaption();
        UpdateSevenBandTimer();
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateSevenBandTimer();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        SetSevenBandHost(IsDisposed || Disposing ? null : FindForm());
        UpdateSevenBandTimer();
    }

    private void SetSevenBandHost(Form host)
    {
        if (ReferenceEquals(_sevenHost, host)) return;
        if (_sevenHost != null)
        {
            _sevenHost.VisibleChanged -= OnSevenBandHostChanged;
            _sevenHost.Resize -= OnSevenBandHostChanged;
        }
        _sevenHost = host;
        if (_sevenHost != null)
        {
            _sevenHost.VisibleChanged += OnSevenBandHostChanged;
            _sevenHost.Resize += OnSevenBandHostChanged;
        }
    }

    private void OnSevenBandHostChanged(object sender, EventArgs e) => UpdateSevenBandTimer();

    private void UpdateSevenBandTimer()
    {
        if (_sevenTimer == null || IsDisposed || Disposing) return;
        // A host's visibility/minimize transition need not produce a child visibility
        // event after its final state is applied. Observe the actual top-level state too.
        var host = FindForm();
        _sevenTimer.Enabled = !DisplayFrozen && Visible &&
            (host == null || host.Visible && host.WindowState != FormWindowState.Minimized) &&
            Mode == AdvancedVisualizationMode.AdvancedSpectralBalance && _sevenHasData;
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
        _analyticalLow = BarColorThemes.EnsureContrast(theme.Low, BackColor);
        _analyticalHigh = BarColorThemes.EnsureContrast(theme.High, BackColor);
        _riseColor = BarColorThemes.EnsureContrast(Color.FromArgb(40, 165, 184), BackColor);
        _fallColor = BarColorThemes.EnsureContrast(Color.FromArgb(211, 125, 56), BackColor);
        _thresholdBelowColor = BarColorThemes.MonitoringBelow(ui);
        _thresholdAboveColor = BarColorThemes.MonitoringAbove(ui);
        _leftChannelColor = BarColorThemes.ChannelLeft(ui);
        _rightChannelColor = BarColorThemes.ChannelRight(ui);
        for (var group = 0; group < 3; group++) _balanceColors[group] = BarColorThemes.BalanceGroup(group, ui);
        ForeColor = ui.SecondaryText;
        _gridPen.Color = Color.FromArgb(88, ui.Grid);
        _majorGridPen.Color = Color.FromArgb(142, ui.MajorGrid);
        _framePen.Color = Color.FromArgb(180, ui.Frame);
        _linePen.Color = _analyticalHigh;
        _contourFillBrush.Color = Color.FromArgb(62, theme.Mid);
        _textBrush.Color = ui.SecondaryText;
        _primaryTextBrush.Color = ui.PrimaryText;
        _rmsHistoryPen.Color = _analyticalLow;
        _peakHistoryPen.Color = ui.PrimaryText;
        _surfaceBrush.Color = ui.RaisedSurface;
        _peakPen.Color = ui.PrimaryText;
        _precisionPen.Color = Color.FromArgb(180, ui.MajorGrid);
        _scanPen.Color = Color.FromArgb(42, theme.High);
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
            var fontHeight = Font.GetHeight(graphics);
            if (_paintFontHeight != fontHeight)
            {
                _paintFontHeight = fontHeight;
                UpdateHistoryCaption();
            }

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
                case AdvancedVisualizationMode.AdvancedSpectralBalance:
                    PaintAdvancedSpectralBalance(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.StereoSpectrum:
                    PaintStereoSpectrum(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.DynamicsHistory:
                    PaintDynamicsHistory(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.SpectralHistory:
                    PaintSpectralHistory(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.Overview:
                    PaintOverview(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.StereoCompare:
                    PaintStereoCompare(graphics, width, height);
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
            SetSevenBandHost(null);
            _sevenTimer?.Stop();
            _sevenTimer?.Dispose();
            for (var i = 0; i < _levelBrushes.Length; i++)
            {
                _levelBrushes[i]?.Dispose();
                _radialPens[i]?.Dispose();
                _matrixSurfaceBrushes[i]?.Dispose();
            }
            foreach (var path in _matrixCardPaths) path?.Dispose();
            foreach (var pen in _historyTracePens) pen?.Dispose();
            _matrixFrequencyFont?.Dispose();
            _matrixValueFont?.Dispose();
            _primaryTextBrush.Dispose();
            _analysisBodyFont?.Dispose();
            _analysisTitleFont?.Dispose();
            _rmsHistoryPen.Dispose();
            _peakHistoryPen.Dispose();
            _dynamicsRmsPath.Dispose();
            _dynamicsPeakPath.Dispose();
            _dynamicsBreakPath.Dispose();

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
            _singleLineText.Dispose();
            _rightText.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PaintWaterfall(Graphics graphics, int width, int height)
    {
        PaintHistoryImage(graphics, width, height, false);
    }

    private void PaintRadialSpectrum(Graphics graphics, int width, int height)
    {
        RadialLeftKeyBounds = RadialRightReadoutBounds = RectangleF.Empty;
        EnsureRadialLayout(graphics, width, height);
        if (_radialRadius <= 0) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var centerX = _radialCenter.X;
        var centerY = _radialCenter.Y;
        var radius = _radialRadius;
        var innerRadius = radius * 0.20f;
        var span = radius - innerRadius;
        var radialGuides = GuidesVisible ? 6 : 2;
        for (var guide = 0; guide <= radialGuides; guide++)
        {
            var r = innerRadius + span * guide / radialGuides;
            graphics.DrawEllipse(_majorGridPen, centerX - r, centerY - r, 2 * r, 2 * r);
        }
        var penWidth = _style == VisualStyle.Outline ? 1f : Math.Max(1f, Math.Min(5f, (float)(2 * Math.PI * radius / BandCount) * 0.65f));
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
                if (_style == VisualStyle.Glow)
                {
                    _linePen.Color = Color.FromArgb((int)(60 * EffectStrength), pen.Color);
                    _linePen.Width = penWidth + 4;
                    graphics.DrawLine(_linePen, centerX + direction.X * innerRadius, centerY + direction.Y * innerRadius,
                        centerX + direction.X * end, centerY + direction.Y * end);
                }
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
        if (!_radialCaptionBounds.IsEmpty)
            graphics.DrawString(_radialLayoutCaption, Font, _textBrush, _radialCaptionBounds, _centeredText);
        if (!_radialLegendBounds.IsEmpty)
            graphics.DrawString(_radialLayoutLegend, Font, _textBrush, _radialLegendBounds, _centeredText);
        if (_radialLabelsVisible)
        {
            for (var index = 0; index < s_radialLabelBands.Length; index++)
                graphics.DrawString(_bandLabels[s_radialLabelBands[index]], Font, _textBrush,
                    _radialLabelBounds[index], _centeredText);
        }
        if (GuidesVisible && span >= _paintFontHeight * 3)
        {
            var stride = span / 6 >= _paintFontHeight + 2 ? 1 : 2;
            for (var guide = 0; guide <= 6; guide += stride)
            {
                var r = innerRadius + span * guide / 6;
                graphics.DrawString(s_referenceDbLabels[guide], Font, _primaryTextBrush,
                    centerX + 5, centerY - r - _paintFontHeight / 2);
            }
        }
        var leftTextEdge = centerX - radius;
        var rightTextEdge = centerX + radius;
        if (_radialLabelsVisible)
            foreach (var label in _radialLabelBounds)
            {
                leftTextEdge = Math.Min(leftTextEdge, label.Left);
                rightTextEdge = Math.Max(rightTextEdge, label.Right);
            }
        var sideWidth = leftTextEdge - 28;
        var sideRightX = rightTextEdge + 20;
        var sideRightWidth = width - sideRightX - 12;
        if (width >= 700 && height >= 160 && sideWidth >= 150 && sideRightWidth >= 150)
        {
            EnsureAnalysisFonts(true);
            var line = _analysisBodyFont.GetHeight(graphics) + 7;
            var y = Math.Max(35, centerY - line * 2);
            RadialLeftKeyBounds = new RectangleF(12, y, sideWidth - 12, line * 3);
            RadialRightReadoutBounds = new RectangleF(sideRightX, y, sideRightWidth, line * 4);
            graphics.DrawString("DISPLAY KEY", _analysisTitleFont, _primaryTextBrush,
                new RectangleF(12, y, sideWidth - 12, line), _singleLineText);
            DrawSignalLegend(graphics, 12, y + line, sideWidth - 12, line, "Current band level", _analyticalLow, false);
            DrawSignalLegend(graphics, 12, y + line * 2, sideWidth - 12, line, "Held peaks · 300 ms", _theme.Ui.PrimaryText, true);
            var rightX = sideRightX;
            var rightWidth = sideRightWidth;
            graphics.DrawString("STRONGEST DISPLAY BAND", _analysisTitleFont, _primaryTextBrush,
                new RectangleF(rightX, y, rightWidth, line), _singleLineText);
            graphics.DrawString(_radialStrongest, _analysisBodyFont, _textBrush,
                new RectangleF(rightX, y + line, rightWidth, line * 2), _singleLineText);
            graphics.DrawString("Clockwise · radius ≤−72 … 0 dBFS", _analysisBodyFont, _textBrush,
                new RectangleF(rightX, y + line * 3, rightWidth, line), _singleLineText);
        }
    }

    private void EnsureRadialLayout(Graphics graphics, int width, int height)
    {
        if (ReferenceEquals(_radialLayoutFont, Font) && _radialLayoutSize == new Size(width, height) &&
            _radialLayoutDpi == graphics.DpiY) return;
        _radialLayoutFont = Font;
        _radialLayoutSize = new Size(width, height);
        _radialLayoutDpi = graphics.DpiY;
        _radialCaptionBounds = _radialLegendBounds = RectangleF.Empty;
        _radialLabelsVisible = false;
        _radialRadius = 0;
        Array.Clear(_radialLabelBounds, 0, _radialLabelBounds.Length);
        _radialCenter = new PointF(width / 2f, height / 2f);
        if (width <= 2 || height <= 2) return;

        var scale = graphics.DpiY / 96f;
        var padding = Math.Min(Math.Max(3f, 4 * scale), Math.Min(width, height) / 8f);
        var gap = Math.Max(3f, 5 * scale);
        var textHeight = (float)Math.Ceiling(Font.GetHeight(graphics)) + 2 * scale;
        var availableWidth = Math.Max(0, width - 2 * padding);
        _radialLayoutCaption = ChooseRadialText(graphics, availableWidth, _radialCaption,
            "Frequency clockwise • 12 o'clock", "Clockwise from 12 o'clock");
        _radialLayoutLegend = ChooseRadialText(graphics, availableWidth, "Inner −72 / outer 0 dBFS",
            "Radius −72 → 0 dBFS", "−72 → 0 dBFS");
        var captionHeight = _radialLayoutCaption.Length > 0 ? textHeight + gap : 0;
        var legendHeight = _radialLayoutLegend.Length > 0 ? textHeight + gap : 0;
        var minimumPlotDiameter = Math.Min(80 * scale, Math.Min(width, height) * 0.6f);
        // small views keep the actual plot big instead of squeezing it under unreadable text
        if (height - 2 * padding - captionHeight - legendHeight < minimumPlotDiameter)
            captionHeight = 0;
        if (height - 2 * padding - captionHeight - legendHeight < minimumPlotDiameter)
            legendHeight = 0;
        if (captionHeight > 0)
            _radialCaptionBounds = new RectangleF(padding, padding, availableWidth, textHeight);
        if (legendHeight > 0)
            _radialLegendBounds = new RectangleF(padding, height - padding - textHeight, availableWidth, textHeight);

        var plot = new RectangleF(padding, padding + captionHeight, availableWidth,
            Math.Max(0, height - 2 * padding - captionHeight - legendHeight));
        _radialCenter = new PointF(plot.X + plot.Width / 2, plot.Y + plot.Height / 2);
        var widestLabel = 0f;
        for (var index = 0; index < s_radialLabelBands.Length; index++)
        {
            var measured = graphics.MeasureString(_bandLabels[s_radialLabelBands[index]], Font);
            var labelWidth = (float)Math.Ceiling(measured.Width) + 2 * scale;
            _radialLabelBounds[index] = new RectangleF(0, 0, labelWidth, textHeight);
            widestLabel = Math.Max(widestLabel, labelWidth);
        }
        var labeledRadius = Math.Min(plot.Width / 2 - widestLabel - gap, plot.Height / 2 - textHeight - gap);
        _radialLabelsVisible = labeledRadius >= Math.Max(24 * scale, textHeight);
        _radialRadius = _radialLabelsVisible ? labeledRadius :
            Math.Max(0, Math.Min(plot.Width, plot.Height) / 2 - Math.Max(2.5f, scale));
        if (!_radialLabelsVisible)
        {
            Array.Clear(_radialLabelBounds, 0, _radialLabelBounds.Length);
            return;
        }
        for (var index = 0; index < s_radialLabelBands.Length; index++)
        {
            var direction = s_radialDirections[s_radialLabelBands[index]];
            var bounds = _radialLabelBounds[index];
            bounds.X = _radialCenter.X + direction.X * (_radialRadius + gap) -
                (direction.X < -0.25f ? bounds.Width : direction.X > 0.25f ? 0 : bounds.Width / 2);
            bounds.Y = _radialCenter.Y + direction.Y * (_radialRadius + gap) -
                (direction.Y < -0.25f ? bounds.Height : direction.Y > 0.25f ? 0 : bounds.Height / 2);
            _radialLabelBounds[index] = bounds;
        }
    }

    private string ChooseRadialText(Graphics graphics, float width, string full, string shortText, string compact)
    {
        if (graphics.MeasureString(full, Font).Width <= width) return full;
        if (graphics.MeasureString(shortText, Font).Width <= width) return shortText;
        return graphics.MeasureString(compact, Font).Width <= width ? compact : string.Empty;
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
            DrawSpectrumGrid(graphics, 0, width - 1, top, baseline);

            for (var band = 0; band < BandCount; band++)
            {
                var x = SpectrumBandX(band, width);
                var y = baseline - ((_spectrum[band] / 255f) * plotHeight);
                _plotPoints[band] = new PointF(x, y);
            }

            _contourPath.Reset();
            AppendPlotLines();
            _contourPath.AddLine(_plotPoints[BandCount - 1].X, baseline, _plotPoints[0].X, baseline);
            _contourPath.CloseFigure();

            _contourFillBrush.Color = Color.FromArgb(36, _theme.Low);
            if (_style != VisualStyle.Outline) graphics.FillPath(_contourFillBrush, _contourPath);

            _linePen.Width = Math.Max(1f, Math.Min(2.5f, height / 100f));
            _linePen.Color = _analyticalLow;
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
        DrawSpectrumLine(graphics, width, top, baseline, false, _analyticalLow, 2f);
        DrawCaption(graphics, width, "Current spectrum • dashed 300 ms peak hold • −72 … 0 dBFS");
        if (width >= 600 && height >= 80)
        {
            var h = GetCaptionHeight(width, height) - 4;
            _thresholdBrush.Color = BackColor;
            graphics.FillRectangle(_thresholdBrush, 3, 2, width - 6, h);
            DrawSignalLegend(graphics, 8, 2, width * 0.29f, h, "Live spectrum", _analyticalLow, false);
            DrawSignalLegend(graphics, width * 0.32f, 2, width * 0.39f, h, "Held envelope · 300 ms", _theme.Ui.PrimaryText, true);
            graphics.DrawString("−72 … 0 dBFS", Font, _textBrush, new RectangleF(width * 0.74f, 2, width * 0.25f, h), _centeredText);
        }
    }

    private void DrawSignalLegend(Graphics graphics, float x, float y, float width, float height,
        string text, Color color, bool dashed)
    {
        if (width < 30 || height < 1) return;
        _linePen.Color = color;
        _linePen.Width = 2;
        _linePen.DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid;
        graphics.DrawLine(_linePen, x, y + height / 2, x + 22, y + height / 2);
        graphics.DrawString(text, Font, _textBrush, new RectangleF(x + 27, y, width - 27, height), _centeredText);
        _linePen.DashStyle = DashStyle.Solid;
    }

    private void PaintThresholdMonitor(Graphics graphics, int width, int height)
    {
        var threshold = _thresholdByte;
        GetCurvePlotBounds(width, height, out var top, out var baseline);
        var plotHeight = Math.Max(1f, baseline - top);
        var thresholdY = (int)Math.Round(top + plotHeight * (1f - (threshold / 255f)));
        for (var band = 0; band < BandCount; band++)
        {
            var level = _spectrum[band];
            if (level == 0)
                continue;
            var color = level >= threshold ? _thresholdAboveColor : _thresholdBelowColor;
            var cellHeight = Math.Max(1f, plotHeight * level / 255f);
            _thresholdBrush.Color = color;
            var x = (float)SpectrumAxis.Position(_bandPlan[band].LowerHz) * (width - 1);
            var right = (float)SpectrumAxis.Position(_bandPlan[band].UpperHz) * (width - 1);
            if (right > x)
            {
                if (_style == VisualStyle.Outline)
                {
                    _linePen.Color = BarColorThemes.EnsureContrast(color, BackColor);
                    _linePen.Width = 1;
                    graphics.DrawRectangle(_linePen, x, baseline - cellHeight, Math.Max(0.5f, right - x - 0.5f), cellHeight);
                }
                else graphics.FillRectangle(_thresholdBrush, x, baseline - cellHeight, Math.Max(0.5f, right - x - 0.5f), cellHeight);
            }
            if (level >= threshold && right > x)
            {
                _thresholdBrush.Color = _theme.Ui.PrimaryText;
                graphics.FillRectangle(_thresholdBrush, x, baseline - cellHeight, Math.Max(0.5f, right - x - 0.5f), 2f);
            }
        }

        _peakPen.Color = _theme.Ui.PrimaryText;
        _peakPen.DashStyle = DashStyle.Dash;
        var previousWidth = _peakPen.Width;
        _peakPen.Width = 3f;
        graphics.DrawLine(_peakPen, 0, thresholdY, width, thresholdY);
        _peakPen.Width = previousWidth;
        // The threshold coincides with a calibrated dB row. Retain that reference
        // on top of the broader contrast rule, including when a band crosses it.
        DrawSpectrumGrid(graphics, 0, width - 1, top, baseline);
        DrawCaption(graphics, width, _thresholdCaption);
        if (width >= 480 && height >= 80)
        {
            var h = GetCaptionHeight(width, height) - 4;
            _thresholdBrush.Color = BackColor;
            graphics.FillRectangle(_thresholdBrush, 3, 2, width - 6, h);
            DrawSignalLegend(graphics, 8, 2, width * 0.27f, h, _thresholdBelowLabel, _thresholdBelowColor, false);
            DrawSignalLegend(graphics, width * 0.29f, 2, width * 0.41f, h, _thresholdAboveLabel, _thresholdAboveColor, false);
            graphics.DrawString("Quantized dBFS", Font, _textBrush, new RectangleF(width * 0.72f, 2, width * 0.27f, h), _centeredText);
        }
    }

    private void PaintBandMatrix(Graphics graphics, int width, int height)
    {
        EnsureMatrixLayout(graphics, width, height);
        var scale = _matrixScale;
        if (_matrixHeaderHeight > 0)
        {
            graphics.DrawString("BAND MATRIX", _matrixValueFont, _primaryTextBrush, _matrixPadding, 3 * scale);
            graphics.DrawString(_selectedMatrixCaption ?? "BASS <250 Hz  •  MID 250 Hz–4 kHz  •  HIGH ≥4 kHz  •  click to pin; arrows select", _matrixFrequencyFont, _textBrush,
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
            var hz = _bandPlan[band].CenterHz;
            _thresholdBrush.Color = _balanceColors[hz < 250 ? 0 : hz < 4000 ? 1 : 2];
            graphics.FillRectangle(_thresholdBrush, rect.X + 1, rect.Y + 1,
                Math.Min(3 * scale, Math.Max(0.1f, rect.Width - 2)), Math.Max(0.1f, rect.Height - 2));
            if (band == _selectedMatrixBand)
            {
                _linePen.Color = _theme.Ui.Focus;
                _linePen.Width = 2;
                graphics.DrawRectangle(_linePen, rect.X, rect.Y, rect.Width, rect.Height);
            }
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
            if (GuidesVisible)
                for (var reference = 1; reference < 3; reference++)
                    graphics.DrawLine(_precisionPen, innerX + innerWidth * reference / 3, meterY - scale,
                        innerX + innerWidth * reference / 3, meterY + 4 * scale);
        }
        graphics.SmoothingMode = previousSmoothing;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Mode != AdvancedVisualizationMode.BandMatrix || e.Button != MouseButtons.Left) return;
        Focus();
        for (var band = 0; band < BandCount; band++)
            if (_matrixCards[band].Contains(e.Location)) { SelectMatrixBand(band); break; }
    }

    protected override bool IsInputKey(Keys keyData) => Mode == AdvancedVisualizationMode.BandMatrix &&
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Mode == AdvancedVisualizationMode.BandMatrix)
        {
            var step = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1
                : e.KeyCode == Keys.Up ? -Math.Max(1, _matrixColumns) : e.KeyCode == Keys.Down ? Math.Max(1, _matrixColumns) : 0;
            if (step != 0) { SelectMatrixBand(Math.Max(0, Math.Min(BandCount - 1, _selectedMatrixBand + step))); e.Handled = true; }
        }
        base.OnKeyDown(e);
    }

    private void SelectMatrixBand(int band)
    {
        _selectedMatrixBand = band;
        _selectedMatrixCaption = "PINNED  " + _bandLabels[band] + "  ·  " + s_matrixValueLabels[_spectrum[band]] +
            "  ·  click again to refresh";
        AccessibleDescription = _selectedMatrixCaption;
        AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        Invalidate();
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
        EnsureAnalysisFonts(true);
        var scale = graphics.DpiY / 96f;
        var lineHeight = _analysisBodyFont.GetHeight(graphics);
        var labeled = width >= 400 * scale && height >= 120 * scale;
        var values = ShowOctaveValues && labeled && height >= lineHeight * 6;
        var top = height >= 80 ? GetCaptionHeight(width, height) + 3f * scale : 2f;
        if (values) top += lineHeight + 4 * scale;
        var baseline = Math.Max(top + 1, height - (labeled ? lineHeight + 6 * scale : height >= 80 ? 23f : 2f));
        var plotHeight = Math.Max(1, baseline - top);
        var left = labeled ? Math.Max(42 * scale, graphics.MeasureString("−72", _analysisBodyFont).Width + 9 * scale) : 0;
        var right = Math.Max(left + 1, width - 3 * scale);
        OctaveSpectrumPlotBounds = RectangleF.FromLTRB(left, top, right, baseline);
        var groupWidth = (right - left) / OctaveCount;
        if (labeled)
            for (var guide = 0; guide <= 6; guide++)
            {
                var y = baseline - plotHeight * guide / 6f;
                graphics.DrawLine(guide % 2 == 0 ? _majorGridPen : _gridPen, left, y, right, y);
                if (guide % 2 == 0 || plotHeight >= lineHeight * 12)
                    graphics.DrawString(s_referenceDbLabels[guide], _analysisBodyFont, _textBrush,
                        new RectangleF(0, y - lineHeight / 2, left - 5 * scale, lineHeight), _rightText);
            }
        for (var group = 0; group < OctaveCount; group++)
        {
            var level = _octaveLevels[group];
            var barHeight = plotHeight * level / 255f;
            var x = left + group * groupWidth;
            if (level > 0)
                graphics.FillRectangle(_levelBrushes[level], x + 2, baseline - Math.Max(1, barHeight),
                    Math.Max(1, groupWidth - 4), Math.Max(1, barHeight));
            graphics.DrawLine(_gridPen, x + 1, baseline, x + groupWidth - 1, baseline);
            if (GuidesVisible)
                for (var guide = 1; guide < 6; guide++)
                    graphics.DrawLine(_precisionPen, x + 1, baseline - plotHeight * guide / 6f,
                        x + groupWidth - 1, baseline - plotHeight * guide / 6f);
            if (values)
                graphics.DrawString(s_matrixValueLabels[level], _analysisBodyFont, _primaryTextBrush,
                    new RectangleF(x, top - lineHeight - 3 * scale, groupWidth, lineHeight), _centeredText);
            if (groupWidth >= 28 && height >= 80)
                graphics.DrawString(groupWidth >= 100 ? s_octaveBandLabels[group] : s_octaveLowerLabels[group], _analysisBodyFont, _textBrush,
                    new RectangleF(x, baseline + 2 * scale, groupWidth, lineHeight + 3 * scale), _centeredText);
        }
        DrawCaption(graphics, width, "Approx. summed display power • octave groups by center • ≤−72 … 0 dBFS");
    }

    private void PaintSpectralFlux(Graphics graphics, int width, int height)
    {
        if (!_fluxComparisonAvailable)
        {
            var message = FluxWaitingMessage;
            graphics.DrawString(message, Font, _textBrush,
                new RectangleF(4, Math.Max(0, height * .5f - _paintFontHeight), Math.Max(1, width - 8), Math.Max(1, _paintFontHeight * 2)), _centeredText);
            DrawCaption(graphics, width, "Level change requires two consecutive measured frames");
            return;
        }
        var center = height * 0.5f;
        var span = Math.Max(1, height * 0.5f - (height >= 80 ? 24 : 2));
        graphics.DrawLine(_majorGridPen, 0, center, width, center);
        if (GuidesVisible)
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
            var extent = span * Math.Min(1f, Math.Abs(difference) * 72f / (255f * DeltaRangeDb));
            _thresholdBrush.Color = difference > 0 ? _riseColor : _fallColor;
            var y = difference > 0 ? center - extent : center;
            graphics.FillRectangle(_thresholdBrush, band * columnWidth, y,
                Math.Max(0.5f, columnWidth - 1), Math.Max(1, extent));
            if (Math.Abs(difference) * 72f / 255f > DeltaRangeDb)
            {
                _thresholdBrush.Color = _theme.Ui.PrimaryText;
                graphics.FillRectangle(_thresholdBrush, band * columnWidth,
                    difference > 0 ? center - span : center + span - 3, Math.Max(0.5f, columnWidth - 1), 3);
            }
        }
        if (height >= 140 && width >= 320)
        {
            graphics.DrawString(_deltaRiseLabel, Font, _textBrush, 4, center - span);
            graphics.DrawString("0", Font, _textBrush, 4, center - _paintFontHeight);
            graphics.DrawString(_deltaFallLabel, Font, _textBrush, 4, center + span - _paintFontHeight);
        }
        else if (height >= 80 && width >= 180)
            graphics.DrawString("0", Font, _textBrush, 4, center - _paintFontHeight);
        DrawCaption(graphics, width, _deltaCaption);
    }


    private void PaintOctaveWaterfall(Graphics graphics, int width, int height)
    {
        PaintHistoryImage(graphics, width, height, true);
    }

    private void PaintSpectralHistory(Graphics graphics, int width, int height)
    {
        var scale = graphics.DpiY / 96f;
        if (!ReferenceEquals(_historyMetricsFont, Font) || _historyMetricsDpi != graphics.DpiY)
        {
            _historyMetricsFont = Font;
            _historyMetricsDpi = graphics.DpiY;
            _historyFrequencyLabelWidth = 0;
            for (var index = 0; index <= 4; index++)
            {
                var band = index == 4 ? BandCount - 1 : index * 20;
                _historyFrequencyLabelWidth = Math.Max(_historyFrequencyLabelWidth, graphics.MeasureString(_bandLabels[band], Font).Width);
            }
            _historyFrequencyLabelWidth += 8 * scale;
            _historyDbLabelWidth = graphics.MeasureString("0 dBFS", Font).Width + 8 * scale;
        }
        var labeled = width >= Math.Max(320 * scale, 5 * _historyFrequencyLabelWidth) &&
            height >= Math.Max(120 * scale, 7 * _paintFontHeight);
        var top = GetCaptionHeight(width, height) + 3f * scale;
        var bottom = Math.Max(top + 1, height - (labeled ? _paintFontHeight + 7 * scale : 3 * scale));
        var left = labeled ? 7 * scale : 1f;
        var right = Math.Max(left + 1, width - (labeled ? _historyDbLabelWidth : 2f));
        var span = Math.Max(1, right - left);
        var depthHeight = Math.Max(1, bottom - top) * 0.22f;
        var signalHeight = Math.Max(1, bottom - top) * 0.73f;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var guide = 0; guide <= 3; guide++)
        {
            var y = bottom - signalHeight * guide / 3;
            graphics.DrawLine(_gridPen, left, y, right, y);
            if (labeled)
                graphics.DrawString(s_referenceDbLabels[guide * 2], Font, _textBrush, right + 3 * scale, y - _paintFontHeight / 2);
        }
        var frames = Math.Min(_historyCount, PerspectiveHistoryFrames);
        var slices = Math.Min(PerspectiveSlices, frames);
        for (var slice = slices - 1; slice >= 0; slice--)
        {
            var depth = slices <= 1 ? 0 : slice / (float)(slices - 1);
            // pick real history entries; the perspective depth is frame order, not time
            var age = slices <= 1 ? 0 : slice * (frames - 1) / (slices - 1);
            var offset = GetHistoryFrame(age) * BandCount;
            var x0 = left + span * 0.14f * depth;
            var traceWidth = span * (1 - 0.22f * depth);
            var baseline = bottom - depthHeight * depth;
            var traceHeight = signalHeight * (1 - 0.32f * depth);
            var pen = _historyTracePens[slice];
            if (GuidesVisible)
                graphics.DrawLine(_gridPen, x0, baseline, x0 + traceWidth, baseline);
            for (var band = 1; band < BandCount; band++)
            {
                var previous = _history[offset + band - 1];
                var current = _history[offset + band];
                if (previous == 0 && current == 0) continue;
                graphics.DrawLine(pen, x0 + traceWidth * (band - 1) / (BandCount - 1),
                    baseline - traceHeight * previous / 255f,
                    x0 + traceWidth * band / (BandCount - 1), baseline - traceHeight * current / 255f);
            }
        }
        if (labeled)
            for (var index = 0; index <= 4; index++)
            {
                var band = index == 4 ? BandCount - 1 : index * 20;
                var x = left + span * band / (BandCount - 1);
                graphics.DrawString(_bandLabels[band], Font, _textBrush,
                    new RectangleF(Math.Max(0, Math.Min(width - _historyFrequencyLabelWidth, x - _historyFrequencyLabelWidth / 2)),
                        bottom + 3 * scale, _historyFrequencyLabelWidth, _paintFontHeight + 2 * scale), _centeredText);
            }
        DrawCaption(graphics, width, _historyCaption);
    }


    private void PaintStyleOverlay(Graphics graphics, int width, int height)
    {
        if (IsMeasurementMode(_mode)) return;
        if (_style == VisualStyle.Scanline &&
            (_mode == AdvancedVisualizationMode.Contour || _mode == AdvancedVisualizationMode.PeakTrace ||
             _mode == AdvancedVisualizationMode.ThresholdMonitor))
        {
            _scanPen.Color = Color.FromArgb((int)(70 * EffectStrength), _analyticalHigh);
            GetCurvePlotBounds(width, height, out var top, out var baseline);
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
        DrawSpectrumGrid(graphics, 0, width - 1, top, baseline);
    }

    private void DrawSpectrumGrid(Graphics graphics, float left, float right, float top, float bottom)
    {
        foreach (var db in SpectrumAxis.Decibels)
        {
            var y = bottom - (bottom - top) * (float)LevelScale.Normalize(db);
            graphics.DrawLine(_gridPen, left, y, right, y);
        }
        foreach (var hz in SpectrumAxis.Frequencies)
        {
            var x = left + (right - left) * (float)SpectrumAxis.Position(hz);
            graphics.DrawLine(_gridPen, x, top, x, bottom);
        }
    }

    internal RectangleF SpectrumPlotBounds
    {
        get
        {
            GetCurvePlotBounds(ClientSize.Width, ClientSize.Height, out var top, out var bottom);
            return RectangleF.FromLTRB(0, top, Math.Max(1, ClientSize.Width - 1), bottom);
        }

    }

    internal RectangleF GetStereoSpectrumPlotBounds(int channel) => _stereoSpectrumPlotBounds[channel];

    private float SpectrumBandX(int band, int width) =>
        (float)SpectrumAxis.Position(_bandPlan[band].CenterHz) * Math.Max(1, width - 1);

    private void GetCurvePlotBounds(int width, int height, out float top, out float baseline)
    {
        top = GetCaptionHeight(width, height);
        if (top == 0 && (_mode is AdvancedVisualizationMode.Contour or AdvancedVisualizationMode.PeakTrace
            or AdvancedVisualizationMode.ThresholdMonitor))
        {
            // The host reserves room for these native dB/frequency labels.
            // Avoid reserving a second font-height margin inside a compact curve.
            top = 2;
            baseline = Math.Max(top + 1f, height - 2f);
            return;
        }
        if (top == 0) top = Math.Max(2f, height * 0.06f);
        baseline = Math.Max(top + 1f, height - Math.Max(2f, Font.GetHeight(DeviceDpi) / 2f));
    }

    private void DrawSpectrumLine(Graphics graphics, int width, float top, float baseline, bool peaks, Color color, float thickness)
    {
        var plotHeight = Math.Max(1f, baseline - top);
        for (var band = 0; band < BandCount; band++)
        {
            var level = peaks ? _heldPeaks[band] : _spectrum[band];
            _plotPoints[band] = new PointF(SpectrumBandX(band, width),
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
        // AddLines(PointF[]) copies the whole array every paint, so reuse the points and the
        // scalar path calls to avoid allocating throwaway point objects each frame
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
        if (DisplayFrozen) return;
        _audio.Update(samples, frames, channels, sampleRate);
        ObserveFluxAudio(_audio);
        UpdateSevenBandBallistics();
        UpdateAudioLabels();
        UpdateAnalysisLabels();
        Invalidate();
    }

    internal void SetAudioData(AudioVisualizationData data)
    {
        if (DisplayFrozen) return;
        _audio.CopyFrom(data);
        ObserveFluxAudio(_audio);
        UpdateSevenBandBallistics();
        UpdateAudioLabels();
        UpdateAnalysisLabels();
        Invalidate();
    }

    // Deterministic still previews use an already-copied frame, not elapsed live
    // attack/release time. Never changes measurements or the capture source.
    internal void SettlePreviewBallistics()
    {
        _sevenHasData = _audio.HasSevenBand;
        for (var band = 0; band < SevenBandAnalyzer.BandCount; band++)
        {
            var power = _audio.SevenBandPower[band];
            var level = _sevenHasData && !double.IsNaN(power)
                ? (float)(LevelScale.Normalize(LevelScale.PowerToDb(power)) * SevenBandRangeDb) : 0;
            _sevenLevels[band] = _sevenPeaks[band] = level;
            _sevenPeakHoldMs[band] = SevenBandPeakHoldMs;
            _sevenLabelTenths[band] = int.MaxValue;
        }
        _sevenLastTick = 0;
        UpdateSevenBandBallistics();
        Invalidate();
    }

    internal void SynchronizeSpectrum(byte[] values)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        if (DisplayFrozen) return;
        ResetHistory();
        // Synchronization seeds one already-observed snapshot, never an implicit zero.
        if (_fluxAudioBound) _fluxAudioFrameNew = _fluxAudioAvailable;
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
            _audioShortLabels[i] = silent ? "−∞" : _audioDb[i].ToString("0.0");
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
        if (_captionWaveCount != _waveDisplayCount || _captionWaveRate != _audio.SampleRate || _captionWaveRange != _waveRange || _captionWaveStereo != _audio.HasStereo)
        {
            _captionWaveCount = _waveDisplayCount;
            _captionWaveRate = _audio.SampleRate;
            _captionWaveRange = _waveRange;
            _captionWaveStereo = _audio.HasStereo;
            var durationMs = 1000d * Math.Max(0, _waveDisplayCount - 1) / _audio.SampleRate;
            _waveCaption = (_audio.HasStereo ? "Front-left waveform" : "Mono waveform") + " • " + durationMs.ToString("0.00") +
                " ms • auto range ±" + _waveRange.ToString("G4") + " FS";
            for (var tick = 0; tick < 5; tick++)
            {
                _waveTimeLabels[tick] = (durationMs * tick / 4).ToString("0.##") + (tick == 4 ? " ms" : string.Empty);
                var amplitude = _waveRange * (1 - tick / 2d);
                _waveAmplitudeLabels[tick] = (amplitude > 0 ? "+" : string.Empty) + amplitude.ToString("G3") + " FS";
            }
        }
    }

    private void EnsureAnalysisFonts(bool fitSpectrum = false)
    {
        var bodySize = Math.Max(7f, Font.SizeInPoints - 0.5f);
        if (fitSpectrum)
        {
            var availableLineHeight = Math.Max(1f, (ClientSize.Height - 58 * DeviceDpi / 96f) / 15f);
            bodySize = Math.Min(bodySize, Math.Max(7f,
                Font.SizeInPoints * availableLineHeight / Font.GetHeight(DeviceDpi)));
        }
        if (ReferenceEquals(Font, _analysisFontSource) && _analysisBodyFont != null
            && _analysisFontSize == bodySize) return;
        _analysisFontSource = Font;
        _analysisFontSize = bodySize;
        _analysisBodyFont?.Dispose();
        _analysisTitleFont?.Dispose();
        _analysisBodyFont = new Font(Font.FontFamily, bodySize);
        _analysisTitleFont = new Font(Font.FontFamily, bodySize + 1f, FontStyle.Bold);
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

    internal RectangleF OverviewSpectrumPlotBounds { get; private set; }

    private void PaintOverview(Graphics graphics, int width, int height)
    {
        EnsureAnalysisFonts(true);
        var scale = graphics.DpiY / 96f;
        var bodyHeight = _analysisBodyFont.GetHeight(graphics);
        var composite = width >= 400 && height >= 100;
        var spectrumWidth = composite ? (int)(width * 0.70f) : width;
        var subplotWidth = Math.Max(1, spectrumWidth - (composite ? 5 : 0));
        var footer = width >= 180 * scale && height >= 100 * scale + bodyHeight
            ? (int)Math.Ceiling(bodyHeight + 7 * scale) : 0;
        var spectrumHeight = Math.Max(1, height - footer);
        GetCurvePlotBounds(subplotWidth, spectrumHeight, out var top, out var bottom);
        OverviewSpectrumPlotBounds = RectangleF.FromLTRB(0, top, Math.Max(1, subplotWidth - 1), bottom);
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(0, 0, subplotWidth, height), CombineMode.Intersect);
        PaintContour(graphics, subplotWidth, spectrumHeight);
        DrawCaption(graphics, subplotWidth, "SPECTRUM · Hz · −72 … 0 dBFS");
        if (footer > 0)
            DrawSpectrumFrequencyLabels(graphics, OverviewSpectrumPlotBounds.Left, OverviewSpectrumPlotBounds.Right,
                height - footer, bodyHeight + 2 * scale, subplotWidth, scale);
        graphics.Restore(state);
        if (!composite) return;
        graphics.DrawLine(_majorGridPen, spectrumWidth, 4, spectrumWidth, height - 4);
        state = graphics.Save();
        graphics.TranslateTransform(spectrumWidth + 5, 0);
        graphics.SetClip(new Rectangle(0, 0, width - spectrumWidth - 5, height), CombineMode.Intersect);
        PaintStereoLevels(graphics, width - spectrumWidth - 5, height, true);
        graphics.Restore(state);
    }

    private void PaintStereoCompare(Graphics graphics, int width, int height)
    {
        EnsureAnalysisFonts(true);
        var scale = graphics.DpiY / 96f;
        var labeled = width >= 380 * scale && height >= 130 * scale;
        var bodyHeight = _analysisBodyFont.GetHeight(graphics);
        var left = labeled ? 44 * scale : 2;
        var right = Math.Max(left + 1, width - (labeled ? 22 * scale : 2));
        var top = GetCaptionHeight(width, height) + 3 * scale;
        var bottom = Math.Max(top + 1, height - (labeled ? bodyHeight + 8 * scale : 2));
        StereoComparePlotBounds = RectangleF.FromLTRB(left, top, right, bottom);
        DrawSpectrumGrid(graphics, left, right, top, bottom);
        if (labeled)
        {
            for (var tick = 0; tick <= 6; tick++)
                graphics.DrawString(s_referenceDbLabels[tick], _analysisBodyFont, _textBrush, 2,
                    bottom - (bottom - top) * tick / 6f - bodyHeight / 2);
            DrawSpectrumFrequencyLabels(graphics, left, right, bottom + 3 * scale, bodyHeight + 2 * scale, width, scale);
        }
        var caption = !_audio.HasChannelSpectrum ? "STEREO COMPARE • frequency data unavailable"
            : !_audio.HasStereo ? "STEREO COMPARE • MONO solid • RIGHT unavailable"
            : "STEREO COMPARE • LEFT solid / RIGHT dashed • full-scale-sine band power • −72 … 0 dBFS";
        DrawCaption(graphics, width, caption);
        if (!_audio.HasChannelSpectrum) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var channel = 0; channel < (_audio.HasStereo ? 2 : 1); channel++)
        {
            var bands = channel == 0 ? _audio.LeftSpectrum : _audio.RightSpectrum;
            _linePen.Color = channel == 0 ? _leftChannelColor : _rightChannelColor;
            _linePen.Width = 2 * scale;
            _linePen.DashStyle = channel == 0 ? DashStyle.Solid : DashStyle.Dash;
            for (var band = 0; band < BandCount; band++)
                _plotPoints[band] = new PointF(left + (right - left) * (float)SpectrumAxis.Position(_bandPlan[band].CenterHz),
                    bottom - (bottom - top) * bands[band] / 255f);
            for (var band = 1; band < BandCount; band++)
                if (bands[band] > 0 || bands[band - 1] > 0)
                    graphics.DrawLine(_linePen, _plotPoints[band - 1], _plotPoints[band]);
        }
        _linePen.DashStyle = DashStyle.Solid;
    }

    private void PaintStereoSpectrum(Graphics graphics, int width, int height)
    {
        EnsureAnalysisFonts(true);
        var scale = graphics.DpiY / 96f;
        var bodyHeight = _analysisBodyFont.GetHeight(graphics);
        var titleHeight = _analysisTitleFont.GetHeight(graphics);
        // Preserve the scale endpoints when enlarged toolbar text leaves a short
        // plot. Intermediate labels can use a stride while all grid rows remain.
        var labeled = width >= 340 * scale && height >= 4 * bodyHeight + 12 * scale;
        var compactIdentifiers = !labeled && width >= 180 * scale && height >= 80 * scale;
        var header = labeled ? bodyHeight + 10 * scale : 0;
        var footer = labeled ? bodyHeight + 7 * scale : 0;
        var leftReserve = labeled ? Math.Max(48 * scale,
            graphics.MeasureString(SpectrumAxis.DecibelLabels[12], _analysisBodyFont).Width + 6 * scale) : 2 * scale;
        var rightReserve = labeled ? Math.Max(10 * scale,
            graphics.MeasureString(SpectrumAxis.FrequencyLabels[15], _analysisBodyFont).Width / 2) : 2 * scale;
        var minimumPanelHeight = 12 * (bodyHeight + 2 * scale) + titleHeight + 15 * scale;
        // A short viewport still needs separate left/right panels even when the
        // full caption does not fit. Base orientation on usable panel width.
        var columns = width >= 480 * scale && height - header - footer < 2 * minimumPanelHeight &&
            width >= 2 * (leftReserve + rightReserve + 120 * scale) + 12 * scale;
        // A wrapped toolbar can leave a short, wide viewport. Keep all thirteen
        // reference ticks readable by consolidating repeated metadata, rather than
        // shrinking the window's text or silently dropping intermediate labels.
        var compactChrome = columns && height - header - footer - titleHeight - 15 * scale <
            12 * (bodyHeight + 2 * scale);
        if (compactChrome) { header = 0; footer = bodyHeight + 3 * scale; }
        var panelWidth = columns ? (width - 12 * scale) / 2 : width;
        if (labeled && !compactChrome)
            graphics.DrawString(width >= 620 * scale
                ? "STEREO SPECTRUM  •  front L/R  •  full-scale-sine band power, −72 … 0 dBFS"
                : "STEREO SPECTRUM  •  L/R  •  sine ref  •  −72 … 0 dBFS",
                _analysisBodyFont, _textBrush, 8 * scale, 4 * scale);
        var panelHeight = Math.Max(1, (height - header - footer) / (columns ? 1 : 2));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var channel = 0; channel < 2; channel++)
        {
            var panelX = columns ? channel * (panelWidth + 12 * scale) : 0;
            var panelY = header + (columns ? 0 : channel * panelHeight);
            var left = panelX + leftReserve;
            var right = Math.Max(left + 1, panelX + panelWidth - rightReserve);
            var top = panelY + (compactChrome ? bodyHeight + 4 * scale : labeled ? titleHeight + 8 * scale : compactIdentifiers ? bodyHeight + 6 * scale : 2 * scale);
            var bottom = Math.Max(top + 1, panelY + panelHeight - (compactChrome ? 3 : 7) * scale);
            _stereoSpectrumPlotBounds[channel] = RectangleF.FromLTRB(left, top, right, bottom);
            graphics.FillRectangle(_surfaceBrush, left, top, right - left, bottom - top);
            if (compactChrome)
                graphics.DrawString(channel == 0
                        ? _audio.Channels >= 2 ? "LEFT • full-scale sine = 0 dBFS" : "MONO • full-scale sine = 0 dBFS"
                        : "RIGHT • full-scale sine = 0 dBFS", _analysisBodyFont, _primaryTextBrush,
                    new RectangleF(left, panelY + 2 * scale, right - left, bodyHeight + 2 * scale), _singleLineText);
            else if (labeled)
                graphics.DrawString(_channelSpectrumLabels[channel], _analysisTitleFont, _primaryTextBrush,
                    new RectangleF(left, panelY + 2 * scale, right - left, titleHeight + 4 * scale), _singleLineText);
            else if (compactIdentifiers)
                graphics.DrawString(channel == 0 ? _audio.Channels >= 2 ? "LEFT" : "MONO" : "RIGHT",
                    _analysisBodyFont, _primaryTextBrush, left, panelY + 2 * scale);
            DrawSpectrumGrid(graphics, left, right, top, bottom);
            if (labeled)
                for (var tick = 0; tick < SpectrumAxis.Decibels.Length; tick++)
                {
                    var db = SpectrumAxis.Decibels[tick];
                    var y = bottom - (bottom - top) * (float)LevelScale.Normalize(db);
                    var labelStride = Math.Max(1, (int)Math.Ceiling(12 * (bodyHeight + 2 * scale) / (bottom - top)));
                    if (tick != 12 && tick % labelStride != 0) continue;
                    graphics.DrawString(SpectrumAxis.DecibelLabel(db), _analysisBodyFont, _textBrush,
                        panelX + 3 * scale, y - bodyHeight / 2);
                }
            if (labeled && (columns || channel == 1))
                DrawSpectrumFrequencyLabels(graphics, left, right, height - footer,
                    bodyHeight + 2 * scale, (int)Math.Floor(panelX + panelWidth), scale);
            if (!_audio.HasChannelSpectrum || (channel == 1 && !_audio.HasStereo))
            {
                if (labeled)
                    graphics.DrawString(channel == 1 && _audio.HasSamples && !_audio.HasStereo ? "RIGHT unavailable — mono input" : "Frequency data unavailable",
                        _analysisBodyFont, _textBrush, left + 6 * scale, top + 4 * scale);
                continue;
            }
            var bands = channel == 0 ? _audio.LeftSpectrum : _audio.RightSpectrum;
            _linePen.Color = channel == 0 ? _leftChannelColor : _rightChannelColor;
            _linePen.Width = 1.8f * scale;
            _linePen.DashStyle = channel == 0 ? DashStyle.Solid : DashStyle.Dash;
            var previous = new PointF(left, bottom - (bottom - top) * bands[0] / 255f);
            for (var band = 0; band < BandCount; band++)
            {
                var x = left + (right - left) * (float)SpectrumAxis.Position(_bandPlan[band].CenterHz);
                var y = bottom - (bottom - top) * bands[band] / 255f;
                if (bands[band] > 0)
                {
                    var bandLeft = left + (right - left) * (float)SpectrumAxis.Position(_bandPlan[band].LowerHz);
                    var bandRight = left + (right - left) * (float)SpectrumAxis.Position(_bandPlan[band].UpperHz);
                    if (bandRight > bandLeft)
                        graphics.FillRectangle(_levelBrushes[bands[band]], bandLeft, y,
                            bandRight - bandLeft, Math.Max(scale, bottom - y));
                }
                var point = new PointF(x, y);
                if (band > 0 && (bands[band] > 0 || bands[band - 1] > 0)) graphics.DrawLine(_linePen, previous, point);
                previous = point;
            }
        }
        _linePen.DashStyle = DashStyle.Solid;
    }

    private void DrawSpectrumFrequencyLabels(Graphics graphics, float left, float right, float y, float height, int width, float scale)
    {
        var lastText = SpectrumAxis.FrequencyLabels[SpectrumAxis.Frequencies.Length - 1];
        var lastWidth = graphics.MeasureString(lastText, _analysisBodyFont).Width;
        var lastLeft = Math.Min(width - lastWidth, right - lastWidth / 2);
        var previousRight = float.MinValue;
        for (var i = 0; i < SpectrumAxis.Frequencies.Length; i++)
        {
            var text = SpectrumAxis.FrequencyLabels[i];
            var textWidth = graphics.MeasureString(text, _analysisBodyFont).Width;
            var x = left + (right - left) * (float)SpectrumAxis.Position(SpectrumAxis.Frequencies[i]);
            var labelLeft = Math.Max(0, Math.Min(width - textWidth, x - textWidth / 2));
            if (labelLeft < previousRight + 4 * scale ||
                i < SpectrumAxis.Frequencies.Length - 1 && labelLeft + textWidth + 4 * scale > lastLeft) continue;
            graphics.DrawString(text, _analysisBodyFont, _textBrush,
                new RectangleF(labelLeft, y, textWidth, height), _centeredText);
            previousRight = labelLeft + textWidth;
        }
    }

    private void DrawAnalysisGrid(Graphics graphics, float left, float right, float top, float bottom, bool labeled, float scale)
    {
        var guides = GuidesVisible ? 6 : 3;
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
        if (labeled)
        {
            DrawSignalLegend(graphics, 8 * scale, 1, width * 0.25f, header - 1, "RMS", _rmsHistoryPen.Color, true);
            DrawSignalLegend(graphics, width * 0.28f, 1, width * 0.30f, header - 1, "Sample peak", _peakHistoryPen.Color, false);
            graphics.DrawString("20 s · gaps retained", _analysisBodyFont, _textBrush,
                new RectangleF(width * 0.62f, 1, width * 0.37f, header - 1), _centeredText);
        }
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
            if (labeled) graphics.DrawString(_dynamicsLabels[channel], _analysisTitleFont, _primaryTextBrush,
                new RectangleF(left, panelY + 2 * scale, right - left, titleHeight + 3 * scale), _singleLineText);
            else if (compactIdentifiers)
                graphics.DrawString(channel == 0 ? channels == 2 ? "LEFT" : "MONO" : "RIGHT",
                    _analysisBodyFont, _primaryTextBrush, left, panelY + 2 * scale);
            DrawAnalysisGrid(graphics, left, right, top, bottom, labeled, scale);
            BuildDynamicsPaths(channel, left, right, top, bottom, oldestFrame);
            if (_dynamicsBreakPath.PointCount > 0) graphics.DrawPath(_precisionPen, _dynamicsBreakPath);
            if (_dynamicsRmsPath.PointCount > 0) graphics.DrawPath(_rmsHistoryPen, _dynamicsRmsPath);
            if (_dynamicsPeakPath.PointCount > 0) graphics.DrawPath(_peakHistoryPen, _dynamicsPeakPath);
        }
        if (labeled)
            for (var tick = 0; tick <= 4; tick++)
                graphics.DrawString(tick == 4 ? "LATEST" : tick == 0 ? "−20 s" : tick == 1 ? "−15 s" : tick == 2 ? "−10 s" : "−5 s",
                    _analysisBodyFont, _textBrush, new RectangleF(Math.Max(0, Math.Min(width - 64 * scale, left + (right - left) * tick / 4 - 32 * scale)), height - footer, 64 * scale, bodyHeight + 2 * scale), _centeredText);
    }

    private void BuildDynamicsPaths(int channel, float left, float right, float top, float bottom, long oldestFrame)
    {
        _dynamicsRmsPath.Reset();
        _dynamicsPeakPath.Reset();
        _dynamicsBreakPath.Reset();
        for (var index = 0; index < _audio.ObservationCount; index++)
        {
            var entry = _audio.ObservationAt(index);
            if (entry.EndFrame <= oldestFrame || !entry.Available || (channel == 1 && !entry.Stereo)) continue;
            var x1 = (float)Math.Max(left, left + (right - left) * (entry.StartFrame - oldestFrame) / (20d * _audio.SampleRate));
            var x2 = (float)Math.Min(right, left + (right - left) * (entry.EndFrame - oldestFrame) / (20d * _audio.SampleRate));
            var rms = channel == 0 ? entry.LeftRms : entry.RightRms;
            var peak = channel == 0 ? entry.LeftPeak : entry.RightPeak;
            var rmsY = DynamicsLevelY(rms, top, bottom);
            var peakY = DynamicsLevelY(peak, top, bottom);
            var prior = index > 0 ? _audio.ObservationAt(index - 1) : default;
            var contiguous = index > 0 && !entry.Discontinuity && prior.Available &&
                (channel == 0 || prior.Stereo) && prior.EndFrame == entry.StartFrame;
            if (entry.Discontinuity)
            {
                _dynamicsBreakPath.StartFigure();
                _dynamicsBreakPath.AddLine(x1, top, x1, bottom);
            }
            // start a new figure after a gap so we don't draw a line across missing data
            if (!contiguous)
            {
                _dynamicsRmsPath.StartFigure();
                _dynamicsPeakPath.StartFigure();
            }
            else
            {
                _dynamicsRmsPath.AddLine(x1, DynamicsLevelY(channel == 0 ? prior.LeftRms : prior.RightRms, top, bottom), x1, rmsY);
                _dynamicsPeakPath.AddLine(x1, DynamicsLevelY(channel == 0 ? prior.LeftPeak : prior.RightPeak, top, bottom), x1, peakY);
            }
            _dynamicsRmsPath.AddLine(x1, rmsY, x2, rmsY);
            _dynamicsPeakPath.AddLine(x1, peakY, x2, peakY);
        }
    }

    private static float DynamicsLevelY(double amplitude, float top, float bottom) =>
        bottom - (bottom - top) * (float)LevelScale.Normalize(AudioVisualizationData.AmplitudeToDb(amplitude));

    private void PaintStereoLevels(Graphics graphics, int width, int height, bool fitSpectrum = false)
    {
        if (!_audio.HasSamples) { DrawUnavailable(graphics, width, height); return; }
        EnsureAnalysisFonts(fitSpectrum);
        var scale = graphics.DpiY / 96f;
        var bodyHeight = _analysisBodyFont.GetHeight(graphics);
        var titleHeight = _analysisTitleFont.GetHeight(graphics);
        var labeled = !fitSpectrum && width >= 260 * scale && height >= Math.Max(140 * scale, bodyHeight * 8);
        var compactLabels = !labeled && width >= 140 * scale && height >= Math.Max(100 * scale, bodyHeight * 6);
        var showLabels = labeled || compactLabels;
        var channels = _audio.HasStereo ? 2 : 1;
        var cardWidth = width / (float)channels;
        var top = GetCaptionHeight(width, height) + (showLabels ? titleHeight + 12 * scale : 3 * scale);
        var footerHeight = showLabels ? 2 * bodyHeight + 10 * scale : 3 * scale;
        var bottom = Math.Max(top + 1, height - footerHeight);
        var plotHeight = bottom - top;
        _peakPen.DashStyle = DashStyle.Solid;
        for (var channel = 0; channel < channels; channel++)
        {
            var x = channel * cardWidth + 6 * scale;
            var innerWidth = Math.Max(scale, cardWidth - 12 * scale);
            var axisWidth = showLabels && innerWidth >= 100 * scale ? 34 * scale : 0;
            var meterLeft = x + axisWidth;
            var meterWidth = Math.Max(scale, innerWidth - axisWidth);
            graphics.FillRectangle(_surfaceBrush, meterLeft, top, meterWidth, plotHeight);
            var guides = GuidesVisible ? 6 : 2;
            for (var guide = 0; guide <= guides; guide++)
            {
                var y = bottom - plotHeight * guide / guides;
                graphics.DrawLine(_majorGridPen, meterLeft, y, x + innerWidth, y);
                if (axisWidth > 0)
                    graphics.DrawString(s_referenceDbLabels[guide * 6 / guides], _analysisBodyFont, _textBrush, x + 2 * scale, y - bodyHeight / 2);
            }
            for (var measure = 0; measure < 2; measure++)
            {
                var index = channel * 2 + measure;
                var normalized = (float)LevelScale.Normalize(_audioDb[index]);
                var barWidth = Math.Max(scale, Math.Min(110 * scale, meterWidth * 0.28f));
                var barX = meterLeft + meterWidth * (measure == 0 ? 0.25f : 0.75f) - barWidth / 2;
                var fillHeight = plotHeight * normalized;
                _thresholdBrush.Color = channel == 0 ? _leftChannelColor : _rightChannelColor;
                if (normalized > 0)
                {
                    graphics.FillRectangle(_thresholdBrush, barX, bottom - fillHeight, barWidth, fillHeight);
                    graphics.DrawLine(_peakPen, barX - 1, bottom - fillHeight, barX + barWidth + 1, bottom - fillHeight);
                }
                if (showLabels)
                {
                    var labelX = meterLeft + meterWidth * measure / 2;
                    graphics.DrawString(measure == 0 ? "RMS" : compactLabels ? "Pk" : "Sample peak", _analysisBodyFont, _textBrush,
                        new RectangleF(labelX, bottom + 2 * scale, meterWidth / 2, bodyHeight + scale), _centeredText);
                    graphics.DrawString(compactLabels ? _audioShortLabels[index] : _audioLabels[index], _analysisBodyFont, _primaryTextBrush,
                        new RectangleF(labelX, bottom + bodyHeight + 4 * scale, meterWidth / 2, bodyHeight + scale), _centeredText);
                }
            }
            if (showLabels)
                graphics.DrawString(_audio.HasStereo ? channel == 0 ? "LEFT" : "RIGHT" : "MONO", compactLabels ? _analysisBodyFont : _analysisTitleFont, _primaryTextBrush,
                    new RectangleF(x, GetCaptionHeight(width, height) + 3 * scale, innerWidth, titleHeight + 2 * scale), _centeredText);
        }
        DrawCaption(graphics, width, compactLabels ? "RMS / sample peak • dBFS" : _audioCaption);
    }

    private void PaintOscilloscope(Graphics graphics, int width, int height)
    {
        if (!_audio.HasSamples || _waveDisplayCount < 1) { DrawUnavailable(graphics, width, height); return; }
        EnsureAnalysisFonts(true);
        var scale = graphics.DpiY / 96f;
        var lineHeight = _analysisBodyFont.GetHeight(graphics);
        var labeled = width >= 300 * scale && height >= Math.Max(130 * scale, lineHeight * 7);
        var left = labeled ? Math.Max(58 * scale, graphics.MeasureString(_waveAmplitudeLabels[0] ?? "+1 FS", _analysisBodyFont).Width + 8 * scale) : 0;
        var right = Math.Max(left + 1, width - (labeled ? 18 * scale : 1));
        var top = height >= 80 ? Math.Max(25f, GetCaptionHeight(width, height) + (labeled ? lineHeight / 2 : 3f)) : 2f;
        var bottom = Math.Max(top + 1, height - (labeled ? lineHeight + 7 * scale : 4f));
        WaveformPlotBounds = RectangleF.FromLTRB(left, top, right, bottom);
        var center = (top + bottom) / 2;
        var halfHeight = (bottom - top) / 2;
        graphics.DrawLine(_majorGridPen, left, center, right, center);
        var guides = GuidesVisible ? 8 : 4;
        for (var i = 1; i < guides; i++)
            graphics.DrawLine(_gridPen, left + (right - left) * i / guides, top, left + (right - left) * i / guides, bottom);
        if (labeled)
            for (var tick = 0; tick < 5; tick++)
            {
                var y = top + (bottom - top) * tick / 4f;
                if (tick != 2) graphics.DrawLine(_gridPen, left, y, right, y);
                graphics.DrawString(_waveAmplitudeLabels[tick] ?? "0 FS", _analysisBodyFont, _textBrush,
                    new RectangleF(0, y - lineHeight / 2, left - 5 * scale, lineHeight), _rightText);
                var x = left + (right - left) * tick / 4f;
                var textWidth = graphics.MeasureString(_waveTimeLabels[tick] ?? "0", _analysisBodyFont).Width + 2;
                graphics.DrawString(_waveTimeLabels[tick] ?? "0", _analysisBodyFont, _textBrush,
                    new RectangleF(Math.Max(left, Math.Min(width - textWidth, x - textWidth / 2)), bottom + 3 * scale, textWidth, lineHeight), _centeredText);
            }
        _linePen.Width = 1.8f;
        _linePen.Color = _analyticalLow;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var pixelColumns = Math.Max(1, (int)(right - left));
        if (_waveDisplayCount <= pixelColumns)
        {
            var previous = new PointF(left, center - (float)(_audio.Wave[_waveStart] / _waveRange) * halfHeight);
            if (_waveDisplayCount == 1)
                graphics.DrawLine(_linePen, left, previous.Y, right, previous.Y);
            for (var i = 1; i < _waveDisplayCount; i++)
            {
                var point = new PointF(left + i * (right - left) / Math.Max(1, _waveDisplayCount - 1),
                    center - (float)(_audio.Wave[_waveStart + i] / _waveRange) * halfHeight);
                graphics.DrawLine(_linePen, previous, point);
                previous = point;
            }
        }
        else
        {
            // keep min/max when several samples land on one pixel column so peaks don't vanish
            var previousEnd = new PointF(left, center - (float)(_audio.Wave[_waveStart] / _waveRange) * halfHeight);
            for (var x = 0; x < pixelColumns; x++)
            {
                var start = x * _waveDisplayCount / pixelColumns;
                var end = (x + 1) * _waveDisplayCount / pixelColumns;
                float minimum = float.MaxValue, maximum = float.MinValue;
                for (var sample = start; sample < end; sample++)
                {
                    var value = _audio.Wave[_waveStart + sample];
                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                }
                graphics.DrawLine(_linePen, left + x, center - (float)(maximum / _waveRange) * halfHeight,
                    left + x, center - (float)(minimum / _waveRange) * halfHeight);
                var first = new PointF(left + x, center - (float)(_audio.Wave[_waveStart + start] / _waveRange) * halfHeight);
                if (x > 0) graphics.DrawLine(_linePen, previousEnd, first);
                previousEnd = new PointF(left + x, center - (float)(_audio.Wave[_waveStart + end - 1] / _waveRange) * halfHeight);
            }
        }
        DrawCaption(graphics, width, _waveCaption);
    }

    private void PaintSpectralBalance(Graphics graphics, int width, int height)
    {
        var top = height >= 100 ? Math.Max(30f, GetCaptionHeight(width, height) + 8f) : 2f;
        var footer = width >= 400 && height >= 140 ? _paintFontHeight + 7 : 3;
        var rowHeight = Math.Max(1, (height - top - footer) / 3);
        for (var group = 0; group < 3; group++)
        {
            var x = width >= 300 ? 170f : 2f;
            var y = top + group * rowHeight;
            var available = Math.Max(1, width - x - (width >= 300 ? 70 : 2));
            graphics.FillRectangle(_surfaceBrush, x, y + 3, available, Math.Max(1, rowHeight - 6));
            if (GuidesVisible)
                for (var tick = 1; tick < 4; tick++)
                    graphics.DrawLine(_precisionPen, x + available * tick / 4, y + 3, x + available * tick / 4, y + rowHeight - 3);
            _thresholdBrush.Color = _balanceColors[group];
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
        if (footer > 3)
        {
            var available = Math.Max(1, width - 240);
            for (var tick = 0; tick <= 4; tick++)
                graphics.DrawString(s_percentageLabels[tick], Font, _textBrush,
                    new RectangleF(170 + available * tick / 4f - 25, height - footer + 2, 50, footer - 2), _centeredText);
        }
        DrawCaption(graphics, width, "RELATIVE POWER SHARES • estimated from displayed bands • percent of total");
    }

    private static readonly string[] s_percentageLabels = { "0%", "25%", "50%", "75%", "100%" };

    private static readonly string[] s_sevenShortNames = { "Sub", "Bass", "Low-Mid", "Mid", "Presence", "Treble", "Air" };
    private static readonly string[] s_sevenShortRanges = { "20–60", "60–250", "250–500", "0.5–2k", "2–6k", "6–10k", "10–20k" };
    private readonly string[] _sevenRanges = new string[SevenBandAnalyzer.BandCount];
    private readonly string[] _sevenCompactRanges = new string[SevenBandAnalyzer.BandCount];
    private int _sevenRangeSampleRate = -1;
    private const float SevenBandRangeDb = 72f;
    private const int SevenBandAttackMs = 30;
    private const int SevenBandReleaseMs = 250;
    private const int SevenBandPeakHoldMs = 800;
    private const float SevenBandPeakDecayDbPerSecond = 24f;
    private int _sevenCaptionKey = -1;
    private static readonly string[] s_sevenAxisLabels = { "0", "−12", "−24", "−36", "−48", "−60", "−72" };
    private readonly StringFormat _rightText = new StringFormat
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.None,
        FormatFlags = StringFormatFlags.NoWrap
    };
    private string _sevenCaption = "7 bands • PCM samples not supplied";

    // NaN means the band has no data right now
    internal double GetSevenBandDisplayDb(int band) =>
        _sevenHasData && double.IsNaN(_audio.SevenBandPower[band]) ? double.NaN : _sevenLevels[band] - SevenBandRangeDb;

    internal double GetSevenBandPeakDb(int band) => _sevenPeaks[band] - SevenBandRangeDb;

    internal string GetSevenBandLabel(int band) => _sevenLabels[band];
    internal RectangleF SevenBandPlotBounds { get; private set; }
    internal bool SevenBandRangesVisible { get; private set; }

    internal string GetSevenBandRangeLabel(int band, bool compact)
    {
        var sampleRate = _audio.SampleRate;
        if (_sevenRangeSampleRate != sampleRate)
        {
            var nyquist = sampleRate / 2d;
            for (var index = 0; index < SevenBandAnalyzer.BandCount; index++)
            {
                var definition = SevenBandAnalyzer.Definitions[index];
                if (sampleRate <= 0 || definition.UpperHz <= nyquist)
                {
                    _sevenRanges[index] = definition.RangeLabel;
                    _sevenCompactRanges[index] = s_sevenShortRanges[index];
                }
                else if (definition.LowerHz >= nyquist)
                    _sevenRanges[index] = _sevenCompactRanges[index] = "n/a";
                else
                {
                    string Number(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                    var lower = definition.LowerHz;
                    _sevenRanges[index] = nyquist >= 1000
                        ? (lower >= 1000 ? Number(lower / 1000) : Number(lower) + " Hz") + "–" + Number(nyquist / 1000) + " kHz"
                        : Number(lower) + "–" + Number(nyquist) + " Hz";
                    _sevenCompactRanges[index] = nyquist >= 1000
                        ? Number(lower / 1000) + "–" + Number(nyquist / 1000) + "k"
                        : Number(lower) + "–" + Number(nyquist);
                }
            }
            _sevenRangeSampleRate = sampleRate;
        }
        return compact ? _sevenCompactRanges[band] : _sevenRanges[band];
    }

    private void UpdateSevenBandBallistics()
    {
        if (DisplayFrozen) return;
        var now = Stopwatch.GetTimestamp();
        var dtMs = _sevenLastTick == 0 ? 0f : (float)Math.Max(0d, (now - _sevenLastTick) * 1000d / Stopwatch.Frequency);
        _sevenLastTick = now;
        _sevenHasData = _audio.HasSevenBand;
        for (var band = 0; band < SevenBandAnalyzer.BandCount; band++)
        {
            var power = _audio.SevenBandPower[band];
            var measured = _sevenHasData && !double.IsNaN(power);
            var target = measured ? (float)(LevelScale.Normalize(LevelScale.PowerToDb(power)) * SevenBandRangeDb) : 0f;
            _sevenLevels[band] = BarBallistics.StepLevel(_sevenLevels[band], target, dtMs / 1000f,
                SevenBandRangeDb, SevenBandAttackMs, SevenBandReleaseMs, false);
            BarBallistics.StepPeak(ref _sevenPeaks[band], ref _sevenPeakHoldMs[band], _sevenLevels[band], dtMs,
                SevenBandPeakHoldMs, SevenBandPeakDecayDbPerSecond, 0f);

            var tenths = !_sevenHasData ? int.MaxValue : double.IsNaN(power) ? int.MaxValue - 1
                : _sevenLevels[band] <= 0.05f ? int.MinValue
                : (int)Math.Round((_sevenLevels[band] - SevenBandRangeDb) * 10);
            if (tenths == _sevenLabelTenths[band] && _sevenLabels[band] != null) continue;
            _sevenLabelTenths[band] = tenths;
            _sevenLabels[band] = tenths == int.MaxValue ? "—" : tenths == int.MaxValue - 1 ? "n/a"
                : tenths == int.MinValue ? "< −72"
                : (tenths / 10d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture).Replace('-', '−');
        }
        UpdateSevenBandTimer();
    }

    private Color SevenBandColor(int band)
    {
        var t = band / (float)(SevenBandAnalyzer.BandCount - 1);
        return t < 0.5f ? Blend(_theme.Low, _theme.Mid, t * 2f) : Blend(_theme.Mid, _theme.High, (t - 0.5f) * 2f);
    }

    private void PaintAdvancedSpectralBalance(Graphics graphics, int width, int height)
    {
        EnsureAnalysisFonts(true);
        const int count = SevenBandAnalyzer.BandCount;
        var lineHeight = (float)Math.Ceiling(_analysisBodyFont.GetHeight(graphics));
        var valueHeight = (float)Math.Ceiling(_analysisTitleFont.GetHeight(graphics));
        var slotEstimate = width / (float)count;
        var tier = slotEstimate >= 96 ? 2 : slotEstimate >= 52 ? 1 : 0;
        var axisWidth = width >= 210 ? (float)Math.Ceiling(lineHeight * 2.6f) : 0f;
        var left = 3 + axisWidth;
        var right = width - 4f;
        var slotWidth = (right - left) / count;
        var showValue = height >= 70;
        var showRange = height >= 90 && slotEstimate >= 36;
        if (showRange)
            for (var band = 0; band < count; band++)
                if (graphics.MeasureString(GetSevenBandRangeLabel(band, tier != 2), _analysisBodyFont).Width > slotWidth)
                {
                    // Exact low-rate boundaries can be longer than the nominal labels.
                    // Hide the optional row as a whole rather than ellipsizing a frequency.
                    showRange = false;
                    break;
                }
        var showName = height >= 110 && tier >= 1;
        var rows = (showValue ? 1 : 0) + (showRange ? 1 : 0) + (showName ? 1 : 0);
        var labelBlock = (showValue ? valueHeight : 0) + ((showRange ? 1 : 0) + (showName ? 1 : 0)) * lineHeight + (rows > 0 ? 4 : 0);
        var top = GetCaptionHeight(width, height);
        top = top > 0 ? top + 4 : 3;
        var bottom = height - labelBlock - 3f;
        var plotHeight = bottom - top;
        SevenBandPlotBounds = new RectangleF(left, top, Math.Max(0, right - left), Math.Max(0, plotHeight));
        SevenBandRangesVisible = showRange && plotHeight >= 8 && slotWidth >= 4;

        var captionState = !_audio.HasSamples ? 0 : !_audio.HasSevenBand ? 1 : 2;
        var captionKey = captionState == 2 ? _audio.SampleRate * 4 + 2 : captionState;
        if (_sevenCaptionKey != captionKey)
        {
            _sevenCaptionKey = captionKey;
            _sevenCaption = captionState == 0 ? "7 bands • PCM samples not supplied"
                : captionState == 1 ? "7 bands • waiting for 7-band analysis"
                : "7 bands • band power in dBFS (full-scale sine = 0 dB) • " +
                  (1000d * BandSpectrumAnalyzer.DefaultWindowLengths(_audio.SampleRate)[1] / _audio.SampleRate).ToString("0") + " ms Hann FFT";
        }
        if (plotHeight >= 8 && slotWidth >= 4)
        {
            for (var db = 0; db <= 72; db += 12)
            {
                var y = bottom - db / SevenBandRangeDb * plotHeight;
                graphics.DrawLine(db % 24 == 0 ? _majorGridPen : _gridPen, left, y, right, y);
                if (axisWidth > 0 && (db % 24 == 0 || plotHeight >= lineHeight * 12))
                    graphics.DrawString(s_sevenAxisLabels[(72 - db) / 12], _analysisBodyFont, _textBrush,
                        new RectangleF(0, y - lineHeight / 2, axisWidth, lineHeight), _rightText);
            }

            var barWidth = Math.Max(2f, slotWidth * 0.68f);
            for (var band = 0; band < count; band++)
            {
                var slotLeft = left + band * slotWidth;
                var x = slotLeft + (slotWidth - barWidth) / 2;
                graphics.FillRectangle(_surfaceBrush, x, top, barWidth, plotHeight);
                var color = SevenBandColor(band);
                var unreachable = _sevenHasData && double.IsNaN(_audio.SevenBandPower[band]);
                var barHeight = unreachable ? 0 : _sevenLevels[band] / SevenBandRangeDb * plotHeight;
                if (barHeight >= 0.5f)
                {
                    _thresholdBrush.Color = color;
                    graphics.FillRectangle(_thresholdBrush, x, bottom - barHeight, barWidth, barHeight);
                    _thresholdBrush.Color = Blend(color, _theme.Ui.PrimaryText, 0.45f);
                    graphics.FillRectangle(_thresholdBrush, x, bottom - barHeight, barWidth, Math.Min(2f, barHeight));
                }

                if (!unreachable && _sevenPeaks[band] > 0.5f)
                    graphics.FillRectangle(_primaryTextBrush, x, Math.Max(top, bottom - _sevenPeaks[band] / SevenBandRangeDb * plotHeight - 1f), barWidth, 2f);

                var rowTop = bottom + 4;
                var cell = new RectangleF(slotLeft, 0, slotWidth, lineHeight);
                if (showValue)
                {
                    cell.Y = rowTop;
                    cell.Height = valueHeight;
                    graphics.DrawString(_sevenLabels[band] ?? "—", _analysisTitleFont, _primaryTextBrush, cell, _centeredText);
                    rowTop += valueHeight;
                    cell.Height = lineHeight;
                }

                if (showName)
                {
                    cell.Y = rowTop;
                    graphics.DrawString(tier == 2 ? SevenBandAnalyzer.Definitions[band].Name : s_sevenShortNames[band],
                        _analysisBodyFont, _primaryTextBrush, cell, _centeredText);
                    rowTop += lineHeight;
                }

                if (showRange)
                {
                    cell.Y = rowTop;
                    graphics.DrawString(GetSevenBandRangeLabel(band, tier != 2),
                        _analysisBodyFont, _textBrush, cell, _centeredText);
                }
            }
        }

        DrawCaption(graphics, width, _sevenCaption);
    }

    private void DrawCaption(Graphics graphics, int width, string text)
    {
        var captionHeight = GetCaptionHeight(width, ClientSize.Height);
        if (captionHeight == 0) return;
        _thresholdBrush.Color = BackColor;
        graphics.FillRectangle(_thresholdBrush, 2, 2, width - 4, captionHeight - 3);
        graphics.DrawString(text, Font, _textBrush, new RectangleF(4, 3, width - 8, captionHeight - 5), _centeredText);
    }

    private int GetCaptionHeight(int width, int height)
    {
        // The selected mode and inspector already identify a compact calibrated
        // spectrum. Keep its grid usable before reserving a repeated caption.
        if ((_mode is AdvancedVisualizationMode.Contour or AdvancedVisualizationMode.PeakTrace
            or AdvancedVisualizationMode.ThresholdMonitor) && height < 120 * DeviceDpi / 96f) return 0;
        return width >= 180 && height >= 80
            ? Math.Max(22, (int)Math.Ceiling(_paintFontHeight > 0 ? _paintFontHeight : Font.GetHeight()) + 9) : 0;
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
        try { image = new Bitmap(HistoryImageWidth, frames, HistoryImageWidth * sizeof(int), PixelFormat.Format32bppPArgb, handle.AddrOfPinnedObject()); }
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
        var legendHeight = GetHistoryLegendHeight(width, height);
        var plotTop = GetCaptionHeight(width, height) + legendHeight;
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
        if (GuidesVisible)
        {
            var columns = octaves ? OctaveCount : 8;
            for (var column = 1; column < columns; column++)
                graphics.DrawLine(_precisionPen, width * column / (float)columns, plotTop, width * column / (float)columns, plotTop + plotHeight);
        }
        DrawCaption(graphics, width, _historyCaption);
        if (legendHeight > 0)
        {
            var y = GetCaptionHeight(width, height);
            var rampLeft = Math.Max(100, width / 3);
            var rampWidth = Math.Max(20, width / 3);
            for (var stop = 0; stop < 64; stop++)
            {
                _thresholdBrush.Color = Color.FromArgb(_paletteArgb[stop * 255 / 63]);
                graphics.FillRectangle(_thresholdBrush, rampLeft + rampWidth * stop / 64f, y + 3,
                    rampWidth / 64f + 1, Math.Max(4, legendHeight - 7));
            }
            graphics.DrawString("≤−72", Font, _textBrush, rampLeft - 48, y);
            graphics.DrawString("0 dBFS", Font, _textBrush, rampLeft + rampWidth + 4, y);
            if (width >= 900) graphics.DrawString("Quantized display level", Font, _textBrush, 5, y);
        }
        if (width >= 480 && plotHeight >= _paintFontHeight * 5 && plotTop + plotHeight - top >= _paintFontHeight * 4)
        {
            var markerWidth = Math.Min(width * 0.45f, graphics.MeasureString(_historyOldestLabel, Font).Width + 12);
            var markerHeight = _paintFontHeight + 3;
            _thresholdBrush.Color = Color.FromArgb(228, BackColor);
            graphics.FillRectangle(_thresholdBrush, width - markerWidth - 3, top + 2, markerWidth, markerHeight);
            graphics.DrawString(_historyOldestLabel, Font, _primaryTextBrush,
                new RectangleF(width - markerWidth - 1, top + 2, markerWidth - 4, markerHeight), _centeredText);
            graphics.FillRectangle(_thresholdBrush, width - markerWidth - 3, plotTop + plotHeight - markerHeight - 2, markerWidth, markerHeight);
            graphics.DrawString("Latest received", Font, _primaryTextBrush,
                new RectangleF(width - markerWidth - 1, plotTop + plotHeight - markerHeight - 2, markerWidth - 4, markerHeight), _centeredText);
        }
    }

    private int GetHistoryLegendHeight(int width, int height) =>
        width >= 500 && height >= Math.Max(120, _paintFontHeight * 6) ? (int)Math.Ceiling(Math.Max(18, _paintFontHeight + 3)) : 0;

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
        InvalidateFluxBaseline();
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
        var top = GetCaptionHeight(ClientSize.Width, ClientSize.Height) +
            (_mode == AdvancedVisualizationMode.SpectralHistory ? 0 : GetHistoryLegendHeight(ClientSize.Width, ClientSize.Height));
        var bottom = _mode == AdvancedVisualizationMode.OctaveWaterfall && ClientSize.Width >= 350 && ClientSize.Height >= 100 ? 20 : 0;
        var frames = _mode == AdvancedVisualizationMode.SpectralHistory
            ? Math.Min(_historyCount, PerspectiveHistoryFrames)
            : Math.Min(_historyCount, Math.Max(1, ClientSize.Height - top - bottom));
        var oldest = GetHistoryFrame(frames - 1);
        var newest = GetHistoryFrame(0);
        var tenths = (int)((_historyTicks[newest] - _historyTicks[oldest]) * 10 / Stopwatch.Frequency);
        if (tenths == _historySpanTenths && frames == _captionFrames && _captionHistoryMode == _mode) return;
        _historySpanTenths = tenths;
        _historyOldestLabel = "Oldest · " + (tenths / 10d).ToString("0.0") + " s before latest";
        _captionFrames = frames;
        _captionHistoryMode = _mode;
        _historyCaption = _mode == AdvancedVisualizationMode.SpectralHistory
            ? "Creative perspective • " + frames + " display frames • irregular intervals, not a time axis • latest at front"
            : (tenths / 10d).ToString("0.0") + " s received-frame span • " + frames + (frames == 1 ? " frame" : " frames") + " • oldest at top / newest at bottom" +
                (_mode == AdvancedVisualizationMode.OctaveWaterfall ? " • approx. octave power" : string.Empty);
    }

    private void UpdateAggregates()
    {
        Array.Clear(_octavePower, 0, _octavePower.Length);
        Array.Clear(_balance, 0, _balance.Length);
        var strongest = 0;
        for (var band = 0; band < BandCount; band++)
        {
            if (_spectrum[band] > _spectrum[strongest]) strongest = band;
            var power = s_displayPower[_spectrum[band]];
            _octavePower[_octaveByBand[band]] += power;
            var hz = _bandPlan[band].CenterHz;
            _balance[hz < 250 ? 0 : hz < 4000 ? 1 : 2] += power;
        }
        if (_radialStrongestBand != strongest || _radialStrongestLevel != _spectrum[strongest])
        {
            _radialStrongestBand = strongest;
            _radialStrongestLevel = _spectrum[strongest];
            _radialStrongest = _spectrum[strongest] == 0 ? "No band above display floor"
                : _bandLabels[strongest] + " · " + s_levelLabels[_spectrum[strongest]];
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
            // one frame can cover both the hold and the release; burn the leftover time right away
            // so a slow frame or UI stall doesn't stretch the hold
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
            or AdvancedVisualizationMode.StereoSpectrum or AdvancedVisualizationMode.DynamicsHistory or AdvancedVisualizationMode.SpectralHistory
            or AdvancedVisualizationMode.AdvancedSpectralBalance or AdvancedVisualizationMode.Overview or AdvancedVisualizationMode.StereoCompare;

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
        if (DisplayFrozen) return;
        // Resume starts a fresh display history; the frozen interval is not an observed delta.
        var resumed = _resumeSpectrum;
        if (resumed) { ResetHistory(); _resumeSpectrum = false; }
        if (_mode == AdvancedVisualizationMode.SpectralFlux && _fluxAudioBound && _fluxAudioAvailable &&
            !_fluxAudioFrameNew && _fluxBaselineValid) return;

        var copyCount = Math.Min(values.Length, BandCount);
        var observed = !_fluxAudioBound || (_fluxAudioAvailable && _fluxAudioFrameNew);
        if (observed)
        {
            Array.Copy(_spectrum, _previousSpectrum, _spectrum.Length);
            _fluxComparisonAvailable = _fluxBaselineValid;
            _fluxBaselineValid = true;
        }
        else if (_fluxAudioBound && !_fluxAudioAvailable) InvalidateFluxBaseline();
        Array.Clear(_spectrum, 0, _spectrum.Length);
        Array.Copy(values, 0, _spectrum, 0, copyCount);
        if (observed && !_fluxComparisonAvailable) Array.Copy(_spectrum, _previousSpectrum, BandCount);
        if (_fluxAudioBound) _fluxAudioFrameNew = false;
        UpdateFluxAccessibility();
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

    private string FluxWaitingMessage => _fluxAudioBound && !_fluxAudioAvailable
        ? "Audio unavailable · waiting for two measured frames"
        : "Waiting for another measured frame";

    private void InvalidateFluxBaseline()
    {
        _fluxBaselineValid = _fluxComparisonAvailable = false;
        UpdateFluxAccessibility();
    }

    private void UpdateFluxAccessibility()
    {
        if (_mode == AdvancedVisualizationMode.SpectralFlux)
            AccessibleDescription = _fluxComparisonAvailable
                ? "Display-level change between two consecutive observed frames; quantized dB levels."
                : FluxWaitingMessage;
    }

    private void ObserveFluxAudio(AudioVisualizationData data)
    {
        var boundary = !_fluxAudioBound || !_fluxAudioAvailable || !data.HasSamples ||
            !ReferenceEquals(_fluxSourceEpoch, data.SourceEpoch) || _fluxSampleRate != data.SampleRate ||
            _fluxChannels != data.Channels || data.EndFrame < _fluxEndFrame ||
            data.EndFrame - _fluxEndFrame > data.WindowFrames;
        _fluxAudioFrameNew = data.HasSamples && (boundary || data.EndFrame > _fluxEndFrame);
        _fluxAudioBound = true; _fluxAudioAvailable = data.HasSamples;
        _fluxSourceEpoch = data.SourceEpoch; _fluxSampleRate = data.SampleRate;
        _fluxChannels = data.Channels; _fluxEndFrame = data.EndFrame;
        if (boundary) InvalidateFluxBaseline();
        else UpdateFluxAccessibility();
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
        for (var i = 0; i < _historyTracePens.Length; i++)
            if (_historyTracePens[i] != null)
            {
                _historyTracePens[i].Color = Blend(BackColor,
                    i == 0 ? _theme.Ui.PrimaryText : _analyticalLow,
                    i == 0 ? 1f : 0.7f - 0.30f * i / (PerspectiveSlices - 1));
                _historyTracePens[i].Width = i == 0 ? 2.2f : 1f;
            }
        for (var level = 0; level < _levelBrushes.Length; level++)
        {
            Color color;
            if (level == 0)
            {
                color = BackColor;
            }
            else
            {
                // zero is gated elsewhere; any non-zero level starts at the Low colour so it
                // stays visible against the background
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
            // History encodes one ordered quantity; decorative multi-hue palettes remain
            // available to radial/matrix/bars without changing this luminance ordering.
            _paletteArgb[level] = Blend(BackColor, _theme.Ui.PrimaryText,
                (float)Math.Pow(level / 255f, 0.85)).ToArgb();
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

// display-only zoom window; the analysis bands themselves keep their real edges
internal static class SpectrumAxis
{
    internal static readonly double[] Frequencies =
        { 20, 30, 40, 60, 100, 200, 300, 400, 600, 1000, 2000, 3000, 4000, 6000, 10000, 20000 };
    internal static readonly double[] Decibels = { 0, -6, -12, -18, -24, -30, -36, -42, -48, -54, -60, -66, -72 };
    internal static readonly string[] FrequencyLabels = Array.ConvertAll(Frequencies, FrequencyLabel);
    internal static readonly string[] DecibelLabels = Array.ConvertAll(Decibels,
        value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
    internal static readonly float[] LegacyGridLevels =
        { 5f / 6, 4f / 6, 3f / 6, 2f / 6, 1f / 6 };

    internal static double Position(double hz) =>
        hz <= 20 ? 0 : hz >= 20000 ? 1 : Math.Log(hz / 20) / Math.Log(1000);

    internal static string FrequencyLabel(double hz) => hz >= 1000
        ? (hz / 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k"
        : hz.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    internal static string DecibelLabel(double db) => DecibelLabels[(int)Math.Round(-db / 6)];

    internal static float[] LocalGridPositions(int left, int right, int width)
    {
        var count = 0;
        foreach (var hz in Frequencies)
        {
            var x = (float)(Position(hz) * width);
            if (x >= left && (x < right || right == width && x == right)) count++;
        }
        if (count == 0) return Array.Empty<float>();
        var result = new float[count];
        var index = 0;
        foreach (var hz in Frequencies)
        {
            var x = (float)(Position(hz) * width);
            if (x >= left && (x < right || right == width && x == right))
                result[index++] = x - left;
        }
        return result;
    }
}
