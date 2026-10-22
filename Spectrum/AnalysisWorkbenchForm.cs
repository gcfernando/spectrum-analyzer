// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Spectrum;

/// <summary>A separate measurement workspace; the original visualizer keeps its settings and signal path.</summary>
internal sealed class AnalysisWorkbenchForm : Form
{
    private VisualTheme _presentation = BarColorThemes.Studio.Ui;
    private Color Canvas => _presentation.Canvas;
    private Color Surface => _presentation.Surface;
    private Color Field => _presentation.ControlSurface;
    private Color Primary => _presentation.PrimaryText;
    private Color Secondary => _presentation.SecondaryText;
    private Color Accent => _presentation.Focus;
    private readonly AnalysisWorkspace _workspace;
    private readonly Analyzer _analyzer;
    private readonly Font _bodyFont = new("Segoe UI", 9f);
    private readonly Font _headingFont = new("Segoe UI Semibold", 15f, FontStyle.Bold);
    private readonly Font _sectionFont = new("Segoe UI Semibold", 9f, FontStyle.Bold);
    private readonly Font _metricFont = new("Segoe UI Semibold", 12f, FontStyle.Bold);
    private readonly ToolTip _tips = new() { AutoPopDelay = 16000, InitialDelay = 400, ReshowDelay = 200 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 100 };
    private readonly List<Control> _busyControls = new();
    private readonly Dictionary<string, Label> _metrics = new(StringComparer.Ordinal);
    private readonly ComboBox _source;
    private readonly ComboBox _floor;
    private readonly ComboBox _response;
    private readonly ComboBox _channel;
    private readonly NumericUpDown _minimumHz;
    private readonly NumericUpDown _maximumHz;
    private readonly NumericUpDown _average;
    private readonly NumericUpDown _smoothing;
    private readonly NumericUpDown _peakHold;
    private readonly NumericUpDown _history;
    private readonly NumericUpDown _selectionStart;
    private readonly NumericUpDown _selectionDuration;
    private readonly TextBox _workspaceName;
    private readonly Label _sourceDetails;
    private readonly Label _state;
    private readonly Label _status;
    private readonly Label _inspection;
    private readonly Label _reference;
    private readonly Label _fileSelection;
    private readonly Label _error;
    private readonly Button _analyzeSelection;
    private readonly AnalysisPlotControl _spectrum;
    private readonly AnalysisPlotControl _spectrogram;
    private readonly AnalysisPlotControl _phase;
    private readonly TabControl _plotTabs;
    private TableLayoutPanel _modernBody;
    private FlowLayoutPanel _settingsRail;
    private FlowLayoutPanel _sessionPanel;
    private FlowLayoutPanel _comparePanel;
    private FlowLayoutPanel _advancedPanel;
    private FlowLayoutPanel _fileSection;
    private FlowLayoutPanel _measurementRail;
    private FlowLayoutPanel _errorPanel;
    private TextBox _errorDetails;
    private Button _errorDetailsToggle;
    private Button _sessionToggle;
    private Button _compareToggle;
    private Button _settingsToggle;
    private Button _detailsToggle;
    private Button _liveContext;
    private Button _fileContext;
    private Button _sourceContext;
    private ComboBox _workspaceSelector;
    private CheckBox _referenceOverlay;
    private TableLayoutPanel _rootLayout;
    private TableLayoutPanel _plotWorkspace;
    private TableLayoutPanel _comparisonPlots;
    private AnalysisPlotControl _comparisonCurrent;
    private AnalysisPlotControl _comparisonReference;
    private AnalysisPlotControl _activeComparisonInspection;
    private ComboBox _layoutSelector;
    private bool _embedded;
    private bool _comparisonView;
    private Form _detailsWindow;
    private bool _presentationGeometryInitialized;
    private readonly Dictionary<string, Label> _inspectionValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _recentWorkspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FlowLayoutPanel> _collapsibleContents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _collapsibleHeaders = new(StringComparer.Ordinal);
    private string _audioPath;
    private string _lastSuccessfulAudioPath;
    private bool _lastFileFailed;
    private int _refreshNeeded = 1;
    private bool _readingControls;
    private volatile bool _busy;
    private volatile bool _closing;

    internal AnalysisWorkbenchForm(AnalysisWorkspace workspace, Analyzer analyzer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _analyzer = analyzer;
        Name = "analysisWorkbench";
        Text = "Spectrum · Analysis Workbench";
        Icon = Properties.Resources.Spectrum;
        AccessibleName = "Spectrum Analysis Workbench";
        AccessibleDescription = "Inspect measured audio, compare references, analyze files, and save measurements.";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(7f, 15f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1200, 800);
        MinimumSize = new Size(920, 650);
        BackColor = Canvas;
        ForeColor = Primary;
        Font = _bodyFont;
        KeyPreview = true;
        AllowDrop = true;
        DragEnter += FileDragEnter;
        DragOver += FileDragEnter;
        DragDrop += async (_, args) => await OpenDroppedAudioAsync(args);

        var root = new TableLayoutPanel
        {
            Name = "analysisWorkbenchLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(16, 12, 16, 10), BackColor = Canvas
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        Controls.Add(root);

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "Analysis Workbench", Font = _headingFont, AutoSize = true, ForeColor = Primary, Margin = Padding.Empty }, 0, 0);
        heading.Controls.Add(new Label { Text = "Measure · inspect · compare", AutoSize = true, ForeColor = Secondary, Margin = new Padding(1, 4, 0, 0) }, 0, 1);
        _state = new Label { Name = "analysisSourceState", Text = "WAITING FOR AUDIO", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Accent, Font = _sectionFont };
        heading.Controls.Add(_state, 1, 0);
        heading.SetRowSpan(_state, 2);
        root.Controls.Add(heading, 0, 0);

        var commands = new FlowLayoutPanel
        {
            Name = "analysisCommands", Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 12)
        };
        commands.Controls.Add(Command("analysisOpenAudio", "Open audio…", async () => await SelectFileContextAsync(), "Choose File context, or open a supported audio file."));
        commands.Controls.Add(Command("analysisResumeLive", "Resume live", () => Execute(() => { _workspace.ResumeLive(); RefreshFromWorkspace(); }, "Live capture resumed"), "Return from an offline file or saved session to live audio."));
        commands.Controls.Add(Command("analysisCaptureReference", "Capture reference", () => Execute(_workspace.CaptureReference, "Reference spectrum captured"), "Save the current measured spectrum for an A/B comparison."));
        commands.Controls.Add(Command("analysisClearReference", "Clear reference", () => Execute(_workspace.ClearReference, "Reference cleared"), "Remove the saved comparison spectrum."));
        commands.Controls.Add(Command("analysisExportCsv", "Export CSV…", ExportCsv, "Export original measured band powers, numerical levels, timestamps, and analysis settings."));
        commands.Controls.Add(Command("analysisSaveSession", "Save session…", SaveSession, "Save measurements and history. This does not save a recording of the audio."));
        commands.Controls.Add(Command("analysisOpenSession", "Open session…", OpenSession, "Inspect a saved measurement session; live capture continues separately."));
        root.Controls.Add(commands, 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 228));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 2);

        var settings = Rail("analysisSettingsRail");
        body.Controls.Add(settings, 0, 0);
        var sourceSection = Section(settings, "Audio source");
        _source = Choice("analysisSourceSelector", "Audio capture source");
        Add(sourceSection, _source);
        var sourceRefresh = Command("analysisRefreshSources", "Refresh sources", RefreshSources, "Refresh available output loopback and input devices.");
        sourceRefresh.Width = 202;
        Add(sourceSection, sourceRefresh);
        _sourceDetails = Note("analysisSourceDetails", "Waiting for measured audio");
        Add(sourceSection, _sourceDetails);
        _channel = Choice("analysisChannelSelector", "Analyzed channel", "Combined", "Left", "Right", "Mid", "Side");
        AddLabeled(sourceSection, "Channel", _channel);

        var range = Section(settings, "Spectrum range");
        _floor = Choice("analysisFloorDb", "Display floor in dBFS", "-60", "-72", "-90", "-120");
        AddLabeled(range, "Display floor · dBFS", _floor);
        _minimumHz = Number("analysisMinimumHz", "Minimum displayed frequency in hertz", 1, 384000, 20, 0, 10);
        _maximumHz = Number("analysisMaximumHz", "Maximum displayed frequency in hertz", 2, 384000, 20000, 0, 100);
        AddLabeled(range, "From · Hz", _minimumHz);
        AddLabeled(range, "To · Hz", _maximumHz);
        var navigation = new FlowLayoutPanel { AutoSize = true, Width = 207, Margin = Padding.Empty, WrapContents = true };
        navigation.Controls.Add(SmallCommand("analysisPanLower", "←", () => Navigate(() => _spectrum.Pan(-.25)), "Pan toward lower frequencies"));
        navigation.Controls.Add(SmallCommand("analysisZoomIn", "+", () => Navigate(() => _spectrum.Zoom(2)), "Zoom in on the current frequency range"));
        navigation.Controls.Add(SmallCommand("analysisZoomOut", "−", () => Navigate(() => _spectrum.Zoom(.5)), "Zoom out from the current frequency range"));
        navigation.Controls.Add(SmallCommand("analysisPanHigher", "→", () => Navigate(() => _spectrum.Pan(.25)), "Pan toward higher frequencies"));
        navigation.Controls.Add(Command("analysisResetZoom", "Full range", () => Navigate(_spectrum.ResetZoom), "Reset to the full supported frequency range."));
        Add(range, navigation);
        Add(range, Note("analysisScaleExplanation", "The scale changes visibility, not audio volume."));

        var response = Section(settings, "Analysis response");
        _response = Choice("analysisResponsePreset", "Analysis response preset", "Fast", "Balanced", "Detailed");
        AddLabeled(response, "Response preset", _response);
        _average = Number("analysisAveragingMs", "Power averaging time in milliseconds", 0, 10000, 200, 0, 50);
        _smoothing = Number("analysisSmoothingBands", "Smoothing radius in neighboring bands", 0, 8, 1, 0, 1);
        _peakHold = Number("analysisPeakHoldMs", "Spectrum peak hold in milliseconds", 0, 60000, 500, 0, 100);
        _history = Number("analysisHistorySeconds", "Spectrogram history duration in seconds", 2, 120, 30, 0, 5);
        AddLabeled(response, "Average · ms", _average);
        AddLabeled(response, "Smooth · bands", _smoothing);
        AddLabeled(response, "Peak hold · ms", _peakHold);
        AddLabeled(response, "History · seconds", _history);
        Add(response, Note("analysisResponseExplanation", "Fast follows transients. Detailed uses longer averaging. Smoothing combines neighboring bands."));

        var file = Section(settings, "File selection");
        _fileSelection = Note("analysisSelectedFile", "Open an audio file to analyze a selected section.");
        Add(file, _fileSelection);
        _selectionStart = Number("analysisSelectionStartSeconds", "Start time of file selection in seconds", 0, 86400, 0, 2, 1);
        _selectionDuration = Number("analysisSelectionDurationSeconds", "Duration of file selection in seconds", .1m, 600, 30, 2, 1);
        AddLabeled(file, "Start · seconds", _selectionStart);
        AddLabeled(file, "Duration · seconds", _selectionDuration);
        _analyzeSelection = Command("analysisAnalyzeSelection", "Analyze selection", async () => await AnalyzeSelectionAsync(), "Reanalyze the selected time range of the opened audio file.");
        _analyzeSelection.Width = 202;
        _analyzeSelection.Enabled = false;
        Add(file, _analyzeSelection);

        var custom = Section(settings, "Custom workspace");
        _workspaceName = new TextBox
        {
            Name = "analysisWorkspaceName", AccessibleName = "Custom workspace name", Width = 202,
            MaxLength = 80, BackColor = Field, ForeColor = Primary, BorderStyle = BorderStyle.FixedSingle
        };
        AddLabeled(custom, "Name", _workspaceName);
        var presetCommands = new FlowLayoutPanel { AutoSize = true, Width = 207, Margin = Padding.Empty };
        var saveWorkspace = Command("analysisSaveWorkspace", "Save…", SaveWorkspace, "Save a named workspace with its analysis and display settings.");
        var loadWorkspace = Command("analysisLoadWorkspace", "Load…", LoadWorkspace, "Import a saved workspace from another session or computer.");
        saveWorkspace.Width = loadWorkspace.Width = 96;
        presetCommands.Controls.Add(saveWorkspace);
        presetCommands.Controls.Add(loadWorkspace);
        Add(custom, presetCommands);

        var center = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = new Padding(10, 0, 10, 0) };
        center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        body.Controls.Add(center, 1, 0);
        _reference = new Label { Name = "analysisReferenceStatus", Dock = DockStyle.Fill, ForeColor = Secondary, Text = "REFERENCE  None captured", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        center.Controls.Add(_reference, 0, 0);
        _plotTabs = new ModernAnalysisTabControl { Name = "analysisPlotTabs", Dock = DockStyle.Fill, AccessibleName = "Spectrum, spectrogram, and stereo phase views" };
        var spectrumTab = new TabPage("Spectrum") { BackColor = Canvas, Padding = new Padding(1) };
        var spectrogramTab = new TabPage("Spectrogram") { BackColor = Canvas, Padding = new Padding(1) };
        var phaseTab = new TabPage("Stereo phase") { BackColor = Canvas, Padding = new Padding(1) };
        _spectrum = new AnalysisPlotControl(_workspace) { Name = "analysisSpectrumPlot", Dock = DockStyle.Fill, ShowSpectrogram = false, AccessibleName = "Measured frequency spectrum", TabStop = true };
        _spectrogram = new AnalysisPlotControl(_workspace) { Name = "analysisSpectrogramPlot", Dock = DockStyle.Fill, ShowSpectrogram = true, AccessibleName = "Sample-timed spectrogram", TabStop = true };
        _phase = new AnalysisPlotControl(_workspace) { Name = "analysisPhasePlot", Dock = DockStyle.Fill, ShowPhase = true, AccessibleName = "Stereo phase and width display", TabStop = true };
        spectrumTab.Controls.Add(_spectrum);
        spectrogramTab.Controls.Add(_spectrogram);
        phaseTab.Controls.Add(_phase);
        _plotTabs.TabPages.Add(spectrumTab);
        _plotTabs.TabPages.Add(spectrogramTab);
        _plotTabs.TabPages.Add(phaseTab);
        foreach (var dropTarget in new Control[] { root, commands, body, center, _plotTabs, spectrumTab, spectrogramTab, phaseTab, _spectrum, _spectrogram, _phase })
        {
            dropTarget.AllowDrop = true;
            dropTarget.DragEnter += FileDragEnter;
            dropTarget.DragOver += FileDragEnter;
            dropTarget.DragDrop += async (_, args) => await OpenDroppedAudioAsync(args);
        }
        center.Controls.Add(_plotTabs, 0, 1);
        _inspection = new Label
        {
            Name = "analysisBandInspector", AccessibleName = "Exact band measurement", Dock = DockStyle.Fill,
            ForeColor = Primary, BackColor = Surface, Padding = new Padding(10, 8, 10, 6),
            Text = "Point to a band for its exact frequency range and measured level. Click to pin the inspection."
        };
        center.Controls.Add(_inspection, 0, 2);
        _spectrum.InspectionChanged += () => { if (_plotTabs.SelectedIndex == 0) UpdateInspection(_spectrum); };
        _spectrogram.InspectionChanged += () => { if (_plotTabs.SelectedIndex == 1) UpdateInspection(_spectrogram); };
        _phase.InspectionChanged += () => { if (_plotTabs.SelectedIndex == 2) UpdateInspection(_phase); };

        var measurements = Rail("analysisMeasurementRail");
        body.Controls.Add(measurements, 2, 0);
        var levels = Section(measurements, "Digital levels");
        AddMetric(levels, "samplePeak", "Sample peak", "dBFS");
        AddMetric(levels, "truePeak", "Estimated true peak", "dBTP");
        AddMetric(levels, "leftRms", "Left RMS", "dBFS");
        AddMetric(levels, "rightRms", "Right RMS", "dBFS");
        AddMetric(levels, "clipped", "Overload samples", "");
        var stereo = Section(measurements, "Stereo relationships");
        AddMetric(stereo, "correlation", "Correlation", "−1 … +1");
        AddMetric(stereo, "mid", "Mid RMS", "dBFS");
        AddMetric(stereo, "side", "Side RMS", "dBFS");
        Add(stereo, Note("analysisStereoExplanation", "+1: matching channels\n0: uncorrelated\n−1: opposite polarity"));
        var loudness = Section(measurements, "Loudness");
        AddMetric(loudness, "momentary", "Momentary · 400 ms", "LUFS");
        AddMetric(loudness, "shortTerm", "Short-term · 3 seconds", "LUFS");
        AddMetric(loudness, "integrated", "Integrated · since reset", "LUFS");
        Add(loudness, Command("analysisResetMeasurements", "Clear analysis", () => Execute(_workspace.Reset, "Analysis cleared"), "Clear measurements, the reference, and accumulated history. Reanalyze the file selection to restore an offline analysis."));
        Add(loudness, Note("analysisMeasurementExplanation", "Digital audio measurements. These values are not calibrated sound-pressure levels. A dash means unavailable or still warming up."));

        _error = new Label { Name = "analysisError", AutoSize = true, MaximumSize = new Size(1100, 0), ForeColor = Color.FromArgb(255, 184, 144), Visible = false, Margin = new Padding(0, 8, 0, 2) };
        root.Controls.Add(_error, 0, 3);
        _status = new Label { Name = "analysisStatus", Dock = DockStyle.Fill, ForeColor = Secondary, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Waiting for measured audio…" };
        root.Controls.Add(_status, 0, 4);

        _floor.SelectedIndexChanged += (_, _) => ApplySettings();
        _channel.SelectedIndexChanged += (_, _) => ApplySettings();
        _minimumHz.ValueChanged += (_, _) => ApplySettings();
        _maximumHz.ValueChanged += (_, _) => ApplySettings();
        _average.ValueChanged += (_, _) => ApplySettings();
        _smoothing.ValueChanged += (_, _) => ApplySettings();
        _peakHold.ValueChanged += (_, _) => ApplySettings();
        _history.ValueChanged += (_, _) => ApplySettings();
        _response.SelectedIndexChanged += (_, _) => ApplyResponsePreset();
        _source.SelectedIndexChanged += (_, _) => SelectSource();
        _workspaceName.TextChanged += (_, _) => { if (!_readingControls) _workspace.WorkspaceName = _workspaceName.Text.Trim(); };
        _plotTabs.SelectedIndexChanged += (_, _) =>
        {
            if (!_readingControls) _workspace.PreferredView = SelectedView;
            UpdateInspection(_plotTabs.SelectedIndex == 2 ? _phase : _plotTabs.SelectedIndex == 1 ? _spectrogram : _spectrum);
        };
        _workspace.Changed += WorkspaceChanged;
        _refreshTimer.Tick += (_, _) => { if (Interlocked.Exchange(ref _refreshNeeded, 0) != 0) RefreshFromWorkspace(); };
        Shown += (_, _) => { RefreshSources(); RefreshFromWorkspace(); _refreshTimer.Start(); };
        FormClosing += (_, _) => _closing = true;
        VisibleChanged += (_, _) =>
        {
            if (_closing || !_embedded || _detailsWindow == null) return;
            if (!Visible) _detailsWindow.Hide();
            else if (_workspace.DetailsVisible) PresentDetails();
        };
        DpiChanged += (_, _) => QueuePresentationMeasure();
        DpiChangedAfterParent += (_, _) => QueuePresentationMeasure();
        HandleCreated += (_, _) => QueuePresentationMeasure();
        ModernizeLayout();
        ReadSettings();
        ReadWorkspacePresentation();
        RefreshFromWorkspace();
        ApplyPresentation(_presentation);
    }

    internal void ApplyPresentation(VisualTheme theme)
    {
        _presentation = theme;
        SharedPresentation.Apply(this, theme);
        if (_detailsWindow != null) SharedPresentation.Apply(_detailsWindow, theme);
        ((ModernAnalysisTabControl)_plotTabs).ApplyPresentation(theme);
        foreach (var header in _collapsibleHeaders.Values) header.BackColor = theme.RaisedSurface;
        _reference.ForeColor = theme.SecondaryText;
        _state.ForeColor = theme.Focus;
        RefreshFromWorkspace();
    }

    internal void SetEmbedded(bool embedded)
    {
        _embedded = embedded;
        _presentationGeometryInitialized = true;
        ApplyEmbeddedGeometry();
        if (embedded && _workspace.LayoutName == "Studio") ApplyLayout("Compact");
        ReadLayout();
        PerformLayout();
    }

    private void QueuePresentationMeasure()
    {
        if (!_presentationGeometryInitialized || _closing || IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)(() =>
        {
            if (_closing || IsDisposed || _rootLayout == null) return;
            ApplyEmbeddedGeometry(); ReadLayout(); PerformLayout();
        }));
    }

    private void ApplyEmbeddedGeometry()
    {
        var embedded = _embedded;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        int Px(int value) => (int)Math.Round(value * scale);
        Padding Pad(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));
        MinimumSize = embedded ? Size.Empty : new Size(Px(920), Px(650));
        _rootLayout.Padding = embedded ? Pad(4, 4, 4, 4) : Pad(14, 10, 14, 8);
        Controls.Find("analysisHeader", true)[0].Visible = !embedded;
        _status.Visible = !embedded;
        _rootLayout.RowStyles[7].Height = embedded ? 0 : Px(25);
        var commandRow = Controls.Find("analysisCommands", true)[0];
        foreach (Control child in commandRow.Controls) if (child is Label) child.Visible = !embedded;
        commandRow.Padding = embedded ? Padding.Empty : Pad(0, 0, 0, 5);
        foreach (Control child in commandRow.Controls)
        {
            if (child is Button) { child.Width = Px(104); child.Height = Px(embedded ? 27 : 31); child.Margin = Pad(0, 0, 7, embedded ? 3 : 6); }
        }
        _layoutSelector.Width = Px(122); _layoutSelector.Margin = Pad(7, 3, 0, 3);
        var referenceCard = Controls.Find("analysisComparisonCard", true)[0];
        referenceCard.Padding = embedded ? Pad(7, 1, 7, 0) : Pad(9, 5, 9, 2);
        referenceCard.Margin = Pad(0, 0, 0, embedded ? 2 : 7);
        foreach (Control child in _comparePanel.Controls)
        { child.Width = Px(120); child.Height = Px(embedded ? 25 : 31); child.Margin = Pad(0, 0, 7, embedded ? 2 : 6); }
        var ranges = Controls.Find("analysisRangeShortcuts", true)[0];
        ranges.Margin = Pad(0, 0, 0, embedded ? 0 : 4);
        foreach (Control child in ranges.Controls)
        {
            var logicalWidth = child.Name == "analysisRangeVocals" ? 68 : child.Name == "analysisResetZoom" ? 86
                : child.Name.StartsWith("analysisRange", StringComparison.Ordinal) ? 60 : 34;
            child.Width = Px(logicalWidth); child.Height = Px(embedded ? 25 : 29); child.Margin = Pad(0, 0, 5, embedded ? 2 : 4);
        }
        var inspector = Controls.Find("analysisInspectionCard", true)[0];
        inspector.Padding = embedded ? Pad(7, 2, 7, 0) : Pad(10, 7, 10, 5);
        Controls.Find("analysisInspectionFields", true)[0].Visible = !embedded;
        _inspection.Height = Px(embedded ? 18 : 23);
        _plotTabs.ItemSize = embedded ? new Size(Px(106), Px(24)) : new Size(Px(132), Px(34));
        foreach (var plot in new[] { _spectrum, _spectrogram, _phase, _comparisonCurrent, _comparisonReference }) plot.Compact = embedded;
        var meterStrip = Controls.Find("analysisEssentialMeters", true)[0];
        meterStrip.Margin = Pad(0, embedded ? 4 : 9, 0, 0);
        foreach (TableLayoutPanel card in meterStrip.Controls)
        {
            card.Height = Px(embedded ? 48 : 66);
            card.Padding = embedded ? Pad(8, 3, 8, 3) : Pad(10, 6, 10, 5);
            card.RowStyles[0].Height = Px(embedded ? 18 : 20);
        }
        _comparisonPlots.RowStyles[0].Height = Px(19);
    }

    internal void SelectWorkspaceView(string view)
    {
        if (!string.Equals(view, "Analyze", StringComparison.OrdinalIgnoreCase) && !string.Equals(view, "Compare", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose Analyze or Compare.", nameof(view));
        ReadSettings(); ReadWorkspacePresentation();
        _comparisonView = string.Equals(view, "Compare", StringComparison.OrdinalIgnoreCase);
        _plotTabs.Visible = !_comparisonView;
        _comparisonPlots.Visible = _comparisonView;
        _comparePanel.Visible = _comparisonView;
        _reference.Visible = !_comparisonView;
        _referenceOverlay.Visible = !_comparisonView;
        var referenceCard = (TableLayoutPanel)Controls.Find("analysisComparisonCard", true)[0];
        referenceCard.RowStyles[0].SizeType = _comparisonView ? SizeType.Absolute : SizeType.AutoSize;
        referenceCard.RowStyles[0].Height = 0;
        if (_comparisonView) { _workspace.ReferenceVisible = true; _referenceOverlay.Checked = true; }
        RefreshFromWorkspace(); PerformLayout();
    }

    private void ApplyLayout(string name)
    {
        _workspace.LayoutName = name;
        _workspace.SettingsVisible = name != "Compact";
        _workspace.DetailsVisible = name == "Measurement";
        _workspace.SettingsWidth = name == "Measurement" ? 260 : 238;
        ReadLayout();
    }

    private void ReadLayout()
    {
        var scale = Math.Max(1f, _source.Width / 202f);
        _workspace.SettingsWidth = Math.Max(238, Math.Min(420, _workspace.SettingsWidth));
        _settingsRail.Visible = _workspace.SettingsVisible;
        // During construction a child reports Visible=false while its form is hidden.
        _modernBody.ColumnStyles[0].Width = _workspace.SettingsVisible ? _workspace.SettingsWidth * scale : 0;
        _modernBody.ColumnStyles[1].Width = _workspace.SettingsVisible ? 6 * scale : 0;
        PresentDetails();
        _settingsToggle.BackColor = _workspace.SettingsVisible ? _presentation.Selection : Field;
        _detailsToggle.BackColor = _workspace.DetailsVisible ? _presentation.Selection : Field;
        var previous = _readingControls; _readingControls = true;
        try { _layoutSelector.SelectedItem = _workspace.LayoutName; }
        finally { _readingControls = previous; }
    }

    private void ModernizeLayout()
    {
        // Reuse the established inputs and command handlers, replacing only their presentation.
        var oldLayout = Controls[0];
        Button OldButton(string name) => (Button)Controls.Find(name, true)[0];
        var open = OldButton("analysisOpenAudio");
        var live = OldButton("analysisResumeLive");
        var capture = OldButton("analysisCaptureReference");
        var clearReference = OldButton("analysisClearReference");
        var export = OldButton("analysisExportCsv");
        var saveSession = OldButton("analysisSaveSession");
        var openSession = OldButton("analysisOpenSession");
        var saveWorkspace = OldButton("analysisSaveWorkspace");
        var loadWorkspace = OldButton("analysisLoadWorkspace");
        var refreshSource = OldButton("analysisRefreshSources");
        var panLower = OldButton("analysisPanLower");
        var zoomIn = OldButton("analysisZoomIn");
        var zoomOut = OldButton("analysisZoomOut");
        var panHigher = OldButton("analysisPanHigher");
        var resetZoom = OldButton("analysisResetZoom");
        var resetMeasurements = OldButton("analysisResetMeasurements");
        SuspendLayout();
        try
        {
            var root = new TableLayoutPanel
            {
                Name = "analysisWorkbenchLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
                BackColor = Canvas, Padding = new Padding(14, 10, 14, 8), Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            Controls.Add(root);
            _rootLayout = root;

            var header = Horizontal("analysisHeader");
            header.Padding = new Padding(0, 0, 0, 5);
            header.Controls.Add(new Label
            {
                Text = "Analysis Workbench", Font = _headingFont, ForeColor = Primary, AutoSize = true,
                Margin = new Padding(0, 3, 18, 4), AccessibleName = "Analysis Workbench"
            });
            _liveContext = live; _fileContext = open;
            live.Text = "Live"; live.Width = 63;
            live.AccessibleName = "Use live audio";
            open.Text = "File"; open.Width = 63;
            open.AccessibleName = "Use an audio file";
            header.Controls.Add(live); header.Controls.Add(open);
            _sourceContext = Command("analysisSourceContext", "Windows default output ▾", ShowContextSource,
                "Choose the live capture source, or change the audio file in File context.");
            _sourceContext.Width = 190;
            _sourceContext.AutoEllipsis = true;
            header.Controls.Add(_sourceContext);
            _workspaceSelector = Choice("analysisWorkspaceSelector", "Current or saved analysis workspace");
            _workspaceSelector.Width = 178;
            _workspaceSelector.Margin = new Padding(6, 3, 9, 6);
            header.Controls.Add(_workspaceSelector);
            _state.Dock = DockStyle.None; _state.AutoSize = false; _state.Size = new Size(148, 31);
            _state.TextAlign = ContentAlignment.MiddleRight;
            _state.Margin = new Padding(2, 0, 0, 6);
            header.Controls.Add(_state);
            root.Controls.Add(header, 0, 0);

            var commands = Horizontal("analysisCommands");
            commands.Padding = new Padding(0, 0, 0, 5);
            _sessionToggle = Command("analysisSessionGroup", "Session ▾", () => TogglePanel(_sessionPanel, _sessionToggle),
                "Open or save measurement sessions, export numerical data, and manage custom workspaces.");
            _compareToggle = Command("analysisCompareGroup", "Compare ▾", () => TogglePanel(_comparePanel, _compareToggle),
                "Capture or clear the reference used for A/B comparison.");
            _settingsToggle = Command("analysisSettingsToggle", "Settings ▾", ToggleSettings,
                "Show or hide the source, spectrum, and response settings.");
            _detailsToggle = Command("analysisDetailsToggle", "Details ▾", () => TogglePanel(_measurementRail, _detailsToggle),
                "Show detailed channel levels, loudness windows, stereo measurements, and measurement help.");
            foreach (var button in new[] { _sessionToggle, _compareToggle, _settingsToggle, _detailsToggle })
            { button.Width = 104; commands.Controls.Add(button); }
            commands.Controls.Add(new Label
            {
                Text = "Drop audio here · Ctrl+O open · Ctrl+L live", AutoSize = true,
                ForeColor = Secondary, Margin = new Padding(12, 8, 0, 4)
            });
            root.Controls.Add(commands, 0, 1);
            _layoutSelector = Choice("analysisLayoutSelector", "Workspace layout", "Compact", "Studio", "Measurement");
            _layoutSelector.Width = 122; _layoutSelector.Margin = new Padding(7, 3, 0, 3);
            commands.Controls.Add(_layoutSelector);
            _layoutSelector.SelectedIndexChanged += (_, _) => { if (!_readingControls && _layoutSelector.SelectedItem is string name) ApplyLayout(name); };

            _sessionPanel = Horizontal("analysisSessionPanel");
            _sessionPanel.BackColor = Surface; _sessionPanel.Padding = new Padding(10, 8, 10, 3);
            foreach (var button in new[] { saveSession, openSession, export }) _sessionPanel.Controls.Add(button);
            _sessionPanel.Controls.Add(new Label { Text = "Workspace", AutoSize = true, ForeColor = Secondary, Margin = new Padding(13, 8, 6, 4) });
            _workspaceName.Width = 190; _workspaceName.Margin = new Padding(0, 4, 7, 6);
            _sessionPanel.Controls.Add(_workspaceName);
            saveWorkspace.Text = "Save workspace…"; saveWorkspace.Width = 132;
            loadWorkspace.Text = "Load workspace…"; loadWorkspace.Width = 132;
            _sessionPanel.Controls.Add(saveWorkspace); _sessionPanel.Controls.Add(loadWorkspace);
            _sessionPanel.Visible = false;
            root.Controls.Add(_sessionPanel, 0, 2);

            _modernBody = new TableLayoutPanel { Name = "analysisFocusedLayout", Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            _modernBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 238));
            _modernBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 6));
            _modernBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _modernBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(_modernBody, 0, 3);
            _settingsRail = Rail("analysisSettingsRail");
            _settingsRail.Padding = new Padding(9, 7, 5, 7);
            _modernBody.Controls.Add(_settingsRail, 0, 0);
            var splitter = new Button { Name = "analysisSettingsResizeHandle", AccessibleName = "Resize analysis settings", AccessibleDescription = "Drag horizontally, or use left and right arrow keys, to resize the settings column.", Dock = DockStyle.Fill, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat, Cursor = Cursors.VSplit, Text = "" };
            var dragging = false; var dragX = 0; var dragWidth = 0f;
            void ResizeSettings(float width)
            {
                var scale = Math.Max(1f, _source.Width / 202f);
                _workspace.SettingsWidth = (int)Math.Max(238, Math.Min(420, width / scale));
                _modernBody.ColumnStyles[0].Width = _workspace.SettingsWidth * scale;
            }
            splitter.MouseDown += (_, args) => { if (args.Button != MouseButtons.Left) return; dragging = true; splitter.Capture = true; dragX = Cursor.Position.X; dragWidth = _modernBody.ColumnStyles[0].Width; };
            splitter.MouseMove += (_, _) => { if (dragging) ResizeSettings(dragWidth + Cursor.Position.X - dragX); };
            splitter.MouseUp += (_, _) => { dragging = false; splitter.Capture = false; };
            splitter.PreviewKeyDown += (_, args) => { if (args.KeyCode == Keys.Left || args.KeyCode == Keys.Right) args.IsInputKey = true; };
            splitter.KeyDown += (_, args) => { if (args.KeyCode == Keys.Left || args.KeyCode == Keys.Right) { ResizeSettings(_modernBody.ColumnStyles[0].Width + (args.KeyCode == Keys.Left ? -10 : 10)); args.Handled = true; } };
            _modernBody.Controls.Add(splitter, 1, 0);
            var source = CollapsibleSection("analysisSourceGroup", "Source & channels", true);
            Add(source, _source);
            var channelRow = new FlowLayoutPanel { AutoSize = true, Width = 210, WrapContents = false, Margin = Padding.Empty };
            _channel.Width = 158; _channel.Margin = new Padding(0, 0, 7, 5);
            refreshSource.Text = "↻"; refreshSource.Width = 36; refreshSource.Height = 25;
            refreshSource.AccessibleName = "Refresh capture devices";
            channelRow.Controls.Add(_channel); channelRow.Controls.Add(refreshSource);
            AddLabeled(source, "Channel", channelRow); Add(source, _sourceDetails);
            var scale = CollapsibleSection("analysisScaleGroup", "Spectrum scale", true);
            AddLabeled(scale, "Display floor · dBFS", _floor);
            var limits = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Width = 202, Margin = Padding.Empty };
            limits.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            limits.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            limits.Controls.Add(new Label { Text = "From · Hz", AutoSize = true, ForeColor = Secondary }, 0, 0);
            limits.Controls.Add(new Label { Text = "To · Hz", AutoSize = true, ForeColor = Secondary }, 1, 0);
            _minimumHz.Width = 96; _maximumHz.Width = 96;
            _minimumHz.Dock = _maximumHz.Dock = DockStyle.Fill;
            limits.Controls.Add(_minimumHz, 0, 1); limits.Controls.Add(_maximumHz, 1, 1);
            Add(scale, limits);
            Add(scale, Note("analysisScaleExplanation", "The display scale changes visibility, not audio volume."));
            var response = CollapsibleSection("analysisResponseGroup", "Analysis response", true);
            Add(response, _response);
            Add(response, Note("analysisResponseTradeoff", "Fast: shorter window, quicker response. Detailed: longer window, finer frequency detail."));
            _advancedPanel = CollapsibleSection("analysisAdvancedPanel", "Advanced", false, "analysisAdvancedToggle");
            AddLabeled(_advancedPanel, "Power average · ms", _average);
            AddLabeled(_advancedPanel, "Smoothing · bands", _smoothing);
            AddLabeled(_advancedPanel, "Peak hold · ms", _peakHold);
            AddLabeled(_advancedPanel, "History · seconds", _history);
            Add(_advancedPanel, Note("analysisResponseExplanation", "Longer averaging steadies the curve. Smoothing combines neighboring bands."));
            _fileSection = CollapsibleSection("analysisFileSection", "File selection", true);
            Add(_fileSection, _fileSelection);
            AddLabeled(_fileSection, "Start · seconds", _selectionStart);
            AddLabeled(_fileSection, "Duration · seconds", _selectionDuration);
            Add(_fileSection, _analyzeSelection);
            var chooseAudio = Command("analysisChooseAudio", "Choose another file…", async () => await OpenAudioAsync(), "Choose a different audio file.");
            chooseAudio.Width = 202; Add(_fileSection, chooseAudio);
            _fileSection.Parent.Visible = false;
            _settingsRail.Controls.SetChildIndex(_fileSection.Parent, 1);

            var center = new TableLayoutPanel { Name = "analysisPlotWorkspace", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(10, 0, 0, 0) };
            _plotWorkspace = center;
            center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _modernBody.Controls.Add(center, 2, 0);
            var comparison = new TableLayoutPanel { Name = "analysisComparisonCard", AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Surface, Padding = new Padding(9, 5, 9, 2), Margin = new Padding(0, 0, 0, 7) };
            comparison.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); comparison.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            comparison.RowStyles.Add(new RowStyle(SizeType.AutoSize)); comparison.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _reference.Dock = DockStyle.Fill; _reference.AutoSize = false; _reference.Height = 25;
            _reference.TextAlign = ContentAlignment.MiddleLeft;
            comparison.Controls.Add(_reference, 0, 0);
            _referenceOverlay = new CheckBox
            {
                Name = "analysisReferenceOverlay", Text = "Overlay", AccessibleName = "Show reference spectrum overlay",
                AutoSize = true, ForeColor = Primary, Checked = true, Margin = new Padding(4, 5, 0, 3)
            };
            comparison.Controls.Add(_referenceOverlay, 1, 0);
            _comparePanel = Horizontal("analysisComparePanel");
            _comparePanel.Controls.Add(capture); _comparePanel.Controls.Add(clearReference); _comparePanel.Visible = false;
            comparison.Controls.Add(_comparePanel, 0, 1); comparison.SetColumnSpan(_comparePanel, 2);
            center.Controls.Add(comparison, 0, 0);

            var ranges = Horizontal("analysisRangeShortcuts");
            ranges.Margin = new Padding(0, 0, 0, 4);
            AddRangeShortcut(ranges, "analysisRangeFull", "Full", 20, 20000);
            AddRangeShortcut(ranges, "analysisRangeBass", "Bass", 20, 250);
            AddRangeShortcut(ranges, "analysisRangeVocals", "Vocals", 250, 4000);
            AddRangeShortcut(ranges, "analysisRangeHigh", "High", 4000, 20000);
            foreach (var button in new[] { panLower, zoomIn, zoomOut, panHigher })
            { button.Width = 34; button.Height = 29; button.Margin = new Padding(0, 0, 5, 4); ranges.Controls.Add(button); }
            resetZoom.Text = "Reset view"; resetZoom.Width = 86; resetZoom.Height = 29;
            ranges.Controls.Add(resetZoom);
            center.Controls.Add(ranges, 0, 1);
            var viewHost = new Panel { Name = "analysisViewsHost", Dock = DockStyle.Fill, Margin = Padding.Empty };
            viewHost.Controls.Add(_plotTabs); center.Controls.Add(viewHost, 0, 2);
            _comparisonPlots = new TableLayoutPanel { Name = "analysisComparisonPlots", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Visible = false, Margin = Padding.Empty };
            _comparisonPlots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _comparisonPlots.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _comparisonPlots.RowStyles.Add(new RowStyle(SizeType.Absolute, 19)); _comparisonPlots.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _comparisonPlots.Controls.Add(new Label { Name = "analysisCompareCurrentTitle", Text = "Current / averaged", Dock = DockStyle.Fill, ForeColor = Primary }, 0, 0);
            _comparisonPlots.Controls.Add(new Label { Name = "analysisCompareReferenceTitle", Text = "Captured reference", Dock = DockStyle.Fill, ForeColor = Primary, AutoEllipsis = true }, 1, 0);
            _comparisonCurrent = new AnalysisPlotControl(_workspace) { Name = "analysisComparisonCurrentPlot", Dock = DockStyle.Fill, AccessibleName = "Current spectrum for comparison" };
            _comparisonReference = new AnalysisPlotControl(_workspace) { Name = "analysisComparisonReferencePlot", Dock = DockStyle.Fill, ReferenceOnly = true, AccessibleName = "Captured reference spectrum" };
            _activeComparisonInspection = _comparisonCurrent;
            foreach (var comparisonPlot in new[] { _comparisonCurrent, _comparisonReference })
            {
                void ActivateInspection() { _activeComparisonInspection = comparisonPlot; if (_comparisonView) UpdateInspection(comparisonPlot); }
                comparisonPlot.MouseMove += (_, _) => ActivateInspection();
                comparisonPlot.MouseDown += (_, _) => ActivateInspection();
                comparisonPlot.Enter += (_, _) => ActivateInspection();
                comparisonPlot.KeyDown += (_, _) => ActivateInspection();
                comparisonPlot.InspectionChanged += () =>
                { if (_comparisonView && _activeComparisonInspection == comparisonPlot) UpdateInspection(comparisonPlot); };
            }
            _comparisonPlots.Controls.Add(_comparisonCurrent, 0, 1); _comparisonPlots.Controls.Add(_comparisonReference, 1, 1);
            viewHost.Controls.Add(_comparisonPlots);
            var inspector = new TableLayoutPanel
            {
                Name = "analysisInspectionCard", AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = Surface, Padding = new Padding(10, 7, 10, 5), Margin = new Padding(0, 7, 0, 0)
            };
            inspector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize)); inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _inspection.Padding = Padding.Empty; _inspection.Height = 23; _inspection.AutoEllipsis = true;
            inspector.Controls.Add(_inspection, 0, 0);
            var fields = new TableLayoutPanel { Name = "analysisInspectionFields", AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2 };
            var inspectionKeys = new[] { "Frequency", "Level", "Peak", "Delta", "State" };
            var inspectionTitles = new[] { "Frequency", "Measured", "Held curve peak", "Reference Δ", "State" };
            for (var i = 0; i < inspectionKeys.Length; i++)
            {
                fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, i == 0 || i == 4 ? 24 : 17.333f));
                fields.Controls.Add(new Label { Text = inspectionTitles[i], ForeColor = Secondary, AutoSize = true, Margin = new Padding(0, 3, 6, 2) }, i, 0);
                var value = new Label { Name = "analysisInspect" + inspectionKeys[i], AccessibleName = inspectionTitles[i], Text = "—", ForeColor = Primary, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 24, MinimumSize = new Size(0, 24), Margin = new Padding(0, 0, 6, 3), Font = _sectionFont };
                fields.Controls.Add(value, i, 1); _inspectionValues.Add(inspectionKeys[i], value);
            }
            inspector.Controls.Add(fields, 0, 1); center.Controls.Add(inspector, 0, 3);

            var essentials = new TableLayoutPanel { Name = "analysisEssentialMeters", Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 9, 0, 0) };
            for (var i = 0; i < 4; i++) essentials.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            AddEssentialMeter(essentials, 0, "samplePeak", "Sample peak", "dBFS");
            AddEssentialMeter(essentials, 1, "truePeak", "Estimated true peak", "dBTP");
            AddEssentialMeter(essentials, 2, "integrated", "Integrated loudness", "LUFS");
            AddEssentialMeter(essentials, 3, "correlation", "Stereo correlation", "−1 … +1");
            root.Controls.Add(essentials, 0, 4);
            _measurementRail = Horizontal("analysisMeasurementRail");
            _measurementRail.AutoSize = false; _measurementRail.Height = 138; _measurementRail.MinimumSize = new Size(0, 138);
            _measurementRail.AutoScroll = false; _measurementRail.BackColor = Surface;
            _measurementRail.Padding = new Padding(9); _measurementRail.Margin = new Padding(0, 7, 0, 0);
            AddDetailedMeter("leftRms", "Left RMS", "dBFS"); AddDetailedMeter("rightRms", "Right RMS", "dBFS");
            AddDetailedMeter("clipped", "Overload samples", ""); AddDetailedMeter("mid", "Mid RMS", "dBFS");
            AddDetailedMeter("side", "Side RMS", "dBFS"); AddDetailedMeter("momentary", "Momentary · 400 ms", "LUFS");
            AddDetailedMeter("shortTerm", "Short-term · 3 seconds", "LUFS");
            var help = new FlowLayoutPanel { AutoSize = true, Width = 220, MinimumSize = new Size(220, 0), MaximumSize = new Size(220, 0), FlowDirection = FlowDirection.TopDown, WrapContents = false };
            help.Controls.Add(Note("analysisMeasurementExplanation", "Digital levels, not calibrated SPL. A dash means unavailable or warming up: 400 ms for momentary, 3 s for short-term."));
            help.Controls.Add(resetMeasurements); _measurementRail.Controls.Add(help);
            _measurementRail.Visible = false; root.Controls.Add(_measurementRail, 0, 5);

            _errorPanel = Horizontal("analysisErrorPanel"); _errorPanel.BackColor = Color.FromArgb(58, 37, 32);
            _errorPanel.Padding = new Padding(10, 7, 10, 4); _errorPanel.Margin = new Padding(0, 7, 0, 0);
            _error.MaximumSize = new Size(850, 0); _error.Margin = new Padding(0, 5, 9, 5);
            _errorPanel.Controls.Add(_error);
            _errorDetailsToggle = Command("analysisErrorDetailsToggle", "Details", () => _errorDetails.Visible = !_errorDetails.Visible, "Show technical details for this error.");
            _errorDetailsToggle.Width = 76; _errorPanel.Controls.Add(_errorDetailsToggle);
            _errorDetails = new TextBox { Name = "analysisErrorDetails", AccessibleName = "Technical error details", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 90, Width = 850, BackColor = Field, ForeColor = Secondary, BorderStyle = BorderStyle.FixedSingle, Visible = false };
            _errorPanel.Controls.Add(_errorDetails); _errorPanel.Visible = false;
            root.Controls.Add(_errorPanel, 0, 6);
            root.Controls.Add(_status, 0, 7);
            _referenceOverlay.CheckedChanged += (_, _) => UpdateReferenceOverlay();
            _workspaceSelector.SelectedIndexChanged += (_, _) => SelectWorkspaceFromHeader();
            foreach (var target in new Control[] { root, header, commands, _sessionPanel, _modernBody, _settingsRail, center, comparison, ranges, inspector, essentials, _measurementRail })
            {
                target.AllowDrop = true; target.DragEnter += FileDragEnter; target.DragOver += FileDragEnter;
                target.DragDrop += async (_, args) => await OpenDroppedAudioAsync(args);
            }
            root.SizeChanged += (_, _) =>
            {
                var scaleFactor = Math.Max(1f, _liveContext.Height / 31f);
                var compact = root.ClientSize.Width < 1000 * scaleFactor;
                foreach (Control child in commands.Controls) if (child is Label) child.Visible = !_embedded && !compact;
                _sourceContext.Width = (int)Math.Round((compact ? 160 : 190) * scaleFactor);
                _workspaceSelector.Width = (int)Math.Round((compact ? 150 : 178) * scaleFactor);
                _state.Width = (int)Math.Round((compact ? 140 : 148) * scaleFactor);
                var available = Math.Max(180, root.ClientSize.Width - 130);
                _error.MaximumSize = new Size(available, 0);
                _errorDetails.Width = Math.Max(180, root.ClientSize.Width - 40);
            };
            Controls.Remove(oldLayout); oldLayout.Dispose(); root.BringToFront();
        }
        finally { ResumeLayout(true); }
    }

    private FlowLayoutPanel Horizontal(string name) => new()
    {
        Name = name, Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight,
        Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Canvas
    };

    private FlowLayoutPanel CollapsibleSection(string name, string title, bool expanded, string buttonName = null)
    {
        var shell = new FlowLayoutPanel
        {
            Name = name + "Container", Width = 210, MinimumSize = new Size(210, 0), MaximumSize = new Size(210, 0),
            AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 7)
        };
        var content = new FlowLayoutPanel
        {
            Name = name, Width = 210, MinimumSize = new Size(210, 0), MaximumSize = new Size(210, 0),
            AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(0, 5, 0, 2), Margin = Padding.Empty, Visible = expanded
        };
        var header = Command(buttonName ?? name + "Toggle", (expanded ? "▾ " : "▸ ") + title, () =>
        {
            content.Visible = !content.Visible;
            _collapsibleHeaders[name].Text = (content.Visible ? "▾ " : "▸ ") + title;
        }, "Expand or collapse " + title.ToLowerInvariant() + " settings.");
        header.Width = 210; header.Height = 28; header.BackColor = Surface;
        header.UseMnemonic = false;
        header.TextAlign = ContentAlignment.MiddleLeft; header.Font = _sectionFont;
        header.Margin = new Padding(0, 0, 0, 1);
        _collapsibleContents.Add(name, content); _collapsibleHeaders.Add(name, header);
        shell.Controls.Add(header); shell.Controls.Add(content); _settingsRail.Controls.Add(shell);
        return content;
    }

    private void AddRangeShortcut(FlowLayoutPanel row, string name, string text, double lower, double upper)
    {
        var button = Command(name, text, () => Navigate(() => _spectrum.SetFrequencyRange(lower, upper)),
            string.Format(CultureInfo.CurrentCulture, "Show {0:N0} to {1:N0} Hz.", lower, upper));
        button.Width = text == "Vocals" ? 68 : 60; button.Height = 29;
        button.Margin = new Padding(0, 0, 5, 4); row.Controls.Add(button);
    }

    private void AddEssentialMeter(TableLayoutPanel strip, int column, string key, string title, string unit)
    {
        var card = MeterCard(key, title, unit, true);
        card.Dock = DockStyle.Fill; card.Margin = new Padding(column == 0 ? 0 : 7, 0, 0, 0);
        strip.Controls.Add(card, column, 0);
    }

    private void AddDetailedMeter(string key, string title, string unit) => _measurementRail.Controls.Add(MeterCard(key, title, unit, false));

    private TableLayoutPanel MeterCard(string key, string title, string unit, bool essential)
    {
        var card = new TableLayoutPanel
        {
            Name = "analysisMeterCard_" + key, Width = essential ? 220 : 150, Height = 66,
            Padding = new Padding(10, 6, 10, 5), BackColor = Surface, ColumnCount = 2, RowCount = 2,
            Margin = new Padding(0, 0, 7, 6)
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = new Label { Text = title, AutoSize = true, ForeColor = Secondary, Margin = Padding.Empty };
        card.Controls.Add(caption, 0, 0); card.SetColumnSpan(caption, 2);
        var metric = _metrics[key]; metric.Dock = DockStyle.Fill; metric.Margin = Padding.Empty;
        card.Controls.Add(metric, 0, 1);
        card.Controls.Add(new Label { Text = unit, AutoSize = true, ForeColor = Secondary, TextAlign = ContentAlignment.BottomRight, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Margin = new Padding(5, 0, 0, 2) }, 1, 1);
        return card;
    }

    private void TogglePanel(Control panel, Button toggle)
    {
        if (panel == _measurementRail && _embedded)
        {
            _workspace.DetailsVisible = !_workspace.DetailsVisible;
            PresentDetails();
            toggle.BackColor = _workspace.DetailsVisible ? _presentation.Selection : Field;
            return;
        }
        panel.Visible = !panel.Visible;
        if (panel == _measurementRail)
        {
            _measurementRail.AutoScroll = panel.Visible;
            _measurementRail.PerformLayout();
        }
        if (panel == _measurementRail) _workspace.DetailsVisible = panel.Visible;
        toggle.BackColor = panel.Visible ? _presentation.Selection : Field;
    }

    private void PresentDetails()
    {
        if (!_embedded)
        {
            if (_detailsWindow != null) _detailsWindow.Hide();
            if (_measurementRail.Parent != _rootLayout)
            { _rootLayout.Controls.Add(_measurementRail, 0, 5); _measurementRail.Dock = DockStyle.Fill; }
            _measurementRail.Visible = _workspace.DetailsVisible;
            _measurementRail.AutoScroll = _workspace.DetailsVisible;
            return;
        }
        if (_detailsWindow == null)
        {
            _detailsWindow = new Form
            {
                Name = "analysisEmbeddedDetailsWindow", Text = "Spectrum · Detailed measurements", Icon = Icon,
                ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.SizableToolWindow,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(820, 190), MinimumSize = new Size(460, 150), Font = Font
            };
            _detailsWindow.FormClosing += (_, args) =>
            {
                if (_closing) return;
                args.Cancel = true; _detailsWindow.Hide(); _workspace.DetailsVisible = false;
                _detailsToggle.BackColor = Field;
            };
            SharedPresentation.Apply(_detailsWindow, _presentation);
        }
        if (_measurementRail.Parent != _detailsWindow)
        { _detailsWindow.Controls.Add(_measurementRail); _measurementRail.Dock = DockStyle.Fill; }
        _measurementRail.Visible = _workspace.DetailsVisible;
        _measurementRail.AutoScroll = _workspace.DetailsVisible;
        if (_workspace.DetailsVisible && Visible)
        {
            if (!_detailsWindow.Visible) _detailsWindow.Show(TopLevelControl as Form);
        }
        else _detailsWindow.Hide();
    }

    private void ToggleSettings()
    {
        _settingsRail.Visible = !_settingsRail.Visible;
        _workspace.SettingsVisible = _settingsRail.Visible;
        var scale = Math.Max(1f, _source.Width / 202f);
        _modernBody.ColumnStyles[0].Width = _settingsRail.Visible ? _workspace.SettingsWidth * scale : 0;
        _modernBody.ColumnStyles[1].Width = _settingsRail.Visible ? 6 * scale : 0;
        _settingsToggle.BackColor = _settingsRail.Visible ? _presentation.Selection : Field;
    }

    private void ShowContextSource()
    {
        if (_workspace.IsSavedSession) { _sessionPanel.Visible = true; return; }
        if (_workspace.IsOffline) { _ = OpenAudioAsync(); return; }
        if (!_settingsRail.Visible) ToggleSettings();
        _collapsibleContents["analysisSourceGroup"].Visible = true;
        _collapsibleHeaders["analysisSourceGroup"].Text = "▾ Source & channels";
        _settingsRail.ScrollControlIntoView(_source); _source.Focus();
        if (_source.Enabled && _source.Items.Count != 0) _source.DroppedDown = true;
    }

    private void UpdateReferenceOverlay()
    {
        if (_readingControls) return;
        _workspace.ReferenceVisible = _referenceOverlay.Checked;
        _spectrum.ShowReference = _spectrogram.ShowReference = _phase.ShowReference = _referenceOverlay.Checked;
        _spectrum.Invalidate(); _spectrogram.Invalidate();
    }

    private void UpdateWorkspaceSelector()
    {
        if (_workspaceSelector == null || _workspaceSelector.DroppedDown) return;
        var current = string.IsNullOrWhiteSpace(_workspace.WorkspaceName) ? "My analysis" : _workspace.WorkspaceName;
        _readingControls = true;
        try
        {
            _workspaceSelector.Items.Clear(); _workspaceSelector.Items.Add(current);
            foreach (var name in _recentWorkspaces.Keys) if (!string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) _workspaceSelector.Items.Add(name);
            _workspaceSelector.Items.Add("Load workspace…"); _workspaceSelector.Items.Add("Save workspace…");
            _workspaceSelector.SelectedItem = current;
        }
        finally { _readingControls = false; }
    }

    private void SelectWorkspaceFromHeader()
    {
        if (_readingControls || _busy || !(_workspaceSelector.SelectedItem is string selected)) return;
        if (selected == "Load workspace…") LoadWorkspace();
        else if (selected == "Save workspace…") SaveWorkspace();
        else if (_recentWorkspaces.TryGetValue(selected, out var path))
            Execute(() => { _workspace.LoadWorkspace(path); ReadSettings(); ReadWorkspacePresentation(); }, "Workspace loaded");
        UpdateWorkspaceSelector();
    }

    private FlowLayoutPanel Rail(string name) => new()
    {
        Name = name, Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, BackColor = Surface, Padding = new Padding(10, 8, 6, 8), Margin = Padding.Empty
    };

    private FlowLayoutPanel Section(FlowLayoutPanel rail, string title)
    {
        var width = rail.Name == "analysisSettingsRail" ? 210 : 194;
        var section = new FlowLayoutPanel
        {
            Width = width, MinimumSize = new Size(width, 0), MaximumSize = new Size(width, 0), AutoSize = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 13), Padding = Padding.Empty
        };
        var heading = new Label { Text = title, Font = _sectionFont, ForeColor = Accent, AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
        section.Controls.Add(heading);
        rail.Controls.Add(section);
        return section;
    }

    private static void Add(FlowLayoutPanel section, Control control)
    {
        if (control.Width > section.Width - 8) control.Width = section.Width - 8;
        if (control is Label label && label.AutoSize) label.MaximumSize = new Size(section.Width - 8, 0);
        control.Margin = new Padding(0, 0, 0, 6);
        section.Controls.Add(control);
    }

    private void AddLabeled(FlowLayoutPanel section, string text, Control control)
    {
        section.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Secondary, Margin = new Padding(0, 3, 0, 3) });
        Add(section, control);
    }

    private Label Note(string name, string text) => new()
    {
        Name = name, Text = text, AutoSize = true, MaximumSize = new Size(202, 0), ForeColor = Secondary,
        Margin = new Padding(0, 0, 0, 6), AccessibleName = text
    };

    private ComboBox Choice(string name, string accessibleName, params string[] values)
    {
        var choice = new ComboBox
        {
            Name = name, AccessibleName = accessibleName, DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 202, BackColor = Field, ForeColor = Primary, FlatStyle = FlatStyle.Flat, DropDownWidth = 380,
            IntegralHeight = true, MaxDropDownItems = 12
        };
        choice.Items.AddRange(values);
        _busyControls.Add(choice);
        return choice;
    }

    private NumericUpDown Number(string name, string accessibleName, decimal minimum, decimal maximum, decimal value, int places, decimal increment)
    {
        var number = new NumericUpDown
        {
            Name = name, AccessibleName = accessibleName, Minimum = minimum, Maximum = maximum,
            Value = value, DecimalPlaces = places, Increment = increment, Width = 202,
            BackColor = Field, ForeColor = Primary, BorderStyle = BorderStyle.FixedSingle, ThousandsSeparator = true
        };
        _busyControls.Add(number);
        return number;
    }

    private Button Command(string name, string text, Action action, string description)
    {
        var button = new Button
        {
            Name = name, Text = text, AccessibleName = text.Replace("…", ""), AccessibleDescription = description,
            Width = 120, Height = 31, AutoSize = false, FlatStyle = FlatStyle.Flat, BackColor = Field,
            ForeColor = Primary, Margin = new Padding(0, 0, 7, 6), UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = _presentation.Divider;
        button.FlatAppearance.MouseOverBackColor = _presentation.Hover;
        button.FlatAppearance.MouseDownBackColor = _presentation.Selection;
        button.Click += (_, _) => action();
        _tips.SetToolTip(button, description);
        _busyControls.Add(button);
        return button;
    }

    private Button SmallCommand(string name, string text, Action action, string description)
    {
        var button = Command(name, text, action, description);
        button.AccessibleName = description;
        button.Width = 43;
        button.Margin = new Padding(0, 0, 7, 5);
        return button;
    }

    private void AddMetric(FlowLayoutPanel section, string key, string title, string unit)
    {
        var row = new TableLayoutPanel { Width = 190, Height = 54, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = new Label { Text = title, AutoSize = true, ForeColor = Secondary, Margin = Padding.Empty };
        row.Controls.Add(caption, 0, 0);
        row.SetColumnSpan(caption, 2);
        var metric = new Label { Name = "analysisMetric_" + key, AccessibleName = title, Text = "—", Font = _metricFont, Dock = DockStyle.Fill, ForeColor = Primary, Margin = Padding.Empty };
        row.Controls.Add(metric, 0, 1);
        row.Controls.Add(new Label { Text = unit, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Secondary, Margin = Padding.Empty }, 1, 1);
        _metrics.Add(key, metric);
        Add(section, row);
    }

    private void WorkspaceChanged() => Interlocked.Exchange(ref _refreshNeeded, 1);

    private void RefreshFromWorkspace() => RefreshFromWorkspaceCore(_busy);

    private void RefreshFromWorkspaceCore(bool busy)
    {
        if (_closing || IsDisposed) return;
        // Plot wheel/key navigation also changes the range; don't leave its numerical controls stale.
        if (!_minimumHz.ContainsFocus && !_maximumHz.ContainsFocus)
        {
            _readingControls = true;
            try { SetNumber(_minimumHz, _workspace.Settings.MinHz); SetNumber(_maximumHz, _workspace.Settings.MaxHz); }
            finally { _readingControls = false; }
        }
        var frame = _workspace.Latest;
        var measurementsOnly = _workspace.IsSavedSession;
        _channel.Enabled = !busy && !measurementsOnly;
        _response.Enabled = !busy && !measurementsOnly;
        _tips.SetToolTip(_channel, measurementsOnly ? "Saved sessions contain measurements, not audio. Resume live or open the original audio to change channels." : "Choose the measured channel or mid/side signal.");
        _tips.SetToolTip(_response, measurementsOnly ? "Resume live or open the original audio to re-analyze with a different resolution." : "Fast prioritizes response time; Detailed prioritizes frequency resolution.");
        _state.Text = busy ? "ANALYZING FILE" : measurementsOnly ? "SAVED SESSION" : _workspace.IsOffline ? "OFFLINE ANALYSIS" : _workspace.IsUnavailable || frame == null ? "AUDIO UNAVAILABLE" : "LIVE ANALYSIS";
        _state.ForeColor = _workspace.IsOffline ? SharedPresentation.ReferenceCurve(_presentation) : Accent;
        _sourceDetails.Text = frame == null ? "No current measured samples" : string.Format(CultureInfo.CurrentCulture, "{0:N0} Hz · {1} channel{2}", frame.SampleRate, frame.Channels, frame.Channels == 1 ? "" : "s");
        if (frame != null && frame.Channels > 2)
            _sourceDetails.Text += "\nFront L/R measured\nProgramme loudness unavailable";
        var offline = _workspace.IsOffline;
        _source.Enabled = !busy && !offline && _analyzer != null && _source.Items.Count != 0;
        _fileSection.Parent.Visible = offline && !measurementsOnly;
        _liveContext.BackColor = !offline ? _presentation.Selection : Field;
        _fileContext.BackColor = offline ? _presentation.Selection : Field;
        _liveContext.AccessibleDescription = !offline ? "Live audio is the active analysis context." : "Switch analysis to live capture.";
        _fileContext.AccessibleDescription = offline ? "File or saved measurement analysis is active." : "Switch to a previously analyzed file or choose a file.";
        _sourceContext.Text = measurementsOnly ? "Measurement session ▾" : offline ? _workspace.SourceName + " ▾" : (_source.SelectedItem?.ToString() ?? "Choose audio source") + " ▾";
        _tips.SetToolTip(_sourceContext, _sourceContext.Text);
        _status.Text = busy ? "Analyzing the selected audio section…" : measurementsOnly
            ? "Saved measurements · open the original audio or select Live to analyze new samples."
            : offline ? string.Format(CultureInfo.CurrentCulture, "{0} · selection starts at {1:0.##} s · {2}", _workspace.SourceName, _workspace.SelectionStartSeconds, _sourceDetails.Text.Replace("\n", " · "))
            : _workspace.IsUnavailable || frame == null
                ? _workspace.Status + (frame == null ? "" : " · previous measurements retained")
                : "Live capture · " + _sourceDetails.Text.Replace("\n", " · ");
        _status.AccessibleDescription = _workspace.Status;
        _tips.SetToolTip(_status, _workspace.Status);
        var hasReference = _workspace.ReferencePower != null;
        var referenceTime = _workspace.ReferenceCapturedUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        _reference.Text = !hasReference ? "REFERENCE  None · use Compare to capture" : "REFERENCE  " + _workspace.ReferenceName + (referenceTime == null ? "" : " · " + referenceTime);
        _tips.SetToolTip(_reference, _reference.Text);
        _readingControls = true;
        try { _referenceOverlay.Checked = _workspace.ReferenceVisible; }
        finally { _readingControls = false; }
        _referenceOverlay.Enabled = !busy && hasReference;
        _referenceOverlay.Visible = !_comparisonView && hasReference;
        _spectrum.ShowReference = _spectrogram.ShowReference = _phase.ShowReference = _workspace.ReferenceVisible;
        _comparisonCurrent.ShowReference = _workspace.ReferenceVisible;
        Controls.Find("analysisCompareReferenceTitle", true)[0].Text = hasReference ? _workspace.ReferenceName : "Reference · capture to compare";
        if (!_workspaceSelector.ContainsFocus && !string.Equals(_workspaceSelector.SelectedItem as string, string.IsNullOrWhiteSpace(_workspace.WorkspaceName) ? "My analysis" : _workspace.WorkspaceName, StringComparison.Ordinal)) UpdateWorkspaceSelector();
        var values = _workspace.IsUnavailable && !_workspace.IsOffline ? null : _workspace.Measurements;
        if (values == null)
        {
            foreach (var label in _metrics.Values)
            {
                label.Text = "—";
                label.AccessibleDescription = "Unavailable: " + _workspace.Status;
            }
        }
        else
        {
            SetMetric("samplePeak", Db(values.SamplePeakDb));
            SetMetric("truePeak", Db(values.TruePeakDb));
            SetMetric("leftRms", Db(values.LeftRmsDb));
            SetMetric("rightRms", values.HasStereo ? Db(values.RightRmsDb) : "—");
            SetMetric("clipped", values.ClippedSamples.ToString("N0", CultureInfo.CurrentCulture));
            SetMetric("correlation", values.HasStereo && Finite(values.Correlation) ? values.Correlation.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) : "—");
            SetMetric("mid", values.HasStereo ? Db(values.MidRmsDb) : "—");
            SetMetric("side", values.HasStereo ? Db(values.SideRmsDb) : "—");
            SetMetric("momentary", Db(values.MomentaryLufs));
            SetMetric("shortTerm", Db(values.ShortTermLufs));
            SetMetric("integrated", values.LoudnessSupported && values.DurationSeconds >= .4 && values.GateBlocks == 0 ? "Below gate" : Db(values.IntegratedLufs));
            var loudnessHelp = !values.LoudnessSupported ? "Programme loudness is unavailable for this channel layout." : values.DurationSeconds < .4 ? "Warming up: momentary and integrated loudness require 400 ms of audio." : values.GateBlocks == 0 ? "No blocks above the loudness gate. This does not prove digital silence." : "Integrated programme loudness since the last reset.";
            _tips.SetToolTip(_metrics["integrated"], loudnessHelp);
            _metrics["integrated"].AccessibleDescription = _metrics["integrated"].Text + ". " + loudnessHelp;
            _tips.SetToolTip(_metrics["momentary"], !values.LoudnessSupported ? "Programme loudness unavailable for this layout." : values.DurationSeconds < .4 ? "Warming up: requires 400 ms of audio." : "Momentary loudness over 400 ms.");
            _tips.SetToolTip(_metrics["shortTerm"], !values.LoudnessSupported ? "Programme loudness unavailable for this layout." : values.DurationSeconds < 3 ? "Warming up: requires 3 seconds of audio." : "Short-term loudness over 3 seconds.");
            _tips.SetToolTip(_metrics["correlation"], !values.HasStereo ? "Stereo correlation requires two channels." : "Front left/right relationship: +1 matching, 0 uncorrelated, −1 opposite polarity.");
        }
        _analyzeSelection.Enabled = !busy && !string.IsNullOrEmpty(_audioPath);
        _spectrum.RefreshInspection();
        _spectrogram.RefreshInspection();
        _phase.RefreshInspection();
        _comparisonCurrent.RefreshInspection(); _comparisonReference.RefreshInspection();
        if (!_comparisonView) UpdateInspection(_plotTabs.SelectedIndex == 2 ? _phase : _plotTabs.SelectedIndex == 1 ? _spectrogram : _spectrum);
        _comparisonCurrent.Invalidate(); _comparisonReference.Invalidate();
        _spectrum.Invalidate();
        _spectrogram.Invalidate();
        _phase.Invalidate();
    }

    private void SetMetric(string name, string text)
    {
        _metrics[name].Text = text;
        _metrics[name].AccessibleDescription = text;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static string Db(double value) => double.IsNaN(value) || double.IsPositiveInfinity(value) ? "—" : double.IsNegativeInfinity(value) || value <= -299 ? "−∞" : value.ToString("0.0", CultureInfo.CurrentCulture);

    private void UpdateInspection(AnalysisPlotControl plot)
    {
        _inspection.AccessibleDescription = plot.InspectionText;
        _tips.SetToolTip(_inspection, plot.InspectionText);
        if (_inspectionValues.Count == 0) return;
        var reading = plot.Inspection;
        var comparisonContext = _comparisonView ? (plot.ReferenceOnly ? "Reference inspection · " : "Current inspection · ") : "";
        _inspection.Text = plot.ShowPhase ? "Stereo phase · matching channels: vertical · opposite polarity: horizontal"
            : reading.BandNumber == 0 ? "Point to a band to inspect · click to pin · arrow keys pan"
            : string.Format(CultureInfo.CurrentCulture, "Band {0} · {1} · {2}", reading.BandNumber, _workspace.Settings.Channel, reading.IsPinned ? "Pinned" : "Live inspection");
        _inspection.Text = comparisonContext + _inspection.Text;
        _inspectionValues["Frequency"].Text = reading.BandNumber == 0 ? "—" : reading.CenterHz.ToString("N1", CultureInfo.CurrentCulture) + " Hz";
        _tips.SetToolTip(_inspectionValues["Frequency"], reading.BandNumber == 0 ? "Point to a spectrum band." : string.Format(CultureInfo.CurrentCulture, "Exact band: {0:0.###}–{1:0.###} Hz", reading.LowerHz, reading.UpperHz));
        _inspectionValues["Level"].Text = InspectionDb(reading.LevelDb, " dBFS");
        _inspectionValues["Peak"].Text = InspectionDb(reading.PeakDb, " dBFS");
        _inspectionValues["Delta"].Text = Finite(reading.DeltaDb) ? reading.DeltaDb.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + " dB" : "—";
        _inspectionValues["State"].Text = reading.IsUnavailable ? reading.Status : reading.BandNumber == 0 ? "—" : reading.IsBelowFloor ? "Below floor" : reading.Status;
        _tips.SetToolTip(_inspectionValues["State"], reading.Status);
        foreach (var value in _inspectionValues.Values) value.AccessibleDescription = value.Text;
    }

    private static string InspectionDb(double value, string unit) => double.IsNaN(value) || double.IsPositiveInfinity(value) ? "—" : double.IsNegativeInfinity(value) ? "−∞" + unit : value.ToString("0.00", CultureInfo.CurrentCulture) + unit;

    private void ReadSettings()
    {
        _readingControls = true;
        try
        {
            var settings = _workspace.Settings;
            var floorText = settings.FloorDb.ToString("0", CultureInfo.InvariantCulture);
            if (!_floor.Items.Contains(floorText)) _floor.Items.Add(floorText);
            _floor.SelectedItem = floorText;
            _channel.SelectedItem = settings.Channel;
            if (_channel.SelectedIndex < 0) _channel.SelectedIndex = 0;
            SetNumber(_minimumHz, settings.MinHz);
            SetNumber(_maximumHz, settings.MaxHz);
            _response.SelectedItem = settings.Response;
            if (_response.SelectedIndex < 0) _response.SelectedItem = "Balanced";
            SetNumber(_average, settings.AveragingMilliseconds);
            SetNumber(_smoothing, settings.SmoothingBands);
            SetNumber(_peakHold, settings.PeakHoldMilliseconds);
            SetNumber(_history, settings.HistorySeconds);
            _workspaceName.Text = _workspace.WorkspaceName ?? "My workspace";
        }
        finally { _readingControls = false; }
        UpdateWorkspaceSelector();
    }

    private static void SetNumber(NumericUpDown number, double value)
    {
        if (!Finite(value)) return;
        number.Value = Math.Max(number.Minimum, Math.Min(number.Maximum, (decimal)value));
    }

    private void ApplySettings()
    {
        if (_readingControls || _closing || _floor.SelectedItem == null) return;
        if (_minimumHz.Value >= _maximumHz.Value)
        {
            ShowValidation("The upper frequency must be greater than the lower frequency.");
            return;
        }
        var settings = _workspace.Settings;
        settings.FloorDb = double.Parse((string)_floor.SelectedItem, CultureInfo.InvariantCulture);
        settings.CeilingDb = 0;
        settings.Channel = _channel.SelectedItem as string ?? "Combined";
        settings.MinHz = (double)_minimumHz.Value;
        settings.MaxHz = (double)_maximumHz.Value;
        settings.Response = _response.SelectedItem as string ?? "Balanced";
        settings.AveragingMilliseconds = (int)_average.Value;
        settings.SmoothingBands = (int)_smoothing.Value;
        settings.PeakHoldMilliseconds = (int)_peakHold.Value;
        settings.HistorySeconds = (int)_history.Value;
        Execute(_workspace.UpdateSettings, "Analysis settings updated");
    }

    private void ApplyResponsePreset()
    {
        if (_readingControls || _response.SelectedItem == null) return;
        _readingControls = true;
        try
        {
            switch ((string)_response.SelectedItem)
            {
                case "Fast": _average.Value = 60; _smoothing.Value = 0; _peakHold.Value = 250; break;
                case "Detailed": _average.Value = 600; _smoothing.Value = 2; _peakHold.Value = 1000; break;
                default: _average.Value = 200; _smoothing.Value = 1; _peakHold.Value = 500; break;
            }
        }
        finally { _readingControls = false; }
        ApplySettings();
    }

    private void Navigate(Action navigation)
    {
        Execute(navigation, "Frequency range updated");
        ReadSettings();
    }

    private void RefreshSources()
    {
        if (_analyzer == null) { _source.Enabled = false; return; }
        Execute(() =>
        {
            var currentSource = _source.SelectedItem as AnalysisAudioSource ?? _analyzer.SelectedAnalysisSource;
            var oldId = currentSource?.Id;
            var sources = _analyzer.GetAnalysisSources();
            _readingControls = true;
            try
            {
                _source.Items.Clear();
                var selected = 0;
                foreach (var source in sources)
                {
                    var index = _source.Items.Add(source);
                    if (string.Equals(oldId, source.Id, StringComparison.OrdinalIgnoreCase) && currentSource.IsLoopback == source.IsLoopback) selected = index;
                }
                if (_source.Items.Count > 0) _source.SelectedIndex = selected;
            }
            finally { _readingControls = false; }
            _source.Enabled = !_busy && _source.Items.Count != 0;
        }, "Audio sources refreshed");
    }

    private void SelectSource()
    {
        if (_readingControls || _analyzer == null || !(_source.SelectedItem is AnalysisAudioSource source)) return;
        Execute(() => { _analyzer.SelectAnalysisSource(source); _workspace.ResumeLive(); }, "Audio source selected");
    }

    private async Task OpenAudioAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Analyze an audio file", Filter = AnalysisAudioFile.FileFilter,
            CheckFileExists = true, Multiselect = false, RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await OpenAudioPathAsync(dialog.FileName);
    }

    private async Task SelectFileContextAsync()
    {
        if (!_workspace.IsOffline && !_lastFileFailed && !string.IsNullOrEmpty(_lastSuccessfulAudioPath) && File.Exists(_lastSuccessfulAudioPath))
            await OpenAudioPathAsync(_lastSuccessfulAudioPath);
        else await OpenAudioAsync();
    }

    private void FileDragEnter(object sender, DragEventArgs args)
    {
        args.Effect = !_busy && !_closing && DroppedAudioPath(args.Data) != null &&
            (args.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async Task OpenDroppedAudioAsync(DragEventArgs args)
    {
        if (_busy || _closing) return;
        var path = DroppedAudioPath(args.Data);
        if (path != null) await OpenAudioPathAsync(path);
    }

    private static string DroppedAudioPath(IDataObject data)
    {
        if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
        var paths = data.GetData(DataFormats.FileDrop) as string[];
        return paths != null && paths.Length == 1 && File.Exists(paths[0]) ? paths[0] : null;
    }

    private async Task OpenAudioPathAsync(string path)
    {
        if (_busy || _closing) return;
        _audioPath = path;
        _fileSelection.Text = Path.GetFileName(_audioPath);
        _tips.SetToolTip(_fileSelection, _audioPath);
        await AnalyzeSelectionAsync();
    }

    private async Task AnalyzeSelectionAsync()
    {
        if (_busy || string.IsNullOrEmpty(_audioPath)) return;
        var path = _audioPath;
        var start = (double)_selectionStart.Value;
        var duration = (double)_selectionDuration.Value;
        SetBusy(true);
        Exception failure = null;
        try { await Task.Run(() => _workspace.AnalyzeFile(path, start, duration)).ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        if (_closing || IsDisposed) return;
        var completion = new TaskCompletionSource<bool>();
        void CompleteOnUi()
        {
            try
            {
                if (_closing || IsDisposed) return;
                if (failure == null)
                {
                    _lastSuccessfulAudioPath = path; _lastFileFailed = false;
                    ClearError(); ReadSettings();
                }
                else { _lastFileFailed = true; ShowError("The audio file could not be analyzed", failure); }
            }
            finally
            {
                if (!_closing && !IsDisposed) SetBusy(false);
                completion.TrySetResult(true);
            }
        }
        try
        {
            // A reflection-hosted STA need not install a WinForms SynchronizationContext.
            // Always finish through the control's owning UI thread before publishing completion.
            if (InvokeRequired) BeginInvoke((Action)CompleteOnUi);
            else CompleteOnUi();
        }
        catch (InvalidOperationException) when (_closing || IsDisposed) { completion.TrySetResult(false); }
        await completion.Task.ConfigureAwait(false);
    }

    private void SetBusy(bool busy)
    {
        if (busy) _busy = true;
        foreach (var control in _busyControls) control.Enabled = !busy;
        if (_analyzer == null || _source.Items.Count == 0) _source.Enabled = false;
        _workspaceName.Enabled = !busy;
        UseWaitCursor = busy;
        RefreshFromWorkspaceCore(busy);
        // The UI and its controls are fully updated when observers see the completed state.
        if (!busy) _busy = false;
    }

    private void ExportCsv()
    {
        using var dialog = SaveDialog("Export numerical measurements", "CSV measurements|*.csv", "Spectrum-measurements.csv");
        if (dialog.ShowDialog(this) == DialogResult.OK) Execute(() => _workspace.ExportCsv(dialog.FileName), "Measurements exported");
    }

    private void SaveSession()
    {
        using var dialog = SaveDialog("Save measurement session · audio is not recorded", "Spectrum session|*.spectrum-session.xml", "Spectrum.spectrum-session.xml");
        if (dialog.ShowDialog(this) == DialogResult.OK) Execute(() => _workspace.SaveSession(dialog.FileName), "Measurement session saved");
    }

    private void OpenSession()
    {
        using var dialog = new OpenFileDialog { Title = "Open measurement session", Filter = "Spectrum session|*.spectrum-session.xml|XML files|*.xml", CheckFileExists = true, RestoreDirectory = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Execute(() => { _workspace.LoadSession(dialog.FileName); ReadSettings(); ReadWorkspacePresentation(); }, "Measurement session opened");
    }

    private void SaveWorkspace()
    {
        _workspace.WorkspaceName = string.IsNullOrWhiteSpace(_workspaceName.Text) ? "My workspace" : _workspaceName.Text.Trim();
        _workspace.PreferredView = SelectedView;
        if (!_embedded) { _workspace.WindowWidth = Width; _workspace.WindowHeight = Height; }
        using var dialog = SaveDialog("Save or export a custom workspace", "Spectrum workspace|*.spectrum-workspace.xml", SafeFileName(_workspace.WorkspaceName) + ".spectrum-workspace.xml");
        if (dialog.ShowDialog(this) == DialogResult.OK) Execute(() =>
        {
            _workspace.SaveWorkspace(dialog.FileName);
            _recentWorkspaces[_workspace.WorkspaceName] = dialog.FileName;
            UpdateWorkspaceSelector();
        }, "Custom workspace saved");
    }

    private void LoadWorkspace()
    {
        using var dialog = new OpenFileDialog { Title = "Load or import a custom workspace", Filter = "Spectrum workspace|*.spectrum-workspace.xml|XML files|*.xml", CheckFileExists = true, RestoreDirectory = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Execute(() =>
        {
            _workspace.LoadWorkspace(dialog.FileName);
            _recentWorkspaces[_workspace.WorkspaceName] = dialog.FileName;
            ReadSettings(); ReadWorkspacePresentation();
        }, "Custom workspace loaded");
    }

    private void ReadWorkspacePresentation()
    {
        ReadLayout();
        _readingControls = true;
        try { _plotTabs.SelectedIndex = _workspace.PreferredView == "Stereo phase" ? 2 : _workspace.PreferredView == "Spectrogram" ? 1 : 0; }
        finally { _readingControls = false; }
        if (_embedded) return;
        var area = Screen.FromControl(this).WorkingArea;
        var desiredWidth = _workspace.WindowWidth > 0 ? _workspace.WindowWidth : Width;
        var desiredHeight = _workspace.WindowHeight > 0 ? _workspace.WindowHeight : Height;
        MinimumSize = new Size(Math.Min(920, area.Width), Math.Min(650, area.Height));
        Size = new Size(Math.Min(area.Width, Math.Max(MinimumSize.Width, desiredWidth)),
            Math.Min(area.Height, Math.Max(MinimumSize.Height, desiredHeight)));
        if (Visible)
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
    }

    private string SelectedView => _plotTabs.SelectedIndex == 2 ? "Stereo phase" : _plotTabs.SelectedIndex == 1 ? "Spectrogram" : "Spectrum";

    private static SaveFileDialog SaveDialog(string title, string filter, string filename) => new()
    {
        Title = title, Filter = filter, FileName = filename, AddExtension = true,
        OverwritePrompt = true, RestoreDirectory = true
    };

    private static string SafeFileName(string name)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        return name.Length > 80 ? name.Substring(0, 80) : name;
    }

    private void Execute(Action action, string success)
    {
        try
        {
            action();
            ClearError();
            RefreshFromWorkspace();
            _status.Text = success + " · " + _status.Text;
        }
        catch (Exception exception) { ShowError("The action could not be completed", exception); }
    }

    private void ShowError(string title, Exception exception)
    {
        var cause = exception.GetBaseException();
        var guidance = cause is FileNotFoundException || cause is DirectoryNotFoundException
            ? "Choose an existing file and try again."
            : cause is UnauthorizedAccessException ? "Choose a file or folder you can access."
            : cause is ArgumentException ? "Check the selected range and settings, then try again."
            : title.Contains("audio file") ? "Choose a supported, readable audio file and try again."
            : "Check your selection and try again.";
        _error.Text = title + ". " + guidance;
        _error.AccessibleDescription = _error.Text;
        _error.Visible = true; _errorPanel.Visible = true;
        _errorDetails.Text = exception.ToString(); _errorDetails.Visible = false;
        _errorDetailsToggle.Visible = true;
    }

    private void ShowValidation(string message)
    {
        _error.Text = message; _error.AccessibleDescription = message;
        _error.Visible = true; _errorPanel.Visible = true;
        _errorDetails.Text = ""; _errorDetails.Visible = false; _errorDetailsToggle.Visible = false;
    }

    private void ClearError()
    {
        _error.Visible = false;
        if (_errorPanel != null) _errorPanel.Visible = false;
        if (_errorDetails != null) { _errorDetails.Visible = false; _errorDetails.Clear(); }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (!_busy && keyData == (Keys.Control | Keys.O)) { _ = OpenAudioAsync(); return true; }
        if (!_busy && keyData == (Keys.Control | Keys.E)) { ExportCsv(); return true; }
        if (!_busy && keyData == (Keys.Control | Keys.L)) { Execute(_workspace.ResumeLive, "Live capture resumed"); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closing = true;
            _workspace.Changed -= WorkspaceChanged;
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _tips.Dispose();
            _detailsWindow?.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            _headingFont.Dispose();
            _sectionFont.Dispose();
            _metricFont.Dispose();
            _bodyFont.Dispose();
        }
    }

    private sealed class ModernAnalysisTabControl : TabControl
    {
        private VisualTheme _theme = BarColorThemes.Studio.Ui;
        internal void ApplyPresentation(VisualTheme theme) { _theme = theme; BackColor = theme.Canvas; Invalidate(); }
        internal ModernAnalysisTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(132, 34);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = _theme.Canvas;
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.Clear(_theme.Canvas);
            using var inactive = new SolidBrush(_theme.ControlSurface);
            using var active = new SolidBrush(_theme.Surface);
            using var underline = new SolidBrush(_theme.Focus);
            for (var index = 0; index < TabPages.Count; index++)
            {
                var bounds = GetTabRect(index);
                args.Graphics.FillRectangle(index == SelectedIndex ? active : inactive, bounds);
                if (index == SelectedIndex) args.Graphics.FillRectangle(underline, bounds.Left, bounds.Bottom - 3, bounds.Width, 3);
                TextRenderer.DrawText(args.Graphics, TabPages[index].Text, Font, bounds,
                    index == SelectedIndex ? _theme.PrimaryText : _theme.SecondaryText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (Focused && index == SelectedIndex) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(bounds, -5, -5), _theme.PrimaryText, _theme.Surface);
            }
        }

        protected override void OnSelectedIndexChanged(EventArgs args) { base.OnSelectedIndexChanged(args); Invalidate(); }
    }
}
