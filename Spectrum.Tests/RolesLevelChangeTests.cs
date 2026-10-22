using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class RolesLevelChangeTests
{
    private static byte[] Levels(byte value) => Enumerable.Repeat(value, 83).ToArray();
    private static AdvancedVisualizationControl Control() => new()
    { Mode = AdvancedVisualizationMode.SpectralFlux, Size = new Size(830, 200), ThemeName = "Studio" };
    private static AudioVisualizationData Audio(object epoch, long end, int rate = 48000, int channels = 2)
    {
        var data = new AudioVisualizationData();
        data.UpdateObservedWindow(Enumerable.Repeat(.125f, 4 * channels).ToArray(), 4, channels, rate,
            end, Math.Max(0, end - 4), Math.Max(0, end - 4), epoch);
        return data;
    }
    private static void Feed(AdvancedVisualizationControl control, AudioVisualizationData audio, byte level)
    { control.SetAudioData(audio); control.SetSpectrum(Levels(level)); }
    private static byte[] Field(AdvancedVisualizationControl control, string name) =>
        (byte[])typeof(AdvancedVisualizationControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);
    private static void AssertWaiting(AdvancedVisualizationControl control)
    {
        Assert.False(control.LevelChangeHasComparison);
        Assert.Contains("waiting", control.AccessibleDescription, StringComparison.OrdinalIgnoreCase);
        using var image = VisualizationReadabilityTests.Render(control);
        var rise = (Color)typeof(AdvancedVisualizationControl).GetField("_riseColor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);
        var fall = (Color)typeof(AdvancedVisualizationControl).GetField("_fallColor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);
        for (var x = 10; x < image.Width - 10; x += 10)
            for (var y = 30; y < image.Height - 20; y += 5)
            {
                Assert.NotEqual(rise.ToArgb(), image.GetPixel(x, y).ToArgb());
                Assert.NotEqual(fall.ToArgb(), image.GetPixel(x, y).ToArgb());
            }
    }

    [Fact]
    public void FirstSuppliedSpectrumWaitsInsteadOfInventingASilentPreviousMeasurement()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Control(); control.SetSpectrum(Levels(200)); AssertWaiting(control);
            Assert.Equal(Levels(200), Field(control, "_previousSpectrum"));
            control.SetSpectrum(Levels(100)); Assert.True(control.LevelChangeHasComparison);
            Assert.Equal(Levels(200), Field(control, "_previousSpectrum"));
            Assert.Equal(Levels(100), Field(control, "_spectrum"));
        });
    }

    [Fact]
    public void DuplicateCapturedWindowCannotCreateOrOverwriteAnObservedDelta()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Control(); var epoch = new object();
            Feed(control, Audio(epoch, 104), 100); Feed(control, Audio(epoch, 104), 230); AssertWaiting(control);
            Assert.Equal(Levels(100), Field(control, "_spectrum"));
            Feed(control, Audio(epoch, 108), 150); Assert.True(control.LevelChangeHasComparison);
            Feed(control, Audio(epoch, 108), 240); Assert.True(control.LevelChangeHasComparison);
            Assert.Equal(Levels(100), Field(control, "_previousSpectrum")); Assert.Equal(Levels(150), Field(control, "_spectrum"));
        });
    }

    [Theory]
    [InlineData("source")] [InlineData("rate")] [InlineData("channels")]
    [InlineData("gap")] [InlineData("reverse")] [InlineData("unavailable")]
    [InlineData("resume")] [InlineData("synchronize")] [InlineData("plan")]
    public void DiscontinuitiesRequireTwoNewObservedFramesBeforeDisplayingChange(string boundary)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = Control(); var epoch = new object();
            Feed(control, Audio(epoch, 104), 100); Feed(control, Audio(epoch, 108), 150);
            Assert.True(control.LevelChangeHasComparison);
            var nextEpoch = boundary == "source" ? new object() : epoch;
            var rate = boundary == "rate" ? 44100 : 48000; var channels = boundary == "channels" ? 1 : 2;
            var end = boundary == "gap" ? 200 : boundary == "reverse" ? 4 : 112;
            if (boundary == "unavailable")
            {
                var unavailable = new AudioVisualizationData(); unavailable.SetUnavailableWindow(108, 2, 48000, epoch);
                Feed(control, unavailable, 0); AssertWaiting(control);
            }
            if (boundary == "resume") { control.DisplayFrozen = true; control.DisplayFrozen = false; }
            if (boundary == "plan") control.SetBandPlan(BandPlan.CreateLogarithmic(83, 20, 18000, 48000));
            if (boundary == "synchronize")
            {
                control.SetAudioData(Audio(nextEpoch, end, rate, channels)); control.SynchronizeSpectrum(Levels(200));
            }
            else Feed(control, Audio(nextEpoch, end, rate, channels), 200);
            AssertWaiting(control);
            Feed(control, Audio(nextEpoch, end + 4, rate, channels), 100);
            Assert.True(control.LevelChangeHasComparison);
            Assert.Equal(Levels(200), Field(control, "_previousSpectrum")); Assert.Equal(Levels(100), Field(control, "_spectrum"));
        });
    }
}
