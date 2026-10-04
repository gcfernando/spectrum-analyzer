using System;
using System.Drawing;
using System.Threading;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class VisualThemeTests
{
    [Fact]
    public void StudioIsExposedAndResolved()
    {
        Assert.Contains("Studio", BarColorThemes.Names);
        Assert.Equal(BarColorThemes.Studio.Low, BarColorThemes.Resolve(" studio ").Low);
    }

    [Fact]
    public void ActiveLowStopsMeetContrastTargetOnVisualizationSurfaces()
    {
        foreach (var name in BarColorThemes.Names)
        {
            var theme = BarColorThemes.Resolve(name);
            Assert.True(Contrast(theme.Low, theme.Ui.VisualizationSurface) >= 3d, $"{name} meter low stop");
            Assert.True(Contrast(theme.Low, theme.Ui.WaveSurface) >= 3d, $"{name} wave low stop");
        }
    }

    [Theory]
    [InlineData("Center", "Spectrum")]
    [InlineData("Mirror", "Bricks")]
    [InlineData("Note Map", "Contour")]
    [InlineData("Ambient Particles", "Spectrum")]
    public void DeprecatedModesMigrateToRetainedModes(string legacyMode, string expectedMode)
    {
        FormAudioSpectrum.ResolveConfiguredVisualState(legacyMode, "Glow", out var mode, out var style);

        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedMode is "Spectrum" ? "Glow" : "None", style);
    }

    [Fact]
    public void WaveThemeApplicationUpdatesTheDedicatedSurfaceImmediately()
    {
        RunOnSta(() =>
        {
            using var wave = new WaveSpectrumControl(4);
            wave.SetTheme(BarColorThemes.MidnightPrism);

            Assert.Equal(BarColorThemes.MidnightPrism.Ui.WaveSurface, wave.BackColor);
        });
    }

    private static double Contrast(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05d) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05d);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte value)
        {
            var component = value / 255d;
            return component <= 0.04045d
                ? component / 12.92d
                : Math.Pow((component + 0.055d) / 1.055d, 2.4d);
        }

        return (0.2126d * Linearize(color.R)) +
               (0.7152d * Linearize(color.G)) +
               (0.0722d * Linearize(color.B));
    }

    private static void RunOnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw failure;
    }
}
