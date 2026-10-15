// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Randomizing everything at once should still land on valid, consistent settings.
public class CombinedRandomizationTests
{
    [Fact]
    public void AllCategoriesChangeAndTheLastSixteenCombinationsAreAvoided()
    {
        var modes = (string[])typeof(FormAudioSpectrum)
            .GetField("s_visualModes", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var selector = new VisualSelectionRandomizer();
        var random = new Random(4127);
        var current = new VisualSelection("Spectrum", "Glow", "Ice");
        var recent = new Queue<VisualSelection>();
        var seenModes = new HashSet<string>();
        var seenThemes = new HashSet<string>();
        var seenStyles = new HashSet<string>();
        for (var i = 0; i < 5000; i++)
        {
            var next = selector.Next(current, modes, BarColorThemes.Names, random);
            Assert.NotEqual(current.Mode, next.Mode);
            Assert.NotEqual(current.Style, next.Style);
            Assert.NotEqual(current.Theme, next.Theme);
            Assert.Contains(next.Style, VisualStyles.GetSupportedNames(next.Mode));
            Assert.DoesNotContain(next, recent);
            recent.Enqueue(next);
            if (recent.Count > 16)
                recent.Dequeue();
            seenModes.Add(next.Mode);
            seenThemes.Add(next.Theme);
            seenStyles.Add(next.Style);
            current = next;
        }
        Assert.Equal(modes.Length, seenModes.Count);
        Assert.Equal(BarColorThemes.Names.Length, seenThemes.Count);
        Assert.Equal(VisualStyles.Names.Length, seenStyles.Count);
    }

    [Fact]
    public void EveryModeHasAnEqualSelectionSlotRegardlessOfItsStyleCount()
    {
        var modes = new[] { "Spectrum", "Bricks", "LED" };
        var themes = new[] { "Ice", "Sunset", "Studio" };
        var candidateCounts = new[] { 10, 4, 6 };
        for (var modeIndex = 0; modeIndex < modes.Length; modeIndex++)
        {
            var combinations = new HashSet<VisualSelection>();
            for (var index = 0; index < candidateCounts[modeIndex]; index++)
            {
                var random = new ScriptedRandom(new[] { 3, candidateCounts[modeIndex] }, new[] { modeIndex, index });
                var next = new VisualSelectionRandomizer().Next(
                    new VisualSelection("Wave", "None", "Ice"), modes, themes, random);
                Assert.Equal(modes[modeIndex], next.Mode);
                Assert.NotEqual("None", next.Style);
                Assert.NotEqual("Ice", next.Theme);
                Assert.True(combinations.Add(next));
                Assert.Equal(2, random.Calls);
            }
        }
    }

    [Fact]
    public void TinyPoolsRelaxHistoryWithoutRelaxingCategoryChanges()
    {
        var selector = new VisualSelectionRandomizer();
        var current = new VisualSelection("Center", "None", "Ice");
        for (var i = 0; i < 100; i++)
        {
            var next = selector.Next(current, new[] { "Center", "Mirror" }, new[] { "Ice", "Studio" }, new Random(i));
            Assert.NotEqual(current.Mode, next.Mode);
            Assert.NotEqual(current.Theme, next.Theme);
            Assert.Equal("None", next.Style);
            current = next;
        }
    }

    [Fact]
    public void SingletonCategoriesAreRetainedAndEmptyPoolsAreRejected()
    {
        var selector = new VisualSelectionRandomizer();
        var current = new VisualSelection("Center", "None", "Ice");
        var random = new Random(42);
        for (var i = 0; i < 3; i++)
            Assert.Equal(current, selector.Next(current, new[] { "Center" }, new[] { "Ice" }, random));
        Assert.Throws<ArgumentException>(() => selector.Next(current, Array.Empty<string>(), new[] { "Ice" }, random));
        Assert.Throws<ArgumentException>(() => selector.Next(current, new[] { "Center" }, Array.Empty<string>(), random));
        Assert.Throws<ArgumentNullException>(() => selector.Next(current, new[] { "Center" }, new[] { "Ice" }, null));
        Assert.Equal(0, FormAudioSpectrum.GetDifferentRandomIndex(0, 1, random));
        Assert.Throws<ArgumentOutOfRangeException>(() => FormAudioSpectrum.GetDifferentRandomIndex(0, 0, random));
    }

    [Theory]
    [InlineData("Glow")]
    [InlineData("None")]
    [InlineData("Precision")]
    public void CombinedActionSynchronizesSelectorsAndAllRenderers(string style)
    {
        RunOnSta(() =>
        {
            using var form = CreateForm(style);
            var modeSelector = GetField<ComboBox>(form, "_modeSelector");
            var styleSelector = GetField<ComboBox>(form, "_styleSelector");
            var themeSelector = GetField<ComboBox>(form, "_themeSelector");
            var observations = 0;
            EventHandler observe = (sender, args) =>
            {
                observations++;
                var expectedMode = GetField<string>(form, "_visualMode");
                var expectedStyle = VisualStyles.Parse(GetField<string>(form, "_visualStyle"));
                var theme = BarColorThemes.Resolve(GetField<string>(form, "_barTheme"));
                foreach (var bar in GetField<VerticalProgressBar[]>(form, "_progressBars"))
                {
                    Assert.StartsWith(expectedMode + "|", (string)bar.Tag);
                    Assert.Equal(expectedStyle, bar.VisualStyle);
                    Assert.Equal(theme.Low, bar.HeatLowColor);
                    Assert.Equal(theme.Ui.VisualizationSurface, bar.BackColor);
                }
                if (expectedMode != "Spectrum" && expectedMode != "Bricks" && expectedMode != "LED")
                    Assert.Equal(expectedStyle, GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer").Style);
            };
            modeSelector.SelectedIndexChanged += observe;
            styleSelector.SelectedIndexChanged += observe;
            themeSelector.SelectedIndexChanged += observe;
            form.RandomizeVisualSelection(new Random(72));
            Assert.True(observations > 0);
            Assert.NotEqual("Spectrum", modeSelector.SelectedItem);
            Assert.NotEqual(style, styleSelector.SelectedItem);
            Assert.NotEqual("Ice", themeSelector.SelectedItem);
            Assert.Equal(GetField<string>(form, "_visualMode"), modeSelector.SelectedItem);
            Assert.Equal(GetField<string>(form, "_visualStyle"), styleSelector.SelectedItem);
            Assert.Equal(GetField<string>(form, "_barTheme"), themeSelector.SelectedItem);
            Assert.False(GetField<bool>(form, "_initializingSelectors"));
        });
    }

    internal static FormAudioSpectrum CreateForm(string style = "None", string mode = "Spectrum")
    {
        // The test project output lacks a reference the legacy app needs, so we load the built app artifact ourselves.
        DirectoryInfo directory = null;
        string configuration = null;
        foreach (var artifactDirectory in new[] { AppDomain.CurrentDomain.BaseDirectory,
            Path.GetDirectoryName(typeof(CombinedRandomizationTests).Assembly.Location) })
        {
            var candidate = new DirectoryInfo(artifactDirectory);
            var candidateConfiguration = candidate.Parent?.Name;
            while (candidate != null && !File.Exists(Path.Combine(candidate.FullName, "Spectrum.sln")))
                candidate = candidate.Parent;
            if (candidate == null) continue;
            directory = candidate;
            configuration = candidateConfiguration;
            break;
        }
        if (directory == null)
            throw new DirectoryNotFoundException("The form integration tests require the built Spectrum repository.");
        var applicationOutput = Path.Combine(directory.FullName, "Spectrum", "bin", configuration);
        ResolveEventHandler resolve = (sender, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (name != "System.Resources.Extensions" && name != "System.Memory" &&
                name != "System.Buffers" && name != "System.Runtime.CompilerServices.Unsafe" && name != "System.Numerics.Vectors")
                return null;
            var hostDirectory = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName);
            var hostDependency = Path.Combine(hostDirectory, name + ".dll");
            return Assembly.LoadFrom(File.Exists(hostDependency)
                ? hostDependency : Path.Combine(applicationOutput, name + ".dll"));
        };
        FormAudioSpectrum form;
        AppDomain.CurrentDomain.AssemblyResolve += resolve;
        try { form = new FormAudioSpectrum(); }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
        SetField(form, "_visualMode", mode);
        SetField(form, "_visualStyle", style);
        SetField(form, "_barTheme", "Ice");
        GetField<ComboBox>(form, "_modeSelector").SelectedItem = mode;
        GetField<ComboBox>(form, "_styleSelector").SelectedItem = style;
        GetField<ComboBox>(form, "_themeSelector").SelectedItem = "Ice";
        typeof(FormAudioSpectrum).GetMethod("InitializeBarsOptimized", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(form, new object[] { mode });
        return form;
    }

    [Theory]
    [InlineData("Precision")]
    [InlineData("Scanline")]
    public void AdvancedStartupAppliesTheConfiguredStyle(string style)
    {
        RunOnSta(() =>
        {
            using var form = CreateForm(style, "Contour");
            Assert.Equal(VisualStyles.Parse(style), GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer").Style);
            Assert.Equal(style, GetField<ComboBox>(form, "_styleSelector").SelectedItem);
        });
    }

    [Fact]
    public void ManualSelectorsRemainSynchronizedForEverySupportedCombination()
    {
        RunOnSta(() =>
        {
            using var form = CreateForm();
            SetField(form, "_applyingRandomSelection", true);
            var modes = GetField<ComboBox>(form, "_modeSelector");
            var styles = GetField<ComboBox>(form, "_styleSelector");
            var themes = GetField<ComboBox>(form, "_themeSelector");
            for (var modeIndex = 0; modeIndex < modes.Items.Count; modeIndex++)
            {
                modes.SelectedIndex = modeIndex;
                var mode = (string)modes.SelectedItem;
                Assert.Equal(mode, GetField<string>(form, "_visualMode"));
                Assert.Equal(VisualStyles.GetSupportedNames(mode),
                    System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Cast<string>(styles.Items)));
                for (var styleIndex = 0; styleIndex < styles.Items.Count; styleIndex++)
                {
                    styles.SelectedIndex = styleIndex;
                    for (var themeIndex = 0; themeIndex < themes.Items.Count; themeIndex++)
                    {
                        themes.SelectedIndex = themeIndex;
                        var theme = BarColorThemes.Resolve((string)themes.SelectedItem);
                        var style = VisualStyles.Parse((string)styles.SelectedItem);
                        Assert.Equal(styles.SelectedItem, GetField<string>(form, "_visualStyle"));
                        Assert.Equal(themes.SelectedItem, GetField<string>(form, "_barTheme"));
                        if (mode != "Spectrum" && mode != "Bricks" && mode != "LED")
                            Assert.Equal(style, GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer").Style);
                        foreach (var bar in GetField<VerticalProgressBar[]>(form, "_progressBars"))
                        {
                            Assert.Equal(style, bar.VisualStyle);
                            Assert.Equal(theme.Low, bar.HeatLowColor);
                            Assert.Equal(theme.Ui.PrimaryText, bar.PeakLineColor);
                            Assert.False(bar.PeakColorMatchesHeat);
                        }
                    }
                }
            }
        });
    }

    [Theory]
    [InlineData("Waterfall")]
    [InlineData("Contour")]
    [InlineData("Level Change")]
    [InlineData("Stereo Levels")]
    [InlineData("Oscilloscope")]
    public void SwitchingViewsUsesTheLatestFrameAndClearsOldSignalsDuringSilence(string mode)
    {
        RunOnSta(() =>
        {
            using var form = CreateForm();
            var values = GetField<byte[]>(form, "_applyBuffer");
            for (var i = 0; i < values.Length; i++) values[i] = 42;
            var advanced = GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            var stale = new byte[83];
            for (var i = 0; i < stale.Length; i++) stale[i] = 220;
            advanced.SetSpectrum(stale);
            SetField(form, "_applyingRandomSelection", true);
            var selector = GetField<ComboBox>(form, "_modeSelector");
            selector.SelectedItem = mode;
            Assert.Equal((byte)42, GetField<byte[]>(advanced, "_spectrum")[0]);
            Assert.Equal(1, GetField<int>(advanced, "_historyCount"));
            Assert.Equal(GetField<byte[]>(advanced, "_spectrum"), GetField<byte[]>(advanced, "_previousSpectrum"));
            selector.SelectedItem = "Spectrum";
            Array.Clear(values, 0, values.Length);
            selector.SelectedItem = mode;
            Assert.Equal((byte)0, GetField<byte[]>(advanced, "_spectrum")[0]);
        });
    }

    internal static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    internal static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    internal static void RunOnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ScriptedRandom : Random
    {
        private readonly int[] _bounds;
        private readonly int[] _values;
        internal ScriptedRandom(int[] bounds, int[] values) { _bounds = bounds; _values = values; }
        internal int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Assert.Equal(_bounds[Calls], maxValue);
            return _values[Calls++];
        }
    }
}
