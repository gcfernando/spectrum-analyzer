using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Spectrum.Dsp;

namespace Spectrum;

/// <summary>Rendering modes provided by <see cref="AdvancedVisualizationControl"/>.</summary>
public enum AdvancedVisualizationMode
{
    Waterfall,
    RadialSpectrum,
    Contour,
    NoteMap,
    PeakTrace,
    ThresholdMonitor,
    BandMatrix,
    OctaveSpectrum,
    SpectralFlux,
    OrbitHistory,
    OctaveWaterfall,
    TransientMap,
    FrequencyRibbon
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
    private readonly int[] _noteByBand = new int[BandCount];
    private readonly byte[] _noteLevels = new byte[12 * NoteOctaveCount];
    private readonly PointF[] _plotPoints = new PointF[BandCount];
    private readonly SolidBrush[] _levelBrushes = new SolidBrush[256];
    private readonly Pen _gridPen = new Pen(Color.Empty, 1f);
    private readonly Pen _majorGridPen = new Pen(Color.Empty, 1f);
    private readonly Pen _linePen = new Pen(Color.Empty, 1.5f);
    private readonly Pen _softLinePen = new Pen(Color.Empty, 5f);
    private readonly Pen _framePen = new Pen(Color.Empty, 1f);
    private readonly SolidBrush _contourFillBrush = new SolidBrush(Color.Empty);
    private readonly SolidBrush _textBrush = new SolidBrush(Color.Empty);
    private readonly GraphicsPath _contourPath = new GraphicsPath();
    private readonly StringFormat _centeredText = new StringFormat
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.None
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
        for (var i = 0; i < _levelBrushes.Length; i++)
        {
            _levelBrushes[i] = new SolidBrush(Color.Black);
        }

        _theme = BarColorThemes.Resolve(_themeName);
        ApplyThemeChrome(_theme);
        UpdatePalette();
        SetBandPlan(BandPlan.CreateLogarithmic(BandCount, 20, 20000, 48000));
    }

    /// <summary>The active visualization. Changing it does not add a sample to waterfall history.</summary>
    public AdvancedVisualizationMode Mode
    {
        get => _mode;
        set
        {
            ValidateMode(value, nameof(value));
            if (_mode == value)
            {
                return;
            }

            _mode = value;
            Invalidate();
        }
    }

    internal VisualStyle Style
    {
        get => _style;
        set
        {
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

            _history = new byte[value * BandCount];
            _historyFrames = value;
            _historyWriteIndex = 0;
            _historyCount = 0;
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

        _mode = mode;
        Invalidate();
    }

    /// <summary>Copies a frame and updates the palette and mode using this assembly's internal theme type.</summary>
    /// <param name="values">Analyzer display-level frame; this control copies the values it uses.</param>
    /// <param name="theme">Palette already resolved by the application.</param>
    /// <param name="mode">Waterfall, RadialSpectrum, Contour, or NoteMap (case-insensitive).</param>
    internal void SetSpectrum(byte[] values, BarColorTheme theme, string mode)
    {
        if (!TryParseMode(mode, out var parsedMode))
        {
            throw new ArgumentException(
                "Mode must be Waterfall, RadialSpectrum, Contour, or NoteMap.", nameof(mode));
        }

        CopyAndAppendFrame(values);
        _theme = theme;
        _themeName = "Custom";
        ApplyThemeChrome(_theme);
        UpdatePalette();
        _mode = parsedMode;
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
        _softLinePen.Color = Color.FromArgb(48, theme.High);
        _contourFillBrush.Color = Color.FromArgb(62, theme.Mid);
        _textBrush.Color = ui.SecondaryText;
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
                    PaintBandMatrix(graphics, width, height, false);
                    break;
                case AdvancedVisualizationMode.OctaveSpectrum:
                    PaintOctaveSpectrum(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.SpectralFlux:
                    PaintSpectralFlux(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.OrbitHistory:
                    PaintOrbitHistory(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.OctaveWaterfall:
                    PaintOctaveWaterfall(graphics, width, height);
                    break;
                case AdvancedVisualizationMode.TransientMap:
                    PaintBandMatrix(graphics, width, height, true);
                    break;
                case AdvancedVisualizationMode.FrequencyRibbon:
                    PaintFrequencyRibbon(graphics, width, height);
                    break;
            }

            PaintStyleOverlay(graphics, width, height);

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
            }

            _gridPen.Dispose();
            _majorGridPen.Dispose();
            _linePen.Dispose();
            _softLinePen.Dispose();
            _framePen.Dispose();
            _contourFillBrush.Dispose();
            _textBrush.Dispose();
            _contourPath.Dispose();
            _centeredText.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PaintWaterfall(Graphics graphics, int width, int height)
    {
        if (_historyCount == 0)
        {
            return;
        }

        var rows = Math.Min(_historyCount, Math.Max(1, height));
        var firstFrame = _historyWriteIndex - rows;
        if (firstFrame < 0)
        {
            firstFrame += _historyFrames;
        }

        var cellWidth = (float)width / BandCount;
        var rowHeight = (float)height / rows;
        for (var row = 0; row < rows; row++)
        {
            var frameIndex = firstFrame + row;
            if (frameIndex >= _historyFrames)
            {
                frameIndex -= _historyFrames;
            }

            var y = height - ((row + 1) * rowHeight);
            var frameOffset = frameIndex * BandCount;
            for (var band = 0; band < BandCount; band++)
            {
                var x = band * cellWidth;
                graphics.FillRectangle(_levelBrushes[_history[frameOffset + band]],
                    x, y, cellWidth + 1f, rowHeight + 1f);
            }
        }

        for (var band = 10; band < BandCount; band += 10)
        {
            var x = (band * width) / BandCount;
            graphics.DrawLine(_majorGridPen, x, 0, x, height);
        }

        var rowStep = Math.Max(1, rows / 8);
        for (var row = rowStep; row < rows; row += rowStep)
        {
            var y = height - (int)Math.Round(row * rowHeight);
            graphics.DrawLine(_majorGridPen, 0, y, width, y);
        }
    }

    private void PaintRadialSpectrum(Graphics graphics, int width, int height)
    {
        var previousSmoothingMode = graphics.SmoothingMode;
        try
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var centerX = width * 0.5f;
            var centerY = height * 0.5f;
            var radius = Math.Max(1f, Math.Min(width, height) * 0.46f);
            var innerRadius = radius * 0.18f;
            var maxLength = Math.Max(1f, radius - innerRadius);
            var segmentWidth = Math.Max(1f, Math.Min(8f, (float)(2.0 * Math.PI * radius / BandCount) * 0.72f));

            _gridPen.Width = 1f;
            graphics.DrawEllipse(_gridPen, centerX - radius, centerY - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(_gridPen, centerX - (radius * 0.67f), centerY - (radius * 0.67f),
                radius * 1.34f, radius * 1.34f);
            graphics.DrawEllipse(_majorGridPen, centerX - (radius * 0.42f), centerY - (radius * 0.42f),
                radius * 0.84f, radius * 0.84f);

            for (var band = 0; band < BandCount; band += 10)
            {
                var guideAngle = ((Math.PI * 2.0 * band) / BandCount) - (Math.PI / 2.0);
                graphics.DrawLine(_majorGridPen,
                    centerX + ((float)Math.Cos(guideAngle) * innerRadius),
                    centerY + ((float)Math.Sin(guideAngle) * innerRadius),
                    centerX + ((float)Math.Cos(guideAngle) * radius),
                    centerY + ((float)Math.Sin(guideAngle) * radius));
            }

            for (var band = 0; band < BandCount; band++)
            {
                var angle = ((Math.PI * 2.0 * band) / BandCount) - (Math.PI / 2.0);
                var level = _spectrum[band] / 255f;
                var startRadius = innerRadius;
                var endRadius = startRadius + (level * maxLength);
                _linePen.Width = segmentWidth;
                _linePen.Color = _levelBrushes[_spectrum[band]].Color;
                graphics.DrawLine(_linePen,
                    centerX + ((float)Math.Cos(angle) * startRadius),
                    centerY + ((float)Math.Sin(angle) * startRadius),
                    centerX + ((float)Math.Cos(angle) * endRadius),
                    centerY + ((float)Math.Sin(angle) * endRadius));
            }
        }
        finally
        {
            graphics.SmoothingMode = previousSmoothingMode;
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

            var top = Math.Max(2f, height * 0.06f);
            var baseline = Math.Max(top + 1f, height - Math.Max(2f, height * 0.08f));
            var plotHeight = Math.Max(1f, baseline - top);
            for (var guide = 1; guide <= 3; guide++)
            {
                var y = baseline - (plotHeight * guide / 4f);
                graphics.DrawLine(_majorGridPen, 0, y, width, y);
            }

            for (var band = 20; band < BandCount; band += 20)
            {
                var x = (float)band * (width - 1f) / (BandCount - 1);
                graphics.DrawLine(_majorGridPen, x, top, x, baseline);
            }

            for (var band = 0; band < BandCount; band++)
            {
                var x = (float)band * (width - 1f) / (BandCount - 1);
                var y = baseline - ((_spectrum[band] / 255f) * plotHeight);
                _plotPoints[band] = new PointF(x, y);
            }

            _contourPath.Reset();
            _contourPath.AddCurve(_plotPoints, 0, BandCount - 1, 0.25f);
            _contourPath.AddLine(width - 1f, baseline, 0f, baseline);
            _contourPath.CloseFigure();

            var averageLevel = _spectrum[BandCount / 2];
            var themeColor = _levelBrushes[averageLevel].Color;
            _contourFillBrush.Color = Color.FromArgb(72, themeColor);
            graphics.FillPath(_contourFillBrush, _contourPath);

            _linePen.Width = Math.Max(1f, Math.Min(2.5f, height / 100f));
            _linePen.Color = _levelBrushes[220].Color;
            _contourPath.Reset();
            _contourPath.AddCurve(_plotPoints, 0, BandCount - 1, 0.25f);
            _softLinePen.Color = Color.FromArgb(48, _linePen.Color);
            graphics.DrawPath(_softLinePen, _contourPath);
            graphics.DrawPath(_linePen, _contourPath);
            graphics.DrawLine(_gridPen, 0, baseline, width, baseline);
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
        var frames = Math.Min(6, _historyCount);
        for (var age = frames - 1; age >= 0; age--)
        {
            var frame = GetHistoryFrame(age);
            if (frame < 0)
                continue;

            var alpha = 28 + ((frames - age) * 22);
            DrawSpectrumLine(graphics, width, top, baseline, frame * BandCount, Color.FromArgb(alpha, _theme.Mid), 1f);
        }

        DrawSpectrumLine(graphics, width, top, baseline, -1, _theme.High, 2f);
    }

    private void PaintThresholdMonitor(Graphics graphics, int width, int height)
    {
        const byte threshold = 170;
        var cellWidth = Math.Max(1f, width / (float)BandCount);
        var thresholdY = (int)Math.Round(height * (1f - (threshold / 255f)));
        for (var band = 0; band < BandCount; band++)
        {
            var level = _spectrum[band];
            var color = level >= threshold ? _theme.High : level >= 96 ? _theme.Mid : _theme.Low;
            var cellHeight = Math.Max(1f, height * level / 255f);
            using var brush = new SolidBrush(color);
            graphics.FillRectangle(brush, band * cellWidth, height - cellHeight, cellWidth - 1f, cellHeight);
        }

        graphics.DrawLine(_majorGridPen, 0, thresholdY, width, thresholdY);
    }

    private void PaintBandMatrix(Graphics graphics, int width, int height, bool showFlux)
    {
        const int columns = 12;
        var rows = (int)Math.Ceiling(BandCount / (double)columns);
        var cellWidth = width / (float)columns;
        var cellHeight = height / (float)rows;
        for (var band = 0; band < BandCount; band++)
        {
            var column = band % columns;
            var row = band / columns;
            var value = showFlux ? Math.Abs(_spectrum[band] - _previousSpectrum[band]) : _spectrum[band];
            var rect = new RectangleF(column * cellWidth, row * cellHeight, cellWidth, cellHeight);
            graphics.FillRectangle(_levelBrushes[value], rect);
            graphics.DrawRectangle(_gridPen, rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    private void PaintOctaveSpectrum(Graphics graphics, int width, int height)
        {
            const int groups = 10;
            var groupWidth = width / (float)groups;
            for (var group = 0; group < groups; group++)
            {
                var level = GetGroupLevel(_spectrum, group, groups);
                var barHeight = Math.Max(1f, height * level / 255f);
                var x = group * groupWidth;
                graphics.FillRectangle(_levelBrushes[level], x + 1f, height - barHeight, Math.Max(1f, groupWidth - 2f), barHeight);
                graphics.DrawRectangle(_majorGridPen, x, 0, Math.Max(1f, groupWidth - 1f), height - 1f);
            }
        }

    private void PaintSpectralFlux(Graphics graphics, int width, int height)
        {
            var columnWidth = Math.Max(1f, width / (float)BandCount);
            for (var band = 0; band < BandCount; band++)
            {
                var flux = Math.Abs(_spectrum[band] - _previousSpectrum[band]);
                var barHeight = Math.Max(1f, height * flux / 255f);
                graphics.FillRectangle(_levelBrushes[flux], band * columnWidth, height - barHeight, Math.Max(1f, columnWidth - 1f), barHeight);
            }
        }

    private void PaintOrbitHistory(Graphics graphics, int width, int height)
        {
            var centerX = width * 0.5f;
            var centerY = height * 0.5f;
            var radius = Math.Max(1f, Math.Min(width, height) * 0.44f);
            var frames = Math.Min(4, _historyCount);
            for (var age = frames - 1; age >= 0; age--)
            {
                var frame = GetHistoryFrame(age);
                if (frame < 0)
                    continue;

                var radialOffset = age * Math.Max(2f, radius * 0.06f);
                var alpha = 48 + ((frames - age) * 40);
                for (var band = 0; band < BandCount; band++)
                {
                    var angle = ((Math.PI * 2.0 * band) / BandCount) - (Math.PI / 2.0);
                    var level = _history[(frame * BandCount) + band] / 255f;
                    var start = radius * 0.20f + radialOffset;
                    var end = start + (level * (radius * 0.72f - radialOffset));
                    _linePen.Color = Color.FromArgb(alpha, _levelBrushes[_history[(frame * BandCount) + band]].Color);
                    _linePen.Width = Math.Max(1f, radius / 70f);
                    graphics.DrawLine(_linePen,
                        centerX + ((float)Math.Cos(angle) * start), centerY + ((float)Math.Sin(angle) * start),
                        centerX + ((float)Math.Cos(angle) * end), centerY + ((float)Math.Sin(angle) * end));
                }
            }
        }

    private void PaintOctaveWaterfall(Graphics graphics, int width, int height)
        {
            const int groups = 10;
            var rows = Math.Min(_historyCount, Math.Max(1, height));
            if (rows == 0)
                return;

            var cellWidth = width / (float)groups;
            var cellHeight = height / (float)rows;
            for (var row = 0; row < rows; row++)
            {
                var frame = GetHistoryFrame(rows - row - 1);
                if (frame < 0)
                    continue;

                for (var group = 0; group < groups; group++)
                {
                    var level = GetGroupLevel(_history, group, groups, frame * BandCount);
                    graphics.FillRectangle(_levelBrushes[level], group * cellWidth, row * cellHeight, cellWidth + 1f, cellHeight + 1f);
                }
            }
        }

    private void PaintFrequencyRibbon(Graphics graphics, int width, int height)
        {
            DrawContourGrid(graphics, width, height, out var top, out var baseline);
            var frames = Math.Min(5, _historyCount);
            for (var age = frames - 1; age >= 0; age--)
            {
                var frame = GetHistoryFrame(age);
                if (frame < 0)
                    continue;

                var offset = age * Math.Max(1f, height * 0.025f);
                DrawSpectrumLine(graphics, width, top + offset, baseline - offset, frame * BandCount,
                    Color.FromArgb(40 + ((frames - age) * 35), _theme.Mid), Math.Max(1f, 2.2f - (age * 0.2f)));
            }
        }

    private void PaintStyleOverlay(Graphics graphics, int width, int height)
        {
            if (_style == VisualStyle.Precision)
            {
                using var pen = new Pen(Color.FromArgb(180, _theme.Ui.MajorGrid));
                for (var guide = 1; guide < 4; guide++)
                {
                    var y = height * guide / 4f;
                    graphics.DrawLine(pen, 0f, y, width, y);
                }
            }
            else if (_style == VisualStyle.Scanline)
            {
                var y = height * (1f - GetAverageLevel());
                using var pen = new Pen(Color.FromArgb(72, _theme.High));
                graphics.DrawLine(pen, 0f, y, width, y);
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
            top = Math.Max(2f, height * 0.06f);
            baseline = Math.Max(top + 1f, height - Math.Max(2f, height * 0.08f));
            for (var guide = 1; guide <= 3; guide++)
            {
                var y = baseline - ((baseline - top) * guide / 4f);
                graphics.DrawLine(_majorGridPen, 0, y, width, y);
            }
        }

    private void DrawSpectrumLine(Graphics graphics, int width, float top, float baseline, int offset, Color color, float thickness)
        {
            var plotHeight = Math.Max(1f, baseline - top);
            for (var band = 0; band < BandCount; band++)
            {
                var level = offset < 0 ? _spectrum[band] : _history[offset + band];
                _plotPoints[band] = new PointF(
                    (float)band * (width - 1f) / (BandCount - 1),
                    baseline - ((level / 255f) * plotHeight));
            }

            _contourPath.Reset();
            _contourPath.AddCurve(_plotPoints, 0, BandCount - 1, 0.2f);
            _linePen.Color = color;
            _linePen.Width = thickness;
            graphics.DrawPath(_linePen, _contourPath);
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

    private static byte GetGroupLevel(byte[] values, int group, int groupCount, int offset = 0)
        {
            var start = offset + ((group * BandCount) / groupCount);
            var end = offset + (((group + 1) * BandCount) / groupCount);
            byte max = 0;
            for (var index = start; index < end; index++)
                max = Math.Max(max, values[index]);
            return max;
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

        var historyOffset = _historyWriteIndex * BandCount;
        Array.Copy(_spectrum, 0, _history, historyOffset, BandCount);
        _historyWriteIndex++;
        if (_historyWriteIndex == _historyFrames)
        {
            _historyWriteIndex = 0;
        }

        if (_historyCount < _historyFrames)
        {
            _historyCount++;
        }
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

        if (Enum.TryParse(mode?.Replace(" ", string.Empty), true, out AdvancedVisualizationMode parsed))
        {
            parsedMode = parsed;
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
        }
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
