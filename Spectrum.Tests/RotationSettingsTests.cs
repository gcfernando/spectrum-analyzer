using System;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class RotationSettingsTests
{
    [Theory]
    [InlineData(1, 60000)]
    [InlineData(5, 300000)]
    [InlineData(240, 14400000)]
    public void RotationIntervalConvertsMinutesToTimerMilliseconds(int minutes, int expected)
    {
        Assert.Equal(expected, FormAudioSpectrum.GetRotationIntervalMilliseconds(minutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public void RotationIntervalRejectsValuesOutsideTheUiRange(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FormAudioSpectrum.GetRotationIntervalMilliseconds(minutes));
    }

    [Fact]
    public void RandomSelectionAlwaysChangesTheCurrentModeOrTheme()
    {
        var random = new Random(1234);
        for (var count = 2; count <= 14; count++)
        {
            for (var current = 0; current < count; current++)
            {
                var next = FormAudioSpectrum.GetDifferentRandomIndex(current, count, random);
                Assert.InRange(next, 0, count - 1);
                Assert.NotEqual(current, next);
            }
        }
    }
}
