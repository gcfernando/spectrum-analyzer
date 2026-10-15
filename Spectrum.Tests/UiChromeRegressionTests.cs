// Developer :: > Gehan Fernando
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Regression checks on the UI chrome (selectors, buttons, frames).
public class UiChromeRegressionTests
{
    [Fact]
    public void WindowActionsAreSeparatelyNamedStandardKeyboardFocusableButtons()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var chrome = new Ambiance_ControlBox { EnableMaximize = false };
            var close = Assert.IsType<Button>(chrome.Controls["closeButton"]);
            var minimize = Assert.IsType<Button>(chrome.Controls["minimizeButton"]);
            Assert.Equal("Close", close.AccessibilityObject.Name);
            Assert.Equal("Minimize", minimize.AccessibilityObject.Name);
            Assert.Equal(AccessibleRole.PushButton, close.AccessibilityObject.Role);
            Assert.True(close.TabStop);
            Assert.True(minimize.TabStop);
            Assert.False(chrome.TabStop);
            Assert.Equal(new Size(44, 22), chrome.Size);
            Assert.False(chrome.Controls["maximizeButton"].Visible);
            Assert.True(chrome.ClientRectangle.Contains(close.Bounds));
            Assert.True(chrome.ClientRectangle.Contains(minimize.Bounds));
        });
    }

    [Fact]
    public void ChromeBackgroundMouseReleasesNeverExecuteStaleWindowActions()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = new Form();
            using var chrome = new Ambiance_ControlBox();
            form.Controls.Add(chrome);
            _ = form.Handle;
            var closeCount = 0;
            form.FormClosing += (sender, args) => { closeCount++; args.Cancel = true; };
            SendMouse(chrome, "OnMouseMove", MouseButtons.None, 10, 10);
            SendMouse(chrome, "OnMouseDown", MouseButtons.Right, 10, 10);
            SendMouse(chrome, "OnMouseUp", MouseButtons.Right, 10, 10);
            SendMouse(chrome, "OnMouseDown", MouseButtons.Left, 10, 10);
            SendMouse(chrome, "OnMouseUp", MouseButtons.Left, 10, 80);
            Assert.Equal(0, closeCount);
            Assert.False(form.IsDisposed);
            Assert.Equal(FormWindowState.Normal, form.WindowState);
        });
    }

    [Fact]
    public void StandardWindowButtonActionsCloseAndToggleMaximizeWithoutIntermediateMinimization()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = new Form { ShowInTaskbar = false };
            using var chrome = new Ambiance_ControlBox();
            form.Controls.Add(chrome);
            _ = form.Handle;
            var maximize = (Button)chrome.Controls["maximizeButton"];
            var close = (Button)chrome.Controls["closeButton"];
            var minimize = (Button)chrome.Controls["minimizeButton"];
            var closing = 0;
            form.FormClosing += (sender, args) => { closing++; args.Cancel = true; };
            Click(maximize);
            Assert.Equal(FormWindowState.Maximized, form.WindowState);
            Click(maximize);
            Assert.Equal(FormWindowState.Normal, form.WindowState);
            Click(minimize);
            Assert.Equal(FormWindowState.Minimized, form.WindowState);
            Click(close);
            Assert.Equal(1, closing);
        });
    }

    [Theory]
    [InlineData("Platinum Light")]
    [InlineData("High Contrast")]
    public void ThemeChangesPropagateToNamedWindowButtons(string themeName)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = new Form();
            using var container = new Ambiance_ThemeContainer();
            form.Controls.Add(container);
            using var chrome = new Ambiance_ControlBox();
            container.Controls.Add(chrome);
            var theme = BarColorThemes.Resolve(themeName).Ui;
            container.ApplyTheme(theme);
            foreach (Button button in chrome.Controls)
            {
                Assert.Equal(theme.ControlSurface, button.BackColor);
                Assert.Equal(theme.PrimaryText, button.ForeColor);
                Assert.Equal(theme.Hover, button.FlatAppearance.MouseOverBackColor);
                Assert.Equal(theme.Selection, button.FlatAppearance.MouseDownBackColor);
                Assert.Equal(theme.Divider, button.FlatAppearance.BorderColor);
            }
        });
    }

    [Fact]
    public void RotationIntervalHasAccessibleUnitsBoundsAndTracksRandomSelection()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var dialog = new RotationSettingsForm(false, 999, BarColorThemes.PlatinumLight);
            var interval = Assert.IsType<NumericUpDown>(dialog.Controls["rotationInterval"]);
            Assert.Equal("Rotation interval in minutes", interval.AccessibleName);
            Assert.Contains("1 to 240", interval.AccessibleDescription);
            Assert.Equal(1m, interval.Minimum);
            Assert.Equal(240m, interval.Maximum);
            Assert.Equal(240, dialog.IntervalMinutes);
            Assert.False(interval.Enabled);
            ((RadioButton)dialog.Controls["randomOption"]).Checked = true;
            Assert.True(interval.Enabled);
            Assert.True(dialog.RandomEnabled);
            ((RadioButton)dialog.Controls["fixedOption"]).Checked = true;
            Assert.False(interval.Enabled);
        });
    }

    [Theory]
    [InlineData("Platinum Light")]
    [InlineData("High Contrast")]
    public void ProfessionalThemeNamesResolveAndAllControlStatesRemainReadable(string name)
    {
        Assert.Contains(name, BarColorThemes.Names);
        var theme = BarColorThemes.Resolve(" " + name.ToUpperInvariant() + " ");
        var surfaces = new[]
        {
            theme.Ui.Canvas, theme.Ui.HeaderStart, theme.Ui.HeaderEnd, theme.Ui.Surface,
            theme.Ui.RaisedSurface, theme.Ui.ControlSurface, theme.Ui.Selection, theme.Ui.Hover
        };
        foreach (var surface in surfaces)
        {
            Assert.True(Contrast(theme.Ui.PrimaryText, surface) >= 4.5);
            Assert.True(Contrast(theme.Ui.SecondaryText, surface) >= 4.5);
        }
        Assert.True(Contrast(theme.Ui.Focus, theme.Ui.ControlSurface) >= 3);
        foreach (var signal in new[] { theme.Low, theme.Mid, theme.High, theme.Peak })
        {
            Assert.True(Contrast(signal, theme.Ui.VisualizationSurface) >= 3);
            Assert.True(Contrast(signal, theme.Ui.WaveSurface) >= 3);
        }
        Assert.Equal(BarColorThemes.Names.Length,
            BarColorThemes.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static void Click(Button button) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(button, new object[] { EventArgs.Empty });

    private static void SendMouse(Control control, string method, MouseButtons button, int x, int y) =>
        typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(control, new object[] { new MouseEventArgs(button, 1, x, y, 0) });

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte value)
            {
                var component = value / 255d;
                return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
