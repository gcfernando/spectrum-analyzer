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
    NoteMap
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
    private byte[] _history;
    private readonly int[] _noteByBand = new int[BandCount];
    private readonly byte[] _noteLevels = new byte[12 * NoteOctaveCount];
    private readonly PointF[] _plotPoints = new PointF[BandCount];
    private readonly SolidBrush[] _levelBrushes = new SolidBrush[256];
    private readonly Pen _gridPen = new Pen(Color.FromArgb(52, 255, 255, 255), 1f);
    private readonly Pen _linePen = new Pen(Color.White, 1.5f);
    private readonly SolidBrush _contourFillBrush = new SolidBrush(Color.FromArgb(72, 0, 230, 90));
    private readonly SolidBrush _textBrush = new SolidBrush(Color.White);
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

    public AdvancedVisualizationControl()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);

        BackColor = Color.FromArgb(38, 35, 29);
        ForeColor = Color.FromArgb(215, 210, 196);

        _history = new byte[DefaultHistoryFrames * BandCount];
        for (var i = 0; i < _levelBrushes.Length; i++)
        {
            _levelBrushes[i] = new SolidBrush(Color.Black);
        }

        _theme = BarColorThemes.Resolve(_themeName);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(ClientRectangle);
            graphics.Clear(BackColor);

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
            _linePen.Dispose();
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
                graphics.DrawRectangle(_gridPen, x, y, cellWidth, cellHeight);

                var pitchBounds = new RectangleF(0, y, leftMargin - 2f, cellHeight);
                graphics.DrawString(s_pitchNames[pitch], Font, _textBrush, pitchBounds, _centeredText);
            }

            var octaveBounds = new RectangleF(x, topMargin + gridHeight, cellWidth, bottomMargin);
            graphics.DrawString(s_octaveLabels[octave], Font, _textBrush,
                octaveBounds, _centeredText);
        }
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

        parsedMode = default;
        return false;
    }

    private static void ValidateMode(AdvancedVisualizationMode mode, string parameterName)
    {
        if (mode != AdvancedVisualizationMode.Waterfall &&
            mode != AdvancedVisualizationMode.RadialSpectrum &&
            mode != AdvancedVisualizationMode.Contour &&
            mode != AdvancedVisualizationMode.NoteMap)
        {
            throw new ArgumentOutOfRangeException(parameterName, mode, "Unknown visualization mode.");
        }
    }

    private void UpdatePalette()
    {
        for (var level = 0; level < _levelBrushes.Length; level++)
        {
            Color color;
            if (level < 64)
            {
                color = Blend(BackColor, _theme.Low, level / 64f);
            }
            else
            {
                var position = (level - 64) / 191f;
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
