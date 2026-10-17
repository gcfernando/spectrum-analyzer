// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

// Wires the real pieces together to make sure the product behaves as a whole, not just per unit.
public class ProductIntegrationTests
{
    [Fact]
    public void RegistryIsAlphabeticalContainsAllStableNamesAndReturnsAnOwnedCopy()
    {
        var expected = new[]
        {
            "Band Matrix", "Bricks", "Contour", "Dynamics History", "LED", "Level Change",
            "Octave Spectrum", "Octave Waterfall", "Oscilloscope", "Peak Trace", "Radial Spectrum",
            "Advanced Spectral Balance", "Spectral Balance", "Spectral History", "Spectrum", "Stereo Levels", "Stereo Spectrum",
            "Threshold Monitor", "Waterfall", "Overview", "Stereo Compare"
        };
        var first = FormAudioSpectrum.VisualModeNames;
        Assert.Equal(FormAudioSpectrum.SortModeNames(expected), first);
        Assert.Equal(expected.Length, first.Distinct(StringComparer.Ordinal).Count());
        first[0] = "Mutated by caller";
        Assert.Equal(FormAudioSpectrum.SortModeNames(expected), FormAudioSpectrum.VisualModeNames);
        Assert.NotSame(first, FormAudioSpectrum.VisualModeNames);
    }

    [Fact]
    public void NewHistoryModeAppendsWithoutRenumberingAnyExistingAdvancedIdentifier()
    {
        var stableNames = new[]
        {
            "Waterfall", "RadialSpectrum", "Contour", "NoteMap", "PeakTrace", "ThresholdMonitor",
            "BandMatrix", "OctaveSpectrum", "SpectralFlux", "OrbitHistory", "OctaveWaterfall",
            "TransientMap", "FrequencyRibbon", "StereoLevels", "Oscilloscope", "SpectralBalance",
            "StereoSpectrum", "DynamicsHistory", "SpectralHistory", "AdvancedSpectralBalance"
        };
        for (var identifier = 0; identifier < stableNames.Length; identifier++)
            Assert.Equal(stableNames[identifier], Enum.GetName(typeof(AdvancedVisualizationMode), identifier));
        Assert.Equal(20, (int)AdvancedVisualizationMode.Overview);
        Assert.Equal(21, (int)AdvancedVisualizationMode.StereoCompare);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("sv-SE")]
    [InlineData("tr-TR")]
    public void SortingUsesCurrentCultureIgnoringCaseWithAnOrdinalTieBreak(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            var input = new[] { "zeta", "Alpha", "alpha", "Ångström", "Äther", "Iris", "iris", "İris", "ıRİS", "Alpha" };
            var untouched = (string[])input.Clone();
            var expected = input.OrderBy(name => name, Comparer<string>.Create((left, right) =>
            {
                var compared = culture.CompareInfo.Compare(left, right, CompareOptions.IgnoreCase);
                return compared != 0 ? compared : StringComparer.Ordinal.Compare(left, right);
            })).ToArray();
            Assert.Equal(expected, FormAudioSpectrum.SortModeNames(input));
            Assert.Equal(expected, FormAudioSpectrum.SortModeNames(input.Reverse()));
            Assert.Equal(untouched, input);
            Assert.Equal(input.Length, expected.Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void SortedDropdownPreservesIdentifiersSelectionAndKeyboardWraparound()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var selector = CombinedRandomizationTests.GetField<ComboBox>(form, "_modeSelector");
            var names = FormAudioSpectrum.VisualModeNames;
            Assert.Equal(names, selector.Items.Cast<string>().ToArray());
            Assert.Equal(ComboBoxStyle.DropDownList, selector.DropDownStyle);
            Assert.False(string.IsNullOrWhiteSpace(selector.AccessibleName));
            var keyUp = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_KeyUp", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var name in names)
            {
                selector.SelectedItem = name;
                Assert.Equal(name, CombinedRandomizationTests.GetField<string>(form, "_visualMode"));
                FormAudioSpectrum.ResolveConfiguredVisualState(name, "Precision", out var resolved, out _);
                Assert.Equal(name, resolved);
            }
            selector.SelectedIndex = names.Length - 1;
            foreach (var name in names)
            {
                var args = new KeyEventArgs(Keys.Control | Keys.M);
                keyUp.Invoke(form, new object[] { form, args });
                Assert.True(args.Handled);
                Assert.Equal(name, selector.SelectedItem);
                Assert.Equal(name, CombinedRandomizationTests.GetField<string>(form, "_visualMode"));
            }
        });
    }

    [Theory]
    [InlineData(20, 14)]
    [InlineData(30, 21)]
    [InlineData(40, 28)]
    [InlineData(60, 42)]
    public void EveryModeIconIsVisibleDistinctAndContainedAtSupportedScales(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            var drawIcon = typeof(FormAudioSpectrum).GetMethod("DrawModeIcon", BindingFlags.Static | BindingFlags.NonPublic);
            var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            var bounds = new Rectangle(6, 6, width, height);
            foreach (var name in FormAudioSpectrum.VisualModeNames)
            {
                using var bitmap = new Bitmap(width + 12, height + 12);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    drawIcon.Invoke(null, new object[] { graphics, name, bounds, Color.White });
                }
                var pixels = new List<int>();
                var active = 0;
                for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A == 0) continue;
                    Assert.True(bounds.Contains(x, y), $"{name} icon painted outside {bounds} at ({x}, {y}).");
                    active++;
                    pixels.Add(y * bitmap.Width + x);
                    pixels.Add(pixel.ToArgb());
                }
                Assert.True(active >= width / 2, $"{name} icon has insufficient visible detail.");
                var fingerprint = string.Join(",", pixels);
                Assert.DoesNotContain(fingerprint, fingerprints.Values);
                fingerprints.Add(name, fingerprint);
            }
        });
    }

    [Theory]
    [InlineData(900, 250)]
    [InlineData(1184, 378)]
    [InlineData(1920, 1080)]
    public void ResizePreservesSelectionAndKeepsVisibleSelectorsWithinTheClientArea(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var selector = CombinedRandomizationTests.GetField<ComboBox>(form, "_modeSelector");
            selector.SelectedItem = "Spectral History";
            form.ClientSize = new Size(width, height);
            form.PerformLayout();
            typeof(FormAudioSpectrum).GetMethod("PositionSelectionPanel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            var panel = CombinedRandomizationTests.GetField<FlowLayoutPanel>(form, "_selectionPanel");
            panel.PerformLayout();
            Assert.Equal("Spectral History", selector.SelectedItem);
            Assert.True(panel.Parent.ClientRectangle.Contains(panel.Bounds), $"Selectors exceed the {width}x{height} client area.");
            var visibleControls = new List<Control> { selector,
                CombinedRandomizationTests.GetField<Button>(form, "_viewButton") };
            if (!CombinedRandomizationTests.GetField<bool>(form, "_compactToolbar"))
                visibleControls.Add(CombinedRandomizationTests.GetField<Button>(form, "_rotationSettingsButton"));
            if (width >= 700)
            {
                visibleControls.Add(CombinedRandomizationTests.GetField<ComboBox>(form, "_styleSelector"));
                visibleControls.Add(CombinedRandomizationTests.GetField<ComboBox>(form, "_themeSelector"));
            }
            foreach (var child in visibleControls)
            {
                Assert.True(child.Width > 0 && child.Left >= 0 && child.Right <= panel.ClientSize.Width,
                    $"{child.Name} is horizontally clipped at {width}x{height}.");
                foreach (var other in visibleControls.Where(other => other != child))
                    Assert.False(child.Bounds.IntersectsWith(other.Bounds), $"{child.Name} overlaps {other.Name}.");
            }
        });
    }

    [Theory]
    [InlineData(900, 250)]
    [InlineData(1184, 378)]
    public void EverySelectedStyleFitsTheActualNativeItemAfterIconArrowAndTextPadding(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: "Spectrum");
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            form.ClientSize = new Size(width, height);
            form.PerformLayout();
            typeof(FormAudioSpectrum).GetMethod("PositionSelectionPanel", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            var selector = CombinedRandomizationTests.GetField<ComboBox>(form, "_styleSelector");
            var modeSelector = CombinedRandomizationTests.GetField<ComboBox>(form, "_modeSelector");
            using var bitmap = new Bitmap(selector.Width, selector.Height);
            using var graphics = Graphics.FromImage(bitmap);
            Assert.Equal(VisualStyles.GetEffectNames("Spectrum"), selector.Items.Cast<string>().ToArray());
            foreach (var style in VisualStyles.GetEffectNames("Spectrum"))
            {
                selector.SelectedItem = style;
                Assert.Equal(style, selector.SelectedItem);
                var info = new ComboBoxInfo { Size = Marshal.SizeOf(typeof(ComboBoxInfo)) };
                Assert.True(GetComboBoxInfo(selector.Handle, ref info), "The selected item must expose its native text/arrow geometry.");
                Assert.True(info.Button.Right > info.Button.Left, "The native drop-down arrow must be excluded.");
                var itemWidth = info.Item.Right - info.Item.Left;
                // The selector item draws its text a fixed offset from the left and keeps a small right margin, so we pin those numbers.
                var textWidth = itemWidth - 34 - 4;
                var required = TextRenderer.MeasureText(graphics, style, modeSelector.Font,
                    new Size(int.MaxValue, selector.ItemHeight),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                Assert.True(required.Width <= textWidth,
                    $"{style} requires {required.Width}px but only {textWidth}px remain after the actual arrow, icon and padding at {width}x{height}.");
            }
        });
    }

    [Theory]
    [InlineData("Spectrum", "Glow", "Ice", "Fixed", 1)]
    [InlineData("Spectral History", "Precision", "Studio", "Random", 240)]
    public void UserSettingsSaveAndReloadWithAnIsolatedProvider(
        string mode, string style, string theme, string rotation, int minutes)
    {
        var provider = new IsolatedSettingsProvider();
        var original = CreateIsolatedPreferences(provider);
        original.Mode = mode;
        original.Style = style;
        original.Theme = theme;
        original.RotationMode = rotation;
        original.RotationIntervalMinutes = minutes;
        original.Save();
        Assert.Equal(1, provider.SaveCalls);
        var reloaded = CreateIsolatedPreferences(provider);
        reloaded.Reload();
        Assert.Equal(mode, reloaded.Mode);
        Assert.Equal(style, reloaded.Style);
        Assert.Equal(theme, reloaded.Theme);
        Assert.Equal(rotation, reloaded.RotationMode);
        Assert.Equal(minutes, reloaded.RotationIntervalMinutes);
        FormAudioSpectrum.ResolveConfiguredVisualState(reloaded.Mode, reloaded.Style, out var resolvedMode, out var resolvedStyle);
        Assert.Equal(mode, resolvedMode);
        Assert.Equal(style, resolvedStyle);
    }

    private static VisualizerPreferences CreateIsolatedPreferences(SettingsProvider provider)
    {
        var settings = new VisualizerPreferences();
        settings.Providers.Clear();
        settings.Providers.Add(provider);
        foreach (SettingsProperty property in settings.Properties)
            property.Provider = provider;
        return settings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public NativeRectangle Item;
        public NativeRectangle Button;
        public int ButtonState;
        public IntPtr Combo, ItemWindow, ListWindow;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr combo, ref ComboBoxInfo info);

    private sealed class IsolatedSettingsProvider : SettingsProvider
    {
        private readonly Dictionary<string, object> _saved = new(StringComparer.Ordinal);
        public IsolatedSettingsProvider() => Initialize("Spectrum isolated test settings", new NameValueCollection());
        public override string ApplicationName { get; set; } = "Spectrum.Tests";
        internal int SaveCalls { get; private set; }

        public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection properties)
        {
            var values = new SettingsPropertyValueCollection();
            foreach (SettingsProperty property in properties)
            {
                var value = _saved.TryGetValue(property.Name, out var saved)
                    ? saved
                    : Convert.ChangeType(property.DefaultValue, property.PropertyType, CultureInfo.InvariantCulture);
                values.Add(new SettingsPropertyValue(property) { PropertyValue = value, IsDirty = false });
            }
            return values;
        }

        public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection values)
        {
            SaveCalls++;
            foreach (SettingsPropertyValue value in values)
                _saved[value.Name] = value.PropertyValue;
        }
    }
}
