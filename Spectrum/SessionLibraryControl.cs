using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Spectrum;

internal sealed class SessionLibraryControl : UserControl
{
    private readonly AnalysisWorkspace _workspace;
    private readonly AnalysisSessionLibrary _library;
    private readonly ListBox _list;
    private readonly TextBox _search, _newName, _name, _notes;
    private readonly ComboBox _sort;
    private readonly CheckBox _favoritesOnly, _favorite;
    private readonly Label _metadata, _status, _error;
    private readonly Button _open, _compare, _export, _remove, _saveDetails;
    private readonly ToolTip _tips = new();
    private readonly List<Button> _commands = new();
    private bool _layoutReady, _sizing;
    private bool _reading;
    private VisualTheme _theme;
    private readonly Font _headingFont;
    private readonly Font _bodyFont;
    public SessionLibraryControl(AnalysisWorkspace workspace, string directory = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _library = new AnalysisSessionLibrary(directory);
        Name = "sessionLibrary"; Dock = DockStyle.Fill; Padding = new Padding(16);
        _bodyFont = new Font("Segoe UI", 9f); Font = _bodyFont;
        _headingFont = new Font(Font.FontFamily, 15f, FontStyle.Bold);
        var layout = new TableLayoutPanel { Name = "libraryLayout", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        foreach (var kind in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize })
            layout.RowStyles.Add(new RowStyle(kind, kind == SizeType.Percent ? 100 : 0));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(layout);
        var title = new Label { Name = "libraryHeading", Text = "Session library", Font = _headingFont, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
        layout.Controls.Add(title, 0, 0);
        var commands = Flow("libraryCommands");
        _newName = Field("libraryNewName", "Name for the current analysis", 210); _newName.Text = workspace.WorkspaceName;
        commands.Controls.Add(_newName);
        commands.Controls.Add(Command("librarySaveAnalysis", "Save analysis", () => Save(false), "Save current measurements without audio samples."));
        commands.Controls.Add(Command("librarySaveReference", "Save reference", () => Save(true), "Save the reference already captured in Compare."));
        commands.Controls.Add(Command("libraryImport", "Import…", Import, "Import a saved measurement session or workspace containing a reference. The source file is preserved."));
        layout.Controls.Add(commands, 0, 1);
        var filters = Flow("libraryFilters");
        _search = Field("librarySearch", "Search names, notes, sources and channels", 230); filters.Controls.Add(_search);
        SharedPresentation.SetCueText(_search, "Search names, notes or sources…");
        _sort = new ComboBox { Name = "librarySort", AccessibleName = "Sort saved analyses", DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, FlatStyle = FlatStyle.Flat };
        _sort.Items.AddRange(new object[] { "Recent activity", "Newest saved", "Name", "Favorites first" }); _sort.SelectedIndex = 0; filters.Controls.Add(_sort);
        _favoritesOnly = new CheckBox { Name = "libraryFavoritesOnly", Text = "Favorites only", AccessibleName = "Show favorite library entries", AutoSize = true, Margin = new Padding(8, 5, 8, 0) }; filters.Controls.Add(_favoritesOnly);
        filters.Controls.Add(Command("libraryRefresh", "Refresh", RefreshLibrary, "Refresh the saved files and recover from restored or repaired files."));
        layout.Controls.Add(filters, 0, 2);
        var body = new TableLayoutPanel { Name = "libraryBrowser", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 5, 0, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.Controls.Add(body, 0, 3);
        _list = new ListBox { Name = "libraryEntries", AccessibleName = "Saved analyses and references", Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 96, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 12, 0) };
        body.Controls.Add(_list, 0, 0);
        var details = new TableLayoutPanel { Name = "libraryEntryDetails", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Margin = Padding.Empty,
            AutoScroll = true, AutoScrollMargin = new Size(0, 6) };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 8; row++) details.RowStyles.Add(new RowStyle(row == 4 ? SizeType.Absolute : SizeType.AutoSize, row == 4 ? 100 : 0));
        body.Controls.Add(details, 1, 0);
        details.Controls.Add(new Label { Text = "Selected analysis", AutoSize = true, Margin = new Padding(0, 2, 0, 6) }, 0, 0);
        _name = Field("libraryEntryName", "Saved analysis name", 240); _name.Dock = DockStyle.Fill; _name.MaxLength = 160; details.Controls.Add(_name, 0, 1);
        _metadata = new Label { Name = "libraryMetadata", AccessibleName = "Saved analysis metadata", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) }; details.Controls.Add(_metadata, 0, 2);
        _favorite = new CheckBox { Name = "libraryFavorite", Text = "Favorite", AutoSize = true }; details.Controls.Add(_favorite, 0, 3);
        _notes = Field("libraryNotes", "Notes for the selected analysis", 240); _notes.Multiline = true; _notes.ScrollBars = ScrollBars.Vertical; _notes.MaxLength = 4000; _notes.Dock = DockStyle.Fill; _notes.MinimumSize = new Size(0, 70); details.Controls.Add(_notes, 0, 4);
        _saveDetails = Command("librarySaveDetails", "Save name / notes", SaveDetails, "Save the selected entry's name, notes and favorite state."); _saveDetails.Width = 150; details.Controls.Add(_saveDetails, 0, 5);
        var actions = Flow("libraryEntryActions");
        _open = Command("libraryOpen", "Open analysis", OpenSelected, "Open the selected measurement session in Analyze.");
        _compare = Command("libraryCompare", "Use as reference", CompareSelected, "Apply this saved curve as a reference while preserving the current measurement and settings.");
        _compare.Width = 140; actions.Controls.Add(_open); actions.Controls.Add(_compare); details.Controls.Add(actions, 0, 6);
        var fileActions = Flow("libraryFileActions");
        _export = Command("libraryExport", "Export…", ExportSelected, "Export a copy of the selected Spectrum XML file.");
        _remove = Command("libraryRemove", "Remove listing", RemoveSelected, "Unlink this entry from the library index. Its saved file is preserved in the library folder.");
        fileActions.Controls.Add(_export); fileActions.Controls.Add(_remove); details.Controls.Add(fileActions, 0, 7);
        _error = new Label { Name = "libraryError", AccessibleName = "Library error", AutoSize = true, Dock = DockStyle.Fill, Visible = false, Margin = new Padding(0, 3, 0, 5) }; layout.Controls.Add(_error, 0, 4);
        _status = new Label { Name = "libraryStatus", AccessibleName = "Library status", AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty }; layout.Controls.Add(_status, 0, 5);
        _tips.SetToolTip(_search, "Search names, notes, origin, channels or entry type.");
        _tips.SetToolTip(_status, _library.RootDirectory);
        _search.TextChanged += (_, _) => Reload(); _sort.SelectedIndexChanged += (_, _) => Reload(); _favoritesOnly.CheckedChanged += (_, _) => Reload();
        _list.SelectedIndexChanged += (_, _) => ReadSelection(); _list.DoubleClick += (_, _) => { if (Selected?.Kind == AnalysisLibraryKind.Session) OpenSelected(); else CompareSelected(); };
        _list.DrawItem += DrawEntry;
        _favorite.CheckedChanged += (_, _) => { if (!_reading && Selected != null) SaveDetails(); };
        _library.Changed += LibraryChanged;
        _layoutReady = true;
        ApplyPresentation(BarColorThemes.Resolve("Neutral").Ui); SizeForCurrentDpi(); Reload();
    }

    public event Action<AnalysisLibraryNavigationRequest> NavigationRequested;
    internal AnalysisSessionLibrary Library => _library;
    private AnalysisLibraryEntry Selected => _list.SelectedItem as AnalysisLibraryEntry;
    public void ApplyPresentation(VisualTheme theme)
    {
        _theme = theme; SharedPresentation.Apply(this, theme); BackColor = theme.Canvas;
        _list.BackColor = theme.ControlSurface; _list.ForeColor = theme.PrimaryText;
        _error.ForeColor = BarColorThemes.EnsureContrast(Color.FromArgb(211, 101, 74), theme.Canvas); _list.Invalidate();
    }
    public void RefreshLibrary() { _library.Refresh(); Reload(); }
    protected override void OnLayout(LayoutEventArgs args)
    {
        if (_layoutReady && !_sizing) SizeForCurrentDpi();
        base.OnLayout(args);
    }
    protected override void OnDpiChangedAfterParent(EventArgs args)
    {
        base.OnDpiChangedAfterParent(args); SizeForCurrentDpi();
    }
    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args); SizeForCurrentDpi();
    }
    protected override void OnFontChanged(EventArgs args)
    {
        base.OnFontChanged(args); SizeForCurrentDpi();
    }
    private void SizeForCurrentDpi()
    {
        if (!_layoutReady || _sizing || IsDisposed) return;
        _sizing = true;
        try
        {
            int Px(int logical) => (int)Math.Ceiling(logical * DeviceDpi / 96d);
            Padding = new Padding(Px(16));
            foreach (var button in _commands)
            {
                button.Margin = new Padding(0, 0, Px(7), Px(6));
                var text = TextRenderer.MeasureText(button.Text, button.Font);
                var preferred = button.GetPreferredSize(Size.Empty);
                var minimum = button == _saveDetails ? 150 : button == _compare ? 140 : 118;
                button.Size = new Size(Math.Max(preferred.Width, Math.Max(Px(minimum), text.Width + Px(24))),
                    Math.Max(preferred.Height, Math.Max(Px(30), text.Height + Px(12))));
            }
            _newName.Width = Px(210);
            _search.Width = Math.Max(Px(230), TextRenderer.MeasureText("Search names, notes or sources…", _search.Font).Width + Px(12));
            _sort.Width = Math.Max(Px(140), _sort.Items.Cast<object>().Select(item => TextRenderer.MeasureText(item.ToString(), _sort.Font).Width).DefaultIfEmpty(0).Max() + Px(30));
            _list.ItemHeight = Math.Max(Px(96), Font.Height * 3 + Px(24));
            _list.Margin = new Padding(0, 0, Px(12), 0);
            _notes.MinimumSize = new Size(0, Px(70));
            if (_notes.Parent is TableLayoutPanel details) details.RowStyles[4].Height = Px(100);
        }
        finally { _sizing = false; }
    }
    private void LibraryChanged()
    {
        if (IsDisposed || Disposing) return;
        if (IsHandleCreated && InvokeRequired) { try { BeginInvoke((Action)Reload); } catch (InvalidOperationException) { } }
        else Reload();
    }
    private void Reload()
    {
        if (_list == null || _reading) return;
        var selected = Selected?.Id; _reading = true;
        try
        {
            var entries = _library.Query(_search.Text, (AnalysisLibrarySort)Math.Max(0, _sort.SelectedIndex), _favoritesOnly.Checked);
            _list.BeginUpdate(); _list.Items.Clear(); foreach (var entry in entries) _list.Items.Add(entry);
            if (selected.HasValue) for (var index = 0; index < _list.Items.Count; index++) if (((AnalysisLibraryEntry)_list.Items[index]).Id == selected.Value) _list.SelectedIndex = index;
            if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.EndUpdate(); _status.Text = entries.Count == 0 ? "No saved analyses match. Save the current analysis or import a Spectrum file." : entries.Count + " saved " + (entries.Count == 1 ? "entry" : "entries") + " · measurements only, no recorded audio";
            if (!string.IsNullOrEmpty(_library.LastError)) ShowError(_library.LastError);
        }
        finally { _reading = false; }
        ReadSelection();
    }
    private void ReadSelection()
    {
        if (_reading) return; _reading = true;
        try
        {
            var entry = Selected; _name.Text = entry?.Name ?? ""; _notes.Text = entry?.Notes ?? ""; _favorite.Checked = entry?.IsFavorite ?? false;
            if (string.IsNullOrEmpty(_library.LastError) && (entry == null || entry.IsAvailable)) { _error.Visible = false; _error.Text = ""; }
            _name.Enabled = _notes.Enabled = _favorite.Enabled = _saveDetails.Enabled = _remove.Enabled = entry != null;
            _open.Enabled = entry?.IsAvailable == true && entry.Kind == AnalysisLibraryKind.Session;
            _compare.Enabled = _export.Enabled = entry?.IsAvailable == true;
            _metadata.Text = entry == null ? "Select a saved analysis to inspect its measurements and notes."
                : !entry.IsAvailable ? "Saved file unavailable · " + entry.Problem
                : entry.Kind + " · " + entry.BandCount + " bands\n" + entry.SampleRate.ToString("N0", CultureInfo.CurrentCulture) + " Hz · " + entry.Channel + " · " + entry.Response +
                    (entry.Channels > 0 ? " · " + entry.Channels + " channels" : "") + (entry.DurationSeconds > 0 ? "\n" + entry.DurationSeconds.ToString("0.##", CultureInfo.CurrentCulture) + " s analyzed" : "") +
                    "\nSaved " + entry.CreatedUtc.ToLocalTime().ToString("g") + (entry.LastOpenedUtc.HasValue ? "\nLast opened " + entry.LastOpenedUtc.Value.ToLocalTime().ToString("g") : "") +
                    (entry.CapturedUtc.HasValue ? "\nMeasured " + entry.CapturedUtc.Value.ToLocalTime().ToString("g") : "") +
                    (string.IsNullOrEmpty(entry.SourceName) ? "" : "\nOrigin: " + entry.SourceName);
            if (entry != null && !entry.IsAvailable) ShowError(entry.Problem);
        }
        finally { _reading = false; }
    }
    private void Save(bool reference) => Execute(() =>
    {
        var entry = _library.SaveCurrent(_workspace, _newName.Text, referenceOnly: reference); Select(entry.Id);
    }, reference ? "Reference saved in the local library." : "Analysis saved in the local library without audio.");
    private void SaveDetails() => Execute(() =>
    {
        var entry = RequireSelection(); _library.UpdateMetadata(entry.Id, _name.Text, _notes.Text, _favorite.Checked); Select(entry.Id);
    }, "Saved name, notes and favorite state.");
    private void OpenSelected() => Execute(() =>
    {
        var entry = RequireSelection(); _library.OpenSession(entry.Id, _workspace); NavigationRequested?.Invoke(new AnalysisLibraryNavigationRequest("Analyze", entry));
    }, "Saved analysis opened in Analyze.");
    private void CompareSelected() => Execute(() =>
    {
        var entry = RequireSelection(); _library.ApplyReference(entry.Id, _workspace); NavigationRequested?.Invoke(new AnalysisLibraryNavigationRequest("Compare", entry));
    }, "Saved curve applied as the reference. Current measurements were retained.");
    private void Import()
    {
        using var dialog = new OpenFileDialog { Title = "Import saved analysis or reference", Filter = "Spectrum files|*.spectrum-session.xml;*.spectrum-workspace.xml|XML files|*.xml", CheckFileExists = true, RestoreDirectory = true };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        Execute(() => { var entry = _library.Import(dialog.FileName); Select(entry.Id); }, "Imported a measurement copy. The original file was preserved.");
    }
    private void ExportSelected()
    {
        var entry = Selected; if (entry == null) return;
        var extension = entry.Kind == AnalysisLibraryKind.Session ? ".spectrum-session.xml" : ".spectrum-workspace.xml";
        var safeName = new string(entry.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        using var dialog = new SaveFileDialog { Title = "Export library entry", Filter = "Spectrum XML|*" + extension, FileName = safeName + extension, AddExtension = true, OverwritePrompt = true, RestoreDirectory = true };
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) Execute(() => _library.Export(entry.Id, dialog.FileName), "Exported the saved measurements.");
    }
    private void RemoveSelected() => Execute(() => { _library.Remove(RequireSelection().Id); }, "Library listing removed. Its saved XML file was preserved.");
    private AnalysisLibraryEntry RequireSelection() => Selected ?? throw new InvalidOperationException("Select a saved analysis first.");
    private void Select(Guid id) { Reload(); for (var index = 0; index < _list.Items.Count; index++) if (((AnalysisLibraryEntry)_list.Items[index]).Id == id) { _list.SelectedIndex = index; break; } }
    private void Execute(Action action, string success)
    {
        try { action(); _error.Visible = false; _error.Text = ""; _status.Text = string.IsNullOrEmpty(_library.LastError) ? success : success + " " + _library.LastError; }
        catch (Exception exception) when (exception is System.IO.IOException || exception is System.IO.InvalidDataException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is InvalidOperationException || exception is System.Xml.XmlException || exception is FormatException || exception is OverflowException || exception is KeyNotFoundException)
        { ShowError("The library action could not finish. " + exception.Message); }
    }
    private void ShowError(string message) { _error.Text = message; _error.Visible = true; }
    private void DrawEntry(object sender, DrawItemEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _list.Items.Count) return;
        var entry = (AnalysisLibraryEntry)_list.Items[args.Index]; var selected = (args.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? _theme.Selection : _theme.ControlSurface); args.Graphics.FillRectangle(background, args.Bounds);
        int Px(int logical) => (int)Math.Ceiling(logical * DeviceDpi / 96d);
        var thumbnail = new Rectangle(args.Bounds.Left + Px(9), args.Bounds.Top + Px(18), Px(96), Math.Max(Px(30), args.Bounds.Height - Px(36)));
        using (var surface = new SolidBrush(_theme.VisualizationSurface)) args.Graphics.FillRectangle(surface, thumbnail);
        using (var border = new Pen(_theme.Divider)) args.Graphics.DrawRectangle(border, thumbnail);
        if (entry.SpectrumPower.Count > 1)
        {
            var points = entry.SpectrumPower.Select((p, i) => new PointF(thumbnail.Left + i * thumbnail.Width / (float)(entry.SpectrumPower.Count - 1),
                thumbnail.Bottom - (float)(Math.Max(0, Math.Min(1, (p > 0 ? 10 * Math.Log10(p) + 120 : 0) / 120)) * thumbnail.Height))).ToArray();
            using var signal = new Pen(entry.Kind == AnalysisLibraryKind.Reference ? SharedPresentation.ReferenceCurve(_theme) : SharedPresentation.CurrentCurve(_theme), 1.5f * DeviceDpi / 96f); args.Graphics.DrawLines(signal, points);
        }
        var x = thumbnail.Right + Px(12); var width = Math.Max(Px(10), args.Bounds.Right - x - Px(10));
        var line = Math.Max(Px(24), Font.Height);
        TextRenderer.DrawText(args.Graphics, entry.ToString(), Font, new Rectangle(x, args.Bounds.Top + Px(9), width, line), _theme.PrimaryText, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        var summary = entry.IsAvailable ? entry.Kind + " · " + entry.SampleRate.ToString("N0") + " Hz · " + entry.Channel + " · " + entry.Response : "Saved file unavailable";
        TextRenderer.DrawText(args.Graphics, summary, Font, new Rectangle(x, args.Bounds.Top + Px(9) + line, width, line), _theme.SecondaryText, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(args.Graphics, string.IsNullOrWhiteSpace(entry.Notes) ? "Saved " + entry.CreatedUtc.ToLocalTime().ToString("g") : entry.Notes.Replace('\r', ' ').Replace('\n', ' '),
            Font, new Rectangle(x, args.Bounds.Top + Px(9) + line * 2, width, line), _theme.SecondaryText, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        args.DrawFocusRectangle();
    }
    private static FlowLayoutPanel Flow(string name) => new() { Name = name, Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 6) };
    private static TextBox Field(string name, string accessible, int width) => new() { Name = name, AccessibleName = accessible, Width = width, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 8, 6) };
    private Button Command(string name, string text, Action action, string description)
    {
        var button = new Button { Name = name, Text = text, AccessibleName = text.Replace("…", ""), AccessibleDescription = description,
            Width = 118, Height = 30, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 7, 6), UseVisualStyleBackColor = false };
        _commands.Add(button); button.Click += (_, _) => action(); _tips.SetToolTip(button, description); return button;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _library.Changed -= LibraryChanged; _tips.Dispose(); }
        base.Dispose(disposing); if (disposing) { _headingFont.Dispose(); _bodyFont.Dispose(); }
    }
}
