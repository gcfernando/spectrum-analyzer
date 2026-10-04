using System;
using System.Configuration;
using System.Linq;
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
    [InlineData("Pulse", "Bricks", "Pulse")]
    [InlineData("Glow", "Spectrum", "Glow")]
    public void LegacyModesMigrateToDeterministicBaseAndStyle(string legacyMode, string expectedMode, string expectedStyle)
    {
        FormAudioSpectrum.ResolveConfiguredVisualState(legacyMode, null, out var mode, out var style);

        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedStyle, style);
    }

    [Fact]
    public void UnsupportedModesShowNoAppliedStyleWithoutDiscardingMigrationIntent()
    {
        FormAudioSpectrum.ResolveConfiguredVisualState("Pulse", null, out var mode, out var style);

        Assert.False(FormAudioSpectrum.IsVisualStyleSupported(mode));
        Assert.Equal("Pulse", style);
        Assert.Equal("None", FormAudioSpectrum.GetAppliedVisualStyle(mode, style));
    }

    [Theory]
    [InlineData("Spectrum", true)]
    [InlineData("LED", true)]
    [InlineData("Wave", true)]
    [InlineData("Bricks", false)]
    [InlineData("Contour", false)]
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
        Assert.Equal(new[] { "None", "Pulse", "Glow" }, VisualStyles.Names);
        Assert.DoesNotContain("Pulse", GetVisualModes());
        Assert.DoesNotContain("Glow", GetVisualModes());
    }

    private static string[] GetVisualModes() =>
        (string[])typeof(FormAudioSpectrum)
            .GetField("s_visualModes", BindingFlags.Static | BindingFlags.NonPublic)
            .GetValue(null);

}
