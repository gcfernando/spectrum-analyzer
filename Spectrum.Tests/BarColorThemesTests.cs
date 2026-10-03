using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class BarColorThemesTests
{
    [Fact]
    public void Aurora_IsResolvedCaseInsensitivelyAndAfterTrimming()
    {
        var expected = BarColorThemes.Aurora;
        var actual = BarColorThemes.Resolve("  aUrOrA ");

        Assert.Equal(expected.Low, actual.Low);
        Assert.Equal(expected.Mid, actual.Mid);
        Assert.Equal(expected.High, actual.High);
        Assert.Equal(expected.Peak, actual.Peak);
        Assert.Equal(expected.IntensityCurve, actual.IntensityCurve);
    }

    [Fact]
    public void UnknownTheme_UsesTheClassicPalette()
    {
        var expected = BarColorThemes.ClassicSmooth;
        var actual = BarColorThemes.Resolve("not-a-theme");

        Assert.Equal(expected.Low, actual.Low);
        Assert.Equal(expected.Mid, actual.Mid);
        Assert.Equal(expected.High, actual.High);
        Assert.Equal(expected.Peak, actual.Peak);
        Assert.Equal(expected.IntensityCurve, actual.IntensityCurve);
    }
}
