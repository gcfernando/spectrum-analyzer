using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Spectrum;

// Presentation only: values, handlers and scientific signal roles remain owned by their controls.
internal static class SharedPresentation
{
    private sealed class ComboAppearance { internal VisualTheme Theme; }
    private static readonly ConditionalWeakTable<ComboBox, ComboAppearance> ComboThemes = new();
    private sealed class CueText { internal string Text; }
    private static readonly ConditionalWeakTable<TextBox, CueText> Cues = new();

    internal static void SetCueText(TextBox textBox, string text)
    {
        if (textBox == null) throw new ArgumentNullException(nameof(textBox));
        if (!Cues.TryGetValue(textBox, out var cue))
        {
            cue = new CueText(); Cues.Add(textBox, cue);
            var state = cue;
            textBox.HandleCreated += (_, _) => SendMessage(textBox.Handle, 0x1501, (IntPtr)1, state.Text ?? "");
        }
        cue.Text = text ?? "";
        if (textBox.IsHandleCreated && !textBox.IsDisposed) SendMessage(textBox.Handle, 0x1501, (IntPtr)1, cue.Text);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, string lParam);
    internal static void Apply(Control root, VisualTheme theme)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        if (root is AnalysisPlotControl plot) { plot.ApplyPresentation(theme); return; }
        var field = root is ComboBox || root is TextBoxBase || root is NumericUpDown || root is ListBox;
        root.BackColor = field ? theme.ControlSurface : root is Form ? theme.Canvas
            : root is Button ? theme.ControlSurface : root is Panel || root is GroupBox || root is TabPage ? theme.Surface
            : root.Parent?.BackColor ?? theme.Canvas;
        root.ForeColor = theme.PrimaryText;
        if (root is Label label && !label.Name.StartsWith("analysisMetric_", StringComparison.Ordinal)
            && !label.Name.StartsWith("analysisInspect", StringComparison.Ordinal)) label.ForeColor = theme.SecondaryText;
        if (root is Button button)
        {
            button.UseVisualStyleBackColor = false;
            button.FlatAppearance.BorderColor = theme.Divider;
            button.FlatAppearance.MouseOverBackColor = theme.Hover;
            button.FlatAppearance.MouseDownBackColor = theme.Selection;
        }
        if (root is ComboBox combo)
        {
            if (!ComboThemes.TryGetValue(combo, out var appearance))
            {
                appearance = new ComboAppearance(); ComboThemes.Add(combo, appearance);
                combo.DrawItem += PaintComboItem;
                combo.FontChanged += (_, _) => combo.ItemHeight = Math.Max(combo.Font.Height + 3, 18);
            }
            appearance.Theme = theme;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.FlatStyle = FlatStyle.Flat;
            combo.ItemHeight = Math.Max(combo.Font.Height + 3, 18);
        }
        if (root is CheckBox check) check.UseVisualStyleBackColor = false;
        if (root is RadioButton radio) radio.UseVisualStyleBackColor = false;
        foreach (Control child in root.Controls) Apply(child, theme);
        root.Invalidate();
    }

    private static void PaintComboItem(object sender, DrawItemEventArgs args)
    {
        var combo = (ComboBox)sender;
        if (!ComboThemes.TryGetValue(combo, out var appearance)) return;
        var theme = appearance.Theme;
        var selected = (args.State & DrawItemState.Selected) != 0 && (args.State & DrawItemState.ComboBoxEdit) == 0;
        using var fill = new SolidBrush(selected ? theme.Selection : theme.ControlSurface);
        args.Graphics.FillRectangle(fill, args.Bounds);
        var text = args.Index >= 0 && args.Index < combo.Items.Count ? combo.GetItemText(combo.Items[args.Index]) : combo.Text;
        TextRenderer.DrawText(args.Graphics, text, args.Font, Rectangle.Inflate(args.Bounds, -3, 0),
            combo.Enabled ? theme.PrimaryText : theme.DisabledText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((args.State & DrawItemState.Focus) != 0) args.DrawFocusRectangle();
    }

    internal static Color CurrentCurve(VisualTheme theme) => BarColorThemes.EnsureContrast(Color.FromArgb(85, 217, 222), theme.VisualizationSurface);
    internal static Color ReferenceCurve(VisualTheme theme) => BarColorThemes.EnsureContrast(Color.FromArgb(255, 178, 92), theme.VisualizationSurface);
}
