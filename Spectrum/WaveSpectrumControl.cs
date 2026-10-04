using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Spectrum;

internal sealed class WaveSpectrumControl : Control
{
    private readonly byte[] _targets;
    private readonly float[] _levels;
    private readonly PointF[] _points;
    private readonly PointF[] _trailPoints;
    private readonly Timer _timer;
    private DateTime _lastTick;
    private Color _lowColor = Color.LimeGreen;
    private Color _midColor = Color.Gold;
    private Color _highColor = Color.Orange;
    private Color _peakColor = Color.Red;
    private VisualStyle _style;
    private bool _hasTrail;

    internal WaveSpectrumControl(int bandCount)
    {
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

    internal void SetSpectrum(byte[] values)
    {
        var count = Math.Min(values?.Length ?? 0, _targets.Length);
        if (count > 0)
            Buffer.BlockCopy(values, 0, _targets, 0, count);
        Array.Clear(_targets, count, _targets.Length - count);

        if (!_timer.Enabled)
        {
            _lastTick = DateTime.UtcNow;
            _timer.Start();
        }
    }

    internal void SetDisplayedLevelsForTesting(float[] levels)
    {
        var count = Math.Min(levels?.Length ?? 0, _levels.Length);
        if (count > 0)
            Array.Copy(levels, _levels, count);
        Array.Clear(_levels, count, _levels.Length - count);
        _timer.Stop();
        Invalidate();
    }

    internal void SetTheme(BarColorTheme theme)
    {
        _lowColor = theme.Low;
        _midColor = theme.Mid;
        _highColor = theme.High;
        _peakColor = theme.Peak;
        BackColor = theme.Ui.WaveSurface;
        Invalidate();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_points.Length == 0 || ClientSize.Width <= 1 || ClientSize.Height <= 1)
            return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var height = ClientSize.Height - 1f;
        PopulateFrequencyPoints(_levels, ClientSize.Width, ClientSize.Height, _points);

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

            Array.Copy(_points, _trailPoints, _points.Length);
            _hasTrail = true;
        }
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var elapsedSeconds = Math.Min(0.2f, Math.Max(0.001f, (float)(now - _lastTick).TotalSeconds));
        _lastTick = now;

        var moving = false;
        for (var i = 0; i < _levels.Length; i++)
        {
            _levels[i] = BarBallistics.StepLevel(_levels[i], _targets[i], elapsedSeconds, 255, 110, 440, false);
            if (Math.Abs(_levels[i] - _targets[i]) > 0.06f)
                moving = true;
        }

        Invalidate();
        if (!moving)
            _timer.Stop();
    }

    private Color GetLevelColor(float level)
    {
        level = Math.Max(0f, Math.Min(1f, level));
        if (level <= 0.55f)
            return Lerp(_lowColor, _midColor, level / 0.55f);
        if (level <= 0.85f)
            return Lerp(_midColor, _highColor, (level - 0.55f) / 0.30f);
        return Lerp(_highColor, _peakColor, (level - 0.85f) / 0.15f);
    }

    private float GetAverageLevel()
    {
        if (_levels.Length == 0)
            return 0f;

        var total = 0f;
        for (var i = 0; i < _levels.Length; i++)
            total += _levels[i];

        return Math.Max(0f, Math.Min(1f, total / (_levels.Length * 255f)));
    }

    private static Color Lerp(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.A + (to.A - from.A) * amount),
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

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
