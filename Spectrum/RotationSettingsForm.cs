// Developer :: > Gehan Fernando
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Spectrum;

// Small dialog for choosing fixed vs timed-random visual rotation
internal sealed class RotationSettingsForm : Form
{
    private readonly RadioButton _fixedOption;
    private readonly RadioButton _randomOption;
    private readonly NumericUpDown _interval;
    private readonly Font _formFont = new("Segoe UI", 9f);
    private readonly Font _headingFont;

    // Built in code (no designer) and colored from the current theme so it matches the main window
    public RotationSettingsForm(bool randomEnabled, int intervalMinutes, BarColorTheme theme)
    {
        var ui = theme.Ui;
        // Fixed-size modal dialog, centered over the parent and kept out of the taskbar
        Text = "Mode, Style and Theme Rotation";
        Icon = Properties.Resources.Spectrum;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleDimensions = new SizeF(7f, 15f);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(380, 246);
        BackColor = ui.Surface;
        ForeColor = ui.PrimaryText;
        Font = _formFont;
        _headingFont = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);

        var heading = new Label
        {
            AutoSize = false,
            Text = "Visual selection",
            Font = _headingFont,
            Location = new Point(20, 14),
            Size = new Size(330, 26),
            ForeColor = ui.PrimaryText
        };

        var subtitle = new Label
        {
            AutoSize = false,
            Text = "Choose how the visualization changes while audio is playing.",
            Location = new Point(21, 43),
            Size = new Size(338, 28),
            ForeColor = ui.SecondaryText
        };

        // Thin accent line under the header
        var accent = new Panel
        {
            BackColor = ui.Focus,
            Location = new Point(20, 76),
            Size = new Size(340, 1)
        };

        _fixedOption = new RadioButton
        {
            AutoSize = true,
            Text = "&Fixed",
            Name = "fixedOption",
            AccessibleName = "Fixed visual selection",
            TabIndex = 0,
            ForeColor = ui.PrimaryText,
            Location = new Point(22, 87),
            Checked = !randomEnabled
        };

        var fixedDescription = new Label
        {
            AutoSize = false,
            Text = "Keep your selected mode, style and theme.",
            Location = new Point(45, 108),
            Size = new Size(300, 20),
            ForeColor = ui.SecondaryText
        };

        _randomOption = new RadioButton
        {
            AutoSize = true,
            Text = "Timed &random",
            Name = "randomOption",
            AccessibleName = "Timed random visual selection",
            TabIndex = 1,
            ForeColor = ui.PrimaryText,
            Location = new Point(22, 132),
            Checked = randomEnabled
        };

        var randomDescription = new Label
        {
            AutoSize = false,
            Text = "Select a new mode, style and theme together.",
            Location = new Point(45, 153),
            Size = new Size(320, 20),
            ForeColor = ui.SecondaryText
        };

        var intervalLabel = new Label
        {
            AutoSize = true,
            Text = "Change &every",
            TabIndex = 2,
            Location = new Point(45, 184),
            ForeColor = ui.SecondaryText
        };

        _interval = new NumericUpDown
        {
            Name = "rotationInterval",
            AccessibleName = "Rotation interval in minutes",
            AccessibleDescription = "Time between random visual selections, from 1 to 240 minutes.",
            TabIndex = 3,
            Minimum = 1,
            Maximum = 240,
            // Clamp in case a saved value is outside the allowed range, NumericUpDown would throw
            Value = Math.Max(1, Math.Min(240, intervalMinutes)),
            Location = new Point(132, 180),
            Size = new Size(62, 25),
            BackColor = ui.ControlSurface,
            ForeColor = ui.PrimaryText
        };

        var minutesLabel = new Label
        {
            AutoSize = true,
            Text = "minutes",
            Location = new Point(200, 184),
            ForeColor = ui.SecondaryText
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(194, 208),
            Size = new Size(78, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = ui.ControlSurface,
            ForeColor = ui.PrimaryText
        };
        // Flat buttons need their hover/pressed colors set by hand to follow the theme
        cancelButton.FlatAppearance.BorderColor = ui.Divider;
        cancelButton.FlatAppearance.MouseOverBackColor = ui.Hover;
        cancelButton.FlatAppearance.MouseDownBackColor = ui.Selection;
        cancelButton.TabIndex = 5;

        var applyButton = new Button
        {
            Text = "Apply",
            DialogResult = DialogResult.OK,
            Location = new Point(280, 208),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = ui.Selection,
            ForeColor = ui.PrimaryText
        };
        applyButton.FlatAppearance.BorderColor = ui.Focus;
        applyButton.FlatAppearance.MouseOverBackColor = ui.Hover;
        applyButton.FlatAppearance.MouseDownBackColor = ui.Selection;
        applyButton.TabIndex = 4;

        Controls.AddRange(new Control[]
        {
            heading, subtitle, accent, _fixedOption, fixedDescription, _randomOption, randomDescription,
            intervalLabel, _interval,
            minutesLabel, cancelButton, applyButton
        });

        // Enter applies, Esc cancels; interval is only editable while random mode is on
        AcceptButton = applyButton;
        CancelButton = cancelButton;
        _interval.Enabled = randomEnabled;
        _randomOption.CheckedChanged += (sender, args) => _interval.Enabled = _randomOption.Checked;
        ApplyPresentation(ui);
    }

    internal void ApplyPresentation(VisualTheme theme)
    {
        SharedPresentation.Apply(this, theme);
        if (AcceptButton is Button apply) apply.BackColor = theme.Selection;
    }

    // What the caller reads after the dialog returns OK
    public bool RandomEnabled => _randomOption.Checked;

    public int IntervalMinutes => (int)_interval.Value;

    // Fonts are created by us, so we have to dispose them ourselves
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _headingFont.Dispose();
            _formFont.Dispose();
        }
    }
}
