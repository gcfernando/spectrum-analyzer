// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class PinLayoutRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void StatefulUtilitiesPreserveGeometryAtResponsiveLayoutBoundaries(float fontScale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm();
            var fonts = ScaleFonts(form, fontScale);
            try
            {
                // Font scaling simulates larger control text; it is not a physical monitor-DPI test.
                Reflow(form);
                var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
                var chrome = Field<Ambiance_ControlBox>(form, "ambiance_ControlBox");
                var theme = Field<Ambiance_ThemeContainer>(form, "ambiance_ThemeSpectrum");
                var total = panel.Controls.Cast<Control>().Where(control => !(control is Label))
                    .Sum(control => control.Width + control.Margin.Horizontal);
                var labelWidth = panel.Controls.OfType<Label>().Sum(label => label.Width + label.Margin.Horizontal);
                var inlineThreshold = total + chrome.Width + TextRenderer.MeasureText("Audio Spectrum Analyzer", theme.Font).Width + 24 + 12 + 16;
                var compactTitleThreshold = total + chrome.Width + TextRenderer.MeasureText("Spectrum", theme.Font).Width + 24 + 12 + 16;
                var widths = new[] { inlineThreshold, inlineThreshold - 1, inlineThreshold + 1,
                    compactTitleThreshold - 1, compactTitleThreshold, compactTitleThreshold + 1,
                    900, 1200, total + 23, total + 24, total + 25,
                    inlineThreshold + labelWidth - 1, inlineThreshold + labelWidth, inlineThreshold + labelWidth + 1 }
                    .Where(width => width >= form.MinimumSize.Width).Distinct().ToArray();
                foreach (var width in widths)
                {
                    form.ClientSize = new Size(width, 600);
                    Reflow(form);
                    var before = Geometry(form);
                    SaveEvidence(form, "Boundary-" + fontScale, form.ClientSize, "unpinned");
                    for (var repeat = 0; repeat < 20; repeat++)
                    {
                        Call(form, "TogglePin");
                        Assert.True(form.TopMost);
                        Assert.NotEqual(0, GetWindowLong(form.Handle, -20) & 8);
                        Assert.Equal("Pinned", Field<Button>(form, "_pinButton").Text);
                        if (repeat == 0) SaveEvidence(form, "Boundary-" + fontScale, form.ClientSize, "pinned");
                        Assert.Equal(before, Geometry(form));
                        Call(form, "TogglePin");
                        Assert.False(form.TopMost);
                        Assert.Equal(0, GetWindowLong(form.Handle, -20) & 8);
                        Assert.Equal("Pin", Field<Button>(form, "_pinButton").Text);
                        Assert.Equal(before, Geometry(form));
                    }
                    form.SetDisplayFrozen(true);
                    Assert.Equal("Resume", Field<Button>(form, "_freezeButton").Text);
                    Assert.Equal(before, Geometry(form));
                    form.SetDisplayFrozen(false);
                    Assert.Equal("Freeze", Field<Button>(form, "_freezeButton").Text);
                    Assert.Equal(before, Geometry(form));
                    // Exercise both guide labels without saving the test's choices to user settings.
                    var guides = Field<bool>(form, "_showGuides");
                    CombinedRandomizationTests.SetField(form, "_showGuides", !guides);
                    Call(form, "UpdateUtilityButtons");
                    Assert.Equal(before, Geometry(form));
                    CombinedRandomizationTests.SetField(form, "_showGuides", guides);
                    Call(form, "UpdateUtilityButtons");
                    Assert.Equal(before, Geometry(form));
                }
            }
            finally
            {
                form.Dispose();
                foreach (var font in fonts) font.Dispose();
            }
        });
    }

    [Fact]
    public void DefaultWindowKeepsTheToolbarInlineWithAReadableTitleInBothPinStates()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm();
            form.ClientSize = new Size(1200, 480);
            Reflow(form);
            var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
            var theme = Field<Ambiance_ThemeContainer>(form, "ambiance_ThemeSpectrum");
            Assert.Equal(7, panel.Top);
            Assert.False(panel.WrapContents);
            Assert.True(TextRenderer.MeasureText(theme.Text, theme.Font).Width <= theme.TitleRightInset - 16);
            var before = Geometry(form);
            Call(form, "TogglePin");
            Assert.True(form.TopMost);
            Assert.Equal(before, Geometry(form));
            Call(form, "TogglePin");
            Assert.False(form.TopMost);
            Assert.Equal(before, Geometry(form));
        });
    }

    [Fact]
    public void PinRemainsStableThroughResizeMoveMinimizeRestoreAndModeChanges()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm();
            var modes = Field<ComboBox>(form, "_modeSelector");
            foreach (var size in new[] { new Size(900, 350), new Size(1200, 480), new Size(1600, 600) })
            {
                form.ClientSize = size;
                var baseline = new Dictionary<string, string[]>();
                var choices = new[] { "Radial Spectrum", "Spectrum", "Stereo Spectrum", "Bricks" };
                foreach (var mode in choices)
                {
                    modes.SelectedItem = mode;
                    Reflow(form);
                    baseline[mode] = Geometry(form);
                    SaveEvidence(form, mode, size, "unpinned");
                }
                Call(form, "TogglePin");
                Assert.True(form.TopMost);
                foreach (var mode in choices)
                {
                    // Change the actual native selector while pinned and compare the same
                    // settled mode with its unpinned baseline, not a different mode's plot.
                    modes.SelectedItem = mode;
                    Reflow(form);
                    var before = baseline[mode];
                    Assert.Equal(before, Geometry(form));
                    SaveEvidence(form, mode, size, "pinned");
                    var location = form.Location;
                    form.Location = new Point(location.X + 8, location.Y + 8);
                    Assert.Equal(before.Skip(2), Geometry(form).Skip(2));
                    form.Location = location;
                    form.WindowState = FormWindowState.Minimized;
                    Application.DoEvents();
                    form.WindowState = FormWindowState.Normal;
                    Application.DoEvents();
                    Assert.True(form.TopMost);
                    Reflow(form);
                    Assert.Equal(before, Geometry(form));
                }
                Call(form, "TogglePin");
                Assert.False(form.TopMost);
                Assert.Equal(baseline[choices.Last()], Geometry(form));
            }
        });
    }

    private static FormAudioSpectrum CreateShownForm()
    {
        var form = CombinedRandomizationTests.CreateForm(mode: "Radial Spectrum");
        CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
        var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
        form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
        form.ShowInTaskbar = false;
        form.Show();
        form.ClientSize = new Size(1600, 600);
        Reflow(form);
        return form;
    }

    private static string[] Geometry(FormAudioSpectrum form)
    {
        var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
        var theme = Field<Ambiance_ThemeContainer>(form, "ambiance_ThemeSpectrum");
        return new[] { form.Bounds.ToString(), form.ClientSize.ToString(), form.MinimumSize.ToString(),
                panel.Bounds.ToString(), panel.WrapContents.ToString(), theme.TitleRightInset.ToString(),
                Field<Control>(form, "_captureStatusLabel").Bounds.ToString(),
                Field<Control>(form, "_advancedVisualizer").Bounds.ToString(), theme.Bounds.ToString(), theme.Text,
                Field<Ambiance_ControlBox>(form, "ambiance_ControlBox").Bounds.ToString() }
            .Concat(Field<Ambiance_ControlBox>(form, "ambiance_ControlBox").Controls.Cast<Control>()
                .Select(control => control.Name + ":" + control.Bounds + ":" + control.Visible))
            .Concat(panel.Controls.Cast<Control>().Select(control => control.Name + ":" + control.Bounds + ":" + control.Visible))
            .Concat(Field<VerticalProgressBar[]>(form, "_progressBars").Select(bar => bar.Bounds.ToString()))
            .ToArray();
    }

    private static List<Font> ScaleFonts(Form form, float scale)
    {
        var controls = Descendants(form).Prepend(form).ToArray();
        var originals = controls.Select(control => control.Font).ToArray();
        var fonts = new List<Font>();
        for (var index = 0; index < controls.Length; index++)
        {
            var font = new Font(originals[index].FontFamily, originals[index].SizeInPoints * scale, originals[index].Style);
            fonts.Add(font);
            controls[index].Font = font;
        }
        return fonts;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Reflow(FormAudioSpectrum form)
    {
        Call(form, "PositionSelectionPanel");
        Call(form, "RecalculateBarLayout");
        form.PerformLayout();
    }

    private static void SaveEvidence(FormAudioSpectrum form, string mode, Size size, string state)
    {
        var directory = Environment.GetEnvironmentVariable("SPECTRUM_PIN_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, form.ClientRectangle);
        bitmap.Save(Path.Combine(directory, mode.Replace(' ', '-') + "-" + size.Width + "x" + size.Height + "-" + state + ".png"), ImageFormat.Png);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
    private static void Call(object target, string method) =>
        target.GetType().GetMethod(method, PrivateInstance).Invoke(target, null);
}
