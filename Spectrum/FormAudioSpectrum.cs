// Developer :: > Gehan Fernando
using System;
using System.Configuration;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Spectrum.Dsp;

namespace Spectrum;

public partial class FormAudioSpectrum : Form
{
    private const int BAR_COUNT = 83;
    private const int NOISE_GATE_THRESHOLD = 2;
    private const string AdvancedSpectralBalanceMode = "Advanced Spectral Balance";
    private static readonly string[] s_advancedModes =
    {
        "Waterfall", "Radial Spectrum", "Contour", "Peak Trace", "Threshold Monitor", "Band Matrix",
        "Octave Spectrum", "Level Change", "Octave Waterfall", "Stereo Levels", "Oscilloscope", "Spectral Balance", "Advanced Spectral Balance", "Stereo Spectrum", "Dynamics History", "Spectral History"
    };
    private static readonly string[] s_visualModes = BuildModeNames();
    private static readonly string[] s_colorThemes = BarColorThemes.Names;

    internal static string[] ColorThemeNames => (string[])s_colorThemes.Clone();
    internal static string[] VisualModeNames => (string[])s_visualModes.Clone();

    private static string[] BuildModeNames()
    {
        var names = new List<string> { "Spectrum", "Bricks", "LED" };
        names.AddRange(s_advancedModes);
        return SortModeNames(names);
    }

    internal static string[] SortModeNames(IEnumerable<string> names)
    {
        if (names == null)
            throw new ArgumentNullException(nameof(names));
        var result = new List<string>(names);
        foreach (var name in result)
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Mode names must not be empty.", nameof(names));
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, true);
        result.Sort((first, second) =>
        {
            var order = comparer.Compare(first, second);
            return order != 0 ? order : StringComparer.Ordinal.Compare(first, second);
        });
        return result.ToArray();
    }

    // timing is wall-clock based so the bars feel the same at any refresh rate, snappy up, lazy down
    internal const int SPECTRUM_ATTACK_MS = 45;
    internal const int SPECTRUM_RELEASE_MS = 380;
    internal const int SPECTRUM_PEAK_HOLD_MS = 300;

    // labels go through the same log mapping as the bars, otherwise they drift off
    private static readonly double[] s_axisLandmarksHz = SpectrumAxis.Frequencies;
    private static readonly double[] s_legacyAxisLandmarksHz = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    private const float AXIS_LABEL_HEIGHT = 14f; // designer pixels, scaled later
    private const int AXIS_LABEL_MIN_GAP = 4;

    // dB marks sit on both sides and share LevelScale so they line up with bar heights
    private static readonly double[] s_axisLandmarksDb = SpectrumAxis.Decibels;

    // ticks reuse the fill's level rectangle so nothing is off by a pixel
    private static readonly float[] s_gridlineLevels = BuildGridlineLevels();

    private static float[] BuildGridlineLevels()
    {
        var levels = new float[s_axisLandmarksDb.Length];
        for (var i = 0; i < s_axisLandmarksDb.Length; i++)
            levels[i] = (float)LevelScale.Normalize(s_axisLandmarksDb[i]);
        return levels;
    }

    // safe default until we learn the real device sample rate (fine for anything 41.8 kHz and up)
    private static readonly BandPlan s_defaultPlan = BandPlan.CreateLogarithmic(BAR_COUNT, 20, 20000, 48000);

    private VerticalProgressBar[] _progressBars;
    private AdvancedVisualizationControl _advancedVisualizer;
    private Label[] _axisLabels;
    private Label[] _dbAxisLabelsLeft;
    private Label[] _dbAxisLabelsRight;

    // center/mirror grow outward from the middle, so they need dB labels on both halves
    private Label[] _dbAxisLabelsLeftMirror;
    private Label[] _dbAxisLabelsRightMirror;

    private BandPlan _layoutPlan;
    private BandPlan _spectrumPlanBuffer;
    private BandPlan _appliedPlan;
    private string _visualMode;
    private string _visualStyle;
    private string _barTheme;
    private ComboBox _modeSelector;
    private ComboBox _styleSelector;
    private ComboBox _themeSelector;
    private Button _rotationSettingsButton;
    private ToolTip _selectionToolTip;
    private Label _captureStatusLabel;
    private System.Windows.Forms.Timer _captureStatusTimer;
    private Exception _preferencesLoadError;
    private FlowLayoutPanel _selectionPanel;
    private Label _modeSelectorLabel;
    private Label _styleSelectorLabel;
    private Label _themeSelectorLabel;
    private bool _initializingSelectors;
    private bool _positioningSelectors;
    private bool _randomRotationEnabled;
    private bool _applyingRandomSelection;
    private readonly VisualSelectionRandomizer _randomSelections = new();
    private bool _automaticSaveErrorShown;
    private int _rotationIntervalMinutes = 5;
    private readonly Random _random = new();
    private System.Windows.Forms.Timer _rotationTimer;
    private readonly byte[] _spectrumBuffer;
    private readonly byte[] _applyBuffer;
    private readonly AudioVisualizationData _audioBuffer = new();
    private readonly AudioVisualizationData _audioApplyBuffer = new();

    private volatile bool _updatePending;
    private readonly object _updateLock = new();

    private Analyzer _analyzer;
    private volatile bool _isDisposed;

    public FormAudioSpectrum()
    {
        InitializeComponent();
        ambiance_ThemeSpectrum.Sizable = true;

        _spectrumBuffer = new byte[BAR_COUNT];
        _applyBuffer = new byte[BAR_COUNT];

        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint,
            true);

        UpdateStyles();
        InitializeSelectionControls();
    }

    private void FormAudioSpectrum_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F12)
        {
            TopMost = !TopMost;
            return;
        }

        if (e.Control && e.KeyCode == Keys.M)
        {
            SelectNextItem(_modeSelector);
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.T)
        {
            SelectNextItem(_themeSelector);
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.R)
        {
            ShowRotationSettings();
            e.Handled = true;
        }
    }

    private static void SelectNextItem(ComboBox selector)
    {
        if (selector.Items.Count == 0) return;
        selector.SelectedIndex = (selector.SelectedIndex + 1) % selector.Items.Count;
    }

    private void FormAudioSpectrum_Load(object sender, EventArgs e)
    {
        LoadVisualPreferences(out var configuredMode, out var configuredStyle, out var configuredTheme);
        ResolveConfiguredVisualState(configuredMode, configuredStyle, out _visualMode, out _visualStyle);
        _barTheme = ResolveConfiguredTheme(configuredTheme);

        _initializingSelectors = true;
        try
        {
            _modeSelector.SelectedItem = _visualMode;
            _styleSelector.SelectedItem = GetAppliedVisualStyle(_visualMode, _visualStyle);
            _themeSelector.SelectedItem = _barTheme;
        }
        finally
        {
            _initializingSelectors = false;
        }
        UpdateStyleSelector();

        InitializeBarsOptimized(_visualMode);
        UpdateRotationSettingsButton();
        ConfigureRotationTimer();
        CenterToScreen();
        if (ambiance_ThemeSpectrum.SmartBounds)
            ambiance_ThemeSpectrum.CorrectBounds(Screen.FromControl(this).WorkingArea);

        _analyzer = new Analyzer
        {
            // saved mode got restored before the analyzer existed, so flip 7-band on now
            SevenBandEnabled = string.Equals(_visualMode, AdvancedSpectralBalanceMode, StringComparison.OrdinalIgnoreCase)
        };
        Analyzer.OnChange += Spectrum_Change;
        UpdateCaptureStatus();
        _captureStatusTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _captureStatusTimer.Tick += (sender, args) => UpdateCaptureStatus();
        _captureStatusTimer.Start();

        Shown += (s, e) =>
        {
            PositionSelectionPanel();
            RecalculateBarLayout();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Taskbar.SetState(Handle, Taskbar.TaskbarStates.NoProgress);
    }

    private void LoadVisualPreferences(out string mode, out string style, out string theme)
    {
        var snapshot = VisualPreferenceSnapshot.Load(
            key => ConfigurationManager.AppSettings[key], () => VisualizerPreferences.Default);
        mode = snapshot.Mode;
        style = snapshot.Style;
        theme = snapshot.Theme;
        _randomRotationEnabled = snapshot.RandomEnabled;
        _rotationIntervalMinutes = snapshot.IntervalMinutes;
        if (snapshot.Error != null)
            ReportPreferenceLoadError(snapshot.Error);
    }

    private void ReportPreferenceLoadError(Exception exception)
    {
        _preferencesLoadError = exception;
        _randomRotationEnabled = false;
        _rotationIntervalMinutes = 5;
        System.Diagnostics.Trace.TraceError("Preferences could not be loaded: {0}", exception);
        MessageBox.Show(this,
            $"Saved preferences could not be read. Configuration defaults will be used; the original settings file has not been deleted or overwritten. Changes cannot be saved until the settings file is repaired and Spectrum restarted.\n\n{exception.Message}",
            "Preferences unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void UpdateCaptureStatus()
    {
        if (_isDisposed || _analyzer == null)
            return;
        var status = _analyzer.CurrentStatus;
        var source = string.IsNullOrWhiteSpace(status.DeviceName) ? "No available output device" : status.DeviceName;
        var format = status.SampleRate > 0
            ? $" | {status.SampleRate:N0} Hz / {status.Channels} ch"
            : string.Empty;
        var error = status.ErrorCode.HasValue ? $" (error {status.ErrorCode.Value})" : string.Empty;
        var text = $"{status.State} | WASAPI loopback | {source}{format} | {status.Message}{error}";
        if (_captureStatusLabel.Text == text)
            return;
        _captureStatusLabel.Text = text;
        _captureStatusLabel.AccessibleDescription = text;
        _selectionToolTip.SetToolTip(_captureStatusLabel, text);
    }

    private void InitializeSelectionControls()
    {
        _selectionPanel = new FlowLayoutPanel
        {
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            Location = new Point(0, 7),
            Name = "selectionPanel",
            Size = new Size(560, 25),
            TabIndex = 1,
            WrapContents = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        _modeSelectorLabel = new Label
        {
            AutoSize = false,
            ForeColor = Color.FromArgb(215, 210, 196),
            Font = ambiance_ThemeSpectrum.Font,
            Text = "&Mode",
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(39, 24),
            Margin = new Padding(0, 0, 0, 0)
        };

        _modeSelector = CreateSelector("modeSelector", 150, s_visualModes, false);
        _modeSelector.AccessibleName = "Visualization mode";
        _modeSelector.AccessibleDescription = "Press Ctrl+M to cycle visualization modes";
        _modeSelector.TabIndex = 0;

        _styleSelectorLabel = new Label
        {
            AutoSize = false,
            ForeColor = Color.FromArgb(215, 210, 196),
            Font = ambiance_ThemeSpectrum.Font,
            Text = "&Style",
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(37, 24),
            Margin = new Padding(7, 0, 0, 0)
        };

        _styleSelector = CreateSelector("styleSelector", 110, VisualStyles.Names, false, true);
        _styleSelector.AccessibleName = "Optional visual style";
        _styleSelector.AccessibleDescription = "Optional visual emphasis, limited to styles supported by the selected mode.";
        _styleSelector.TabIndex = 1;

        _themeSelectorLabel = new Label
        {
            AutoSize = false,
            ForeColor = Color.FromArgb(215, 210, 196),
            Font = ambiance_ThemeSpectrum.Font,
            Text = "&Theme",
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(45, 24),
            Margin = new Padding(7, 0, 0, 0)
        };

        _themeSelector = CreateSelector("themeSelector", 160, s_colorThemes, true);
        _themeSelector.AccessibleName = "Bar color theme";
        _themeSelector.AccessibleDescription = "Press Ctrl+T to cycle bar color themes";
        _themeSelector.TabIndex = 2;

        _rotationSettingsButton = new Button
        {
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.FromArgb(235, 232, 225),
            BackColor = Color.FromArgb(38, 35, 29),
            Font = ambiance_ThemeSpectrum.Font,
            Name = "rotationSettingsButton",
            Size = new Size(76, 24),
            Text = "Fixed",
            TabIndex = 3,
            Margin = new Padding(8, 0, 0, 0),
            AccessibleName = "Mode, style, and theme rotation settings"
        };
        _rotationSettingsButton.FlatAppearance.BorderColor = Color.FromArgb(82, 75, 60);
        _rotationSettingsButton.Click += (sender, args) => ShowRotationSettings();
        _selectionToolTip = new ToolTip();
        _selectionToolTip.SetToolTip(_rotationSettingsButton, "Choose fixed or timed random mode, style and theme (Ctrl+R)");
        _selectionToolTip.SetToolTip(_modeSelector, "Visualization geometry from captured audio. Ctrl+M cycles alphabetically.");
        _selectionToolTip.SetToolTip(_styleSelector, "Presentation effects only; styles never change the measured signal.");
        _selectionToolTip.SetToolTip(_themeSelector, "Professional instrument palette and application appearance. Ctrl+T cycles themes.");

        _captureStatusLabel = new Label
        {
            AutoEllipsis = true,
            Text = "Starting audio capture...",
            AccessibleName = "Audio capture source and status",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ambiance_ThemeSpectrum.Font,
            TabStop = false,
            BackColor = Color.Transparent
        };
        ambiance_ThemeSpectrum.Controls.Add(_captureStatusLabel);

        _selectionPanel.Controls.Add(_modeSelectorLabel);
        _selectionPanel.Controls.Add(_modeSelector);
        _selectionPanel.Controls.Add(_styleSelectorLabel);
        _selectionPanel.Controls.Add(_styleSelector);
        _selectionPanel.Controls.Add(_themeSelectorLabel);
        _selectionPanel.Controls.Add(_themeSelector);
        _selectionPanel.Controls.Add(_rotationSettingsButton);
        _modeSelector.SelectedIndexChanged += ModeSelector_SelectedIndexChanged;
        _styleSelector.SelectedIndexChanged += StyleSelector_SelectedIndexChanged;
        _themeSelector.SelectedIndexChanged += ThemeSelector_SelectedIndexChanged;
        ambiance_ThemeSpectrum.Controls.Add(_selectionPanel);
        PositionSelectionPanel();
        ambiance_ThemeSpectrum.SizeChanged += (s, e) =>
        {
            PositionSelectionPanel();
            RecalculateBarLayout();
        };
        foreach (var selector in new[] { _modeSelector, _styleSelector, _themeSelector })
            selector.FontChanged += (s, e) => RefreshSelectionLayout();
        _captureStatusLabel.FontChanged += (s, e) => RefreshSelectionLayout();
        _rotationSettingsButton.FontChanged += (s, e) => RefreshSelectionLayout();
    }

    private ComboBox CreateSelector(string name, int width, string[] choices, bool isThemeSelector, bool isStyleSelector = false)
    {
        var selector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(38, 35, 29),
            ForeColor = Color.FromArgb(235, 232, 225),
            Font = ambiance_ThemeSpectrum.Font,
            Name = name,
            Size = new Size(width, 24),
            ItemHeight = 22,
            Margin = new Padding(0, 0, 0, 0),
            IntegralHeight = true,
            MaxDropDownItems = 10
        };
        foreach (var choice in choices)
            selector.Items.Add(choice);
        selector.DrawItem += (sender, args) => DrawSelectorItem(args, isThemeSelector, isStyleSelector);
        return selector;
    }

    private void DrawSelectorItem(DrawItemEventArgs e, bool isThemeSelector, bool isStyleSelector)
    {
        if (e.Index < 0)
            return;

        var selected = (e.State & DrawItemState.Selected) != 0;
        var ui = BarColorThemes.Resolve(_barTheme).Ui;
        var background = selected ? ui.Selection : ui.ControlSurface;
        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);

        var selector = isThemeSelector ? _themeSelector : isStyleSelector ? _styleSelector : _modeSelector;
        var scale = Math.Max(1f, selector.Font.SizeInPoints / 9f);
        var choice = (string)(isThemeSelector ? _themeSelector.Items[e.Index] :
            isStyleSelector ? _styleSelector.Items[e.Index] : _modeSelector.Items[e.Index]);
        var textLeft = e.Bounds.Left + (int)Math.Ceiling(8 * scale);
        if (isThemeSelector)
        {
            var theme = BarColorThemes.Resolve(choice);
            var swatchWidth = (int)Math.Ceiling(30 * scale);
            var swatchHeight = Math.Max(4, e.Bounds.Height - (int)Math.Ceiling(12 * scale));
            var swatch = new Rectangle(e.Bounds.Left + (int)Math.Ceiling(7 * scale),
                e.Bounds.Top + (e.Bounds.Height - swatchHeight) / 2, swatchWidth, swatchHeight);
            using var lowBrush = new SolidBrush(theme.Low);
            using var midBrush = new SolidBrush(theme.Mid);
            using var highBrush = new SolidBrush(theme.High);
            using var swatchPen = new Pen(ui.Frame);
            e.Graphics.FillRectangle(lowBrush, swatch.Left, swatch.Top, swatch.Width / 3, swatch.Height);
            e.Graphics.FillRectangle(midBrush, swatch.Left + swatch.Width / 3, swatch.Top, swatch.Width / 3, swatch.Height);
            e.Graphics.FillRectangle(highBrush, swatch.Left + swatch.Width * 2 / 3, swatch.Top,
                swatch.Width - swatch.Width * 2 / 3, swatch.Height);
            e.Graphics.DrawRectangle(swatchPen, swatch);
            textLeft = e.Bounds.Left + GetSelectorTextInset(selector.Font, true);
        }
        else if (isStyleSelector)
        {
            var iconHeight = (int)Math.Ceiling(14 * scale);
            var icon = new Rectangle(e.Bounds.Left + (int)Math.Ceiling(7 * scale),
                e.Bounds.Top + (e.Bounds.Height - iconHeight) / 2, (int)Math.Ceiling(20 * scale), iconHeight);
            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(icon.Left, icon.Top);
            e.Graphics.ScaleTransform(scale, scale);
            DrawStyleIcon(e.Graphics, choice, new Rectangle(0, 0, 20, 14), _barTheme);
            e.Graphics.Restore(state);
            textLeft = e.Bounds.Left + GetSelectorTextInset(selector.Font, false);
        }
        else
        {
            var theme = BarColorThemes.Resolve(_barTheme);
            var iconHeight = (int)Math.Ceiling(14 * scale);
            DrawModeIcon(e.Graphics, choice, new Rectangle(e.Bounds.Left + (int)Math.Ceiling(7 * scale),
                e.Bounds.Top + (e.Bounds.Height - iconHeight) / 2, (int)Math.Ceiling(20 * scale), iconHeight), theme.High);
            textLeft = e.Bounds.Left + GetSelectorTextInset(selector.Font, false);
        }

        TextRenderer.DrawText(e.Graphics, choice, selector.Font,
            new Rectangle(textLeft, e.Bounds.Top, Math.Max(0, e.Bounds.Right - textLeft - 4), e.Bounds.Height),
            ui.PrimaryText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (selected && e.Bounds.Width > 0 && e.Bounds.Height > 0)
        {
            using var selectionPen = new Pen(ui.Focus, 2f);
            e.Graphics.DrawRectangle(selectionPen, e.Bounds.Left, e.Bounds.Top,
                e.Bounds.Width - 1, e.Bounds.Height - 1);
        }
    }

    private static void DrawStyleIcon(Graphics graphics, string style, Rectangle bounds, string themeName)
    {
        var color = BarColorThemes.Resolve(themeName).High;
        using var pen = new Pen(color, 1.5f);
        using var brush = new SolidBrush(color);
        if (string.Equals(style, "Pulse", StringComparison.OrdinalIgnoreCase))
        {
            graphics.DrawLines(pen, new[]
            {
                new Point(bounds.Left, bounds.Bottom - 3), new Point(bounds.Left + 5, bounds.Bottom - 3),
                new Point(bounds.Left + 9, bounds.Top + 1), new Point(bounds.Left + 13, bounds.Bottom - 3),
                new Point(bounds.Right - 1, bounds.Bottom - 3)
            });
        }
        else if (string.Equals(style, "Glow", StringComparison.OrdinalIgnoreCase))
        {
            using var glowPen = new Pen(Color.FromArgb(90, color), 4f);
            graphics.DrawEllipse(glowPen, bounds.Left + 4, bounds.Top + 1, 11, 11);
            graphics.FillEllipse(brush, bounds.Left + 7, bounds.Top + 4, 5, 5);
        }
        else
        {
            switch (style)
            {
                case "Trail":
                    for (var i = 0; i < 3; i++)
                    {
                        using var trailPen = new Pen(Color.FromArgb(80 + i * 70, color), 1.4f);
                        graphics.DrawLine(trailPen, bounds.Left + 2, bounds.Bottom - 2 - i * 4,
                            bounds.Right - 2, bounds.Bottom - 4 - i * 4);
                    }
                    break;
                case "Scanline":
                    graphics.DrawRectangle(pen, bounds.Left + 2, bounds.Top + 1, bounds.Width - 5, bounds.Height - 3);
                    graphics.DrawLine(pen, bounds.Left, bounds.Top + bounds.Height / 2,
                        bounds.Right - 1, bounds.Top + bounds.Height / 2);
                    break;
                case "Precision":
                    for (var i = 1; i <= 3; i++)
                    {
                        graphics.DrawLine(pen, bounds.Left + i * bounds.Width / 4, bounds.Top + 1,
                            bounds.Left + i * bounds.Width / 4, bounds.Bottom - 1);
                        graphics.DrawLine(pen, bounds.Left + 1, bounds.Top + i * bounds.Height / 4,
                            bounds.Right - 1, bounds.Top + i * bounds.Height / 4);
                    }
                    break;
                default:
                    graphics.DrawEllipse(pen, bounds.Left + 5, bounds.Top + 2, 10, 10);
                    graphics.DrawLine(pen, bounds.Left + 6, bounds.Bottom - 3, bounds.Left + 14, bounds.Top + 3);
                    break;
            }
        }
    }

    private static void DrawModeIcon(Graphics graphics, string mode, Rectangle bounds, Color color)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var state = graphics.Save();
        try
        {
            graphics.SetClip(bounds, CombineMode.Intersect);
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / 24f, bounds.Height / 18f);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawModeGlyph(graphics, mode, color);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawModeGlyph(Graphics graphics, string mode, Color color)
    {
        using var pen = new Pen(color, 1.6f);
        using var brush = new SolidBrush(color);
        switch (mode)
        {
            case "Spectrum":
                DrawIconBars(graphics, brush, new[] { 5, 10, 14, 8, 11 });
                break;
            case "Bricks":
                for (var column = 0; column < 5; column++)
                    for (var row = 0; row < 1 + column % 3; row++)
                        graphics.FillRectangle(brush, 2 + column * 4, 13 - row * 4, 3, 3);
                break;
            case "LED":
                for (var column = 0; column < 4; column++)
                    for (var row = 0; row < 2 + column % 2; row++)
                        graphics.FillEllipse(brush, 3 + column * 5, 13 - row * 5, 3, 3);
                break;
            case "Oscilloscope":
                graphics.DrawLines(pen, new[]
                {
                    new Point(2, 9), new Point(5, 9), new Point(8, 3),
                    new Point(12, 15), new Point(16, 5), new Point(19, 9), new Point(22, 9)
                });
                break;
            case "Stereo Levels":
                graphics.FillRectangle(brush, 4, 3, 5, 12);
                graphics.FillRectangle(brush, 15, 7, 5, 8);
                graphics.DrawLine(pen, 3, 1, 10, 1);
                graphics.DrawLine(pen, 14, 4, 21, 4);
                break;
            case "Stereo Spectrum":
                graphics.DrawLine(pen, 2, 9, 22, 9);
                graphics.DrawLines(pen, new[] { new Point(2, 7), new Point(7, 2), new Point(12, 6), new Point(18, 4), new Point(22, 7) });
                graphics.DrawLines(pen, new[] { new Point(2, 11), new Point(7, 13), new Point(12, 16), new Point(18, 12), new Point(22, 14) });
                break;
            case "Dynamics History":
                graphics.DrawLines(pen, new[] { new Point(2, 6), new Point(6, 3), new Point(11, 5), new Point(16, 2), new Point(22, 4) });
                using (var rmsPen = new Pen(Color.FromArgb(160, color), 2.5f))
                    graphics.DrawLines(rmsPen, new[] { new Point(2, 13), new Point(6, 11), new Point(11, 13), new Point(16, 9), new Point(22, 11) });
                graphics.DrawLine(pen, 2, 16, 22, 16);
                break;
            case "Spectral Balance":
                for (var row = 0; row < 3; row++)
                    graphics.FillRectangle(brush, 2, 2 + row * 5, 20 - row * 6, 3);
                break;
            case "Advanced Spectral Balance":
                for (var bar = 0; bar < 7; bar++)
                {
                    var barHeight = new[] { 14, 11, 8, 12, 9, 6, 4 }[bar];
                    graphics.FillRectangle(brush, 2 + bar * 3, 20 - barHeight, 2, barHeight);
                }
                break;
            case "Waterfall":
                for (var row = 0; row < 4; row++)
                    for (var column = 0; column < 6; column++)
                        using (var cell = new SolidBrush(Color.FromArgb(60 + ((column + row * 2) % 4) * 60, color)))
                            graphics.FillRectangle(cell, 2 + column * 3, 2 + row * 4, 2, 3);
                break;
            case "Octave Waterfall":
                for (var row = 0; row < 3; row++)
                    for (var column = 0; column < 3; column++)
                        using (var cell = new SolidBrush(Color.FromArgb(75 + ((column + row) % 3) * 85, color)))
                            graphics.FillRectangle(cell, 2 + column * 7, 2 + row * 5, 5, 3);
                graphics.DrawLine(pen, 22, 2, 22, 15);
                break;
            case "Band Matrix":
                for (var row = 0; row < 3; row++)
                    for (var column = 0; column < 4; column++)
                        graphics.DrawRectangle(pen, 2 + column * 5, 2 + row * 5, 3, 3);
                break;
            case "Radial Spectrum":
                var center = new Point(12, 9);
                for (var i = 0; i < 8; i++)
                {
                    var angle = (Math.PI * 2 * i / 8) - (Math.PI / 2);
                    graphics.DrawLine(pen,
                        center.X + (int)(Math.Cos(angle) * 3),
                        center.Y + (int)(Math.Sin(angle) * 3),
                        center.X + (int)(Math.Cos(angle) * 7),
                        center.Y + (int)(Math.Sin(angle) * 7));
                }
                break;
            case "Contour":
                graphics.DrawLines(pen, new[]
                {
                    new Point(2, 15), new Point(6, 9), new Point(10, 3),
                    new Point(15, 11), new Point(22, 6)
                });
                break;
            case "Peak Trace":
                graphics.DrawLines(pen, new[] { new Point(2, 15), new Point(7, 8), new Point(12, 11), new Point(17, 7), new Point(22, 12) });
                using (var heldPen = new Pen(color, 1.3f) { DashStyle = DashStyle.Dot })
                    graphics.DrawLines(heldPen, new[] { new Point(2, 8), new Point(7, 3), new Point(12, 6), new Point(17, 2), new Point(22, 5) });
                break;
            case "Threshold Monitor":
                DrawIconBars(graphics, brush, new[] { 4, 8, 12, 6, 10 });
                graphics.DrawLine(pen, 1, 7, 23, 7);
                break;
            case "Octave Spectrum":
                graphics.FillRectangle(brush, 2, 10, 5, 6);
                graphics.FillRectangle(brush, 9, 3, 5, 13);
                graphics.FillRectangle(brush, 16, 7, 5, 9);
                break;
            case "Level Change":
                graphics.DrawLine(pen, 1, 9, 23, 9);
                for (var i = 0; i < 4; i++)
                    graphics.DrawLine(pen, 4 + i * 5, 9, 4 + i * 5, i % 2 == 0 ? 3 : 15);
                break;
            case "Spectral History":
                for (var i = 0; i < 3; i++)
                    using (var historyPen = new Pen(Color.FromArgb(90 + i * 75, color), 1.5f))
                        graphics.DrawLines(historyPen, new[] { new Point(2, 7 + i * 4), new Point(8, 2 + i * 4), new Point(14, 5 + i * 4), new Point(21, 3 + i * 4) });
                break;
            default:
                throw new ArgumentException("No icon registered for this visualization mode.", nameof(mode));
        }
    }

    private static void DrawIconBars(Graphics graphics, SolidBrush brush, int[] heights)
    {
        for (var i = 0; i < heights.Length; i++)
            graphics.FillRectangle(brush, 2 + i * 4, 16 - heights[i], 3, heights[i]);
    }

    private void PositionSelectionPanel()
    {
        if (_selectionPanel == null || _positioningSelectors) return;
        _positioningSelectors = true;
        _selectionPanel.SuspendLayout();
        try
        {
            var width = ambiance_ThemeSpectrum.ClientSize.Width;
            ambiance_ControlBox.Location = new Point(Math.Max(0, width - ambiance_ControlBox.Width - 8), 8);
            // old-style chrome stays a fixed size even when WinForms scales the children
            var windowButtons = new[] { "closeButton", "minimizeButton", "maximizeButton" };
            for (var i = 0; i < windowButtons.Length; i++)
                if (ambiance_ControlBox.Controls[windowButtons[i]] is Button windowButton)
                    windowButton.Bounds = new Rectangle(i * 22, 0, 20, 22);
            var scale = Math.Max(1f, _modeSelector.Font.SizeInPoints / 9f);
            var selectors = new[] { _modeSelector, _styleSelector, _themeSelector };
            var minimumWidths = new[] { 210, 140, 180 };
            var controlsWidth = 0;
            var rowHeight = 0;
            for (var i = 0; i < selectors.Length; i++)
            {
                var selector = selectors[i];
                selector.Visible = true;
                var fontHeight = TextRenderer.MeasureText("Ag", selector.Font).Height;
                selector.ItemHeight = Math.Max(22, fontHeight + (int)Math.Ceiling(6 * scale));
                var info = new SelectorComboBoxInfo { Size = Marshal.SizeOf(typeof(SelectorComboBoxInfo)) };
                var arrowAndBorder = GetComboBoxInfo(selector.Handle, ref info)
                    ? selector.Width - (info.Item.Right - info.Item.Left)
                    : SystemInformation.VerticalScrollBarWidth + 6;
                var textWidth = 0;
                foreach (string item in selector.Items)
                    textWidth = Math.Max(textWidth, TextRenderer.MeasureText(item, selector.Font).Width);
                selector.Width = Math.Max((int)Math.Ceiling(minimumWidths[i] * scale),
                    textWidth + GetSelectorTextInset(selector.Font, i == 2) + arrowAndBorder + 6);
                selector.DropDownWidth = Math.Max(selector.Width, (int)Math.Ceiling(240 * scale));
                controlsWidth += selector.Width + selector.Margin.Horizontal;
                rowHeight = Math.Max(rowHeight, selector.Height);
            }
            _rotationSettingsButton.Visible = true;
            _rotationSettingsButton.Width = Math.Max((int)Math.Ceiling(88 * scale),
                TextRenderer.MeasureText("Random 240m", _rotationSettingsButton.Font).Width + (int)Math.Ceiling(16 * scale));
            _rotationSettingsButton.Height = rowHeight;
            controlsWidth += _rotationSettingsButton.Width + _rotationSettingsButton.Margin.Horizontal;
            var titleWidth = TextRenderer.MeasureText("Audio Spectrum Analyzer", ambiance_ThemeSpectrum.Font).Width;
            var inline = rowHeight <= 29 && controlsWidth + ambiance_ControlBox.Width + titleWidth + 40 <= width;
            var labels = new[] { _modeSelectorLabel, _styleSelectorLabel, _themeSelectorLabel };
            var labelsWidth = 0;
            foreach (var label in labels)
            {
                label.Width = TextRenderer.MeasureText(label.Text.Replace("&", string.Empty), label.Font).Width + 4;
                label.Height = rowHeight;
                labelsWidth += label.Width + label.Margin.Horizontal;
            }
            var showLabels = inline && controlsWidth + labelsWidth + ambiance_ControlBox.Width + titleWidth + 40 <= width;
            foreach (var label in labels) label.Visible = showLabels;
            _selectionPanel.WrapContents = !inline;
            _selectionPanel.Width = inline
                ? controlsWidth + (showLabels ? labelsWidth : 0)
                : Math.Max(1, width - 24);
            _selectionPanel.Location = inline
                ? new Point(Math.Max(8, width - ambiance_ControlBox.Width - 24 - _selectionPanel.Width), 7)
                : new Point(12, 40);
            _selectionPanel.ResumeLayout(true);
            _selectionPanel.Height = Math.Max(rowHeight,
                _selectionPanel.GetPreferredSize(new Size(_selectionPanel.Width, int.MaxValue)).Height);
            ambiance_ThemeSpectrum.TitleRightInset = inline ? Math.Max(16, _selectionPanel.Left - 12)
                : Math.Max(16, width - ambiance_ControlBox.Width - 24);
            ambiance_ThemeSpectrum.Text = "Audio Spectrum Analyzer";
            if (_captureStatusLabel != null)
            {
                var statusHeight = Math.Max(22, TextRenderer.MeasureText("Ag", _captureStatusLabel.Font).Height + 6);
                _captureStatusLabel.Bounds = new Rectangle(12,
                    Math.Max(0, ambiance_ThemeSpectrum.Height - statusHeight - 4), Math.Max(1, width - 24), statusHeight);
            }
        }
        finally
        {
            _selectionPanel.ResumeLayout(true);
            _positioningSelectors = false;
        }
    }

    private void RefreshSelectionLayout()
    {
        PositionSelectionPanel();
        RecalculateBarLayout();
    }

    private static int GetSelectorTextInset(Font font, bool theme) =>
        (int)Math.Ceiling((theme ? 45 : 34) * Math.Max(1f, font.SizeInPoints / 9f));

    [StructLayout(LayoutKind.Sequential)]
    private struct SelectorNativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SelectorComboBoxInfo
    {
        public int Size;
        public SelectorNativeRect Item, Button;
        public int ButtonState;
        public IntPtr ComboBox, Edit, List;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr handle, ref SelectorComboBoxInfo info);

    private static string ResolveConfiguredChoice(string configuredValue, string[] choices, string fallback)
    {
        var normalized = (configuredValue ?? string.Empty).Trim();
        foreach (var choice in choices)
        {
            if (string.Equals(choice, normalized, StringComparison.OrdinalIgnoreCase))
                return choice;
        }
        return fallback;
    }

    private static string ResolveConfiguredMode(string configuredValue)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
            return "Spectrum";

        var normalized = configuredValue.Trim();
        switch (normalized.ToLowerInvariant())
        {
            case "spectral flux":
                return "Level Change";
            case "transient map":
            case "level change map":
                return "Level Change";
            case "orbit history":
            case "frequency ribbon":
                return "Peak Trace";
            case "wave":
                return "Contour";
            case "dots":
                return "LED";
            case "lollipop":
                return "Spectrum";
            case "center":
                return "Spectrum";
            case "mirror":
                return "Bricks";
            case "note map":
            case "notemap":
                return "Contour";
            case "ambient particles":
                return "Spectrum";
            case "pulse":
                return "Bricks";
            case "glow":
                return "Spectrum";
            case "ppmi i":
            case "ppmi i b":
            case "ppmi i a":
            case "ppmi i bbc":
            case "ppmii":
            case "ppm2":
            case "iec2":
            case "bbc":
            case "ebu":
                return "LED";
            default:
                return ResolveConfiguredChoice(normalized, s_visualModes, "Bricks");
        }
    }

    private static string ResolveConfiguredTheme(string configuredValue)
    {
        var normalized = (configuredValue ?? string.Empty).Trim();
        if (string.Equals(normalized, "mono", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "monochromecyan", StringComparison.OrdinalIgnoreCase))
            return "MonoCyan";

        return ResolveConfiguredChoice(normalized, s_colorThemes, "ClassicSmooth");
    }

    internal static void ResolveConfiguredVisualState(
        string configuredMode, string configuredStyle, out string mode, out string style)
    {
        var legacyMode = (configuredMode ?? string.Empty).Trim();
        mode = ResolveConfiguredMode(legacyMode);
        style = VisualStyles.Resolve(configuredStyle);

        if (string.Equals(legacyMode, "Pulse", StringComparison.OrdinalIgnoreCase))
            style = "Pulse";
        else if (string.Equals(legacyMode, "Glow", StringComparison.OrdinalIgnoreCase))
            style = "Glow";

        style = GetAppliedVisualStyle(mode, style);
    }

    internal static bool IsVisualStyleSupported(string mode) => VisualStyles.IsSupported(mode);

    internal static string GetAppliedVisualStyle(string mode, string style) =>
        VisualStyles.IsSupported(mode, style) ? VisualStyles.Resolve(style) : "None";

    private static bool IsAdvancedMode(string mode)
    {
        foreach (var advancedMode in s_advancedModes)
        {
            if (string.Equals(mode, advancedMode, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static AdvancedVisualizationMode ResolveAdvancedMode(string mode)
    {
        switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "waterfall":
                return AdvancedVisualizationMode.Waterfall;
            case "radial spectrum":
                return AdvancedVisualizationMode.RadialSpectrum;
            case "peak trace":
                return AdvancedVisualizationMode.PeakTrace;
            case "threshold monitor":
                return AdvancedVisualizationMode.ThresholdMonitor;
            case "band matrix":
                return AdvancedVisualizationMode.BandMatrix;
            case "octave spectrum":
                return AdvancedVisualizationMode.OctaveSpectrum;
            case "level change":
                return AdvancedVisualizationMode.SpectralFlux;
            case "octave waterfall":
                return AdvancedVisualizationMode.OctaveWaterfall;
            case "stereo levels":
                return AdvancedVisualizationMode.StereoLevels;
            case "oscilloscope":
                return AdvancedVisualizationMode.Oscilloscope;
            case "spectral balance":
                return AdvancedVisualizationMode.SpectralBalance;
            case "advanced spectral balance":
                return AdvancedVisualizationMode.AdvancedSpectralBalance;
            case "stereo spectrum":
                return AdvancedVisualizationMode.StereoSpectrum;
            case "dynamics history":
                return AdvancedVisualizationMode.DynamicsHistory;
            case "spectral history":
                return AdvancedVisualizationMode.SpectralHistory;
            default:
                return AdvancedVisualizationMode.Contour;
        }
    }

    private void ModeSelector_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_initializingSelectors || _progressBars == null || !(_modeSelector.SelectedItem is string mode))
            return;

        var previousMode = _visualMode;
        _visualMode = mode;
        UpdateModeDescription();
        _visualStyle = GetAppliedVisualStyle(_visualMode, _visualStyle);
        var theme = BarColorThemes.Resolve(_barTheme);
        for (var i = 0; i < _progressBars.Length; i++)
        {
            var progress = _progressBars[i];
            progress.Tag = $"{mode}|{i + 1}";
            ApplyMeterPresetOptimized(progress, mode);
            ApplyColorThemeOptimized(progress, theme);
            progress.Invalidate();
        }

        _advancedVisualizer.Mode = ResolveAdvancedMode(mode);
        _advancedVisualizer.ApplyTheme(theme);
        ApplyThemeChrome(theme);
        ApplyVisualStyle();
        UpdateStyleSelector();
        if (!_applyingRandomSelection)
            SaveVisualPreferences();
        ResetRotationTimer();
        SynchronizeActiveVisualizer(previousMode);
        RecalculateBarLayout();
    }

    private void StyleSelector_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_initializingSelectors || _progressBars == null || !(_styleSelector.SelectedItem is string style))
            return;

        _visualStyle = GetAppliedVisualStyle(_visualMode, style);
        ApplyVisualStyle();
        if (!_applyingRandomSelection)
            SaveVisualPreferences();
    }

    private void ApplyVisualStyle()
    {
        var style = VisualStyles.Parse(GetAppliedVisualStyle(_visualMode, _visualStyle));
        foreach (var progress in _progressBars)
        {
            progress.VisualStyle = style;
            progress.Invalidate();
        }

        _advancedVisualizer.Style = style;
    }

    private void UpdateStyleSelector()
    {
        var wasInitializing = _initializingSelectors;
        _initializingSelectors = true;
        try
        {
            var choices = VisualStyles.GetSupportedNames(_visualMode);
            _styleSelector.Items.Clear();
            foreach (var choice in choices)
                _styleSelector.Items.Add(choice);

            var supported = choices.Length > 1;
            _styleSelector.Enabled = supported;
            _styleSelector.AccessibleDescription = supported
                ? "Optional visual emphasis available for this mode."
                : $"Styles are unavailable for {_visualMode}; None is applied.";
            _visualStyle = GetAppliedVisualStyle(_visualMode, _visualStyle);
            _styleSelector.SelectedItem = _visualStyle;
        }
        finally
        {
            _initializingSelectors = wasInitializing;
        }
    }

    private void ThemeSelector_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_initializingSelectors || _progressBars == null || !(_themeSelector.SelectedItem is string themeName))
            return;

        _barTheme = themeName;
        var theme = BarColorThemes.Resolve(themeName);
        _advancedVisualizer.ApplyTheme(theme);
        foreach (var progress in _progressBars)
        {
            ApplyColorThemeOptimized(progress, theme);
            progress.ApplyThemeChrome(theme.Ui);
            progress.Invalidate();
        }
        ApplyThemeChrome(theme);
        if (!_applyingRandomSelection)
            SaveVisualPreferences();
        ResetRotationTimer();
    }

    private void ShowRotationSettings()
    {
        using var dialog = new RotationSettingsForm(_randomRotationEnabled, _rotationIntervalMinutes, BarColorThemes.Resolve(_barTheme));
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var oldRandomEnabled = _randomRotationEnabled;
        var oldInterval = _rotationIntervalMinutes;
        _randomRotationEnabled = dialog.RandomEnabled;
        _rotationIntervalMinutes = dialog.IntervalMinutes;
        if (!SaveVisualPreferences())
        {
            _randomRotationEnabled = oldRandomEnabled;
            _rotationIntervalMinutes = oldInterval;
            return;
        }

        UpdateRotationSettingsButton();
        ConfigureRotationTimer();
    }

    private void UpdateRotationSettingsButton()
    {
        if (_rotationSettingsButton == null)
            return;

        var compact = ambiance_ThemeSpectrum.ClientSize.Width < 800;
        _rotationSettingsButton.Text = !_randomRotationEnabled
            ? "Fixed"
            : compact ? "Random" : $"Random {_rotationIntervalMinutes}m";
        _rotationSettingsButton.AccessibleDescription = _randomRotationEnabled
            ? $"Random mode, style, and theme selection, changing every {_rotationIntervalMinutes} minutes. Press Ctrl+R to change."
            : "Fixed mode, style, and theme selection. Press Ctrl+R to change.";
    }

    private void ConfigureRotationTimer()
    {
        if (_rotationTimer == null)
        {
            _rotationTimer = new System.Windows.Forms.Timer();
            _rotationTimer.Tick += RotationTimer_Tick;
        }

        if (_randomRotationEnabled)
        {
            _rotationTimer.Stop();
            _rotationTimer.Interval = GetRotationIntervalMilliseconds(_rotationIntervalMinutes);
            _rotationTimer.Start();
        }
        else
        {
            _rotationTimer.Stop();
        }
        UpdateRotationSettingsButton();
    }

    private void ResetRotationTimer()
    {
        if (!_randomRotationEnabled || _rotationTimer == null)
            return;
        _rotationTimer.Stop();
        _rotationTimer.Start();
    }

    private void RotationTimer_Tick(object sender, EventArgs e)
    {
        RandomizeVisualSelection(_random);
        SaveVisualPreferences(automatic: true);
    }

    internal void RandomizeVisualSelection(Random random)
    {
        var previousMode = _visualMode;
        var selection = _randomSelections.Next(
            new VisualSelection(_visualMode, _visualStyle, _barTheme), s_visualModes, s_colorThemes, random);
        var wasInitializing = _initializingSelectors;
        var wasApplying = _applyingRandomSelection;
        _initializingSelectors = true;
        _applyingRandomSelection = true;
        ambiance_ThemeSpectrum.SuspendLayout();
        try
        {
            // set everything first, then notify, so nobody sees a half-applied state
            _visualMode = selection.Mode;
            _visualStyle = selection.Style;
            _barTheme = selection.Theme;
            var theme = BarColorThemes.Resolve(_barTheme);
            for (var i = 0; i < _progressBars.Length; i++)
            {
                var progress = _progressBars[i];
                progress.Tag = $"{_visualMode}|{i + 1}";
                ApplyMeterPresetOptimized(progress, _visualMode);
                ApplyColorThemeOptimized(progress, theme);
                progress.ApplyThemeChrome(theme.Ui);
            }
            _advancedVisualizer.Mode = ResolveAdvancedMode(_visualMode);
            _advancedVisualizer.ApplyTheme(theme);
            ApplyThemeChrome(theme);
            ApplyVisualStyle();
            SynchronizeActiveVisualizer(previousMode);
            RecalculateBarLayout();
            _modeSelector.SelectedItem = _visualMode;
            UpdateModeDescription();
            UpdateStyleSelector();
            _themeSelector.SelectedItem = _barTheme;
        }
        finally
        {
            _initializingSelectors = wasInitializing;
            _applyingRandomSelection = wasApplying;
            ambiance_ThemeSpectrum.ResumeLayout(false);
        }
        ResetRotationTimer();
    }

    private void SynchronizeActiveVisualizer(string previousMode)
    {
        if (_analyzer != null)
            _analyzer.SevenBandEnabled = string.Equals(_visualMode, AdvancedSpectralBalanceMode, StringComparison.OrdinalIgnoreCase);
        // hidden renderers get no frames, so feed them one before showing or they flash empty
        if (IsAdvancedMode(_visualMode))
        {
            _advancedVisualizer.SetAudioData(_audioApplyBuffer);
            if (!IsAdvancedMode(previousMode))
                _advancedVisualizer.SynchronizeSpectrum(_applyBuffer);
        }
        else
        {
            for (var i = 0; i < _progressBars.Length; i++)
                _progressBars[i].SetTargetValueUI(_applyBuffer[i]);
        }
    }

    internal static int GetRotationIntervalMilliseconds(int minutes)
    {
        if (minutes < 1 || minutes > 240)
            throw new ArgumentOutOfRangeException(nameof(minutes), "The rotation interval must be 1 to 240 minutes.");
        return checked(minutes * 60 * 1000);
    }

    internal static int GetDifferentRandomIndex(int current, int count, Random random)
    {
        if (random == null)
            throw new ArgumentNullException(nameof(random));
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 1)
            return 0;
        if (current < 0 || current >= count)
            return random.Next(count);

        var next = random.Next(count - 1);
        if (next >= current)
            next++;
        return next;
    }

    internal static string GetDifferentRandomStyle(string mode, string currentStyle, Random random)
    {
        if (random == null)
            throw new ArgumentNullException(nameof(random));

        var styles = VisualStyles.GetSupportedNames(mode);
        var currentIndex = Array.IndexOf(styles, GetAppliedVisualStyle(mode, currentStyle));
        return styles[GetDifferentRandomIndex(currentIndex, styles.Length, random)];
    }

    private bool SaveVisualPreferences(bool automatic = false)
    {
        if (_preferencesLoadError != null)
        {
            ReportPreferenceSaveError(_preferencesLoadError, automatic);
            return false;
        }
        try
        {
            var preferences = VisualizerPreferences.Default;
            preferences.Mode = _visualMode;
            preferences.Style = GetAppliedVisualStyle(_visualMode, _visualStyle);
            preferences.Theme = _barTheme;
            preferences.RotationMode = _randomRotationEnabled ? "Random" : "Fixed";
            preferences.RotationIntervalMinutes = _rotationIntervalMinutes;
            preferences.Save();
            _automaticSaveErrorShown = false;
            return true;
        }
        catch (ConfigurationErrorsException ex)
        {
            ReportPreferenceSaveError(ex, automatic);
        }
        catch (IOException ex)
        {
            ReportPreferenceSaveError(ex, automatic);
        }
        catch (UnauthorizedAccessException ex)
        {
            ReportPreferenceSaveError(ex, automatic);
        }
        return false;
    }

    private void ReportPreferenceSaveError(Exception exception, bool automatic)
    {
        if (automatic && _automaticSaveErrorShown)
            return;

        _automaticSaveErrorShown = automatic;
        MessageBox.Show(this, $"Current choices will apply until the application closes, but could not be saved.\n\n{exception.Message}",
            "Preferences not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void InitializeBarsOptimized(string visualMode)
    {
        _progressBars = new VerticalProgressBar[BAR_COUNT];
        var theme = BarColorThemes.Resolve(_barTheme);

        ambiance_ThemeSpectrum.SuspendLayout();
        try
        {
            var backgroundColor = theme.Ui.VisualizationSurface;

            for (var i = 0; i < BAR_COUNT; i++)
            {
                var progress = new VerticalProgressBar
                {
                    BackColor = backgroundColor,
                    Maximum = 255,
                    Name = $"ProgressBar_{i + 1:D2}",
                    Tag = $"{visualMode}|{i + 1}",
                    Size = new Size(14, 320),
                    Location = new Point(11 + i * 13, 50),
                    Visible = true,
                    GridlineLevels = s_gridlineLevels
                };

                ApplyMeterPresetOptimized(progress, visualMode);
                ApplyColorThemeOptimized(progress, theme);
                progress.ApplyThemeChrome(theme.Ui);
                progress.VisualStyle = VisualStyles.Parse(GetAppliedVisualStyle(_visualMode, _visualStyle));

                _progressBars[i] = progress;
                ambiance_ThemeSpectrum.Controls.Add(progress);
            }

            _advancedVisualizer = new AdvancedVisualizationControl
            {
                Mode = ResolveAdvancedMode(visualMode),
                ThemeName = _barTheme,
                Style = VisualStyles.Parse(GetAppliedVisualStyle(_visualMode, _visualStyle)),
                Visible = IsAdvancedMode(visualMode)
            };
            ambiance_ThemeSpectrum.Controls.Add(_advancedVisualizer);

            _axisLabels = new Label[s_axisLandmarksHz.Length];
            for (var i = 0; i < _axisLabels.Length; i++)
            {
                var hz = s_axisLandmarksHz[i];
                var label = new Label
                {
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(150, 150, 150),
                    Font = new Font("Segoe UI", 7f),
                    Text = hz >= 1000 ? $"{hz / 1000:0.#}k" : $"{hz:0}",
                    Name = $"AxisLabel_{hz:0}",
                };

                _axisLabels[i] = label;
                ambiance_ThemeSpectrum.Controls.Add(label);
            }

            _dbAxisLabelsLeft = new Label[s_axisLandmarksDb.Length];
            _dbAxisLabelsRight = new Label[s_axisLandmarksDb.Length];
            _dbAxisLabelsLeftMirror = new Label[s_axisLandmarksDb.Length];
            _dbAxisLabelsRightMirror = new Label[s_axisLandmarksDb.Length];
            for (var i = 0; i < s_axisLandmarksDb.Length; i++)
            {
                var db = s_axisLandmarksDb[i];
                var text = db.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

                var left = CreateDbAxisLabel(text, $"DbAxisLabelLeft_{text}");
                var right = CreateDbAxisLabel(text, $"DbAxisLabelRight_{text}");
                var leftMirror = CreateDbAxisLabel(text, $"DbAxisLabelLeftMirror_{text}");
                var rightMirror = CreateDbAxisLabel(text, $"DbAxisLabelRightMirror_{text}");

                _dbAxisLabelsLeft[i] = left;
                _dbAxisLabelsRight[i] = right;
                _dbAxisLabelsLeftMirror[i] = leftMirror;
                _dbAxisLabelsRightMirror[i] = rightMirror;
                ambiance_ThemeSpectrum.Controls.Add(left);
                ambiance_ThemeSpectrum.Controls.Add(right);
                ambiance_ThemeSpectrum.Controls.Add(leftMirror);
                ambiance_ThemeSpectrum.Controls.Add(rightMirror);
            }
        }
        finally
        {
            ambiance_ThemeSpectrum.ResumeLayout(false);
        }

        ApplyThemeChrome(theme);
        UpdateModeDescription();
        RecalculateBarLayout();
    }

    private void UpdateModeDescription()
    {
        var description = _visualMode switch
        {
            "Oscilloscope" => "Actual front-left PCM waveform; auto-ranging is labeled, not a spectral curve.",
            "Stereo Levels" => "Independent front-channel RMS and sample peaks in amplitude-relative dBFS; not calibrated VU or LUFS.",
            "Stereo Spectrum" => "Independent front-left and front-right frequency-band measurements.",
            "Dynamics History" => "Captured RMS and sample-peak history; missing audio intervals remain gaps.",
            "Spectral History" => "Perspective history of measured display spectra; frame spacing is not a uniform sample-time axis.",
            "Waterfall" => "Measured frequency intensity over received display frames, not a calibrated sample-time spectrogram.",
            "Octave Waterfall" => "Grouped frequency-band power history reconstructed from display values.",
            "Octave Spectrum" => "Grouped octave-range power estimated from quantized frequency-band measurements.",
            "Advanced Spectral Balance" => "Seven FFT-measured bands, 20 Hz–20 kHz, in dBFS relative to a full-scale sine; not simulated from the 83 display bands.",
            "Spectral Balance" => "Estimated relative bass, mid and high power from measured display bands.",
            "Level Change" => "Signed frame-to-frame displayed-level change; not a beat or transient detector.",
            "Threshold Monitor" => "Measured frequency bands compared with the visible -24 dBFS reference.",
            "Peak Trace" => "Current frequency spectrum with a genuine held-peak envelope.",
            "Band Matrix" => "All measured bands shown as compact labeled frequency and level cards.",
            "Radial Spectrum" => "Measured logarithmic frequency bands arranged clockwise around a circle.",
            _ => "Measured 83-band frequency spectrum with full-scale-sine-relative dBFS and elapsed-time ballistics."
        };
        _modeSelector.AccessibleDescription = description + " Press Ctrl+M to cycle alphabetically.";
        _selectionToolTip.SetToolTip(_modeSelector, description);
    }

    private static Label CreateDbAxisLabel(string text, string name) => new()
    {
        AutoSize = true,
        BackColor = Color.Transparent,
        ForeColor = Color.FromArgb(150, 150, 150),
        Font = new Font("Segoe UI", 7f),
        Text = text,
        Name = name,
    };

    private int GetMaxDbAxisLabelWidth()
    {
        var max = 0;
        if (_dbAxisLabelsLeft != null)
        {
            foreach (var label in _dbAxisLabelsLeft)
                if (label != null) max = Math.Max(max, label.PreferredWidth);
        }
        return max;
    }

    private int GetMaxDbAxisLabelHeight()
    {
        var max = 0;
        if (_dbAxisLabelsLeft != null)
        {
            foreach (var label in _dbAxisLabelsLeft)
                if (label != null) max = Math.Max(max, label.PreferredHeight);
        }
        return max;
    }

    private void RecalculateBarLayout()
    {
        if (_progressBars == null) return;

        var cw = ambiance_ThemeSpectrum.ClientSize.Width;
        var ch = ambiance_ThemeSpectrum.ClientSize.Height;
        if (cw <= 0 || ch <= 0) return;

        var calibrated = UsesSpectrumAxes;
        var scaleY = DeviceDpi / 96f;
        var dbLabelH = Math.Max(0, GetMaxDbAxisLabelHeight());
        var dbLabelHalf = (dbLabelH + 1) / 2;
        var baseStartY = Math.Max(0, (int)Math.Round(50f * scaleY));
        var startY = Math.Max(baseStartY + dbLabelHalf, _selectionPanel.Bottom + dbLabelHalf + AXIS_LABEL_MIN_GAP);
        var actualAxisHeight = 0;
        foreach (var label in _axisLabels)
            actualAxisHeight = Math.Max(actualAxisHeight, label.PreferredHeight);
        var labelH = Math.Max(actualAxisHeight, Math.Max(10, (int)Math.Round(AXIS_LABEL_HEIGHT * scaleY)));
        var statusReserve = _captureStatusLabel.Height + 4;
        var bottomReserve = Math.Max(0, (int)Math.Round(8f * scaleY)) + dbLabelHalf + AXIS_LABEL_MIN_GAP + labelH + statusReserve;
        var barH   = Math.Max(10, ch - startY - bottomReserve);
        var axisLabelY = Math.Min(ch - statusReserve - labelH - AXIS_LABEL_MIN_GAP,
            startY + barH + dbLabelHalf + AXIS_LABEL_MIN_GAP);

        // squeeze the side margins just enough for the dB labels not to hit the bars
        var baseMarginX = Math.Max(4, (int)Math.Round(11f * (float)cw / 1184f));
        var dbLabelReserve = GetMaxDbAxisLabelWidth() + 2 * AXIS_LABEL_MIN_GAP;
        var marginX = Math.Max(baseMarginX, dbLabelReserve);

        // fractional stride lets all 83 bars fit without spilling over the edge
        var availW  = cw - 2 * marginX;
        var strideF = (float)availW / BAR_COUNT;
        var advancedMode = IsAdvancedMode(_visualMode);

        ambiance_ThemeSpectrum.SuspendLayout();
        try
        {
            var plan = _appliedPlan ?? _analyzer?.CurrentBandPlan ?? s_defaultPlan;
            for (var i = 0; i < BAR_COUNT; i++)
            {
                var x = marginX + (int)Math.Round(calibrated ? SpectrumAxis.Position(plan[i].LowerHz) * availW : i * strideF);
                var nextX = marginX + (int)Math.Round(calibrated ? SpectrumAxis.Position(plan[i].UpperHz) * availW : (i + 1) * strideF);
                _progressBars[i].Location = new Point(x, startY);
                _progressBars[i].Size     = new Size(Math.Max(1, nextX - x), barH);
                _progressBars[i].Visible = !advancedMode && nextX > x;
                _progressBars[i].GridlineLevels = calibrated ? s_gridlineLevels : SpectrumAxis.LegacyGridLevels;
                _progressBars[i].VerticalGridlinePositions = calibrated
                    ? SpectrumAxis.LocalGridPositions(x - marginX, nextX - marginX, availW) : Array.Empty<float>();
            }

            _advancedVisualizer.Location = new Point(marginX, startY);
            _advancedVisualizer.Size = new Size(Math.Max(1, availW),
                _visualMode == "Stereo Spectrum"
                    ? Math.Max(1, ch - startY - statusReserve - (int)Math.Round(8 * scaleY))
                    : barH);
            _advancedVisualizer.Visible = advancedMode;
            _advancedVisualizer.SetBandPlan(_appliedPlan ?? _analyzer?.CurrentBandPlan ?? s_defaultPlan);
            LayoutAxisLabels(marginX, strideF, axisLabelY, cw);

            if (advancedMode)
            {
                HideDbAxisLabels(_dbAxisLabelsLeft);
                HideDbAxisLabels(_dbAxisLabelsRight);
                HideDbAxisLabels(_dbAxisLabelsLeftMirror);
                HideDbAxisLabels(_dbAxisLabelsRightMirror);
                if (calibrated && _visualMode != "Stereo Spectrum")
                {
                    var plot = _advancedVisualizer.SpectrumPlotBounds;
                    LayoutDbAxisLabels(marginX, startY + (int)Math.Round(plot.Top),
                        (int)Math.Round(plot.Height), cw, startY - dbLabelHalf, axisLabelY - AXIS_LABEL_MIN_GAP);
                }
                if (_visualMode.Equals("Radial Spectrum", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Note Map", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Band Matrix", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Octave Spectrum", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Octave Waterfall", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Stereo Levels", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Oscilloscope", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Spectral Balance", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals(AdvancedSpectralBalanceMode, StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Spectral History", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Stereo Spectrum", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Dynamics History", StringComparison.OrdinalIgnoreCase))
                    HideFrequencyAxisLabels();
            }
            else
            {
                var fillPadding = _progressBars.Length > 0 ? _progressBars[0].BrickPadding : 0;
                var fillTop = startY + (calibrated ? _progressBars[0].PlotBounds.Top : fillPadding);
                var fillH = calibrated ? _progressBars[0].PlotBounds.Height : Math.Max(1, barH - 2 * fillPadding);
                var topBound = Math.Max(0, startY - dbLabelHalf);
                var bottomBound = axisLabelY - AXIS_LABEL_MIN_GAP;

                if (IsSymmetricFillMode(_visualMode, out var invertedFromCenter))
                {
                    LayoutDbAxisLabelsSymmetric(marginX, fillTop, fillH, cw, topBound, bottomBound, invertedFromCenter);
                }
                else
                {
                    HideDbAxisLabels(_dbAxisLabelsLeftMirror);
                    HideDbAxisLabels(_dbAxisLabelsRightMirror);
                    LayoutDbAxisLabels(marginX, fillTop, fillH, cw, topBound, bottomBound);
                }
            }
        }
        finally
        {
            ambiance_ThemeSpectrum.ResumeLayout(false);
        }
    }

    private void HideFrequencyAxisLabels()
    {
        if (_axisLabels == null) return;
        foreach (var label in _axisLabels)
            label.Visible = false;
    }

    private void LayoutAxisLabels(int marginX, float strideF, int labelY, int containerWidth)
    {
        if (_axisLabels == null) return;

        var plan = _appliedPlan ?? _analyzer?.CurrentBandPlan ?? s_defaultPlan;
        _layoutPlan = plan;

        var landmarks = UsesSpectrumAxes ? s_axisLandmarksHz : s_legacyAxisLandmarksHz;
        var previousRight = int.MinValue;
        var lastWidth = _axisLabels[landmarks.Length - 1].PreferredWidth;
        var endLeft = Math.Min(containerWidth - lastWidth,
            marginX + (int)Math.Round(BAR_COUNT * strideF) - lastWidth / 2);
        for (var i = 0; i < _axisLabels.Length; i++)
        {
            var label = _axisLabels[i];
            label.Top = labelY;
            if (i >= landmarks.Length) { label.Visible = false; continue; }
            var hz = landmarks[i];
            label.Text = SpectrumAxis.FrequencyLabel(hz);

            // skip anything outside what we analyze (e.g. above Nyquist)
            var inRange = UsesSpectrumAxes || hz >= plan.MinHz && hz <= plan.MaxHz;

            // bar i covers [i, i+1) in plan units, same as the drawing code
            var span = BAR_COUNT * strideF - (IsAdvancedMode(_visualMode) && UsesSpectrumAxes ? 1 : 0);
            var centerX = marginX + (int)Math.Round(UsesSpectrumAxes
                ? SpectrumAxis.Position(hz) * span : plan.FrequencyToPosition(hz) * strideF);
            var w = label.PreferredWidth;
            var left = Math.Max(0, Math.Min(containerWidth - w, centerX - (w / 2)));

            // drop labels that would collide when the window is narrow
            var visible = inRange && left >= previousRight + AXIS_LABEL_MIN_GAP
                && (!UsesSpectrumAxes || i == landmarks.Length - 1 || left + w + AXIS_LABEL_MIN_GAP <= endLeft);
            label.Visible = visible;
            if (!visible) continue;

            label.Location = new Point(left, labelY);
            previousRight = left + w;
        }
    }

    private bool UsesSpectrumAxes => _visualMode == "Spectrum" || _visualMode == "Bricks" || _visualMode == "LED"
        || _visualMode == "Contour" || _visualMode == "Peak Trace" || _visualMode == "Threshold Monitor"
        || _visualMode == "Stereo Spectrum";

    // normalize through LevelScale so each label lands at its real bar height (0 to -72 dBFS)
    private void LayoutDbAxisLabels(int marginX, int startY, int barH, int containerWidth, int topBound, int bottomBound)
    {
        if (_dbAxisLabelsLeft == null || _dbAxisLabelsRight == null) return;

        var previousBottomLeft = int.MinValue;
        var previousBottomRight = int.MinValue;
        var lastTop = startY + barH - _dbAxisLabelsLeft[12].Height / 2;

        for (var i = 0; i < s_axisLandmarksDb.Length; i++)
        {
            if (!UsesSpectrumAxes && i % 2 != 0)
            {
                _dbAxisLabelsLeft[i].Visible = _dbAxisLabelsRight[i].Visible = false;
                continue;
            }
            var db = s_axisLandmarksDb[i];
            var norm = LevelScale.Normalize(db);
            var centerY = startY + (int)Math.Round((1.0 - norm) * barH);

            var left = _dbAxisLabelsLeft[i];
            var top = centerY - left.Height / 2;
            var visibleLeft = top >= topBound && top + left.Height <= bottomBound
                && top >= previousBottomLeft + AXIS_LABEL_MIN_GAP
                && (!UsesSpectrumAxes || i == 12 || top + left.Height + AXIS_LABEL_MIN_GAP <= lastTop);
            left.Visible = visibleLeft;
            if (visibleLeft)
            {
                left.Location = new Point(Math.Max(0, marginX - AXIS_LABEL_MIN_GAP - left.PreferredWidth), top);
                previousBottomLeft = top + left.Height;
            }

            var right = _dbAxisLabelsRight[i];
            var visibleRight = top >= topBound && top + right.Height <= bottomBound
                && top >= previousBottomRight + AXIS_LABEL_MIN_GAP
                && (!UsesSpectrumAxes || i == 12 || top + right.Height + AXIS_LABEL_MIN_GAP <= lastTop);
            right.Visible = visibleRight;
            if (visibleRight)
            {
                right.Location = new Point(
                    Math.Min(containerWidth - right.PreferredWidth, containerWidth - marginX + AXIS_LABEL_MIN_GAP),
                    top);
                previousBottomRight = top + right.Height;
            }
        }
    }

    // center/mirror fill both ways from the midpoint, so labels follow that mapping too
    private static bool IsSymmetricFillMode(string visualMode, out bool invertedFromCenter)
    {
        switch ((visualMode ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "center":
                invertedFromCenter = false;
                return true;
            case "mirror":
                invertedFromCenter = true;
                return true;
            default:
                invertedFromCenter = false;
                return false;
        }
    }

    private static void HideDbAxisLabels(Label[] labels)
    {
        if (labels == null) return;
        foreach (var label in labels)
            if (label != null) label.Visible = false;
    }

    // place them symmetrically by normalized distance from the midpoint
    private void LayoutDbAxisLabelsSymmetric(
        int marginX, int startY, int barH, int containerWidth, int topBound, int bottomBound, bool invertedFromCenter)
    {
        if (_dbAxisLabelsLeft == null || _dbAxisLabelsRight == null
            || _dbAxisLabelsLeftMirror == null || _dbAxisLabelsRightMirror == null) return;

        var midY = startY + (barH / 2);
        var halfBarH = barH / 2f;

        var previousBottomLeft = int.MinValue;
        var previousBottomRight = int.MinValue;
        var previousTopLeftMirror = int.MaxValue;
        var previousTopRightMirror = int.MaxValue;

        // walk edges inward so a collision check only needs the last placed neighbor
        var order = new int[s_axisLandmarksDb.Length];
        var distances = new int[s_axisLandmarksDb.Length];
        for (var i = 0; i < s_axisLandmarksDb.Length; i++)
        {
            order[i] = i;
            var norm = (float)LevelScale.Normalize(s_axisLandmarksDb[i]);
            if (invertedFromCenter) norm = 1f - norm;
            distances[i] = (int)Math.Round(halfBarH * norm);
        }
        Array.Sort(order, (a, b) => distances[b].CompareTo(distances[a]));

        foreach (var i in order)
        {
            if (i % 2 != 0)
            {
                _dbAxisLabelsLeft[i].Visible = _dbAxisLabelsRight[i].Visible = false;
                _dbAxisLabelsLeftMirror[i].Visible = _dbAxisLabelsRightMirror[i].Visible = false;
                continue;
            }
            var distance = distances[i];
            var upperCenterY = midY - distance;
            var lowerCenterY = midY + distance;

            var left = _dbAxisLabelsLeft[i];
            var top = upperCenterY - left.Height / 2;
            var visibleLeft = top >= topBound && top + left.Height <= bottomBound
                && top >= previousBottomLeft + AXIS_LABEL_MIN_GAP;
            left.Visible = visibleLeft;
            if (visibleLeft)
            {
                left.Location = new Point(Math.Max(0, marginX - AXIS_LABEL_MIN_GAP - left.PreferredWidth), top);
                previousBottomLeft = top + left.Height;
            }

            var right = _dbAxisLabelsRight[i];
            var visibleRight = top >= topBound && top + right.Height <= bottomBound
                && top >= previousBottomRight + AXIS_LABEL_MIN_GAP;
            right.Visible = visibleRight;
            if (visibleRight)
            {
                right.Location = new Point(
                    Math.Min(containerWidth - right.PreferredWidth, containerWidth - marginX + AXIS_LABEL_MIN_GAP),
                    top);
                previousBottomRight = top + right.Height;
            }

            // no point drawing the lower label if it lands on the upper one
            var leftMirror = _dbAxisLabelsLeftMirror[i];
            var topMirror = lowerCenterY - leftMirror.Height / 2;
            var visibleLeftMirror = distance > 0 && topMirror >= topBound && topMirror + leftMirror.Height <= bottomBound
                && topMirror + leftMirror.Height <= previousTopLeftMirror - AXIS_LABEL_MIN_GAP;
            leftMirror.Visible = visibleLeftMirror;
            if (visibleLeftMirror)
            {
                leftMirror.Location = new Point(Math.Max(0, marginX - AXIS_LABEL_MIN_GAP - leftMirror.PreferredWidth), topMirror);
                previousTopLeftMirror = topMirror;
            }

            var rightMirror = _dbAxisLabelsRightMirror[i];
            var visibleRightMirror = distance > 0 && topMirror >= topBound && topMirror + rightMirror.Height <= bottomBound
                && topMirror + rightMirror.Height <= previousTopRightMirror - AXIS_LABEL_MIN_GAP;
            rightMirror.Visible = visibleRightMirror;
            if (visibleRightMirror)
            {
                rightMirror.Location = new Point(
                    Math.Min(containerWidth - rightMirror.PreferredWidth, containerWidth - marginX + AXIS_LABEL_MIN_GAP),
                    topMirror);
                previousTopRightMirror = topMirror;
            }
        }
    }

    private const int WM_DPICHANGED = 0x02E0;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WM_DPICHANGED)
            RecalculateBarLayout();
    }

    private void OnDisplaySettingsChanged(object sender, EventArgs e)
    {
        if (_isDisposed || IsDisposed || Disposing)
            return;

        if (InvokeRequired)
        {
            try
            {
                Invoke(new Action(() => OnDisplaySettingsChanged(sender, e)));
            }
            catch (InvalidOperationException) when (_isDisposed || IsDisposed || Disposing)
            {
            }
            return;
        }

        var screen = Screen.FromControl(this);
        var wa = screen.WorkingArea;

        if (ambiance_ThemeSpectrum.SmartBounds)
            ambiance_ThemeSpectrum.CorrectBounds(wa);
        else
        {
            var newLeft = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            var newTop = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            if (newLeft != Left || newTop != Top)
                Location = new Point(newLeft, newTop);
        }

        RecalculateBarLayout();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Spectrum_Change(object obj, OnChangeEventArgs e)
    {
        if (_isDisposed || _progressBars == null) return;

        var spectrum = e.Spectrumdata;
        if (spectrum == null || spectrum.Count == 0) return;

        bool postNeeded;
        lock (_updateLock)
        {
            // audio thread can outrun the UI, so keep only the newest frame and queue one update at a time
            var count = Math.Min(spectrum.Count, BAR_COUNT);
            for (var i = 0; i < count; i++)
            {
                var v = spectrum[i];
                _spectrumBuffer[i] = v < NOISE_GATE_THRESHOLD ? (byte)0 : v;
            }
            for (var i = count; i < BAR_COUNT; i++)
                _spectrumBuffer[i] = 0;
            _audioBuffer.CopyFrom(e.Audio);
            _spectrumPlanBuffer = e.Plan;

            postNeeded = !_updatePending;
            _updatePending = true;
        }

        if (!postNeeded) return;

        if (InvokeRequired)
        {
            try
            {
                _ = BeginInvoke(new Action(ApplySpectrumToUI));
            }
            catch (InvalidOperationException) when (_isDisposed || IsDisposed || Disposing)
            {
                lock (_updateLock)
                {
                    _updatePending = false;
                }
            }
        }
        else
            ApplySpectrumToUI();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ApplySpectrumToUI()
    {
        lock (_updateLock)
        {
            _updatePending = false;
            Buffer.BlockCopy(_spectrumBuffer, 0, _applyBuffer, 0, BAR_COUNT);
            _audioApplyBuffer.CopyFrom(_audioBuffer);
            _appliedPlan = _spectrumPlanBuffer;
        }

        // the queued callback can fire after the form is already gone
        if (_isDisposed || _progressBars == null) return;

        var plan = _appliedPlan ?? _analyzer?.CurrentBandPlan;
        if (plan != null && !ReferenceEquals(plan, _layoutPlan))
            RecalculateBarLayout();

        if (IsAdvancedMode(_visualMode))
        {
            _advancedVisualizer.SetAudioData(_audioApplyBuffer);
            _advancedVisualizer.SetSpectrum(_applyBuffer);
        }
        else
        {
            for (var i = 0; i < BAR_COUNT; i++)
                _progressBars[i].SetTargetValueUI(_applyBuffer[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyColorThemeOptimized(VerticalProgressBar progress, BarColorTheme theme)
    {
        progress.HeatLowColor = theme.Low;
        progress.HeatMidColor = theme.Mid;
        progress.HeatHighColor = theme.High;
        progress.HeatPeakColor = theme.Peak;
        progress.HeatIntensityCurve = theme.IntensityCurve;
        progress.PeakLineColor = theme.Ui.PrimaryText;
        progress.PeakColorMatchesHeat = false;
    }

    private void ApplyThemeChrome(BarColorTheme theme)
    {
        var ui = theme.Ui;
        BackColor = ui.Canvas;
        ambiance_ThemeSpectrum.ApplyTheme(ui);
        _selectionPanel.BackColor = Color.Transparent;
        _modeSelectorLabel.ForeColor = ui.PrimaryText;
        _styleSelectorLabel.ForeColor = ui.PrimaryText;
        _themeSelectorLabel.ForeColor = ui.PrimaryText;
        ApplySelectorTheme(_modeSelector, ui);
        ApplySelectorTheme(_styleSelector, ui);
        ApplySelectorTheme(_themeSelector, ui);
        _rotationSettingsButton.BackColor = ui.ControlSurface;
        _rotationSettingsButton.ForeColor = ui.PrimaryText;
        _rotationSettingsButton.FlatAppearance.BorderColor = ui.Divider;
        _rotationSettingsButton.FlatAppearance.MouseOverBackColor = ui.Hover;
        _rotationSettingsButton.FlatAppearance.MouseDownBackColor = ui.Selection;
        _captureStatusLabel.ForeColor = ui.SecondaryText;

        if (_axisLabels != null)
        {
            foreach (var label in _axisLabels)
                label.ForeColor = ui.SecondaryText;
        }
        ApplyAxisTheme(_dbAxisLabelsLeft, ui);
        ApplyAxisTheme(_dbAxisLabelsRight, ui);
        ApplyAxisTheme(_dbAxisLabelsLeftMirror, ui);
        ApplyAxisTheme(_dbAxisLabelsRightMirror, ui);
    }

    private static void ApplySelectorTheme(ComboBox selector, VisualTheme theme)
    {
        if (selector == null)
            return;

        selector.BackColor = theme.ControlSurface;
        selector.ForeColor = theme.PrimaryText;
    }

    private static void ApplyAxisTheme(Label[] labels, VisualTheme theme)
    {
        if (labels == null)
            return;

        foreach (var label in labels)
            label.ForeColor = theme.SecondaryText;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyMeterPresetOptimized(VerticalProgressBar progress, string mode)
    {
        progress.AnimationFps = 60;
        progress.TopEmphasisStart = 0.88f;
        progress.TopEmphasisStrength = 0.60f;
        progress.HeatIntensityCurve = 1.85f;
        progress.PeakLineThickness = 2;

        progress.PeakDecayPerTick = 1.15f;

        mode = (mode ?? string.Empty).Trim().ToLowerInvariant();

        switch (mode)
        {
            case "bricks":
                progress.UseAsymmetricBallistics = true;
                progress.ResponseTimeMs = 110;
                progress.ReleaseTimeMs = 320;
                progress.PeakHoldMilliseconds = 0;
                break;

            case "dots":
                progress.UseAsymmetricBallistics = true;
                progress.ResponseTimeMs = 130;
                progress.ReleaseTimeMs = 620;
                progress.PeakHoldMilliseconds = 0;
                break;

            case "led":
            case "ppmi i":
            case "ppmi i b":
            case "ppmi i a":
            case "ppmi i bbc":
            case "ppmii":
            case "ppm2":
            case "iec2":
            case "bbc":
            case "ebu":
                progress.UseAsymmetricBallistics = true;
                progress.ResponseTimeMs = 90;
                progress.ReleaseTimeMs = 380;
                progress.PeakHoldMilliseconds = 0;
                break;

            case "center":
                progress.UseAsymmetricBallistics = true;
                progress.ResponseTimeMs = 120;
                progress.ReleaseTimeMs = 260;
                progress.PeakHoldMilliseconds = 0;
                break;

            case "mirror":
                progress.UseAsymmetricBallistics = true;
                progress.ResponseTimeMs = 120;
                progress.ReleaseTimeMs = 260;
                progress.PeakHoldMilliseconds = 0;
                break;

            case "wave":
                progress.UseAsymmetricBallistics = true;
                progress.ResponseTimeMs = 110;
                progress.ReleaseTimeMs = 440;
                progress.PeakHoldMilliseconds = 120;
                break;

            default:
                progress.UseAsymmetricBallistics = true;
                progress.ResponseTimeMs = SPECTRUM_ATTACK_MS;
                progress.ReleaseTimeMs = SPECTRUM_RELEASE_MS;
                progress.PeakHoldMilliseconds = SPECTRUM_PEAK_HOLD_MS;
                break;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CleanupResources();
        base.OnFormClosed(e);
    }

    private void CleanupResources()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _rotationTimer?.Stop();
        _rotationTimer?.Dispose();
        _rotationTimer = null;
        _selectionToolTip?.Dispose();
        _selectionToolTip = null;
        _captureStatusTimer?.Stop();
        _captureStatusTimer?.Dispose();
        _captureStatusTimer = null;

        if (_analyzer != null)
        {
            Analyzer.OnChange -= Spectrum_Change;
            _analyzer.Dispose();
            _analyzer = null;
        }

        if (_progressBars != null)
        {
            for (var i = 0; i < _progressBars.Length; i++)
            {
                _progressBars[i]?.Dispose();
                _progressBars[i] = null;
            }
            _progressBars = null;
        }

        _advancedVisualizer?.Dispose();
        _advancedVisualizer = null;
        if (_axisLabels != null)
        {
            for (var i = 0; i < _axisLabels.Length; i++)
            {
                var font = _axisLabels[i]?.Font;
                _axisLabels[i]?.Dispose();
                font?.Dispose();
                _axisLabels[i] = null;
            }
            _axisLabels = null;
        }

        DisposeDbAxisLabels(ref _dbAxisLabelsLeft);
        DisposeDbAxisLabels(ref _dbAxisLabelsRight);
        DisposeDbAxisLabels(ref _dbAxisLabelsLeftMirror);
        DisposeDbAxisLabels(ref _dbAxisLabelsRightMirror);
    }

    private static void DisposeDbAxisLabels(ref Label[] labels)
    {
        if (labels == null) return;

        for (var i = 0; i < labels.Length; i++)
        {
            var font = labels[i]?.Font;
            labels[i]?.Dispose();
            font?.Dispose();
            labels[i] = null;
        }
        labels = null;
    }
}

internal readonly struct VisualSelection
{
    internal VisualSelection(string mode, string style, string theme)
    {
        Mode = mode;
        Style = style;
        Theme = theme;
    }

    internal string Mode { get; }
    internal string Style { get; }
    internal string Theme { get; }
}

// random picker that prefers changing categories and tries not to repeat the last sixteen picks
internal sealed class VisualSelectionRandomizer
{
    private const int RecentSelectionCount = 16;
    private readonly Queue<VisualSelection> _recent = new();

    internal VisualSelection Next(VisualSelection current, string[] modes, string[] themes, Random random)
    {
        if (random == null)
            throw new ArgumentNullException(nameof(random));
        if (modes == null || modes.Length == 0)
            throw new ArgumentException("At least one mode is required.", nameof(modes));
        if (themes == null || themes.Length == 0)
            throw new ArgumentException("At least one theme is required.", nameof(themes));

        var candidatesByMode = new List<List<VisualSelection>>();
        while (true)
        {
            candidatesByMode.Clear();
            foreach (var mode in modes)
            {
                if (modes.Length > 1 && Same(mode, current.Mode))
                    continue;

                var candidates = new List<VisualSelection>();
                var styles = VisualStyles.GetSupportedNames(mode);
                foreach (var style in styles)
                {
                    if (styles.Length > 1 && Same(style, VisualStyles.Resolve(current.Style)))
                        continue;
                    foreach (var theme in themes)
                    {
                        if (themes.Length > 1 && Same(theme, current.Theme))
                            continue;
                        var candidate = new VisualSelection(mode, style, theme);
                        if (!_recent.Contains(candidate))
                            candidates.Add(candidate);
                    }
                }
                if (candidates.Count > 0)
                    candidatesByMode.Add(candidates);
            }

            if (candidatesByMode.Count > 0)
                break;
            // tiny pools force repeats, so loosen the history rule but always still change category
            _recent.Dequeue();
        }

        // pick the mode first so modes with fewer styles aren't picked less often
        var selectedMode = candidatesByMode[random.Next(candidatesByMode.Count)];
        var selection = selectedMode[random.Next(selectedMode.Count)];
        _recent.Enqueue(selection);
        if (_recent.Count > RecentSelectionCount)
            _recent.Dequeue();
        return selection;
    }

    private static bool Same(string first, string second) =>
        string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
}
