// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class NativeMeterFrameRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static IEnumerable<object[]> Cases()
    {
        foreach (var mode in new[] { "Spectrum", "Bricks", "LED" })
        foreach (var appearance in new[] { "Dark", "Light", "High Contrast" })
        foreach (var guides in new[] { false, true })
            yield return new object[] { mode, appearance, guides };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShownNativeMetersKeepInactiveRailsQuietWithoutLosingVerticalStyleOrAccessibleRole(
        string mode, string appearance, bool guides)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: mode);
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            CombinedRandomizationTests.SetField(form, "_appearance", appearance);
            CombinedRandomizationTests.SetField(form, "_showGuides", guides);
            typeof(FormAudioSpectrum).GetMethod("ApplyCurrentAppearance", PrivateInstance).Invoke(form, null);
            typeof(FormAudioSpectrum).GetMethod("ApplyVisualStyle", PrivateInstance).Invoke(form, null);
            var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
            form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
            form.ShowInTaskbar = false;
            form.SetDisplayFrozen(true);
            form.Show();
            Application.DoEvents();
            using var image = VisualizationReadabilityTests.Render(form);
            var bars = CombinedRandomizationTests.GetField<VerticalProgressBar[]>(form, "_progressBars");
            Assert.Equal(83, bars.Length);
            foreach (var bar in bars)
            {
                Assert.True(bar.IsHandleCreated && bar.Visible);
                var parameters = (CreateParams)typeof(VerticalProgressBar).GetProperty("CreateParams", PrivateInstance).GetValue(bar);
                Assert.Equal(0, parameters.Style & 0x00800000);
                Assert.Equal(0, parameters.ExStyle & (0x00000100 | 0x00000200 | 0x00020000));
                var actualStyle = GetWindowLong(bar.Handle, -16);
                var actualExtendedStyle = GetWindowLong(bar.Handle, -20);
                Assert.Equal(0, actualStyle & 0x00800000);
                Assert.Equal(0, actualExtendedStyle & (0x00000100 | 0x00000200 | 0x00020000));
                Assert.Equal(0x04, actualStyle & 0x04);
                Assert.Equal(bar.Size, bar.ClientSize);
                Assert.Equal(AccessibleRole.ProgressBar, bar.AccessibilityObject.Role);
                Assert.Equal(VisualStyle.None, bar.VisualStyle);
                Assert.Equal(guides, bar.ShowGuides);
                var origin = form.PointToClient(bar.PointToScreen(Point.Empty));
                foreach (var fraction in new[] { .12, .23, .37, .48, .62, .73, .88 })
                foreach (var x in new[] { 0, Math.Min(1, bar.Width - 1), bar.Width - 1 })
                {
                    var pixel = image.GetPixel(origin.X + x, origin.Y + (int)((bar.Height - 1) * fraction));
                    Assert.NotEqual(Color.White.ToArgb(), pixel.ToArgb());
                    Assert.True(VisualizationReadabilityTests.Contrast(pixel, bar.BackColor) <= 2,
                        $"{mode}/{appearance}, guides={guides}: inactive rail at {bar.Name} x={x} is too strong ({pixel}).");
                }
            }
            var recreated = bars[40];
            typeof(Control).GetMethod("RecreateHandle", PrivateInstance).Invoke(recreated, null);
            Application.DoEvents();
            Assert.True(recreated.IsHandleCreated);
            Assert.Equal(0, GetWindowLong(recreated.Handle, -16) & 0x00800000);
            Assert.Equal(0, GetWindowLong(recreated.Handle, -20) & (0x00000100 | 0x00000200 | 0x00020000));
            Assert.Equal(0x04, GetWindowLong(recreated.Handle, -16) & 0x04);
            Assert.Equal(recreated.Size, recreated.ClientSize);
            Assert.Equal(AccessibleRole.ProgressBar, recreated.AccessibilityObject.Role);
            using var afterRecreation = VisualizationReadabilityTests.Render(form);
            var recreatedOrigin = form.PointToClient(recreated.PointToScreen(Point.Empty));
            foreach (var x in new[] { 0, recreated.Width - 1 })
            {
                var pixel = afterRecreation.GetPixel(recreatedOrigin.X + x, recreatedOrigin.Y + recreated.Height / 3);
                Assert.NotEqual(Color.White.ToArgb(), pixel.ToArgb());
                Assert.True(VisualizationReadabilityTests.Contrast(pixel, recreated.BackColor) <= 2);
            }
            form.Close();
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
}
