using System;
using System.Drawing;
using System.Windows.Forms;

namespace Spectrum;

internal sealed class RotationSettingsForm : Form
{
    private readonly RadioButton _fixedOption;
    private readonly RadioButton _randomOption;
    private readonly NumericUpDown _interval;
    private readonly Font _formFont = new("Segoe UI", 9f);
    private readonly Font _headingFont;

    public RotationSettingsForm(bool randomEnabled, int intervalMinutes)
    {
        Text = "Mode and Theme Rotation";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(340, 190);
        BackColor = Color.FromArgb(28, 27, 25);
        ForeColor = Color.FromArgb(230, 228, 222);
        Font = _formFont;
        _headingFont = new Font(_formFont, FontStyle.Bold);

        var heading = new Label
        {
            AutoSize = false,
            Text = "Visual selection",
            Font = _headingFont,
            Location = new Point(18, 14),
            Size = new Size(300, 22)
        };

        _fixedOption = new RadioButton
        {
            AutoSize = true,
            Text = "Fixed - keep the current mode and theme",
            Location = new Point(20, 48),
            Checked = !randomEnabled
        };

        _randomOption = new RadioButton
        {
            AutoSize = true,
            Text = "Random - change both automatically",
            Location = new Point(20, 76),
            Checked = randomEnabled
        };

        var intervalLabel = new Label
        {
            AutoSize = true,
            Text = "Change every",
            Location = new Point(40, 111)
        };

        _interval = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 240,
            Value = Math.Max(1, Math.Min(240, intervalMinutes)),
            Location = new Point(128, 107),
            Size = new Size(62, 25),
            BackColor = Color.FromArgb(44, 42, 38),
            ForeColor = Color.White
        };

        var minutesLabel = new Label
        {
            AutoSize = true,
            Text = "minutes",
            Location = new Point(196, 111)
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(158, 150),
            Size = new Size(76, 28),
            FlatStyle = FlatStyle.Flat
        };

        var applyButton = new Button
        {
            Text = "Apply",
            DialogResult = DialogResult.OK,
            Location = new Point(242, 150),
            Size = new Size(76, 28),
            FlatStyle = FlatStyle.Flat
        };

        Controls.AddRange(new Control[]
        {
            heading, _fixedOption, _randomOption, intervalLabel, _interval,
            minutesLabel, cancelButton, applyButton
        });

        AcceptButton = applyButton;
        CancelButton = cancelButton;
        _interval.Enabled = randomEnabled;
        _randomOption.CheckedChanged += (sender, args) => _interval.Enabled = _randomOption.Checked;
    }

    public bool RandomEnabled => _randomOption.Checked;

    public int IntervalMinutes => (int)_interval.Value;

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
