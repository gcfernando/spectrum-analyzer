using System;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class BarColorThemesTests
{
    [Theory]
    [InlineData("  oBsIdIaN gOlD ", nameof(BarColorThemes.ObsidianGold))]
    [InlineData("  mIdNiGhT pRiSm ", nameof(BarColorThemes.MidnightPrism))]
    [InlineData("  eMeRaLd nOiR ", nameof(BarColorThemes.EmeraldNoir))]
    [InlineData("  cRiMsOn vElVeT ", nameof(BarColorThemes.CrimsonVelvet))]
    public void PremiumThemes_AreResolvedCaseInsensitivelyAndAfterTrimming(string name, string expectedTheme)
    {
        var expected = expectedTheme switch
        {
            nameof(BarColorThemes.ObsidianGold) => BarColorThemes.ObsidianGold,
            nameof(BarColorThemes.MidnightPrism) => BarColorThemes.MidnightPrism,
            nameof(BarColorThemes.EmeraldNoir) => BarColorThemes.EmeraldNoir,
            nameof(BarColorThemes.CrimsonVelvet) => BarColorThemes.CrimsonVelvet,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedTheme))
        };

        var actual = BarColorThemes.Resolve(name);

        Assert.Equal(expected.Low, actual.Low);
        Assert.Equal(expected.Mid, actual.Mid);
        Assert.Equal(expected.High, actual.High);
        Assert.Equal(expected.Peak, actual.Peak);
        Assert.Equal(expected.IntensityCurve, actual.IntensityCurve);
    }

    [Fact]
    public void ThemeSelector_ContainsEveryPaletteName()
    {
        Assert.Equal(BarColorThemes.Names, FormAudioSpectrum.ColorThemeNames);
    }

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
