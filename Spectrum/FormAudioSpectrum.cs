using System;
using System.Configuration;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Spectrum.Dsp;

namespace Spectrum;

public partial class FormAudioSpectrum : Form
{
    private const int BAR_COUNT = 83;
    private const int NOISE_GATE_THRESHOLD = 2;
    private static readonly string[] s_advancedModes =
    {
        "Waterfall", "Radial Spectrum", "Contour", "Peak Trace", "Threshold Monitor", "Band Matrix",
        "Octave Spectrum", "Spectral Flux", "Orbit History", "Octave Waterfall", "Transient Map", "Frequency Ribbon"
    };
    private static readonly string[] s_visualModes =
    {
        "Spectrum", "Bricks", "LED", "Dots", "Wave", "Lollipop",
        "Waterfall", "Radial Spectrum", "Contour", "Peak Trace", "Threshold Monitor", "Band Matrix",
        "Octave Spectrum", "Spectral Flux", "Orbit History", "Octave Waterfall", "Transient Map", "Frequency Ribbon"
    };
    private static readonly string[] s_colorThemes = BarColorThemes.Names;

    internal static string[] ColorThemeNames => (string[])s_colorThemes.Clone();

    // Spectrum mode uses elapsed-time ballistics: a 45 ms attack, 380 ms release, and 300 ms peak hold.
    internal const int SPECTRUM_ATTACK_MS = 45;
    internal const int SPECTRUM_RELEASE_MS = 380;
    internal const int SPECTRUM_PEAK_HOLD_MS = 300;

    // Frequency landmarks use the same logarithmic mapping as the bars.
    private static readonly double[] s_axisLandmarksHz = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    private const float AXIS_LABEL_HEIGHT = 14f; // Design-time pixels at the 378 px reference height.
    private const int AXIS_LABEL_MIN_GAP = 4;

    // dB landmarks appear on both sides of the bars and use LevelScale.Normalize to match bar heights.
    private static readonly double[] s_axisLandmarksDb = { 0, -12, -24, -36, -48, -60, -72 };

    // Reference-scale ticks appear inside each bar, excluding the 0 and -72 dB border lines.
    private static readonly float[] s_gridlineLevels = BuildGridlineLevels();

    private static float[] BuildGridlineLevels()
    {
        var levels = new float[s_axisLandmarksDb.Length - 2];
        for (var i = 1; i < s_axisLandmarksDb.Length - 1; i++)
            levels[i - 1] = (float)LevelScale.Normalize(s_axisLandmarksDb[i]);
        return levels;
    }

    // Default geometry is valid before the device sample rate is known for rates of at least 41.8 kHz.
    private static readonly BandPlan s_defaultPlan = BandPlan.CreateLogarithmic(BAR_COUNT, 20, 20000, 48000);

    private VerticalProgressBar[] _progressBars;
    private AdvancedVisualizationControl _advancedVisualizer;
    private WaveSpectrumControl _waveVisualizer;
    private Label[] _axisLabels;
    private Label[] _dbAxisLabelsLeft;
    private Label[] _dbAxisLabelsRight;

    // Center and Mirror modes show dB labels above and below the midpoint; other modes use the standard labels.
    private Label[] _dbAxisLabelsLeftMirror;
    private Label[] _dbAxisLabelsRightMirror;

    private BandPlan _layoutPlan;
    private string _visualMode;
    private string _visualStyle;
    private string _barTheme;
    private ComboBox _modeSelector;
    private ComboBox _styleSelector;
    private ComboBox _themeSelector;
    private Button _rotationSettingsButton;
    private ToolTip _selectionToolTip;
    private FlowLayoutPanel _selectionPanel;
    private Label _modeSelectorLabel;
    private Label _styleSelectorLabel;
    private Label _themeSelectorLabel;
    private bool _initializingSelectors;
    private bool _randomRotationEnabled;
    private bool _applyingRandomSelection;
    private bool _automaticSaveErrorShown;
    private int _rotationIntervalMinutes = 5;
    private readonly Random _random = new();
    private System.Windows.Forms.Timer _rotationTimer;
    private readonly byte[] _spectrumBuffer;
    private readonly byte[] _applyBuffer;

    private volatile bool _updatePending;
    private readonly object _updateLock = new();

    private Analyzer _analyzer;
    private volatile bool _isDisposed;

    public FormAudioSpectrum()
    {
        InitializeComponent();

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
        var preferences = VisualizerPreferences.Default;
        var configuredMode = string.IsNullOrWhiteSpace(preferences.Mode)
            ? ConfigurationManager.AppSettings["Mode"]
            : preferences.Mode;
        var configuredTheme = string.IsNullOrWhiteSpace(preferences.Theme)
            ? ConfigurationManager.AppSettings["Theme"]
            : preferences.Theme;
        var configuredStyle = string.IsNullOrWhiteSpace(preferences.Style)
            ? ConfigurationManager.AppSettings["Style"]
            : preferences.Style;
        ResolveConfiguredVisualState(configuredMode, configuredStyle, out _visualMode, out _visualStyle);
        _barTheme = ResolveConfiguredTheme(configuredTheme);
        _randomRotationEnabled = string.Equals(preferences.RotationMode, "Random", StringComparison.OrdinalIgnoreCase);
        _rotationIntervalMinutes = Math.Max(1, Math.Min(240, preferences.RotationIntervalMinutes));

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

        _analyzer = new Analyzer();
        Analyzer.OnChange += Spectrum_Change;

        Shown += (s, e) =>
        {
            PositionSelectionPanel();
            RecalculateBarLayout();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Taskbar.SetState(Handle, Taskbar.TaskbarStates.NoProgress);
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
        _styleSelector.AccessibleDescription = "Optional Pulse or Glow overlay. Styles apply only to Spectrum, LED, and Wave modes.";
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
            AccessibleName = "Mode and theme rotation settings"
        };
        _rotationSettingsButton.FlatAppearance.BorderColor = Color.FromArgb(82, 75, 60);
        _rotationSettingsButton.Click += (sender, args) => ShowRotationSettings();
        _selectionToolTip = new ToolTip();
        _selectionToolTip.SetToolTip(_rotationSettingsButton, "Choose fixed or timed random mode and theme (Ctrl+R)");

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
        ambiance_ThemeSpectrum.SizeChanged += (s, e) => PositionSelectionPanel();
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

        var choice = (string)(isThemeSelector ? _themeSelector.Items[e.Index] :
            isStyleSelector ? _styleSelector.Items[e.Index] : _modeSelector.Items[e.Index]);
        var textLeft = e.Bounds.Left + 8;
        if (isThemeSelector)
        {
            var theme = BarColorThemes.Resolve(choice);
            var swatch = new Rectangle(e.Bounds.Left + 7, e.Bounds.Top + 6, 30, Math.Max(4, e.Bounds.Height - 12));
            using var lowBrush = new SolidBrush(theme.Low);
            using var midBrush = new SolidBrush(theme.Mid);
            using var highBrush = new SolidBrush(theme.High);
            using var swatchPen = new Pen(ui.Frame);
            e.Graphics.FillRectangle(lowBrush, swatch.Left, swatch.Top, 10, swatch.Height);
            e.Graphics.FillRectangle(midBrush, swatch.Left + 10, swatch.Top, 10, swatch.Height);
            e.Graphics.FillRectangle(highBrush, swatch.Left + 20, swatch.Top, 10, swatch.Height);
            e.Graphics.DrawRectangle(swatchPen, swatch);
            textLeft = swatch.Right + 8;
        }
        else if (isStyleSelector)
        {
            DrawStyleIcon(e.Graphics, choice, new Rectangle(e.Bounds.Left + 7, e.Bounds.Top + 4, 20, 14), _barTheme);
            textLeft = e.Bounds.Left + 34;
        }
        else
        {
            var theme = BarColorThemes.Resolve(_barTheme);
            DrawModeIcon(e.Graphics, choice, new Rectangle(e.Bounds.Left + 7, e.Bounds.Top + 4, 20, 14), theme.High);
            textLeft = e.Bounds.Left + 34;
        }

        TextRenderer.DrawText(e.Graphics, choice, _modeSelector.Font,
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
        using var pen = new Pen(color, 1.6f);
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
            graphics.DrawLine(pen, bounds.Left + 2, bounds.Bottom - 2, bounds.Right - 2, bounds.Top + 2);
        }
    }

    private static void DrawModeIcon(Graphics graphics, string mode, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, 1.6f);
        using var brush = new SolidBrush(color);
        var centerY = bounds.Top + (bounds.Height / 2);

        switch (mode)
        {
            case "Spectrum":
                DrawBars(graphics, brush, bounds, new[] { 5, 10, 7, 13, 9 });
                break;
            case "Bricks":
                for (var row = 0; row < 3; row++)
                    for (var column = 0; column < 3; column++)
                        graphics.FillRectangle(brush, bounds.Left + (column * 6), bounds.Top + 1 + (row * 4), 4, 2);
                break;
            case "LED":
                for (var row = 0; row < 3; row++)
                    for (var column = 0; column < 3; column++)
                        graphics.FillEllipse(brush, bounds.Left + (column * 6), bounds.Top + 1 + (row * 4), 3, 3);
                break;
            case "Dots":
                for (var i = 0; i < 4; i++)
                    graphics.FillEllipse(brush, bounds.Left + 2 + (i * 5), centerY - ((i % 2) * 4), 3, 3);
                break;
            case "Wave":
                graphics.DrawLines(pen, new[]
                {
                    new Point(bounds.Left, centerY), new Point(bounds.Left + 4, centerY - 4),
                    new Point(bounds.Left + 8, centerY), new Point(bounds.Left + 12, centerY + 4),
                    new Point(bounds.Right - 1, centerY)
                });
                break;
            case "Center":
                for (var i = 0; i < 4; i++)
                    graphics.DrawLine(pen, bounds.Left + 2 + (i * 5), centerY - 1, bounds.Left + 2 + (i * 5), centerY + 2);
                break;
            case "Mirror":
                for (var i = 0; i < 4; i++)
                {
                    var x = bounds.Left + 2 + (i * 5);
                    graphics.DrawLine(pen, x, centerY - 1, x, bounds.Top + 1);
                    graphics.DrawLine(pen, x, centerY + 1, x, bounds.Bottom - 1);
                }
                break;
            case "Lollipop":
                for (var i = 0; i < 3; i++)
                {
                    var x = bounds.Left + 3 + (i * 6);
                    graphics.DrawLine(pen, x, centerY, x, bounds.Bottom - 1);
                    graphics.FillEllipse(brush, x - 2, bounds.Top + (i % 2), 4, 4);
                }
                break;
            case "Waterfall":
            case "Octave Waterfall":
            case "Band Matrix":
            case "Transient Map":
                for (var row = 0; row < 3; row++)
                    for (var column = 0; column < 4; column++)
                        if ((row + column) % 3 != 0)
                            graphics.FillRectangle(brush, bounds.Left + (column * 4), bounds.Top + 1 + (row * 4), 3, 3);
                break;
            case "Radial Spectrum":
            case "Orbit History":
                var center = new Point(bounds.Left + (bounds.Width / 2), centerY);
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
            case "Peak Trace":
            case "Frequency Ribbon":
                graphics.DrawLines(pen, new[]
                {
                    new Point(bounds.Left, bounds.Bottom - 2), new Point(bounds.Left + 4, centerY),
                    new Point(bounds.Left + 8, bounds.Top + 2), new Point(bounds.Left + 12, centerY + 1),
                    new Point(bounds.Right - 1, bounds.Top + 4)
                });
                break;
            case "Threshold Monitor":
            case "Octave Spectrum":
                DrawBars(graphics, brush, bounds, new[] { 4, 8, 12, 7, 10 });
                break;
            case "Spectral Flux":
                for (var i = 0; i < 4; i++)
                    graphics.DrawLine(pen, bounds.Left + 2 + (i * 5), bounds.Bottom - 2, bounds.Left + 2 + (i * 5), bounds.Top + (i % 2 == 0 ? 3 : 7));
                break;
            case "Note Map":
                for (var row = 0; row < 3; row++)
                    for (var column = 0; column < 3; column++)
                        if ((row + column) % 2 == 0)
                            graphics.FillRectangle(brush, bounds.Left + (column * 6), bounds.Top + (row * 4), 4, 3);
                        else
                            graphics.DrawRectangle(pen, bounds.Left + (column * 6), bounds.Top + (row * 4), 4, 3);
                break;
            case "Ambient Particles":
                for (var i = 0; i < 6; i++)
                    graphics.FillEllipse(brush, bounds.Left + ((i * 7) % 19), bounds.Top + ((i * 5) % 12), 3, 3);
                break;
        }
    }

    private static void DrawBars(Graphics graphics, SolidBrush brush, Rectangle bounds, int[] heights)
    {
        var centerY = bounds.Top + (bounds.Height / 2);
        for (var i = 0; i < heights.Length; i++)
            graphics.FillRectangle(brush, bounds.Left + (i * 4), centerY - (heights[i] / 2), 2, heights[i]);
    }

    private void PositionSelectionPanel()
    {
        if (_selectionPanel == null) return;

        var width = ambiance_ThemeSpectrum.ClientSize.Width;
        var compact = width < 980;
        var narrow = width < 700;
        _modeSelectorLabel.Visible = !compact;
        _styleSelectorLabel.Visible = !compact && !narrow;
        _themeSelectorLabel.Visible = !compact && !narrow;
        _modeSelector.Width = compact ? 114 : 150;
        _styleSelector.Width = compact ? 88 : 110;
        _themeSelector.Width = compact ? 118 : 160;
        _styleSelector.Visible = !narrow;
        _themeSelector.Visible = !narrow;
        _rotationSettingsButton.Visible = width >= 320;
        _rotationSettingsButton.Width = compact ? 64 : 76;
        _selectionPanel.Width = narrow ? 180 : compact ? 400 : 650;
        var controlBoxRightInset = ambiance_ControlBox.Width + 16;
        _selectionPanel.Location = new Point(
            Math.Max(8, width - controlBoxRightInset - _selectionPanel.Width - 8),
            7);
        ambiance_ThemeSpectrum.TitleRightInset = width < 520
            ? 16
            : Math.Max(16, _selectionPanel.Left - 12);
        ambiance_ThemeSpectrum.Text = width < 520 ? string.Empty : "Audio Spectrum Analyzer";
    }

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

    private static bool IsWaveMode(string mode)
        => string.Equals(mode, "Wave", StringComparison.OrdinalIgnoreCase);

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
            case "spectral flux":
                return AdvancedVisualizationMode.SpectralFlux;
            case "orbit history":
                return AdvancedVisualizationMode.OrbitHistory;
            case "octave waterfall":
                return AdvancedVisualizationMode.OctaveWaterfall;
            case "transient map":
                return AdvancedVisualizationMode.TransientMap;
            case "frequency ribbon":
                return AdvancedVisualizationMode.FrequencyRibbon;
            default:
                return AdvancedVisualizationMode.Contour;
        }
    }

    private void ModeSelector_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_initializingSelectors || _progressBars == null || !(_modeSelector.SelectedItem is string mode))
            return;

        _visualMode = mode;
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
        _waveVisualizer.SetTheme(theme);
        ApplyThemeChrome(theme);
        ApplyVisualStyle();
        UpdateStyleSelector();
        if (!_applyingRandomSelection)
            SaveVisualPreferences();
        ResetRotationTimer();
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

        _waveVisualizer.Style = style;
        _advancedVisualizer.Style = style;
    }

    private void UpdateStyleSelector()
    {
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
            _initializingSelectors = false;
        }
    }

    private void ThemeSelector_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_initializingSelectors || _progressBars == null || !(_themeSelector.SelectedItem is string themeName))
            return;

        _barTheme = themeName;
        var theme = BarColorThemes.Resolve(themeName);
        _advancedVisualizer.ApplyTheme(theme);
        _waveVisualizer.SetTheme(theme);
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
            var preferences = VisualizerPreferences.Default;
            preferences.RotationMode = oldRandomEnabled ? "Random" : "Fixed";
            preferences.RotationIntervalMinutes = oldInterval;
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
        _applyingRandomSelection = true;
        try
        {
            _modeSelector.SelectedIndex = GetDifferentRandomIndex(_modeSelector.SelectedIndex, _modeSelector.Items.Count, _random);
            _styleSelector.SelectedItem = GetDifferentRandomStyle(_visualMode, _visualStyle, _random);
            _themeSelector.SelectedIndex = GetDifferentRandomIndex(_themeSelector.SelectedIndex, _themeSelector.Items.Count, _random);
        }
        finally
        {
            _applyingRandomSelection = false;
        }

        SaveVisualPreferences(automatic: true);
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
        var preferences = VisualizerPreferences.Default;
        preferences.Mode = _visualMode;
        preferences.Style = GetAppliedVisualStyle(_visualMode, _visualStyle);
        preferences.Theme = _barTheme;
        preferences.RotationMode = _randomRotationEnabled ? "Random" : "Fixed";
        preferences.RotationIntervalMinutes = _rotationIntervalMinutes;
        try
        {
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
                Visible = IsAdvancedMode(visualMode)
            };
            ambiance_ThemeSpectrum.Controls.Add(_advancedVisualizer);

            _waveVisualizer = new WaveSpectrumControl(BAR_COUNT)
            {
                Visible = IsWaveMode(visualMode)
            };
            _waveVisualizer.SetTheme(theme);
            _waveVisualizer.Style = VisualStyles.Parse(GetAppliedVisualStyle(_visualMode, _visualStyle));
            ambiance_ThemeSpectrum.Controls.Add(_waveVisualizer);

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
        RecalculateBarLayout();
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

        var scaleY = (float)ch / 378f;
        var dbLabelH = Math.Max(0, GetMaxDbAxisLabelHeight());
        var dbLabelHalf = (dbLabelH + 1) / 2;
        var baseStartY = Math.Max(0, (int)Math.Round(50f * scaleY));
        var startY = Math.Max(0, baseStartY + dbLabelHalf);
        var labelH = Math.Max(10, (int)Math.Round(AXIS_LABEL_HEIGHT * scaleY));
        var bottomReserve = Math.Max(0, (int)Math.Round(8f * scaleY)) + dbLabelHalf + AXIS_LABEL_MIN_GAP + labelH;
        var barH   = Math.Max(10, ch - startY - bottomReserve);
        var axisLabelY = startY + barH + dbLabelHalf + AXIS_LABEL_MIN_GAP;

        // Scale the side margins as needed to fit the dB labels without overlapping the bars.
        var baseMarginX = Math.Max(4, (int)Math.Round(11f * (float)cw / 1184f));
        var dbLabelReserve = GetMaxDbAxisLabelWidth() + 2 * AXIS_LABEL_MIN_GAP;
        var marginX = Math.Max(baseMarginX, dbLabelReserve);

        // Use a fractional stride so all 83 bars fit without overflow.
        var availW  = cw - 2 * marginX;
        var strideF = (float)availW / BAR_COUNT;
        var advancedMode = IsAdvancedMode(_visualMode);
        var waveMode = IsWaveMode(_visualMode);

        ambiance_ThemeSpectrum.SuspendLayout();
        try
        {
            for (var i = 0; i < BAR_COUNT; i++)
            {
                var x     = marginX + (int)(i       * strideF);
                var nextX = marginX + (int)((i + 1) * strideF);
                _progressBars[i].Location = new Point(x, startY);
                _progressBars[i].Size     = new Size(Math.Max(2, nextX - x), barH);
                _progressBars[i].Visible = !advancedMode && !waveMode;
            }

            _advancedVisualizer.Location = new Point(marginX, startY);
            _advancedVisualizer.Size = new Size(Math.Max(1, availW), barH);
            _advancedVisualizer.Visible = advancedMode;
            _advancedVisualizer.SetBandPlan(_analyzer?.CurrentBandPlan ?? s_defaultPlan);
            _waveVisualizer.Location = new Point(marginX, startY);
            _waveVisualizer.Size = new Size(Math.Max(1, availW), barH);
            _waveVisualizer.Visible = waveMode;
            LayoutAxisLabels(marginX, strideF, axisLabelY, cw);

            if (advancedMode)
            {
                HideDbAxisLabels(_dbAxisLabelsLeft);
                HideDbAxisLabels(_dbAxisLabelsRight);
                HideDbAxisLabels(_dbAxisLabelsLeftMirror);
                HideDbAxisLabels(_dbAxisLabelsRightMirror);
                if (_visualMode.Equals("Radial Spectrum", StringComparison.OrdinalIgnoreCase)
                    || _visualMode.Equals("Note Map", StringComparison.OrdinalIgnoreCase))
                    HideFrequencyAxisLabels();
            }
            else
            {
                var fillPadding = _progressBars.Length > 0 ? _progressBars[0].BrickPadding : 0;
                var fillTop = startY + fillPadding;
                var fillH = Math.Max(1, barH - 2 * fillPadding);
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

        var plan = _analyzer?.CurrentBandPlan ?? s_defaultPlan;
        _layoutPlan = plan;

        var previousRight = int.MinValue;
        for (var i = 0; i < _axisLabels.Length; i++)
        {
            var label = _axisLabels[i];
            var hz = s_axisLandmarksHz[i];

            // Omit frequencies outside the analysis range, such as those above Nyquist.
            var inRange = hz >= plan.MinHz && hz <= plan.MaxHz;

            // Match the bar mapping: bar i spans [i, i+1) in plan-position units.
            var centerX = marginX + (int)Math.Round(plan.FrequencyToPosition(hz) * strideF);
            var w = label.PreferredWidth;
            var left = Math.Max(0, Math.Min(containerWidth - w, centerX - (w / 2)));

            // Skip labels that would overlap on narrow windows.
            var visible = inRange && left >= previousRight + AXIS_LABEL_MIN_GAP;
            label.Visible = visible;
            if (!visible) continue;

            label.Location = new Point(left, labelY);
            previousRight = left + w;
        }
    }

    // dB-axis labels use LevelScale.Normalize so each label aligns with its bar height (0 dBFS to -72 dBFS).
    private void LayoutDbAxisLabels(int marginX, int startY, int barH, int containerWidth, int topBound, int bottomBound)
    {
        if (_dbAxisLabelsLeft == null || _dbAxisLabelsRight == null) return;

        var previousBottomLeft = int.MinValue;
        var previousBottomRight = int.MinValue;

        for (var i = 0; i < s_axisLandmarksDb.Length; i++)
        {
            var db = s_axisLandmarksDb[i];
            var norm = LevelScale.Normalize(db);
            var centerY = startY + (int)Math.Round((1.0 - norm) * barH);

            var left = _dbAxisLabelsLeft[i];
            var top = centerY - left.Height / 2;
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
        }
    }

    // Center and Mirror fill around the midpoint in opposite directions, so their dB labels follow the same mapping.
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

    // Center and Mirror labels are placed symmetrically by normalized distance from the bar's midpoint.
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

        // Sort from the edges inward so collision checks only need the previously placed neighbor.
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

            // Add the lower label only when its position differs from the upper one.
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
        if (InvokeRequired)
        {
            Invoke(new Action(() => OnDisplaySettingsChanged(sender, e)));
            return;
        }

        var screen = Screen.FromControl(this);
        var wa = screen.WorkingArea;

        var newLeft = Math.Max(wa.Left, Math.Min(Left, wa.Right  - Width));
        var newTop  = Math.Max(wa.Top,  Math.Min(Top,  wa.Bottom - Height));
        if (newLeft != Left || newTop != Top)
            Location = new Point(newLeft, newTop);

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
            // Keep only the latest frame and post at most one UI update at a time.
            var count = Math.Min(spectrum.Count, BAR_COUNT);
            for (var i = 0; i < count; i++)
            {
                var v = spectrum[i];
                _spectrumBuffer[i] = v < NOISE_GATE_THRESHOLD ? (byte)0 : v;
            }
            for (var i = count; i < BAR_COUNT; i++)
                _spectrumBuffer[i] = 0;

            postNeeded = !_updatePending;
            _updatePending = true;
        }

        if (!postNeeded) return;

        if (InvokeRequired)
            _ = BeginInvoke(new Action(ApplySpectrumToUI));
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
        }

        // A queued update may arrive after the form has been disposed.
        if (_isDisposed || _progressBars == null) return;

        for (var i = 0; i < BAR_COUNT; i++)
        {
            _progressBars[i].SetTargetValueUI(_applyBuffer[i]);
        }

        if (IsAdvancedMode(_visualMode))
            _advancedVisualizer.SetSpectrum(_applyBuffer);
        else if (IsWaveMode(_visualMode))
            _waveVisualizer.SetSpectrum(_applyBuffer);

        var plan = _analyzer?.CurrentBandPlan;
        if (plan != null && !ReferenceEquals(plan, _layoutPlan))
            RecalculateBarLayout();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyColorThemeOptimized(VerticalProgressBar progress, BarColorTheme theme)
    {
        progress.HeatLowColor = theme.Low;
        progress.HeatMidColor = theme.Mid;
        progress.HeatHighColor = theme.High;
        progress.HeatPeakColor = theme.Peak;
        progress.HeatIntensityCurve = theme.IntensityCurve;
        progress.PeakColorMatchesHeat = true;
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
        _waveVisualizer?.Dispose();
        _waveVisualizer = null;
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