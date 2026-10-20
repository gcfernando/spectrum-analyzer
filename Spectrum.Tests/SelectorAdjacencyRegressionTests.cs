// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

[Collection("Capture lifecycle")]
public class SelectorAdjacencyRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeShowAndEveryModeKeepEachMnemonicLabelImmediatelyBeforeItsOwnSelector(bool normalLoad)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm(normalLoad);
            AssertPairedLabelsAndFocus(form);
            var modes = Field<ComboBox>(form, "_modeSelector");
            for (var cycle = 0; cycle < 2; cycle++)
            {
                foreach (var mode in modes.Items.Cast<string>().ToArray())
                {
                    modes.SelectedItem = mode;
                    Application.DoEvents();
                    AssertPairedLabelsAndFocus(form);
                    AssertPinGeometry(form);
                }
                form.ClientSize = new Size(1200, 480);
                Application.DoEvents();
                Assert.All(new[] { "_modeSelectorLabel", "_styleSelectorLabel", "_themeSelectorLabel" },
                    name => Assert.False(Field<Label>(form, name).Visible));
                form.ClientSize = new Size(1400, 600);
                Application.DoEvents();
                AssertPairedLabelsAndFocus(form);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeHandleRecreationFontAndEffectChangesPreservePairingAndImmediatePinGeometry(bool normalLoad)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm(normalLoad);
            var fonts = new List<Font>();
            try
            {
                var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
                foreach (var pointSize in new[] { 11.25f, 9f })
                {
                    var font = new Font("Segoe UI", pointSize);
                    fonts.Add(font);
                    foreach (var child in panel.Controls.Cast<Control>().ToArray()) child.Font = font;
                    Application.DoEvents();
                    AssertPairedLabelsAndFocus(form, pointSize == 9);
                    AssertPinGeometry(form);
                }
                var mode = Field<ComboBox>(form, "_modeSelector");
                mode.SelectedItem = "Radial Spectrum";
                var effect = Field<ComboBox>(form, "_styleSelector");
                foreach (var choice in effect.Items.Cast<string>().ToArray())
                {
                    effect.SelectedItem = choice;
                    Application.DoEvents();
                    AssertPairedLabelsAndFocus(form);
                    AssertPinGeometry(form);
                }
                foreach (var selector in new[] { mode, effect, Field<ComboBox>(form, "_themeSelector") })
                {
                    typeof(Control).GetMethod("RecreateHandle", PrivateInstance).Invoke(selector, null);
                    Application.DoEvents();
                    AssertPairedLabelsAndFocus(form);
                    // No test reflow is called: any creation or Pin layout mutation must be observable.
                    for (var repeat = 0; repeat < 10; repeat++) AssertPinGeometry(form);
                }
            }
            finally
            {
                form.Dispose();
                foreach (var font in fonts) font.Dispose();
            }
        });
    }

    private static FormAudioSpectrum CreateShownForm(bool normalLoad)
    {
        var form = CombinedRandomizationTests.CreateForm(mode: "Radial Spectrum");
        CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
        if (!normalLoad)
        {
            var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
            form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
        }
        form.ShowInTaskbar = false;
        form.Show();
        form.ClientSize = new Size(1400, 600);
        Application.DoEvents();
        return form;
    }

    private static void AssertPairedLabelsAndFocus(FormAudioSpectrum form, bool requireVisible = true)
    {
        var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
        var fields = new[] { "_modeSelector", "_styleSelector", "_themeSelector" };
        var keys = new[] { 'm', 'e', 'p' };
        for (var index = 0; index < fields.Length; index++)
        {
            var label = Field<Label>(form, fields[index] + "Label");
            var selector = Field<ComboBox>(form, fields[index]);
            if (requireVisible) Assert.True(label.Visible);
            Assert.True(selector.Visible);
            Assert.True(panel.Controls.GetChildIndex(selector) == panel.Controls.GetChildIndex(label) + 1,
                $"{label.Text} must directly precede {selector.Name}; actual order: " +
                string.Join(", ", panel.Controls.Cast<Control>().Select(control => string.IsNullOrEmpty(control.Name) ? control.Text : control.Name)));
            Assert.Equal(index, selector.TabIndex);
            if (!label.Visible) continue;
            Assert.Equal(label.Top, selector.Top);
            Assert.Equal(label.Right + label.Margin.Right + selector.Margin.Left, selector.Left);
            if (!selector.Enabled) continue;
            Assert.True((bool)typeof(Label).GetMethod("ProcessMnemonic", PrivateInstance).Invoke(label, new object[] { keys[index] }));
            Assert.True(selector.Focused, label.Text + " focused another control.");
        }
        var mode = Field<ComboBox>(form, "_modeSelector");
        var effect = Field<ComboBox>(form, "_styleSelector");
        var palette = Field<ComboBox>(form, "_themeSelector");
        mode.Focus();
        Assert.True(panel.SelectNextControl(mode, true, true, false, true));
        Assert.True((effect.Enabled ? effect : palette).Focused);
    }

    private static void AssertPinGeometry(FormAudioSpectrum form)
    {
        var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
        string[] Geometry() => new[] { form.Bounds.ToString(), form.ClientSize.ToString(), form.MinimumSize.ToString(),
                panel.Bounds.ToString(), Field<Control>(form, "_advancedVisualizer").Bounds.ToString(),
                Field<Control>(form, "_captureStatusLabel").Bounds.ToString(), Field<Control>(form, "ambiance_ControlBox").Bounds.ToString(),
                Field<Ambiance_ThemeContainer>(form, "ambiance_ThemeSpectrum").TitleRightInset.ToString() }
            .Concat(panel.Controls.Cast<Control>().Select(child => child.Name + ":" + child.Text + ":" + child.Bounds + ":" + child.Visible)
                // Only the intended Pin feedback text may change.
                .Where(value => !value.StartsWith("pinButton:")))
            .Append(Field<Control>(form, "_pinButton").Bounds.ToString()).ToArray();
        var before = Geometry();
        var pin = Field<Button>(form, "_pinButton");
        for (var index = 0; index < 2; index++)
        {
            typeof(Button).GetMethod("OnClick", PrivateInstance).Invoke(pin, new object[] { EventArgs.Empty });
            Assert.Equal(index == 0, form.TopMost);
            Assert.Equal(before, Geometry());
        }
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
}
