// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

// Acceptance tests for how the UI holds up at different DPI and font scales.
public class UiScalingAcceptanceTests
{
    private readonly ITestOutputHelper _output;
    public UiScalingAcceptanceTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> LayoutCases()
    {
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (var size in new[] { new Size(900, 250), new Size(1184, 378), new Size(1920, 1080) })
            yield return new object[] { scale, size.Width, size.Height };
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void NativeSelectorTextStatusAndPlotsStayInsideMeasuredScaledLayout(float scale, int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: "Spectrum");
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            ShowWithoutCapture(form);
            using var fonts = ScaleEquivalent(form, scale);
            form.ClientSize = new Size(width, height);
            form.PerformLayout();
            Call(form, "PositionSelectionPanel");
            Call(form, "RecalculateBarLayout");
            form.PerformLayout();
            var panel = CombinedRandomizationTests.GetField<FlowLayoutPanel>(form, "_selectionPanel");
            panel.PerformLayout();
            Assert.Equal(form.ClientSize, panel.Parent.ClientSize);
            Assert.True(panel.Parent.ClientRectangle.Contains(panel.Bounds), $"Panel {panel.Bounds} exceeds {width}x{height} at {scale}.");
            var selectors = new[]
            {
                CombinedRandomizationTests.GetField<ComboBox>(form, "_modeSelector"),
                CombinedRandomizationTests.GetField<ComboBox>(form, "_styleSelector"),
                CombinedRandomizationTests.GetField<ComboBox>(form, "_themeSelector")
            };
            foreach (var selector in selectors)
            {
                Assert.True(selector.Width > 0 && selector.Bottom <= panel.ClientSize.Height,
                    $"{selector.Name} {selector.Bounds} exceeds panel {panel.ClientRectangle} at {scale}.");
                foreach (var text in selector.Items.Cast<string>())
                {
                    selector.SelectedItem = text;
                    var info = new ComboBoxInfo { Size = Marshal.SizeOf(typeof(ComboBoxInfo)) };
                    Assert.True(GetComboBoxInfo(selector.Handle, ref info));
                    var iconPadding = selector == selectors[2] ? 45 : 34;
                    // Icon and text space in the item renderer is measured on its own, apart from the native arrow.
                    var left = GetTextInset(form, selector, iconPadding);
                    var required = TextRenderer.MeasureText(text, selector.Font);
                    var available = info.Item.Right - info.Item.Left - left - 4;
                    Assert.True(required.Width <= available,
                        $"{selector.Name}/{text}: needs {required.Width}px, has {available}px at {scale}, {width}x{height}.");
                    Assert.True(required.Height <= info.Item.Bottom - info.Item.Top,
                        $"{selector.Name}/{text}: font {required.Height}px exceeds native item {info.Item.Bottom - info.Item.Top}px at {scale}.");
                }
            }
            var status = CombinedRandomizationTests.GetField<Label>(form, "_captureStatusLabel");
            var statusHeight = TextRenderer.MeasureText("Capturing | WASAPI loopback | 48,000 Hz / 2 ch", status.Font).Height;
            Assert.True(status.Height >= statusHeight);
            Assert.True(status.Parent.ClientRectangle.Contains(status.Bounds));
            var plot = CombinedRandomizationTests.GetField<VerticalProgressBar[]>(form, "_progressBars")[0];
            Assert.True(plot.Top >= panel.Bottom, $"Plot starts at {plot.Top}, selectors end at {panel.Bottom}.");
            Assert.True(plot.Bottom <= status.Top, $"Plot ends at {plot.Bottom}, status starts at {status.Top}.");
            foreach (var label in CombinedRandomizationTests.GetField<Label[]>(form, "_axisLabels"))
                Assert.True(label.Bottom <= status.Top, $"Axis {label.Text} overlaps status at {scale}.");
            var chrome = form.Controls.OfType<Ambiance_ThemeContainer>().Single().Controls.OfType<Ambiance_ControlBox>().Single();
            foreach (Control button in chrome.Controls)
                if (button.Name != "maximizeButton" || chrome.EnableMaximize)
                    Assert.True(chrome.ClientRectangle.Contains(button.Bounds),
                        $"Window button {button.Name} {button.Bounds} exceeds chrome {chrome.ClientRectangle} at {scale}.");
            _output.WriteLine($"{scale:P0} requested {width}x{height}, actual {form.ClientSize}: panel {panel.Bounds}; native items fit; status {status.Bounds}; plot {plot.Bounds}.");
        });
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void RotationDialogScaledTextAndAccessibleInputRemainContained(float scale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var dialog = new RotationSettingsForm(true, 240, BarColorThemes.PlatinumLight);
            using var fonts = ScaleEquivalent(dialog, scale);
            dialog.PerformLayout();
            foreach (Control control in dialog.Controls)
            {
                Assert.True(dialog.ClientRectangle.Contains(control.Bounds),
                    $"{control.GetType().Name}/{control.Text} {control.Bounds} exceeds {dialog.ClientRectangle} at {scale}.");
                if (control is Label label && !string.IsNullOrEmpty(label.Text))
                {
                    var required = TextRenderer.MeasureText(label.Text, label.Font,
                        new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
                    Assert.True(required.Height <= label.Height,
                        $"Label '{label.Text}' needs {required.Height}px but has {label.Height}px at {scale}; font={label.Font.GetHeight()}, native={TextRenderer.MeasureText(label.Text, label.Font)}.");
                }
            }
            var interval = (NumericUpDown)dialog.Controls["rotationInterval"];
            Assert.Equal("Rotation interval in minutes", interval.AccessibilityObject.Name);
            Assert.True(interval.TabStop && interval.Enabled);
            dialog.ShowInTaskbar = false;
            dialog.Show();
            Assert.True(dialog.SelectNextControl(null, true, true, true, false));
            Assert.IsType<RadioButton>(dialog.ActiveControl);
            Assert.True(dialog.SelectNextControl(dialog.ActiveControl, true, true, true, true));
            Assert.NotNull(dialog.ActiveControl);
            Assert.NotNull(dialog.AcceptButton);
            Assert.NotNull(dialog.CancelButton);
            _output.WriteLine($"{scale:P0}: dialog {dialog.ClientSize}; all native controls and label text contained.");
        });
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void ScaledNativeControlsExposeNamedAccessibleRolesAndFollowTabOrder(float scale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: "Spectrum");
            ShowWithoutCapture(form);
            using var fonts = ScaleEquivalent(form, scale);
            form.ClientSize = new Size(1184, 378);
            var selectors = new[]
            {
                CombinedRandomizationTests.GetField<ComboBox>(form, "_modeSelector"),
                CombinedRandomizationTests.GetField<ComboBox>(form, "_styleSelector"),
                CombinedRandomizationTests.GetField<ComboBox>(form, "_themeSelector")
            };
            var names = new[] { "Visualization mode", "Optional visual style", "Bar color theme" };
            for (var i = 0; i < selectors.Length; i++)
            {
                Assert.Equal(names[i], selectors[i].AccessibilityObject.Name);
                Assert.Equal(AccessibleRole.ComboBox, selectors[i].AccessibilityObject.Role);
                Assert.True(selectors[i].Focus());
                Assert.True(selectors[i].Focused);
                Assert.True(form.SelectNextControl(selectors[i], true, true, true, true));
                if (i < 2) Assert.True(selectors[i + 1].Focused);
            }
            var rotation = CombinedRandomizationTests.GetField<Button>(form, "_rotationSettingsButton");
            Assert.True(rotation.Focused);
            Assert.Equal(AccessibleRole.PushButton, rotation.AccessibilityObject.Role);
            var status = CombinedRandomizationTests.GetField<Label>(form, "_captureStatusLabel");
            Assert.Equal("Audio capture source and status", status.AccessibilityObject.Name);
            Assert.Equal(AccessibleRole.StaticText, status.AccessibilityObject.Role);
            Assert.False(status.TabStop);
            var chrome = form.Controls.OfType<Ambiance_ThemeContainer>().Single().Controls.OfType<Ambiance_ControlBox>().Single();
            foreach (var name in new[] { "minimizeButton", "closeButton" })
            {
                var button = (Button)chrome.Controls[name];
                Assert.False(string.IsNullOrWhiteSpace(button.AccessibilityObject.Name));
                Assert.Equal(AccessibleRole.PushButton, button.AccessibilityObject.Role);
                Assert.True(button.Focus());
                Assert.True(button.Focused);
            }
            _output.WriteLine($"{scale:P0}: native combo/button/static-text accessibility roles, names and tab-order focus passed. No external screen reader was exercised.");
        });
    }

    [Fact]
    public void VisibleSelectorLabelMnemonicsFocusTheirAssociatedNativeCombos()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: "Spectrum");
            form.ClientSize = new Size(1920, 1080);
            ShowWithoutCapture(form);
            var fields = new[] { "_modeSelector", "_styleSelector", "_themeSelector" };
            var keys = new[] { 'm', 's', 't' };
            for (var i = 0; i < fields.Length; i++)
            {
                var label = CombinedRandomizationTests.GetField<Label>(form, fields[i] + "Label");
                var combo = CombinedRandomizationTests.GetField<ComboBox>(form, fields[i]);
                Assert.True(label.Visible);
                Assert.True((bool)typeof(Label).GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(label, new object[] { keys[i] }));
                Assert.True(combo.Focused, $"{label.Text} must focus {combo.Name}, not another selector.");
            }
        });
    }

    private static int GetTextInset(FormAudioSpectrum form, ComboBox selector, int fallback)
    {
        var method = typeof(FormAudioSpectrum).GetMethod("GetSelectorTextInset", BindingFlags.Static | BindingFlags.NonPublic);
        return method == null ? fallback : (int)method.Invoke(null, new object[] { selector.Font, selector.Name == "themeSelector" });
    }

    private static FontSet ScaleEquivalent(Form form, float scale)
    {
        var controls = Descendants(form).Prepend(form).ToArray();
        // Mirrors Program.Main but leaves the test host's global rendering default alone.
        foreach (var control in controls)
        {
            if (control is Label label) label.UseCompatibleTextRendering = false;
            if (control is ButtonBase button) button.UseCompatibleTextRendering = false;
        }
        var original = controls.Select(control => control.Font).ToArray();
        var minimum = form.MinimumSize;
        form.AutoScaleMode = AutoScaleMode.None;
        form.Scale(new SizeF(scale, scale));
        // Squeeze compact physical sizes with bigger fonts, rather than faking it with a doubled minimum width.
        form.MinimumSize = minimum;
        var fonts = new FontSet();
        for (var i = 0; i < controls.Length; i++)
        {
            var font = new Font(original[i].FontFamily, original[i].SizeInPoints * scale, original[i].Style);
            fonts.Items.Add(font);
            controls[i].Font = font;
        }
        return fonts;
    }

    private static void ShowWithoutCapture(FormAudioSpectrum form)
    {
        var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", BindingFlags.Instance | BindingFlags.NonPublic);
        form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
        form.ShowInTaskbar = false;
        form.Show();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Call(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

    private sealed class FontSet : IDisposable
    {
        internal readonly List<Font> Items = new();
        public void Dispose() { foreach (var font in Items) font.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRect Item, Button;
        public int ButtonState;
        public IntPtr ComboBox, Edit, List;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr handle, ref ComboBoxInfo info);
}
