using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Spectrum.Dsp;

namespace Spectrum;

// Window composition only. The existing visualization buffers and DSP remain authoritative.
public partial class FormAudioSpectrum
{
    private FlowLayoutPanel _workspaceNavigation;
    private Panel _workspaceHost;
    private Label _workspaceBrand, _mainInspection, _captureBanner;
    private Button _browseViews, _chooseSource, _captureDetails, _popoutAnalysis;
    private SessionLibraryControl _sessionLibrary;
    private ContextMenuStrip _mainSourceMenu;
    private Panel _compactAnalysisNotice;
    private string _workspaceView = "Visualize";
    private AnalysisFrame _pendingInspectionFrame, _inspectionFrameBuffer;
    private string _inspectionDeviceBuffer;
    private MainSpectrumSnapshot _displayedInspection;
    private int _inspectedBand = -1;
    private string _inspectedChannel = "Combined";
    private long _lastWorkspacePost;
    private readonly VisualizationCatalogPreferences _catalog = new();

    private void InitializeWorkspaceShell()
    {
        _workspaceBrand = new Label { Name = "workspaceBrand", Text = "Spectrum", Font = new Font("Segoe UI Semibold", 11f),
            AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Spectrum audio workspace" };
        _workspaceNavigation = new FlowLayoutPanel { Name = "workspaceNavigation", WrapContents = false,
            AccessibleName = "Workspace navigation", TabIndex = 0, Margin = Padding.Empty };
        foreach (var view in new[] { "Visualize", "Analyze", "Compare", "Sessions" })
        {
            var name = view;
            var button = ShellButton("workspace" + name, name, () => NavigateWorkspace(name));
            button.AccessibleDescription = name + " workspace. Ctrl+" + (Array.IndexOf(new[] { "Visualize", "Analyze", "Compare", "Sessions" }, name) + 1);
            _workspaceNavigation.Controls.Add(button);
        }
        _browseViews = ShellButton("browseVisualizations", "Browse views", ShowModeSearch);
        _browseViews.AccessibleDescription = "Find a visualization by purpose, preview it, or open a favorite. Ctrl+K";
        _workspaceNavigation.Controls.Add(_browseViews);
        _popoutAnalysis = ShellButton("popoutAnalysis", "Pop out", ShowAnalysisWorkbench);
        _workspaceNavigation.Controls.Add(_popoutAnalysis);
        _workspaceHost = new Panel { Name = "workspaceHost", Visible = false, AccessibleName = "Active analysis workspace" };
        _compactAnalysisNotice = new Panel { Name = "compactAnalysisNotice", Dock = DockStyle.Fill, Visible = false };
        var compactText = new Label { Text = "More room for your analysis", Dock = DockStyle.Top, Height = 42, TextAlign = ContentAlignment.MiddleCenter };
        var compactAction = ShellButton("compactOpenAnalysis", "Open analysis window", ShowAnalysisWorkbench);
        compactAction.Dock = DockStyle.Top; compactAction.Height = 34;
        _compactAnalysisNotice.Controls.Add(compactAction); _compactAnalysisNotice.Controls.Add(compactText);
        _workspaceHost.Controls.Add(_compactAnalysisNotice);
        _mainInspection = new Label { Name = "mainSpectrumInspection", AutoEllipsis = true, TabStop = true,
            TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Displayed spectrum band inspection",
            Text = "Point to a band to inspect its frequency and measured level. Left and Right move between bands." };
        _mainInspection.KeyDown += (s, e) =>
        {
            var count = MainSpectrumInspector.GetGeometry(_visualMode) == MainSpectrumGeometry.SevenBandCards ? 7 : BAR_COUNT;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            {
                _inspectedBand = e.KeyCode == Keys.Home ? 0 : e.KeyCode == Keys.End ? count - 1
                    : Math.Max(0, Math.Min(count - 1, _inspectedBand + (e.KeyCode == Keys.Left ? -1 : 1)));
                RefreshMainInspection(); e.Handled = e.SuppressKeyPress = true;
            }
        };
        _captureBanner = new Label { Name = "captureHealthBanner", AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Capture health" };
        _chooseSource = ShellButton("mainChooseSource", "Choose source", ShowMainSourceMenu);
        _captureDetails = ShellButton("mainCaptureDetails", "Details", () => MessageBox.Show(this,
            _captureStatusLabel.AccessibleDescription, "Audio capture", MessageBoxButtons.OK, MessageBoxIcon.Information));
        foreach (var control in new Control[] { _workspaceBrand, _workspaceNavigation, _workspaceHost, _mainInspection,
            _captureBanner, _chooseSource, _captureDetails }) ambiance_ThemeSpectrum.Controls.Add(control);
        _modeSelectorLabel.Text = "&View"; _styleSelectorLabel.Text = "&Style";
        ApplyWorkspacePresentation(CurrentTheme().Ui);
        PositionSelectionPanel();
    }

    private Button ShellButton(string name, string text, Action action)
    {
        var button = new Button { Name = name, Text = text, AccessibleName = text, FlatStyle = FlatStyle.Flat,
            Font = ambiance_ThemeSpectrum.Font, Margin = new Padding(0, 0, 5, 0), TabStop = true,
            UseVisualStyleBackColor = false };
        button.Click += (s, e) => action();
        return button;
    }

    private void LayoutWorkspaceShell()
    {
        if (_workspaceNavigation == null) return;
        var scale = Math.Max(DeviceDpi / 96f, Math.Max(1f, _modeSelector.Font.SizeInPoints / 9f));
        var dpiScale = DeviceDpi / 96f;
        var width = ambiance_ThemeSpectrum.ClientSize.Width;
        var height = ambiance_ThemeSpectrum.ClientSize.Height;
        // Enlarged text needs measured glyph height, not a multiplier on all header whitespace.
        _workspaceBrand.Visible = height > 300 * dpiScale;
        var header = (int)Math.Ceiling(34 * dpiScale);
        if (_workspaceBrand.Visible)
            header = Math.Max(header, TextRenderer.MeasureText("Ag", _workspaceBrand.Font).Height + (int)Math.Ceiling(8 * dpiScale));
        foreach (Button button in _workspaceNavigation.Controls)
            header = Math.Max(header, TextRenderer.MeasureText("Ag", button.Font).Height + (int)Math.Ceiling(15 * dpiScale));
        var controlHeight = Math.Max(26, TextRenderer.MeasureText("Ag", _modeSelector.Font).Height + 9);
        var navigationLeft = (int)Math.Round((_workspaceBrand.Visible ? 122 : 12) * dpiScale);
        _workspaceBrand.Bounds = new Rectangle((int)(16 * dpiScale), (int)(5 * dpiScale), (int)(102 * dpiScale), header - (int)(8 * dpiScale));
        _workspaceNavigation.Bounds = new Rectangle(navigationLeft, (int)(5 * dpiScale), Math.Max(1, width - navigationLeft - ambiance_ControlBox.Width - (int)(18 * dpiScale)), header - (int)(7 * dpiScale));
        foreach (Button button in _workspaceNavigation.Controls)
        {
            button.Size = new Size(TextRenderer.MeasureText(button.Text, button.Font).Width + 24, header - 9);
            button.FlatAppearance.BorderSize = button.Name == "workspace" + _workspaceView ? 1 : 0;
        }
        _popoutAnalysis.Visible = _workspaceView == "Analyze" || _workspaceView == "Compare";
        _browseViews.Visible = _workspaceView == "Visualize";
        _selectionPanel.Location = new Point(12, header + 6);
        _selectionPanel.Width = Math.Max(1, width - 24);
        _selectionPanel.Visible = _workspaceView == "Visualize";
        // Preserve measured native selector widths and semantic order from the established toolbar layout.
        _selectionPanel.WrapContents = true;
        _selectionPanel.Height = _selectionPanel.GetPreferredSize(new Size(_selectionPanel.Width, int.MaxValue)).Height;
        ambiance_ThemeSpectrum.Text = "";
        if (Text != "Spectrum") Text = "Spectrum";
        var footerHeight = Math.Max(24, TextRenderer.MeasureText("Ag", _captureStatusLabel.Font).Height + 8);
        _chooseSource.Size = new Size(Math.Max(110, TextRenderer.MeasureText(_chooseSource.Text, _chooseSource.Font).Width + 20), footerHeight);
        _captureDetails.Size = new Size(Math.Max(65, TextRenderer.MeasureText(_captureDetails.Text, _captureDetails.Font).Width + 20), footerHeight);
        _captureDetails.Location = new Point(width - 12 - _captureDetails.Width, height - footerHeight - 5);
        _chooseSource.Location = new Point(_captureDetails.Left - _chooseSource.Width - 6, _captureDetails.Top);
        _captureStatusLabel.Bounds = new Rectangle(12, _chooseSource.Top, Math.Max(1, _chooseSource.Left - 24), footerHeight);
        var compactFooter = _workspaceView == "Visualize" &&
            height - header - _selectionPanel.Height - footerHeight - controlHeight < 150 * dpiScale;
        _mainInspection.Bounds = compactFooter ? _captureStatusLabel.Bounds
            : new Rectangle(12, _captureStatusLabel.Top - controlHeight - 2, width - 24, controlHeight);
        _mainInspection.Visible = _workspaceView == "Visualize";
        _captureStatusLabel.Visible = !compactFooter;
        // The footer already exposes capture state; prioritize the calibrated viewport in short windows.
        var bannerHeight = height <= 480 * dpiScale ? 0 : Math.Max(19, (int)(20 * scale));
        _captureBanner.Bounds = new Rectangle(12, _selectionPanel.Bottom + (bannerHeight > 0 ? 3 : 0), width - 24, bannerHeight);
        _captureBanner.Visible = _workspaceView == "Visualize" && bannerHeight > 0;
        _workspaceHost.Bounds = new Rectangle(1, header + 5, Math.Max(1, width - 2),
            Math.Max(1, _captureStatusLabel.Top - header - 12));
        _workspaceHost.Visible = _workspaceView != "Visualize";
        var compactAnalysis = (_workspaceView == "Analyze" || _workspaceView == "Compare") && _workspaceHost.Height < 300 * DeviceDpi / 96f;
        _compactAnalysisNotice.Visible = compactAnalysis;
        if (_analysisWorkbench != null && !_analysisWorkbench.TopLevel)
            _analysisWorkbench.Visible = !compactAnalysis && (_workspaceView == "Analyze" || _workspaceView == "Compare");
        if (compactAnalysis) _compactAnalysisNotice.BringToFront();
        if (_workspaceHost.Visible) _workspaceHost.BringToFront();
        _workspaceNavigation.BringToFront(); _chooseSource.BringToFront(); _captureDetails.BringToFront();
        ambiance_ControlBox.BringToFront();
        _mainInspection.BringToFront(); _captureBanner.BringToFront();
    }

    internal void NavigateWorkspace(string name)
    {
        if (name != "Visualize" && name != "Analyze" && name != "Compare" && name != "Sessions")
            throw new ArgumentException("Unknown workspace.", nameof(name));
        _workspaceView = name;
        if (_analyzer != null && name != "Visualize")
        {
            EnsureAnalysisWorkspace();
            if (name == "Sessions")
            {
                if (_sessionLibrary == null)
                {
                    _sessionLibrary = new SessionLibraryControl(_analysisWorkspace) { Dock = DockStyle.Fill };
                    _sessionLibrary.NavigationRequested += request => NavigateWorkspace(request.Workspace);
                    _workspaceHost.Controls.Add(_sessionLibrary);
                }
                _sessionLibrary.ApplyPresentation(CurrentTheme().Ui);
                _sessionLibrary.RefreshLibrary(); _sessionLibrary.Visible = true; _sessionLibrary.BringToFront();
                if (_analysisWorkbench != null && !_analysisWorkbench.TopLevel) _analysisWorkbench.Hide();
            }
            else
            {
                if (_sessionLibrary != null) _sessionLibrary.Hide();
                EnsureWorkbench();
                if (_analysisWorkbench.TopLevel)
                {
                    _analysisWorkbench.Hide(); _analysisWorkbench.Owner = null;
                    _analysisWorkbench.TopLevel = false;
                    _analysisWorkbench.FormBorderStyle = FormBorderStyle.None;
                    _analysisWorkbench.SetEmbedded(true);
                    _workspaceHost.Controls.Add(_analysisWorkbench); _analysisWorkbench.Dock = DockStyle.Fill;
                }
                _analysisWorkbench.SelectWorkspaceView(name);
                _analysisWorkbench.Show(); _analysisWorkbench.BringToFront();
            }
        }
        ApplyWorkspacePresentation(CurrentTheme().Ui);
        RefreshSelectionLayout();
        UpdateCaptureStatus();
    }

    private void EnsureAnalysisWorkspace() => _analysisWorkspace ??= new AnalysisWorkspace();
    private void EnsureWorkbench()
    {
        EnsureAnalysisWorkspace();
        if (_analysisWorkbench != null && !_analysisWorkbench.IsDisposed) return;
        _analysisWorkbench = new AnalysisWorkbenchForm(_analysisWorkspace, _analyzer);
        _analysisWorkbench.ApplyPresentation(CurrentTheme().Ui);
        _analysisWorkbench.FormClosed += (s, e) => { _analysisWorkbench = null; };
    }

    private void ApplyWorkspacePresentation(VisualTheme ui)
    {
        if (_workspaceNavigation == null) return;
        foreach (var control in new Control[] { _workspaceBrand, _workspaceNavigation, _workspaceHost,
            _mainInspection, _captureBanner, _chooseSource, _captureDetails }) SharedPresentation.Apply(control, ui);
        _workspaceBrand.ForeColor = ui.PrimaryText;
        foreach (Button button in _workspaceNavigation.Controls)
        {
            var active = button.Name == "workspace" + _workspaceView;
            button.BackColor = active ? ui.Selection : ui.Canvas;
            button.ForeColor = active ? ui.PrimaryText : ui.SecondaryText;
            button.FlatAppearance.BorderColor = active ? ui.Focus : ui.Divider;
        }
        _analysisWorkbench?.ApplyPresentation(ui); _sessionLibrary?.ApplyPresentation(ui);
        RefreshCaptureBanner();
    }

    private void RefreshCaptureBanner()
    {
        if (_captureBanner == null || _analyzer == null) return;
        var status = _analyzer.CurrentStatus;
        var ui = CurrentTheme().Ui;
        var healthy = status.State == CaptureState.Capturing || status.State == CaptureState.Silent;
        _captureBanner.ForeColor = healthy ? ui.SecondaryText : BarColorThemes.EnsureContrast(Color.FromArgb(241, 185, 87), ui.Surface);
        _captureBanner.Text = _displayFrozen ? "DISPLAY FROZEN · capture continues independently"
            : status.State == CaptureState.Silent ? "DIGITAL SILENCE · the measured channels contain zero-valued audio"
            : healthy ? "LIVE SPECTRUM · measured band power · dBFS" : "AUDIO UNAVAILABLE · " + status.Message;
        _captureBanner.AccessibleDescription = _captureBanner.Text;
        if (_workspaceView != "Visualize" && _analysisWorkspace?.IsOffline == true)
            _captureStatusLabel.Text = (_analysisWorkspace.IsSavedSession ? "Saved session" : _analysisWorkspace.SourceKind) + " · " + _analysisWorkspace.SourceName + " | System capture: " + status.State;
        RefreshMainInspection();
    }

    private void ShowMainSourceMenu()
    {
        if (_analyzer == null) return;
        if (_mainSourceMenu?.Visible == true) return;
        var menu = _mainSourceMenu ??= new ContextMenuStrip { Name = "mainSourceMenu" };
        menu.Renderer = new MenuRenderer(CurrentTheme().Ui);
        // Escape handling continues after Closed; keep the native dropdown alive until form cleanup.
        foreach (var old in menu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        menu.Items.Clear();
        foreach (var source in _analyzer.GetAnalysisSources())
        {
            var selected = source;
            var item = new ToolStripMenuItem(source.ToString()) { Checked = source.Id == _analyzer.SelectedAnalysisSource.Id };
            item.Click += (s, e) => { _analyzer.SelectAnalysisSource(selected); UpdateCaptureStatus(); };
            menu.Items.Add(item);
        }
        menu.Show(_chooseSource, new Point(0, _chooseSource.Height));
    }

    private void AttachBarInspection(VerticalProgressBar bar)
    {
        bar.MouseMove += (s, e) => { _inspectedChannel = "Combined"; _inspectedBand = Array.IndexOf(_progressBars, bar); RefreshMainInspection(); };
        bar.MouseDown += (s, e) => { _inspectedChannel = "Combined"; _inspectedBand = Array.IndexOf(_progressBars, bar); _mainInspection.Focus(); RefreshMainInspection(); };
    }

    private void RefreshMainInspection()
    {
        if (_mainInspection == null || _analyzer == null) return;
        var readout = MainSpectrumInspector.Inspect(_displayedInspection, _inspectedBand, _visualMode, _analyzer.CurrentStatus,
            _displayFrozen, selectedChannel: _inspectedChannel);
        var compactFooter = _mainInspection.Top == _captureStatusLabel.Top;
        var text = compactFooter ? readout.CaptureText + " · " + readout.Text : readout.Text;
        if (_mainInspection.Text != text) _mainInspection.Text = text;
        var description = readout.Text + ". " + readout.CaptureText + ". " + _captureStatusLabel.Text;
        if (_mainInspection.AccessibleDescription != description)
        {
            _mainInspection.AccessibleDescription = description;
            _selectionToolTip.SetToolTip(_mainInspection, description);
        }
    }

    private void InspectAdvancedSpectrum(object sender, MouseEventArgs args)
    {
        var control = _advancedVisualizer;
        var plan = _displayedInspection?.Frame?.Plan;
        var plot = _visualMode == "Overview" ? control.OverviewSpectrumPlotBounds
            : _visualMode == "Stereo Compare" ? control.StereoComparePlotBounds
            : _visualMode == AdvancedSpectralBalanceMode ? control.SevenBandPlotBounds : control.SpectrumPlotBounds;
        var cards = _visualMode == "Band Matrix" ? Enumerable.Range(0, BAR_COUNT).Select(control.GetMatrixCardBounds).ToArray() : null;
        var hit = MainSpectrumHitTest.HitTest(_visualMode, plan, args.Location, plot, cards,
            _visualMode == "Stereo Spectrum" ? new[] { control.GetStereoSpectrumPlotBounds(0), control.GetStereoSpectrumPlotBounds(1) } : null,
            control.RadialCenter, control.RadialOuterRadius);
        _inspectedBand = hit.BandIndex; _inspectedChannel = hit.Channel ?? "Combined";
        RefreshMainInspection();
    }
}
