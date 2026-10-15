// Developer :: > Gehan Fernando
using System;
using System.Configuration;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Guards the project structure so layers don't quietly start depending on things they shouldn't.
public class ArchitectureRegressionTests
{
    [Theory]
    [InlineData("configuration")]
    [InlineData("io")]
    [InlineData("access")]
    public void UserLoadFailurePreservesCompleteApplicationDefaults(string failure)
    {
        var exception = CreateReadFailure(failure);
        var snapshot = VisualPreferenceSnapshot.Load(ConfiguredValue, () => throw exception);

        Assert.Equal("Bricks", snapshot.Mode);
        Assert.Equal("Glow", snapshot.Style);
        Assert.Equal("Ice", snapshot.Theme);
        Assert.False(snapshot.RandomEnabled);
        Assert.Equal(5, snapshot.IntervalMinutes);
        Assert.Same(exception, snapshot.Error);
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("io")]
    [InlineData("access")]
    public void ConfigurationFailureUsesBuiltInDefaultsWithoutReadingUserFile(string failure)
    {
        var exception = CreateReadFailure(failure);
        var userRead = false;
        var snapshot = VisualPreferenceSnapshot.Load(
            key => key == "Style" ? throw exception : "partial",
            () =>
            {
                userRead = true;
                throw new InvalidOperationException();
            });

        Assert.Equal("Spectrum", snapshot.Mode);
        Assert.Equal("None", snapshot.Style);
        Assert.Equal("ClassicSmooth", snapshot.Theme);
        Assert.False(snapshot.RandomEnabled);
        Assert.Equal(5, snapshot.IntervalMinutes);
        Assert.Same(exception, snapshot.Error);
        Assert.False(userRead);
    }

    [Fact]
    public void UnexpectedPreferenceExceptionsAreNotSwallowed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            VisualPreferenceSnapshot.Load(key => throw new InvalidOperationException(), () => null));
        Assert.Throws<InvalidOperationException>(() =>
            VisualPreferenceSnapshot.Load(ConfiguredValue, () => throw new InvalidOperationException()));
    }

    [Theory]
    [InlineData("", "Glow")]
    [InlineData("None", "None")]
    [InlineData("Precision", "Precision")]
    public void SnapshotCombinesConfigurationWithExplicitUserValues(string savedStyle, string expectedStyle)
    {
        var preferences = CreatePreferences(new Dictionary<string, object>
        {
            ["Mode"] = "Spectrum",
            ["Style"] = savedStyle,
            ["Theme"] = "",
            ["RotationMode"] = "Random",
            ["RotationIntervalMinutes"] = 999
        });

        var snapshot = VisualPreferenceSnapshot.Load(ConfiguredValue, () => preferences);

        Assert.Equal("Spectrum", snapshot.Mode);
        Assert.Equal(expectedStyle, snapshot.Style);
        Assert.Equal("Ice", snapshot.Theme);
        Assert.True(snapshot.RandomEnabled);
        Assert.Equal(240, snapshot.IntervalMinutes);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public void MalformedSettingsProviderPreservesConfigurationWithoutWriting()
    {
        var provider = new MemorySettingsProvider(new Dictionary<string, object>(), "<settings>");
        var preferences = CreatePreferences(provider);
        var snapshot = VisualPreferenceSnapshot.Load(ConfiguredValue, () => preferences);

        Assert.Equal("Bricks", snapshot.Mode);
        Assert.Equal("Glow", snapshot.Style);
        Assert.Equal("Ice", snapshot.Theme);
        Assert.IsType<ConfigurationErrorsException>(snapshot.Error);
        Assert.IsType<XmlException>(snapshot.Error.InnerException);
        Assert.False(provider.WasWritten);
    }

    [Fact]
    public void LateUserPropertyFailureDiscardsEarlierOverridesAtomically()
    {
        var preferences = CreatePreferences(new Dictionary<string, object>
        {
            ["Mode"] = "Contour",
            ["Style"] = "Precision",
            ["Theme"] = "Studio",
            ["RotationMode"] = "Random"
        });
        var malformed = new MemorySettingsProvider(new Dictionary<string, object>(), "<settings>");
        malformed.Initialize("MalformedIntervalProvider", new NameValueCollection());
        preferences.Providers.Add(malformed);
        preferences.Properties[nameof(VisualizerPreferences.RotationIntervalMinutes)].Provider = malformed;

        var snapshot = VisualPreferenceSnapshot.Load(ConfiguredValue, () => preferences);

        Assert.Equal("Bricks", snapshot.Mode);
        Assert.Equal("Glow", snapshot.Style);
        Assert.Equal("Ice", snapshot.Theme);
        Assert.False(snapshot.RandomEnabled);
        Assert.Equal(5, snapshot.IntervalMinutes);
        Assert.IsType<ConfigurationErrorsException>(snapshot.Error);
        Assert.False(malformed.WasWritten);
    }

    private static string ConfiguredValue(string key) =>
        key == "Mode" ? "Bricks" : key == "Style" ? "Glow" : "Ice";

    private static Exception CreateReadFailure(string failure) =>
        failure == "configuration" ? new ConfigurationErrorsException("Invalid settings") :
        failure == "io" ? new IOException("Settings unavailable") :
        new UnauthorizedAccessException("Settings inaccessible");

    private static VisualizerPreferences CreatePreferences(Dictionary<string, object> values) =>
        CreatePreferences(new MemorySettingsProvider(values));

    private static VisualizerPreferences CreatePreferences(MemorySettingsProvider provider)
    {
        var preferences = new VisualizerPreferences();
        provider.Initialize("ArchitectureRegressionProvider", new NameValueCollection());
        preferences.Providers.Add(provider);
        foreach (SettingsProperty property in preferences.Properties)
            property.Provider = provider;
        return preferences;
    }

    private sealed class MemorySettingsProvider : SettingsProvider
    {
        private readonly Dictionary<string, object> _values;
        private readonly string _xml;

        internal MemorySettingsProvider(Dictionary<string, object> values, string xml = null)
        {
            _values = values;
            _xml = xml;
        }

        public override string ApplicationName { get; set; } = "ArchitectureRegression";
        internal bool WasWritten { get; private set; }

        public override SettingsPropertyValueCollection GetPropertyValues(
            SettingsContext context, SettingsPropertyCollection properties)
        {
            if (_xml != null)
            {
                try
                {
                    var document = new XmlDocument { XmlResolver = null };
                    document.LoadXml(_xml);
                }
                catch (XmlException exception)
                {
                    throw new ConfigurationErrorsException("Malformed settings XML", exception);
                }
            }
            var result = new SettingsPropertyValueCollection();
            foreach (SettingsProperty property in properties)
            {
                var value = _values.TryGetValue(property.Name, out var saved)
                    ? saved
                    : property.PropertyType == typeof(int) ? (object)5 : property.DefaultValue;
                result.Add(new SettingsPropertyValue(property) { PropertyValue = value });
            }
            return result;
        }

        public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection values)
        {
            WasWritten = true;
            throw new InvalidOperationException("Preference reads must not write settings.");
        }
    }

    [Theory]
    [InlineData("", "Glow", "Glow")]
    [InlineData("None", "Glow", "None")]
    [InlineData("Precision", "Glow", "Precision")]
    [InlineData("", null, "None")]
    [InlineData("", "invalid", "None")]
    public void StartupStyleRespectsUnsetAndExplicitPreferences(string savedStyle, string configuredStyle, string expected)
    {
        var preferences = CreatePreferences(new Dictionary<string, object> { ["Style"] = savedStyle });
        var snapshot = VisualPreferenceSnapshot.Load(
            key => key == "Style" ? configuredStyle : key == "Mode" ? "Spectrum" : "ClassicSmooth",
            () => preferences);
        FormAudioSpectrum.ResolveConfiguredVisualState(snapshot.Mode, snapshot.Style, out var mode, out var resolvedStyle);

        Assert.Equal("Spectrum", mode);
        Assert.Equal(expected, resolvedStyle);
    }

    [Fact]
    public void UnsetPreferenceDefaultsAllowConfigurationFallback()
    {
        foreach (var name in new[] { "Mode", "Theme", "Style" })
        {
            var property = typeof(VisualizerPreferences).GetProperty(name);
            Assert.Equal("", property.GetCustomAttribute<DefaultSettingValueAttribute>().Value);
        }
    }

    [Fact]
    public void OptionalTaskbarOperationRunsWhenAvailable()
    {
        var invoked = false;
        Assert.True(Taskbar.TryExecuteOptional(() =>
        {
            invoked = true;
            return 0;
        }));
        Assert.True(invoked);
    }

    [Fact]
    public void OptionalTaskbarOperationToleratesUnavailableComServer()
    {
        Assert.False(Taskbar.TryExecuteOptional(() => throw new COMException("Unavailable")));
    }

    [Fact]
    public void OptionalTaskbarOperationToleratesFailedHresult()
    {
        Assert.False(Taskbar.TryExecuteOptional(() => unchecked((int)0x80004005)));
        Assert.False(Taskbar.TryExecuteOptional(() => unchecked((int)0x80070005)));
        Assert.True(Taskbar.TryExecuteOptional(() => 1));
    }

    [Fact]
    public void OptionalTaskbarOperationToleratesUnsupportedInterface()
    {
        Assert.False(Taskbar.TryExecuteOptional(() => throw new InvalidCastException()));
        Assert.False(Taskbar.TryExecuteOptional(() => throw new PlatformNotSupportedException()));
    }

    [Fact]
    public void OptionalTaskbarOperationDoesNotHideProgrammingErrors()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Taskbar.TryExecuteOptional(() => throw new InvalidOperationException()));
    }

    [Fact]
    public void TaskbarCallsFromMtaThreadDoNotActivateShellCom()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Taskbar.SetState(new IntPtr(1), Taskbar.TaskbarStates.NoProgress);
                Taskbar.SetValue(new IntPtr(1), 1, 10);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }

    [Fact]
    public void TaskbarAcceptsMissingWindowAndInvalidProgressAsNoOps()
    {
        Taskbar.SetState(IntPtr.Zero, Taskbar.TaskbarStates.NoProgress);
        Taskbar.SetValue(IntPtr.Zero, 1, 10);
        Taskbar.SetValue(new IntPtr(1), double.NaN, 10);
        Taskbar.SetValue(new IntPtr(1), 1, double.PositiveInfinity);
        Taskbar.SetValue(new IntPtr(1), -1, 10);
        Taskbar.SetValue(new IntPtr(1), 1, 0);
    }
}
