// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class ViewMenuRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void ShownViewMenuSurvivesRepeatedNestedCloseFocusChangesAndReopensWithoutLeakingGdi(string appearance)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var failures = new List<Exception>();
            ThreadExceptionEventHandler capture = (sender, args) => failures.Add(args.Exception);
            Application.ThreadException += capture;
            try
            {
                using var form = CreateShownForm(appearance);
                using var other = new Form { ShowInTaskbar = false, Size = new Size(180, 100), Text = "Menu focus regression" };
                var open = typeof(FormAudioSpectrum).GetMethod("ShowViewMenu", PrivateInstance);
                open.Invoke(form, null);
                Application.DoEvents();
                var menu = Field<ContextMenuStrip>(form, "_viewMenu");
                Assert.NotNull(menu);
                Assert.True(menu.Visible);
                var nested = menu.Items.OfType<ToolStripMenuItem>()
                    .Where(item => item.Text == "Presets" || item.Text == "Appearance").ToArray();
                Assert.Equal(2, nested.Length);
                menu.Close();
                Application.DoEvents();
                using var process = Process.GetCurrentProcess();
                var warmedGdi = 0;
                for (var iteration = 0; iteration < 16; iteration++)
                {
                    form.Activate();
                    open.Invoke(form, null);
                    Application.DoEvents();
                    Assert.Same(menu, Field<ContextMenuStrip>(form, "_viewMenu"));
                    Assert.False(menu.IsDisposed);
                    var child = nested[iteration % nested.Length];
                    child.Select();
                    child.ShowDropDown();
                    Application.DoEvents();
                    Assert.True(child.DropDown.Visible);
                    if (iteration % 2 == 0)
                    {
                        other.Show();
                        other.Activate();
                        other.Focus();
                    }
                    menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
                    Application.DoEvents();
                    Assert.False(menu.IsDisposed);
                    Assert.False(child.DropDown.IsDisposed);
                    Assert.False(menu.Visible);
                    Assert.Empty(failures);
                    if (iteration == 5) warmedGdi = SettledGdiCount(process);
                }
                var finalGdi = SettledGdiCount(process);
                Assert.True(warmedGdi > 0, "The Windows GDI resource count must be available to verify menu reuse.");
                Assert.True(finalGdi > 0, "The final Windows GDI resource query must succeed before comparing growth.");
                Assert.True(finalGdi - warmedGdi <= 8,
                    $"Repeated menu use grew GDI resources by {finalGdi - warmedGdi}; the permitted growth remains 8.");
                form.Close();
                Application.DoEvents();
                Assert.True(menu.IsDisposed);
                Assert.Empty(failures);
            }
            finally { Application.ThreadException -= capture; }
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void NestedMenusInheritTheSameRendererAndKeepSelectedTextReadable(string appearance)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm(appearance);
            typeof(FormAudioSpectrum).GetMethod("ShowViewMenu", PrivateInstance).Invoke(form, null);
            Application.DoEvents();
            var menu = Field<ContextMenuStrip>(form, "_viewMenu");
            var renderer = Assert.IsAssignableFrom<ToolStripProfessionalRenderer>(menu.Renderer);
            var theme = BarColorThemes.ApplyAppearance(BarColorThemes.Studio, appearance).Ui;
            Assert.Equal(theme.Selection, renderer.ColorTable.MenuItemSelected);
            AssertMenuTheme(menu, renderer, theme);
            menu.Close();
            Application.DoEvents();
            form.Close();
        });
    }

    private static void AssertMenuTheme(ToolStripDropDown menu, ToolStripRenderer renderer, VisualTheme theme)
    {
        Assert.Same(renderer, menu.Renderer);
        if (menu is ToolStripDropDownMenu choices) Assert.True(choices.ShowCheckMargin);
        Assert.Equal(theme.ControlSurface, menu.BackColor);
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator) continue;
            Assert.True(VisualizationReadabilityTests.Contrast(item.ForeColor, theme.ControlSurface) >= 4.5);
            Assert.True(VisualizationReadabilityTests.Contrast(item.ForeColor, theme.Selection) >= 4.5);
            if (item is ToolStripMenuItem parent && parent.HasDropDownItems)
                AssertMenuTheme(parent.DropDown, renderer, theme);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void CompactToolbarRetainsNativePickersAndExposesEveryOverflowCommandWithVisibleCheckStates(float scale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm("Dark");
            var fonts = new List<Font>();
            try
            {
                var controls = Descendants(form).Prepend(form).ToArray();
                var originals = controls.Select(control => control.Font).ToArray();
                var minimum = form.MinimumSize;
                form.AutoScaleMode = AutoScaleMode.None;
                form.Scale(new SizeF(scale, scale));
                // Match the existing scaling fixture: larger native controls/fonts
                // must fit the requested physical window instead of enlarging its minimum.
                form.MinimumSize = minimum;
                for (var index = 0; index < controls.Length; index++)
                {
                    var source = originals[index];
                    var font = new Font(source.FontFamily, source.SizeInPoints * scale, source.Style);
                    fonts.Add(font);
                    controls[index].Font = font;
                }
                form.ClientSize = new Size(900, 250);
                typeof(FormAudioSpectrum).GetMethod("PositionSelectionPanel", PrivateInstance).Invoke(form, null);
                Application.DoEvents();
                Assert.True(form.ClientSize == new Size(900, 250),
                    $"The compact fixture must remain 900x250 at {scale:P0}; actual client={form.ClientSize}, minimum={form.MinimumSize}.");
                Assert.True(Field<bool>(form, "_compactToolbar"),
                    $"The 900x250 toolbar must use overflow at {scale:P0}; container={Field<FlowLayoutPanel>(form, "_selectionPanel").Parent.ClientSize}.");
                var panel = Field<FlowLayoutPanel>(form, "_selectionPanel");
                var pickersAndView = new Control[] { Field<ComboBox>(form, "_modeSelector"), Field<ComboBox>(form, "_styleSelector"),
                    Field<ComboBox>(form, "_themeSelector"), Field<Button>(form, "_viewButton") };
                foreach (var primary in pickersAndView)
                {
                    Assert.True(primary.Visible, $"Compact {primary.Name} must remain visible at {scale:P0}.");
                    Assert.True(panel.ClientRectangle.Contains(primary.Bounds),
                        $"Compact {primary.Name} {primary.Bounds} must fit toolbar {panel.ClientRectangle} at {scale:P0}.");
                }
                Assert.True(panel.Parent.ClientRectangle.Contains(panel.Bounds),
                    $"Compact toolbar {panel.Bounds} must fit container {panel.Parent.ClientRectangle} at {scale:P0}.");
                foreach (var field in new[] { "_rotationSettingsButton", "_guidesButton", "_freezeButton", "_pinButton" })
                    Assert.False(Field<Button>(form, field).Visible, $"Compact {field} must move into View at {scale:P0}.");
                CombinedRandomizationTests.SetField(form, "_showGuides", true);
                form.TopMost = true;
                var open = typeof(FormAudioSpectrum).GetMethod("ShowViewMenu", PrivateInstance);
                open.Invoke(form, null);
                Application.DoEvents();
                var menu = Field<ContextMenuStrip>(form, "_viewMenu");
                var display = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Display controls");
                Assert.True(((ToolStripDropDownMenu)display.DropDown).ShowCheckMargin);
                var commands = display.DropDownItems.OfType<ToolStripMenuItem>().ToArray();
                Assert.Equal(4, commands.Length);
                Assert.Contains(commands, item => item.Text.StartsWith("Rotation settings", StringComparison.Ordinal) && item.Text.Contains("Ctrl+R"));
                Assert.True(commands.Single(item => item.Text.StartsWith("Reference guides", StringComparison.Ordinal)).Checked);
                Assert.True(commands.Single(item => item.Text.StartsWith("Freeze display", StringComparison.Ordinal)).Checked);
                Assert.True(commands.Single(item => item.Text.StartsWith("Keep on top", StringComparison.Ordinal)).Checked);
                menu.Close();
                CombinedRandomizationTests.SetField(form, "_showGuides", false);
                form.SetDisplayFrozen(false);
                form.TopMost = false;
                open.Invoke(form, null);
                Application.DoEvents();
                Assert.All(commands.Skip(1), item => Assert.False(item.Checked));
                menu.Close();
                form.Close();
            }
            finally { foreach (var font in fonts) font.Dispose(); }
        });
    }

    [Fact]
    public void NormalDefaultWindowKeepsTheUtilityButtonsVisible()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CreateShownForm("Dark");
            form.ClientSize = new Size(1200, 480);
            typeof(FormAudioSpectrum).GetMethod("PositionSelectionPanel", PrivateInstance).Invoke(form, null);
            Application.DoEvents();
            Assert.False(Field<bool>(form, "_compactToolbar"));
            foreach (var field in new[] { "_rotationSettingsButton", "_guidesButton", "_freezeButton", "_pinButton", "_viewButton" })
                Assert.True(Field<Button>(form, field).Visible);
            form.Close();
        });
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static FormAudioSpectrum CreateShownForm(string appearance)
    {
        var form = CombinedRandomizationTests.CreateForm();
        CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
        CombinedRandomizationTests.SetField(form, "_barTheme", "Studio");
        CombinedRandomizationTests.SetField(form, "_appearance", appearance);
        typeof(FormAudioSpectrum).GetMethod("ApplyCurrentAppearance", PrivateInstance).Invoke(form, null);
        var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
        form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
        form.ShowInTaskbar = false;
        form.SetDisplayFrozen(true);
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);

    private static int SettledGdiCount(Process process)
    {
        // GetGuiResources counts the whole test host. Settle prior fixtures under
        // identical conditions at both samples; releases are not evidence of a leak.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Application.DoEvents();
        return GetGuiResources(process.Handle, 0);
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr process, int flags);
}
