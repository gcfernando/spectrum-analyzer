// Developer :: > Gehan Fernando
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Spectrum;

// Smooth line-style spectrum: eases each band toward its target level on a ~60fps timer
internal sealed class WaveSpectrumControl : Control
{
    // _targets are the latest incoming values, _levels are what's actually drawn (eased toward targets)
    private readonly byte[] _targets;
    private readonly float[] _levels;
    private readonly PointF[] _points;
    private readonly PointF[] _trailPoints;
    private readonly Timer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastTickTimestamp;
    private long _lastSpectrumTimestamp;
    // If no new frame arrives within this long, we treat the audio as gone and let the wave fall
    internal const int StaleFrameMilliseconds = 500;
    private Color _lowColor = Color.LimeGreen;
    private Color _midColor = Color.Gold;
    private Color _highColor = Color.Orange;
    private Color _peakColor = Color.Red;
    private VisualStyle _style;
    // Previous frame's line, drawn faintly for the Trail style
    private bool _hasTrail;

    // Timer stays stopped until audio shows up, no point ticking an idle control
    internal WaveSpectrumControl(int bandCount)
    {
        if (bandCount < 0) throw new ArgumentOutOfRangeException(nameof(bandCount));
        _targets = new byte[bandCount];
        _levels = new float[bandCount];
        _points = new PointF[bandCount];
        _trailPoints = new PointF[bandCount];
        _timer = new Timer { Interval = 16 };
        _timer.Tick += Timer_Tick;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(50, 50, 50);
    }

    // New data just sets targets and wakes the animation timer; drawing happens on the tick
    internal void SetSpectrum(byte[] values)
    {
        var count = Math.Min(values?.Length ?? 0, _targets.Length);
        if (count > 0)
            Buffer.BlockCopy(values, 0, _targets, 0, count);
        Array.Clear(_targets, count, _targets.Length - count);
        _lastSpectrumTimestamp = _clock.ElapsedTicks;

        if (!_timer.Enabled)
        {
            _lastTickTimestamp = _clock.ElapsedTicks;
            _timer.Start();
        }
    }

    // Lets tests set the drawn levels directly and freezes the animation
    internal void SetDisplayedLevelsForTesting(float[] levels)
    {
        var count = Math.Min(levels?.Length ?? 0, _levels.Length);
        if (count > 0)
            Array.Copy(levels, _levels, count);
        Array.Clear(_levels, count, _levels.Length - count);
        _timer.Stop();
        Invalidate();
    }

    // Jump straight to the new values with no easing, e.g. after a mode switch, and drop the old trail
    internal void SynchronizeSpectrum(byte[] values)
    {
        SetSpectrum(values);
        for (var i = 0; i < _levels.Length; i++)
            _levels[i] = _targets[i];
        _hasTrail = false;
        Invalidate();
    }

    // Pulls the four gradient colors and background from the theme
    internal void SetTheme(BarColorTheme theme)
    {
        _lowColor = theme.Low;
        _midColor = theme.Mid;
        _highColor = theme.High;
        _peakColor = theme.Peak;
        BackColor = theme.Ui.WaveSurface;
        Invalidate();
    }

    // Only repaint when the style actually changes
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

    // Timer is the only disposable we own
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }

    // Draws a translucent fill under the curve, then the line segment by segment so each one gets its own level color
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_points.Length == 0 || ClientSize.Width <= 1 || ClientSize.Height <= 1)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var height = ClientSize.Height - 1f;
        PopulateFrequencyPoints(_levels, ClientSize.Width, ClientSize.Height, _points);
        // One band can't form a line, so draw a single vertical stroke instead
        if (_points.Length == 1)
        {
            if (_levels[0] > 0)
            {
                using var singlePen = new Pen(GetLevelColor(_levels[0] / 255f), 2f);
                e.Graphics.DrawLine(singlePen, _points[0].X, height, _points[0].X, _points[0].Y);
            }
            _hasTrail = false;
            return;
        }

        // Close the path along the bottom edge so the fill is a solid area
        using var fillPath = new GraphicsPath();
        fillPath.AddLines(_points);
        fillPath.AddLine(_points[_points.Length - 1].X, height, _points[0].X, height);
        fillPath.CloseFigure();
        using var fillBrush = new SolidBrush(Color.FromArgb(_style == VisualStyle.Precision ? 28 : 42, _midColor));
        e.Graphics.FillPath(fillBrush, fillPath);

        if (_style == VisualStyle.Trail && _hasTrail)
        {
            using var trailPen = new Pen(Color.FromArgb(58, _midColor), 1.5f)
            {
                LineJoin = LineJoin.Round
            };
            e.Graphics.DrawLines(trailPen, _trailPoints);
        }

        // Pulse brightens the line with overall loudness; scanline rides at the average level
        var pulse = _style == VisualStyle.Pulse
            ? 0.08f + (0.20f * GetAverageLevel())
            : 0f;
        var scanlineY = height * (1f - GetAverageLevel());
        if (_style == VisualStyle.Scanline)
        {
            using var scanPen = new Pen(Color.FromArgb(72, _highColor), 1f);
            e.Graphics.DrawLine(scanPen, 0f, scanlineY, ClientSize.Width - 1f, scanlineY);
        }

        for (var i = 1; i < _points.Length; i++)
        {
            var level = (_levels[i - 1] + _levels[i]) / (2f * 255f);
            var color = GetLevelColor(level);
            if (pulse > 0f)
                color = Lerp(color, Color.White, pulse);
            using var pen = new Pen(color, _style == VisualStyle.Glow ? 4f : _style == VisualStyle.Precision ? 2.75f : 2.25f)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            // Glow = wide faint stroke here, then a crisp core stroke on top below
            if (_style == VisualStyle.Glow)
                pen.Color = Color.FromArgb(54, color);
            e.Graphics.DrawLine(pen, _points[i - 1], _points[i]);

            if (_style == VisualStyle.Glow)
            {
                using var corePen = new Pen(color, 2.25f)
                {
                    LineJoin = LineJoin.Round,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                e.Graphics.DrawLine(corePen, _points[i - 1], _points[i]);
            }

        }

        // Remember this frame's shape for the next trail draw
        Array.Copy(_points, _trailPoints, _points.Length);
        _hasTrail = GetAverageLevel() > 0f;
    }

    // Animation step: advances levels by real elapsed time, and if the feed went stale it zeroes targets mid-step
    private void Timer_Tick(object sender, EventArgs e)
    {
        var now = _clock.ElapsedTicks;
        var elapsedSeconds = Math.Max(0f, (float)(now - _lastTickTimestamp) / Stopwatch.Frequency);
        var deadline = _lastSpectrumTimestamp + Stopwatch.Frequency * StaleFrameMilliseconds / 1000;
        // Split the elapsed time at the stale deadline: normal movement before it, decay toward zero after
        if (now >= deadline)
        {
            var activeSeconds = Math.Max(0f, (float)(deadline - _lastTickTimestamp) / Stopwatch.Frequency);
            activeSeconds = Math.Min(elapsedSeconds, activeSeconds);
            StepLevels(activeSeconds);
            Array.Clear(_targets, 0, _targets.Length);
            elapsedSeconds -= activeSeconds;
        }
        _lastTickTimestamp = now;

        StepLevels(elapsedSeconds);
        var moving = false;
        for (var i = 0; i < _levels.Length; i++)
        {
            if (_levels[i] != _targets[i] || _targets[i] != 0)
                moving = true;
        }

        // Everything settled at silence: stop the timer so we idle for free
        if (!moving && GetAverageLevel() <= 0f)
            _hasTrail = false;

        Invalidate();
        if (!moving)
            _timer.Stop();
    }

    // Shared ballistics with the bar meters: fast attack, slower release
    private void StepLevels(float elapsedSeconds)
    {
        for (var i = 0; i < _levels.Length; i++)
            _levels[i] = BarBallistics.StepLevel(_levels[i], _targets[i], elapsedSeconds, 255, 110, 440, false);
    }

    // Three-segment gradient; the breakpoints (55%, 85%) keep loud peaks from washing out the mid colors
    private Color GetLevelColor(float level)
    {
        level = Math.Max(0f, Math.Min(1f, level));
        if (level <= 0.55f)
            return Lerp(_lowColor, _midColor, level / 0.55f);
        if (level <= 0.85f)
            return Lerp(_midColor, _highColor, (level - 0.55f) / 0.30f);
        return Lerp(_highColor, _peakColor, (level - 0.85f) / 0.15f);
    }

    // Mean of all bands, 0..1; drives pulse, scanline and the "is it silent" check
    private float GetAverageLevel()
    {
        if (_levels.Length == 0)
            return 0f;

        var total = 0f;
        for (var i = 0; i < _levels.Length; i++)
            total += _levels[i];

        return Math.Max(0f, Math.Min(1f, total / (_levels.Length * 255f)));
    }

    // Linear color blend, amount 0..1
    private static Color Lerp(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.A + (to.A - from.A) * amount),
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    // Spreads bands evenly across the width; Y is flipped since 0 is the top in GDI+
    internal static void PopulateFrequencyPoints(float[] levels, int width, int height, PointF[] points)
    {
        var drawableWidth = width - 1f;
        var drawableHeight = height - 1f;
        for (var i = 0; i < points.Length; i++)
        {
            var x = points.Length == 1 ? drawableWidth / 2f : drawableWidth * i / (points.Length - 1f);
            points[i] = new PointF(x, drawableHeight * (1f - (levels[i] / 255f)));
        }
    }
}
