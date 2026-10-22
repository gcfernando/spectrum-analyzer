using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Spectrum;

public partial class FormAudioSpectrum
{
    internal Form CreateVisualizationBrowser()
    {
        var ui = CurrentTheme().Ui;
        var dialog = new Form { Name = "visualizationBrowser", Text = "Find a visualization", Icon = Properties.Resources.Spectrum,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(860, 470), MinimumSize = new Size(680, 400),
            MinimizeBox = false, MaximizeBox = false, Font = ambiance_ThemeSpectrum.Font, AutoScaleMode = AutoScaleMode.Font };
        var search = new TextBox { Name = "visualizationSearch", AccessibleName = "Search visualization names and purposes", Dock = DockStyle.Fill };
        SharedPresentation.SetCueText(search, "Search views…");
        var group = new ComboBox { Name = "visualizationGroup", AccessibleName = "Visualization group", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        group.Items.AddRange(new[] { "All views", "Favorites", "Recent" }.Concat(s_visualModes.Select(ModeGroup).Distinct()).ToArray()); group.SelectedIndex = 0;
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10), ColumnCount = 2 };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        searchRow.Controls.Add(search, 0, 0); searchRow.Controls.Add(group, 1, 0);
        var list = new ListBox { Name = "visualizationResults", AccessibleName = "Matching visualizations", IntegralHeight = false, Dock = DockStyle.Fill };
        var preview = new PictureBox { Name = "visualizationPreview", AccessibleName = "Captured audio snapshot preview, or waiting for audio", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
        var description = new Label { Name = "modeDescription", AccessibleName = "Selected mode purpose", Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(8) };
        var previewLabel = new Label { Text = "PREVIEW · captured snapshot, not live audio", Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 3, 0, 0) };
        var details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        details.Controls.Add(preview); details.Controls.Add(previewLabel); details.Controls.Add(description);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10, 0, 10, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        body.Controls.Add(list, 0, 0); body.Controls.Add(details, 1, 0);
        var choose = new Button { Name = "visualizationOpen", Text = "Open view", AutoSize = true, Height = 30, FlatStyle = FlatStyle.Flat };
        var favorite = new Button { Name = "visualizationFavorite", Text = "Add favorite", AutoSize = true, Height = 30, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 30, FlatStyle = FlatStyle.Flat };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        footer.Controls.Add(cancel); footer.Controls.Add(choose); footer.Controls.Add(favorite);
        var names = new List<string>();
        void Select()
        {
            var mode = list.SelectedIndex < 0 ? null : names[list.SelectedIndex];
            choose.Enabled = favorite.Enabled = mode != null;
            description.Text = mode == null ? "No matching views. Try another name or group." : GetModeDescription(mode);
            favorite.Text = mode != null && _catalog.IsFavorite(mode) ? "Remove favorite" : "Add favorite";
            var old = preview.Image; preview.Image = mode == null ? null : RenderModePreview(mode, new Size(560, 280)); old?.Dispose();
        }
        void Filter(string keep = null)
        {
            names.Clear(); list.Items.Clear();
            var category = group.SelectedItem as string;
            var source = category == "Recent" ? _catalog.Recent : (IReadOnlyList<string>)s_visualModes;
            foreach (var mode in source)
            {
                if (category == "Favorites" && !_catalog.IsFavorite(mode)) continue;
                if (category != "Recent" && category != "All views" && category != "Favorites" && category != ModeGroup(mode)) continue;
                if ((mode + " " + ModeGroup(mode) + " " + GetModeDescription(mode)).IndexOf(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                names.Add(mode); list.Items.Add((_catalog.IsFavorite(mode) ? "★ " : "") + ModeGroup(mode) + " · " + mode);
            }
            list.SelectedIndex = names.Count == 0 ? -1 : Math.Max(0, names.IndexOf(keep)); Select();
        }
        choose.Click += (s, e) => { if (list.SelectedIndex < 0) return; _modeSelector.SelectedItem = names[list.SelectedIndex]; NavigateWorkspace("Visualize"); dialog.DialogResult = DialogResult.OK; };
        favorite.Click += (s, e) => { if (list.SelectedIndex < 0) return; var mode = names[list.SelectedIndex]; _catalog.ToggleFavorite(mode); Filter(mode); };
        list.SelectedIndexChanged += (s, e) => Select();
        list.DoubleClick += (s, e) => { if (choose.Enabled) choose.PerformClick(); };
        search.TextChanged += (s, e) => Filter(); group.SelectedIndexChanged += (s, e) => Filter();
        dialog.Controls.Add(body); dialog.Controls.Add(searchRow); dialog.Controls.Add(footer);
        dialog.AcceptButton = choose; dialog.CancelButton = cancel;
        dialog.Disposed += (s, e) => preview.Image?.Dispose();
        SharedPresentation.Apply(dialog, ui); Filter(_visualMode);
        return dialog;
    }
}
