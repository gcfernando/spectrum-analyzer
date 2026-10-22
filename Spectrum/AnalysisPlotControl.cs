using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum.Dsp;

namespace Spectrum;

internal sealed class AnalysisInspection
{
    internal AnalysisInspection(int bandNumber = 0, double lowerHz = double.NaN, double centerHz = double.NaN,
        double upperHz = double.NaN, double levelDb = double.NaN, double peakDb = double.NaN, double deltaDb = double.NaN,
        bool isPinned = false, bool isBelowFloor = false, bool isUnavailable = false, bool isDigitalSilence = false, string status = "Point to a band")
    {
        BandNumber = bandNumber; LowerHz = lowerHz; CenterHz = centerHz; UpperHz = upperHz;
        LevelDb = levelDb; PeakDb = peakDb; DeltaDb = deltaDb; IsPinned = isPinned;
        IsBelowFloor = isBelowFloor; IsUnavailable = isUnavailable; IsDigitalSilence = isDigitalSilence; Status = status;
    }
    public int BandNumber { get; }
    public double LowerHz { get; }
    public double CenterHz { get; }
    public double UpperHz { get; }
    public double LevelDb { get; }
    public double PeakDb { get; }
    public double DeltaDb { get; }
    public bool IsPinned { get; }
    public bool IsBelowFloor { get; }
    public bool IsUnavailable { get; }
    public bool IsDigitalSilence { get; }
    public string Status { get; }
}

internal sealed class AnalysisPlotControl : Control
{
    private readonly AnalysisWorkspace _workspace;
    private readonly ToolTip _tip = new() { AutoPopDelay = 15000 };
    private bool _showSpectrogram;
    private bool _showPhase;
    private bool _showReference = true;
    private VisualTheme _presentation = BarColorThemes.Studio.Ui;
    public bool ReferenceOnly { get; set; }
    public bool Compact { get; set; }
    public void ApplyPresentation(VisualTheme theme)
    { _presentation = theme; BackColor = theme.VisualizationSurface; ForeColor = theme.PrimaryText; Invalidate(); }
    private int _selectedBand = -1;
    private bool _pinned;
    public AnalysisPlotControl(AnalysisWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(15, 21, 29); ForeColor = Color.FromArgb(223, 234, 245);
        Font = new Font("Segoe UI", 9); TabStop = true;
        AccessibleName = "Measured analysis spectrum";
        AccessibleDescription = "Mouse wheel zooms, arrow keys pan, plus and minus zoom, Escape clears a pinned inspection.";
        ApplyPresentation(_presentation);
    }
    public bool ShowSpectrogram
    {
        get => _showSpectrogram;
        set
        {
            _showSpectrogram = value;
            AccessibleDescription = value
                ? "Sample-timed spectrogram. Color represents measured dBFS; hatched areas have no measured data. Mouse wheel zooms; arrow keys pan."
                : "Mouse wheel zooms, arrow keys pan, plus and minus zoom, Escape clears a pinned inspection.";
            Invalidate();
        }
    }
    public bool ShowPhase { get => _showPhase; set { _showPhase = value; Invalidate(); } }
    public bool ShowReference { get => _showReference; set { _showReference = value; Invalidate(); } }
    public AnalysisInspection Inspection { get; private set; } = new();
    public string InspectionText { get; private set; } = "Hover a band to inspect; click to pin. Wheel to zoom; arrow keys to pan.";
    public event Action InspectionChanged;
    public void RefreshInspection()
    {
        var frame = _workspace.Latest;
        if (_showPhase)
        {
            Inspection = new AnalysisInspection(isUnavailable: frame == null || frame.Channels < 2 || _workspace.IsUnavailable, status: "Stereo phase");
            InspectionText = frame == null || frame.Channels < 2 ? "Stereo phase unavailable: two measured channels are required."
                : _workspace.IsUnavailable ? "Stereo phase unavailable: no current captured audio."
                : $"Recent {_workspace.PhaseWindowSeconds * 1000:N1} ms PCM-window density · horizontal Side (L−R)/2 · vertical Mid (L+R)/2 · auto range ±{_workspace.PhaseExtent:G4} FS";
            AccessibleDescription = InspectionText; InspectionChanged?.Invoke(); return;
        }
        if (frame == null || (_workspace.IsUnavailable && !ReferenceOnly))
        { SetUnavailableInspection("Audio unavailable · no current measurement", "Audio unavailable"); return; }
        if (_selectedBand >= 0 && frame != null && _selectedBand < frame.Plan.Count)
            InspectAt((int)X(frame.Plan[_selectedBand].CenterHz));
        else if (Inspection.IsUnavailable && !_pinned) ClearInspection();
    }
    private int D(int value) => (int)Math.Round(value * Math.Max(1f, DeviceDpi / 96f));
    private Rectangle Plot => Compact ? new Rectangle(D(47), D(23), Math.Max(1, Width - D(61)), Math.Max(1, Height - D(52)))
        : new Rectangle(D(62), D(42), Math.Max(1, Width - D(82)), Math.Max(1, Height - D(90)));
    private static double Db(double power) => power > 0 ? 10 * Math.Log10(power) : double.NegativeInfinity;
    private float X(double hz) => Plot.Left + (float)(Math.Log(Math.Max(.001, hz) / _workspace.Settings.MinHz) /
        Math.Log(_workspace.Settings.MaxHz / _workspace.Settings.MinHz) * Plot.Width);
    private float Y(double power)
    {
        var value = (Db(power) - _workspace.Settings.FloorDb) / (_workspace.Settings.CeilingDb - _workspace.Settings.FloorDb);
        return Plot.Bottom - (float)Math.Max(0, Math.Min(1, value)) * Plot.Height;
    }
    public void ResetZoom()
        => SetFrequencyRange(20, 20000);
    public void SetFrequencyRange(double minimumHz, double maximumHz)
    {
        var candidate = _workspace.Settings.Clone(); candidate.MinHz = minimumHz; candidate.MaxHz = maximumHz; candidate.Validate();
        _workspace.Settings.MinHz = minimumHz; _workspace.Settings.MaxHz = maximumHz;
        _workspace.UpdateSettings(); Invalidate();
    }
    public void Pan(double fractionalSpan)
    {
        var settings = _workspace.Settings;
        var ratio = settings.MaxHz / settings.MinHz;
        var lower = settings.MinHz * Math.Pow(ratio, fractionalSpan);
        lower = Math.Max(1, Math.Min(384000 / ratio, lower));
        settings.MinHz = lower; settings.MaxHz = Math.Min(384000, lower * ratio); _workspace.UpdateSettings(); Invalidate();
    }
    public void Zoom(double factor)
    {
        if (!(factor > 0) || double.IsInfinity(factor)) return;
        var s = _workspace.Settings;
        var center = Math.Sqrt(s.MinHz * s.MaxHz);
        var half = Math.Max(Math.Log(1.02), Math.Min(Math.Log(384000) / 2, Math.Log(s.MaxHz / s.MinHz) * .5 / factor));
        var lo = center / Math.Exp(half); var hi = center * Math.Exp(half);
        if (lo < 1) { hi /= lo; lo = 1; }
        if (hi > 384000) { lo *= 384000 / hi; hi = 384000; }
        // Shifting the full span can round an endpoint just outside the strict limits.
        // Clamp after both shifts, so wheel/key/button navigation reaches an exact boundary.
        s.MinHz = Math.Max(1, lo); s.MaxHz = Math.Min(384000, hi); _workspace.UpdateSettings(); Invalidate();
    }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (!_showPhase) Zoom(e.Delta > 0 ? 1.25 : .8); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_showPhase && !_pinned && Plot.Contains(e.Location)) InspectAt(e.X);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus(); if (_showPhase) return;
        if (e.Button == MouseButtons.Left && Plot.Contains(e.Location))
        { _pinned = true; InspectAt(e.X); }
        else if (e.Button == MouseButtons.Right) ClearInspection();
    }
    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_showPhase) return;
        if (e.KeyCode == Keys.Left) Pan(-.15);
        else if (e.KeyCode == Keys.Right) Pan(.15);
        else if (e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus) Zoom(1.25);
        else if (e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus) Zoom(.8);
        else if (e.KeyCode == Keys.Escape) ClearInspection();
        else return;
        e.Handled = true;
    }
    private void InspectAt(int x)
    {
        var frame = _workspace.Latest;
        if (frame == null || (_workspace.IsUnavailable && !ReferenceOnly))
        { SetUnavailableInspection("Audio unavailable · no current measurement", "Audio unavailable"); return; }
        var hz = _workspace.Settings.MinHz * Math.Pow(_workspace.Settings.MaxHz / _workspace.Settings.MinHz, (x - Plot.Left) / (double)Plot.Width);
        if (hz < frame.Plan.MinHz || hz > frame.Plan.MaxHz)
        { _selectedBand = -1; SetUnavailableInspection("Outside the measured frequency range · no data", "Outside measured range"); return; }
        var band = Math.Max(0, Math.Min(frame.Plan.Count - 1, (int)Math.Floor(frame.Plan.FrequencyToPosition(hz))));
        _selectedBand = band;
        var reference = _workspace.ReferencePower;
        if (ReferenceOnly && (reference == null || reference.Length <= band))
        { SetUnavailableInspection("Capture a reference to compare the same frequency bands", "No reference captured"); return; }
        var fb = frame.Plan[band]; var p = ReferenceOnly ? reference[band] : RawPower(frame, band); var db = Db(p);
        var level = p == 0 ? "zero band power" : db.ToString("F2", CultureInfo.CurrentCulture) + " dBFS";
        var display = p > 0 && db < _workspace.Settings.FloorDb ? " · below display floor" : "";
        var unavailableChannel = !ReferenceOnly && (_workspace.Settings.Channel == "Right" || _workspace.Settings.Channel == "Side") && frame.Channels < 2;
        if (unavailableChannel)
            level = "unavailable for mono capture";
        var displayed = _workspace.DisplayPower;
        var deltaDb = !_workspace.IsUnavailable && reference != null && reference.Length > band && reference[band] > 0 && displayed.Length > band && displayed[band] > 0
            ? Db(displayed[band]) - Db(reference[band]) : double.NaN;
        var delta = !double.IsNaN(deltaDb) ? " · curve Δ " + deltaDb.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + " dB" : "";
        var peaks = ReferenceOnly ? Array.Empty<double>() : _workspace.PeakPower;
        var peakText = peaks.Length > band && peaks[band] > 0 ? Db(peaks[band]).ToString("F2", CultureInfo.CurrentCulture) + " dBFS" : "−∞ dBFS";
        var digitalSilence = !ReferenceOnly && p == 0 && !unavailableChannel && SelectedWindowIsSilent(frame, _workspace.Settings.Channel);
        if (digitalSilence) level = "digital silence (zero band power)";
        var status = unavailableChannel ? "Channel unavailable" : digitalSilence ? "Digital silence" : p == 0 ? "Zero band power"
            : db < _workspace.Settings.FloorDb ? "Below display floor" : ReferenceOnly ? "Captured reference" : "Measured";
        Inspection = new AnalysisInspection(band + 1, fb.LowerHz, fb.CenterHz, fb.UpperHz,
            unavailableChannel ? double.NaN : db, unavailableChannel || peaks.Length <= band ? double.NaN : Db(peaks[band]),
            unavailableChannel ? double.NaN : deltaDb, _pinned, !unavailableChannel && p > 0 && db < _workspace.Settings.FloorDb,
            unavailableChannel, digitalSilence, status);
        InspectionText = $"Band {band + 1} · {fb.LowerHz:N1}–{fb.UpperHz:N1} Hz · center {fb.CenterHz:N1} Hz · {level}{display} · held curve peak {peakText}{delta}";
        if (ReferenceOnly) InspectionText = $"Captured reference curve · Band {band + 1} · {fb.LowerHz:N1}–{fb.UpperHz:N1} Hz · center {fb.CenterHz:N1} Hz · {level}{display}{delta}";
        PublishInspection();
    }
    private void PublishInspection()
    { AccessibleDescription = InspectionText; _tip.SetToolTip(this, InspectionText); InspectionChanged?.Invoke(); Invalidate(); }
    private void SetUnavailableInspection(string text, string status)
    { Inspection = new AnalysisInspection(isPinned: _pinned, isUnavailable: true, status: status); InspectionText = text; PublishInspection(); }
    private void ClearInspection()
    {
        _pinned = false; _selectedBand = -1; Inspection = new AnalysisInspection();
        InspectionText = "Hover a band to inspect; click to pin. Wheel to zoom; arrow keys to pan."; PublishInspection();
    }
    private static bool SelectedWindowIsSilent(AnalysisFrame frame, string channel)
    {
        var valid = (int)Math.Min(frame.FrameCount, frame.EndFrame - frame.FirstValidFrame);
        if (valid == 0) return false; // Saved measurements do not retain a PCM window.
        double Clean(float sample) => float.IsNaN(sample) || float.IsInfinity(sample) ? 0 : sample;
        for (var f = frame.FrameCount - valid; f < frame.FrameCount; f++)
        {
            var left = Clean(frame.Samples[f * frame.Channels]);
            var right = frame.Channels > 1 ? Clean(frame.Samples[f * frame.Channels + 1]) : 0;
            var silent = channel == "Left" ? left == 0 : channel == "Right" ? right == 0
                : channel == "Mid" ? (frame.Channels > 1 ? left + right == 0 : left == 0)
                : channel == "Side" ? left - right == 0 : left == 0 && right == 0;
            if (!silent) return false;
        }
        return true;
    }
    private double RawPower(AnalysisFrame frame, int band)
    {
        var raw = _workspace.RawSelectedPower;
        if (raw.Length > band) return raw[band];
        var channel = _workspace.Settings.Channel;
        if (channel == "Left") return frame.LeftPower[band];
        if (channel == "Right") return frame.Channels > 1 ? frame.RightPower[band] : 0;
        if (channel == "Mid" || channel == "Side")
        { var selected = _workspace.RawSelectedPower; return selected.Length > band ? selected[band] : 0; }
        return frame.Power[band];
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 100 || Height < 110) return;
        if (_showPhase)
        {
            PaintPhase(g);
            if (Focused) { using var focus = new Pen(_presentation.Focus); g.DrawRectangle(focus, 1, 1, Width - 3, Height - 3); }
            return;
        }
        using var text = new SolidBrush(ForeColor);
        using var secondary = new SolidBrush(_presentation.SecondaryText);
        using var grid = new Pen(_presentation.Grid);
        using var major = new Pen(_presentation.MajorGrid);
        var plot = Plot; var settings = _workspace.Settings;
        g.DrawString(ReferenceOnly ? "CAPTURED REFERENCE" : _showSpectrogram ? "SAMPLE-TIMED SPECTROGRAM" : "MEASURED FREQUENCY SPECTRUM", Font, text, D(12), D(Compact ? 3 : 9));
        var frame = _workspace.Latest;
        if (_showSpectrogram && frame != null) PaintHistory(g, frame);
        var intervals = Compact ? 3 : 6;
        for (var i = 0; i <= intervals; i++)
        {
            var y = plot.Top + i * plot.Height / (float)intervals;
            g.DrawLine(i == intervals ? major : grid, plot.Left, y, plot.Right, y);
            var label = _showSpectrogram ? (-settings.HistorySeconds * (1 - i / (double)intervals)).ToString("0.#") + " s"
                : (settings.CeilingDb - i * (settings.CeilingDb - settings.FloorDb) / intervals).ToString("0.#");
            g.DrawString(label, Font, secondary, D(3), y - Font.GetHeight(g) / 2f);
        }
        float lastRight = -100;
        foreach (var hz in new double[] { 1, 5, 10, 20, 30, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 40000, 96000, 192000, 384000 })
        {
            if (hz < settings.MinHz || hz > settings.MaxHz) continue;
            var x = X(hz); g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            var label = hz >= 1000 ? (hz / 1000).ToString("0.#") + "k" : hz.ToString("0");
            var size = g.MeasureString(label, Font);
            if (x - size.Width / 2 < lastRight + D(5)) continue;
            g.DrawString(label, Font, secondary, x - size.Width / 2, plot.Bottom + D(7)); lastRight = x + size.Width / 2;
        }
        var saved = g.Save(); g.SetClip(plot);
        if (frame != null && !_showSpectrogram)
        {
            if (!ReferenceOnly)
            {
                PaintCurve(g, frame.Plan, _workspace.DisplayPower, SharedPresentation.CurrentCurve(_presentation), false, true);
                PaintCurve(g, frame.Plan, _workspace.PeakPower, _presentation.SecondaryText, true, false);
            }
            if (_showReference || ReferenceOnly) PaintCurve(g, frame.Plan, _workspace.ReferencePower, SharedPresentation.ReferenceCurve(_presentation), true, false);
        }
        if (frame != null && _selectedBand >= 0 && _selectedBand < frame.Plan.Count)
        {
            using var pin = new Pen(Color.FromArgb(255, 210, 112)) { DashStyle = DashStyle.Dot };
            var x = X(frame.Plan[_selectedBand].CenterHz); g.DrawLine(pin, x, plot.Top, x, plot.Bottom);
        }
        g.Restore(saved);
        if (frame != null) PaintUnmeasured(g, frame.Plan, secondary);
        if (frame == null || (_workspace.IsUnavailable && !ReferenceOnly) || (ReferenceOnly && _workspace.ReferencePower == null))
        {
            using var overlay = new SolidBrush(Color.FromArgb(205, BackColor)); g.FillRectangle(overlay, plot);
            var message = ReferenceOnly ? "Capture a reference to compare" : frame == null ? "Waiting for audio measurements" : "Audio unavailable · previous measurements retained";
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(message, Font, text, plot, centered);
        }
        var footer = ReferenceOnly ? "Captured curve · same log-frequency / dBFS axes" : _showSpectrogram ? $"Color: {settings.FloorDb:0} to {settings.CeilingDb:0} dBFS · fixed sample hops · hatched = no measured data"
            : "Live / averaged: cyan   Peak: gray dashed" + (_showReference && _workspace.ReferencePower != null ? "   Reference: amber dashed" : "");
        using var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        if (!Compact) g.DrawString(footer, Font, secondary, new RectangleF(D(12), Height - D(21), Width - D(24), D(20)), ellipsis);
        if (Focused)
        { using var focus = new Pen(_presentation.Focus); g.DrawRectangle(focus, 1, 1, Width - 3, Height - 3); }
    }
    private void PaintUnmeasured(Graphics g, BandPlan plan, Brush text)
    {
        var plot = Plot;
        // An opaque tint distinguishes missing coverage from measured quiet power between hatch strokes.
        var neutral = _presentation.SecondaryText;
        var tint = Color.FromArgb((BackColor.R * 7 + neutral.R) / 8,
            (BackColor.G * 7 + neutral.G) / 8, (BackColor.B * 7 + neutral.B) / 8);
        var stroke = Color.FromArgb((BackColor.R + neutral.R) / 2,
            (BackColor.G + neutral.G) / 2, (BackColor.B + neutral.B) / 2);
        using var fill = new HatchBrush(HatchStyle.ForwardDiagonal, stroke, tint);
        using var caption = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        void Shade(float left, float right)
        {
            left = Math.Max(plot.Left, Math.Min(plot.Right, left)); right = Math.Max(plot.Left, Math.Min(plot.Right, right));
            if (right <= left) return;
            var bounds = new RectangleF(left, plot.Top, right - left, plot.Height); g.FillRectangle(fill, bounds);
            if (bounds.Width > D(100)) g.DrawString("No measured data", Font, text, bounds, caption);
        }
        if (_workspace.Settings.MinHz < plan.MinHz) Shade(plot.Left, X(plan.MinHz));
        if (_workspace.Settings.MaxHz > plan.MaxHz) Shade(X(plan.MaxHz), plot.Right);
    }
    private void PaintCurve(Graphics g, BandPlan plan, double[] power, Color color, bool dashed, bool fill)
    {
        if (power == null || power.Length != plan.Count) return;
        var points = Enumerable.Range(0, plan.Count).Select(i => new PointF(X(plan[i].CenterHz), Y(power[i]))).ToArray();
        using var pen = new Pen(color, 1.6f) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
        if (fill)
        {
            var polygon = new[] { new PointF(points[0].X, Plot.Bottom) }.Concat(points).Concat(new[] { new PointF(points[points.Length - 1].X, Plot.Bottom) }).ToArray();
            using var shade = new SolidBrush(Color.FromArgb(30, color)); g.FillPolygon(shade, polygon);
        }
        if (points.Length > 1) g.DrawLines(pen, points);
    }
    private void PaintPhase(Graphics g)
    {
        using var text = new SolidBrush(ForeColor);
        using var secondary = new SolidBrush(_presentation.SecondaryText);
        using var grid = new Pen(_presentation.MajorGrid);
        using var labelFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        using var leftFormat = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        using var rightFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        // Reserve measured text extents so side labels cannot overlap the phase image at larger fonts.
        var fontHeight = (int)Math.Ceiling(Font.GetHeight(g));
        var captionHeight = fontHeight + D(4);
        var sideMargin = (int)Math.Ceiling(g.MeasureString("− SIDE", Font).Width) + D(12);
        var titleHeight = Compact ? 0 : fontHeight + D(8);
        var footerHeight = Compact ? 0 : fontHeight * 2 + D(8);
        var top = Compact ? captionHeight + D(6) : D(8) + titleHeight + captionHeight + D(8);
        var footerTop = Compact ? Height - D(2) : Height - footerHeight - D(6);
        var availableHeight = Math.Max(1, footerTop - captionHeight - D(8) - top);
        var side = Math.Max(1, Math.Min(Width - 2 * sideMargin, availableHeight));
        var square = new Rectangle((Width - side) / 2, top + (availableHeight - side) / 2, side, side);
        if (!Compact) g.DrawString("STEREO PHASE · MID / SIDE DENSITY", Font, text,
            new RectangleF(D(12), D(8), Math.Max(1, Width - D(24)), titleHeight), rightFormat);
        var frame = _workspace.Latest; var density = _workspace.PhaseDensity;
        var available = frame != null && frame.Channels >= 2 && density.Length == 4096 && !_workspace.IsUnavailable;
        if (available && _workspace.PhaseExtent > 0)
        {
            using var image = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            var maximum = Math.Max(1, density.Max());
            for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++)
            {
                var amount = density[y * 64 + x];
                image.SetPixel(x, y, amount == 0 ? BackColor : Color.FromArgb(60 + (int)(195 * Math.Sqrt(amount / (double)maximum)), 85, 217, 222));
            }
            var old = g.InterpolationMode; g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(image, square); g.InterpolationMode = old;
        }
        g.DrawRectangle(grid, square); g.DrawEllipse(grid, square);
        g.DrawLine(grid, square.Left + side / 2f, square.Top, square.Left + side / 2f, square.Bottom);
        g.DrawLine(grid, square.Left, square.Top + side / 2f, square.Right, square.Top + side / 2f);
        g.DrawString("+ MID", Font, secondary, new RectangleF(square.Left, square.Top - captionHeight - D(3), square.Width, captionHeight), labelFormat);
        g.DrawString("− SIDE", Font, secondary,
            new RectangleF(0, square.Top + side / 2f - captionHeight / 2f, Math.Max(1, square.Left - D(6)), captionHeight), leftFormat);
        g.DrawString("+ SIDE", Font, secondary,
            new RectangleF(square.Right + D(6), square.Top + side / 2f - captionHeight / 2f, Math.Max(1, Width - square.Right - D(6)), captionHeight), rightFormat);
        g.DrawString("− MID", Font, secondary, new RectangleF(square.Left, square.Bottom + D(3), square.Width, captionHeight), labelFormat);
        if (!available || _workspace.PhaseExtent == 0)
        {
            var message = !available ? "Stereo phase unavailable · two current measured channels required" : "Digital silence · phase relationship undefined";
            g.DrawString(message, Font, text, square, labelFormat);
        }
        var footer = $"Auto range ±{_workspace.PhaseExtent:G4} FS · recent {_workspace.PhaseWindowSeconds * 1000:N0} ms window\nMatching channels: vertical · opposite phase: horizontal";
        if (!Compact) g.DrawString(footer, Font, secondary, new RectangleF(D(8), footerTop, Math.Max(1, Width - D(16)), footerHeight), labelFormat);
    }
    private void PaintHistory(Graphics g, AnalysisFrame frame)
    {
        var history = _workspace.History;
        if (history.Count == 0) return;
        var plot = Plot; var settings = _workspace.Settings;
        // Rasterize in pixel time/frequency coordinates; drawing cost does not grow with retained rows.
        var width = Math.Max(1, plot.Width); var height = Math.Max(1, plot.Height);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var pixels = new int[width * height];
        var bandForX = new int[width];
        for (var x = 0; x < width; x++)
        {
            var hz = settings.MinHz * Math.Pow(settings.MaxHz / settings.MinHz, (x + .5) / width);
            bandForX[x] = hz < frame.Plan.MinHz || hz > frame.Plan.MaxHz ? -1
                : Math.Max(0, Math.Min(frame.Plan.Count - 1, (int)Math.Floor(frame.Plan.FrequencyToPosition(hz))));
        }
        var newest = _workspace.CurrentSeconds; var row = 0;
        // A missing interval is not a measured zero. Keep the measured intensity
        // scale unchanged and use the same neutral family as unmeasured Hz coverage.
        var neutral = _presentation.SecondaryText;
        var missingTint = Color.FromArgb((BackColor.R * 7 + neutral.R) / 8,
            (BackColor.G * 7 + neutral.G) / 8, (BackColor.B * 7 + neutral.B) / 8).ToArgb();
        var missingStroke = Color.FromArgb((BackColor.R + neutral.R) / 2,
            (BackColor.G + neutral.G) / 2, (BackColor.B + neutral.B) / 2).ToArgb();
        var stripe = Math.Max(4, D(6));
        for (var y = 0; y < height; y++)
        {
            var time = newest - settings.HistorySeconds + (y + .5) * settings.HistorySeconds / height;
            while (row + 1 < history.Count && history[row + 1].Seconds <= time) row++;
            var current = history[row];
            var hop = BandSpectrumAnalyzer.DefaultWindowLengths(frame.SampleRate)[0] / (4d * frame.SampleRate);
            var valid = !current.Gap && current.Seconds <= time && time - current.Seconds <= hop * 1.6;
            for (var x = 0; x < width; x++)
            {
                var band = bandForX[x];
                if (!valid || band < 0 || band >= current.Power.Length)
                {
                    pixels[y * width + x] = (x + y) % stripe == 0 ? missingStroke : missingTint;
                    continue;
                }
                var n = Math.Max(0, Math.Min(1, (Db(current.Power[band]) - settings.FloorDb) / (settings.CeilingDb - settings.FloorDb)));
                var r = (int)(15 + 240 * Math.Pow(n, 2.5));
                var green = (int)(21 + 211 * Math.Sin(n * Math.PI * .8));
                var b = (int)(29 + 175 * Math.Sin(n * Math.PI));
                pixels[y * width + x] = Color.FromArgb(r, green, b).ToArgb();
            }
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (var y = 0; y < height; y++) Marshal.Copy(pixels, y * width, IntPtr.Add(data.Scan0, y * data.Stride), width); }
        finally { bitmap.UnlockBits(data); }
        g.DrawImageUnscaled(bitmap, plot.Location);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { _tip.Dispose(); Font.Dispose(); } base.Dispose(disposing); }
}
