using System;
using System.Configuration;
using System.Reflection;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class VisualStyleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unexpected")]
    public void InvalidOrMissingStylesResolveToNone(string style)
    {
        Assert.Equal("None", VisualStyles.Resolve(style));
    }

    [Theory]
    [InlineData("Pulse", "Bricks", "None")]
    [InlineData("Glow", "Spectrum", "Glow")]
    public void LegacyModesMigrateToDeterministicBaseAndStyle(string legacyMode, string expectedMode, string expectedStyle)
    {
        FormAudioSpectrum.ResolveConfiguredVisualState(legacyMode, null, out var mode, out var style);

        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedStyle, style);
    }

    [Fact]
    public void LegacyModeStyleMigrationNormalizesToItsSupportedStyleSet()
    {
        FormAudioSpectrum.ResolveConfiguredVisualState("Pulse", null, out var mode, out var style);

        Assert.True(FormAudioSpectrum.IsVisualStyleSupported(mode));
        Assert.Equal("None", style);
        Assert.Equal("None", FormAudioSpectrum.GetAppliedVisualStyle(mode, style));
    }

    [Fact]
    public void LedDisallowsGlowAndExposesOnlyItsSupportedStyles()
    {
        Assert.Equal("None", FormAudioSpectrum.GetAppliedVisualStyle("LED", "Glow"));
        Assert.Equal(new[] { "None", "Pulse", "Scanline", "Precision" }, VisualStyles.GetSupportedNames("LED"));
    }

    [Theory]
    [InlineData("Spectrum", true)]
    [InlineData("LED", true)]
    [InlineData("Wave", true)]
    [InlineData("Bricks", true)]
    [InlineData("Contour", true)]
    public void StylesAreAvailableOnlyForSupportedBases(string mode, bool supported)
    {
        Assert.Equal(supported, FormAudioSpectrum.IsVisualStyleSupported(mode));
    }

    [Fact]
    public void StylePreferenceIsUserScopedWithSafeDefault()
    {
        var property = typeof(VisualizerPreferences).GetProperty(nameof(VisualizerPreferences.Style));

        Assert.NotNull(property);
        Assert.NotNull(property.GetCustomAttribute<UserScopedSettingAttribute>());
        Assert.Equal("None", property.GetCustomAttribute<DefaultSettingValueAttribute>().Value);
    }

    [Fact]
    public void StyleSelectorChoicesIncludeSafeDefaultAndOnlyStyles()
    {
        Assert.Equal(new[] { "None", "Pulse", "Glow", "Trail", "Scanline", "Precision" }, VisualStyles.Names);
        Assert.DoesNotContain("Pulse", GetVisualModes());
        Assert.DoesNotContain("Glow", GetVisualModes());
    }

    [Theory]
    [InlineData("Peak Trace")]
    [InlineData("Threshold Monitor")]
    [InlineData("Band Matrix")]
    [InlineData("Octave Spectrum")]
    [InlineData("Level Change")]
    [InlineData("Orbit History")]
    [InlineData("Octave Waterfall")]
    [InlineData("Level Change Map")]
    [InlineData("Frequency Ribbon")]
    public void NewModesAreExposedAndHaveValidStyleChoices(string mode)
    {
        Assert.Contains(mode, GetVisualModes());
        Assert.Contains("None", VisualStyles.GetSupportedNames(mode));
        Assert.Contains("Scanline", VisualStyles.GetSupportedNames(mode));
        Assert.Contains("Precision", VisualStyles.GetSupportedNames(mode));
        Assert.DoesNotContain("Trail", VisualStyles.GetSupportedNames(mode));
    }

    [Theory]
    [InlineData("Spectrum", "Glow")]
    [InlineData("LED", "Pulse")]
    [InlineData("Band Matrix", "Scanline")]
    [InlineData("Bricks", "Precision")]
    public void RandomStyleSelectionReturnsAnotherValidStyle(string mode, string currentStyle)
    {
        var next = FormAudioSpectrum.GetDifferentRandomStyle(mode, currentStyle, new Random(42));

        Assert.Contains(next, VisualStyles.GetSupportedNames(mode));
        Assert.NotEqual(currentStyle, next);
    }

    [Theory]
    [InlineData("Spectral Flux", "Level Change")]
    [InlineData("Transient Map", "Level Change Map")]
    public void LegacyDerivedModeNamesMigrateToAccurateLabels(string legacyName, string expectedName)
    {
        FormAudioSpectrum.ResolveConfiguredVisualState(legacyName, "Trail", out var mode, out var style);

        Assert.Equal(expectedName, mode);
        Assert.Equal("None", style);
    }

    private static string[] GetVisualModes() =>
        (string[])typeof(FormAudioSpectrum)
            .GetField("s_visualModes", BindingFlags.Static | BindingFlags.NonPublic)
            .GetValue(null);

}
