// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class GuiRevisionRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("", "Precision", true)]
    [InlineData("", " Precision ", true)]
    [InlineData("False", "Precision", false)]
    [InlineData("True", "None", true)]
    [InlineData("", "Glow", false)]
    public void IndependentGuidesMigrateOnlyWhenTheUserHasNotMadeAnExplicitChoice(
        string savedGuides, string savedStyle, bool expected)
    {
        var provider = new MemoryProvider(new Dictionary<string, object>
        {
            ["Guides"] = savedGuides, ["Style"] = savedStyle, ["Appearance"] = "Light"
        });
        var preferences = new VisualizerPreferences();
        preferences.Providers.Clear();
        preferences.Providers.Add(provider);
        foreach (SettingsProperty property in preferences.Properties) property.Provider = provider;
        var snapshot = VisualPreferenceSnapshot.Load(
            key => key == "Mode" ? "Spectrum" : key == "Style" ? "None" : "Studio", () => preferences);
        Assert.Equal(expected, snapshot.ShowGuides);
        Assert.Equal("Light", snapshot.Appearance);
        Assert.False(provider.WasWritten);
    }

    [Fact]
    public void EffectsCatalogSeparatesGuidesAndRetiredScanlineWithoutBreakingSavedStyles()
    {
        Assert.Equal(4, (int)VisualStyle.Scanline);
        Assert.Equal(5, (int)VisualStyle.Precision);
        Assert.Equal(6, (int)VisualStyle.Outline);
        Assert.Equal(VisualStyle.Scanline, VisualStyles.Parse("Scanline"));
        Assert.Equal(VisualStyle.Precision, VisualStyles.Parse("Precision"));
        foreach (var mode in FormAudioSpectrum.VisualModeNames)
        {
            var effects = VisualStyles.GetEffectNames(mode);
            Assert.Contains("None", effects);
            Assert.DoesNotContain("Precision", effects);
            Assert.DoesNotContain("Scanline", effects);
            Assert.Equal(effects.Length, effects.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var effect in effects) Assert.Contains(effect, VisualStyles.GetSupportedNames(mode));
        }
        Assert.Contains("Outline", VisualStyles.GetEffectNames("Spectrum"));
    }

    [Theory]
    [InlineData("_modeSelector")]
    [InlineData("_styleSelector")]
    [InlineData("_themeSelector")]
    public void ControlThemeShortcutIsHandledBeforeFocusedNativeSelectorsCanTypeSelect(string focusedSelector)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: "Waterfall");
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var mode = Field<ComboBox>(form, "_modeSelector");
            var style = Field<ComboBox>(form, "_styleSelector");
            var theme = Field<ComboBox>(form, "_themeSelector");
            form.ActiveControl = Field<ComboBox>(form, focusedSelector);
            var originalMode = mode.SelectedItem;
            var originalStyle = style.SelectedItem;
            var expectedTheme = (theme.SelectedIndex + 1) % theme.Items.Count;
            var message = Message.Create(form.Handle, 0x0100, (IntPtr)Keys.T, IntPtr.Zero);
            var arguments = new object[] { message, Keys.Control | Keys.T };
            Assert.True((bool)typeof(FormAudioSpectrum).GetMethod("ProcessCmdKey", PrivateInstance).Invoke(form, arguments));
            Assert.Equal(expectedTheme, theme.SelectedIndex);
            Assert.Equal(originalMode, mode.SelectedItem);
            Assert.Equal(originalStyle, style.SelectedItem);
        });
    }

    [Fact]
    public void FrozenDisplayRejectsFramesAndSevenBandBallisticsAndResumeSeedsANewDelta()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Mode = AdvancedVisualizationMode.SpectralFlux };
            control.SetSpectrum(Enumerable.Repeat((byte)100, 83).ToArray());
            var audio = new AudioVisualizationData();
            audio.Update(new[] { 0.5f, -0.5f }, 2, 1, 48000);
            audio.SetSevenBandPower(Enumerable.Repeat(0.01, 7).ToArray());
            control.SetAudioData(audio);
            control.DisplayFrozen = true;
            var spectrum = (byte[])Field<byte[]>(control, "_spectrum").Clone();
            var seven = (float[])Field<float[]>(control, "_sevenLevels").Clone();
            var count = Field<int>(control, "_historyCount");
            Assert.False(Field<Timer>(control, "_sevenTimer").Enabled);
            control.SetSpectrum(Enumerable.Repeat((byte)240, 83).ToArray());
            control.SynchronizeSpectrum(Enumerable.Repeat((byte)20, 83).ToArray());
            typeof(AdvancedVisualizationControl).GetMethod("UpdateSevenBandBallistics", PrivateInstance).Invoke(control, null);
            Assert.Equal(spectrum, Field<byte[]>(control, "_spectrum"));
            Assert.Equal(seven, Field<float[]>(control, "_sevenLevels"));
            Assert.Equal(count, Field<int>(control, "_historyCount"));
            control.DisplayFrozen = false;
            control.SetSpectrum(Enumerable.Repeat((byte)40, 83).ToArray());
            Assert.Equal(1, Field<int>(control, "_historyCount"));
            Assert.Equal(Field<byte[]>(control, "_spectrum"), Field<byte[]>(control, "_previousSpectrum"));
        });
    }

    [Fact]
    public void SharedMeterTimerDoesNotReleasePeaksOrTrailWhileFrozen()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var bar = new VerticalProgressBar { Maximum = 255, Size = new Size(18, 180), Tag = "Spectrum" };
            bar.CreateControl();
            CombinedRandomizationTests.SetField(bar, "_displayValue", 180f);
            CombinedRandomizationTests.SetField(bar, "_peakValue", 220f);
            CombinedRandomizationTests.SetField(bar, "_peakHoldLeftMs", 500f);
            bar.DisplayFrozen = true;
            bar.SetTargetValueThreadSafe(0);
            var tick = typeof(VerticalProgressBar).GetMethod("TickAll", BindingFlags.Static | BindingFlags.NonPublic);
            for (var i = 0; i < 4; i++) tick.Invoke(null, null);
            Assert.Equal(180f, Field<float>(bar, "_displayValue"));
            Assert.Equal(220f, Field<float>(bar, "_peakValue"));
            Assert.Equal(500f, Field<float>(bar, "_peakHoldLeftMs"));
            bar.DisplayFrozen = false;
            tick.Invoke(null, null);
            Assert.InRange(Field<float>(bar, "_displayValue"), 100f, 180f);
            Assert.Equal(220f, Field<float>(bar, "_peakValue"));
        });
    }

    [Fact]
    public void FrozenModeAndPaletteChangesCannotRestartAutomaticRotation()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            CombinedRandomizationTests.SetField(form, "_randomRotationEnabled", true);
            typeof(FormAudioSpectrum).GetMethod("ConfigureRotationTimer", PrivateInstance).Invoke(form, null);
            var rotation = Field<Timer>(form, "_rotationTimer");
            Assert.True(rotation.Enabled);
            form.SetDisplayFrozen(true);
            Field<ComboBox>(form, "_modeSelector").SelectedItem = "Waterfall";
            Field<ComboBox>(form, "_themeSelector").SelectedItem = "Studio";
            Assert.False(rotation.Enabled);
            Assert.All(Field<VerticalProgressBar[]>(form, "_progressBars"), bar => Assert.True(bar.DisplayFrozen));
            Assert.True(Field<AdvancedVisualizationControl>(form, "_advancedVisualizer").DisplayFrozen);
            form.SetDisplayFrozen(false);
            Assert.True(rotation.Enabled);
            Assert.All(Field<VerticalProgressBar[]>(form, "_progressBars"), bar => Assert.False(bar.DisplayFrozen));
            Assert.False(Field<AdvancedVisualizationControl>(form, "_advancedVisualizer").DisplayFrozen);
        });
    }

    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(72)]
    public void ChangeRangeChangesOnlyGeometryAndMarksOverflowWithoutRenormalizingData(int range)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.SpectralFlux, Size = new Size(830, 120), ThemeName = "Studio"
            };
            Assert.Equal(12, control.DeltaRangeDb);
            var previous = Enumerable.Repeat((byte)100, 83).ToArray();
            var current = (byte[])previous.Clone();
            current[40] = 121; // +5.929 dB, below every allowed range.
            current[50] = 240; // +39.529 dB; overflow for 6, 12 and 24, but not 72.
            current[60] = 0;   // -28.235 dB, preserves falling direction.
            control.SynchronizeSpectrum(previous);
            control.SetSpectrum(current);
            control.DeltaRangeDb = range;
            using var bitmap = VisualizationReadabilityTests.Render(control);
            var rise = Field<Color>(control, "_riseColor").ToArgb();
            var fall = Field<Color>(control, "_fallColor").ToArgb();
            var count = Enumerable.Range(17, 43).Count(y => bitmap.GetPixel(405, y).ToArgb() == rise);
            var expectedExtent = 36 * (21 * 72d / 255) / range;
            Assert.InRange(count, Math.Max(0, (int)Math.Floor(expectedExtent) - 1), (int)Math.Ceiling(expectedExtent) + 1);
            Assert.Equal(fall, bitmap.GetPixel(605, 62).ToArgb());
            if (range < 72) Assert.Equal(BarColorThemes.Studio.Ui.PrimaryText.ToArgb(), bitmap.GetPixel(505, 25).ToArgb());
            else Assert.NotEqual(BarColorThemes.Studio.Ui.PrimaryText.ToArgb(), bitmap.GetPixel(505, 25).ToArgb());
            Assert.Equal(current, Field<byte[]>(control, "_spectrum"));
            Assert.Equal(previous, Field<byte[]>(control, "_previousSpectrum"));
            Assert.Throws<ArgumentOutOfRangeException>(() => control.DeltaRangeDb = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => control.DeltaRangeDb = 13);
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void HistoryPaletteHasOrderedLuminanceAndRetainsTheCorrectFloorAndFullScale(string appearance)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Mode = AdvancedVisualizationMode.Waterfall, Size = new Size(900, 280) };
            control.ApplyTheme(BarColorThemes.ApplyAppearance(BarColorThemes.Aurora, appearance));
            var palette = Field<int[]>(control, "_paletteArgb").Select(Color.FromArgb).ToArray();
            var ui = BarColorThemes.ApplyAppearance(BarColorThemes.Aurora, appearance).Ui;
            Assert.Equal(control.BackColor, palette[0]);
            Assert.Equal(ui.PrimaryText, palette[255]);
            var ascending = Luminance(palette[255]) > Luminance(palette[0]);
            for (var i = 1; i < palette.Length; i++)
                Assert.True(ascending ? Luminance(palette[i]) >= Luminance(palette[i - 1])
                    : Luminance(palette[i]) <= Luminance(palette[i - 1]), $"History luminance reversed at level {i}.");
            control.SetSpectrum(Enumerable.Repeat((byte)160, 83).ToArray());
            using var bitmap = VisualizationReadabilityTests.Render(control);
            var legend = (int)typeof(AdvancedVisualizationControl).GetMethod("GetHistoryLegendHeight", PrivateInstance)
                .Invoke(control, new object[] { control.Width, control.Height });
            Assert.True(legend >= 18, "A readable history view must reserve room for its level legend.");
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CompactLevelCardsPaintChannelAndMeasurementIdentityWithTheirNumbers(int channels)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.StereoLevels, ThemeName = "Studio", Size = new Size(240, 240)
            };
            var samples = new float[64 * channels];
            for (var frame = 0; frame < 64; frame++)
            {
                samples[frame * channels] = frame % 2 == 0 ? 0.5f : 0;
                if (channels == 2) samples[frame * channels + 1] = frame % 2 == 0 ? 0.125f : 0;
            }
            control.SetAudioSamples(samples, 64, channels, 48000);
            using var actual = VisualizationReadabilityTests.Render(control);
            var bodyFont = Field<Font>(control, "_analysisBodyFont");
            var titleFont = Field<Font>(control, "_analysisTitleFont");
            using var metrics = Graphics.FromImage(actual);
            var bodyHeight = bodyFont.GetHeight(metrics);
            var titleHeight = titleFont.GetHeight(metrics);
            var captionHeight = (int)typeof(AdvancedVisualizationControl).GetMethod("GetCaptionHeight", PrivateInstance)
                .Invoke(control, new object[] { control.Width, control.Height });
            var bottom = control.Height - (2 * bodyHeight + 10);
            var cardWidth = control.Width / (float)channels;
            var centered = Field<StringFormat>(control, "_centeredText");
            for (var channel = 0; channel < channels; channel++)
            {
                var x = channel * cardWidth + 6;
                var innerWidth = cardWidth - 12;
                var axisWidth = innerWidth >= 100 ? 34 : 0;
                var meterLeft = x + axisWidth;
                var meterWidth = innerWidth - axisWidth;
                AssertRenderedText(actual, channels == 1 ? "MONO" : channel == 0 ? "LEFT" : "RIGHT", bodyFont,
                    BarColorThemes.Studio.Ui.PrimaryText, control.BackColor,
                    new RectangleF(x, captionHeight + 3, innerWidth, titleHeight + 2), centered);
                for (var measure = 0; measure < 2; measure++)
                {
                    var labelX = meterLeft + meterWidth * measure / 2;
                    AssertRenderedText(actual, measure == 0 ? "RMS" : "Pk", bodyFont,
                        BarColorThemes.Studio.Ui.SecondaryText, control.BackColor,
                        new RectangleF(labelX, bottom + 2, meterWidth / 2, bodyHeight + 1), centered);
                    AssertRenderedText(actual, Field<string[]>(control, "_audioShortLabels")[channel * 2 + measure], bodyFont,
                        BarColorThemes.Studio.Ui.PrimaryText, control.BackColor,
                        new RectangleF(labelX, bottom + bodyHeight + 4, meterWidth / 2, bodyHeight + 1), centered);
                }
            }
        });
    }

    [Theory]
    [InlineData(AdvancedVisualizationMode.Waterfall)]
    [InlineData(AdvancedVisualizationMode.OctaveWaterfall)]
    public void HistoryGuidesChangeThePlotWithoutPaintingAcrossTheCaptionOrLevelLegend(AdvancedVisualizationMode mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Mode = mode, ThemeName = "Studio", Size = new Size(900, 280) };
            for (var frame = 0; frame < 300; frame++) control.SetSpectrum(Enumerable.Repeat((byte)120, 83).ToArray());
            if (mode == AdvancedVisualizationMode.OctaveWaterfall)
                Assert.Contains("approx. octave power", Field<string>(control, "_historyCaption"));
            using var withoutGuides = VisualizationReadabilityTests.Render(control);
            control.ShowGuides = true;
            using var withGuides = VisualizationReadabilityTests.Render(control);
            var captionHeight = (int)typeof(AdvancedVisualizationControl).GetMethod("GetCaptionHeight", PrivateInstance)
                .Invoke(control, new object[] { control.Width, control.Height });
            var legendHeight = (int)typeof(AdvancedVisualizationControl).GetMethod("GetHistoryLegendHeight", PrivateInstance)
                .Invoke(control, new object[] { control.Width, control.Height });
            var plotTop = captionHeight + legendHeight;
            Assert.True(legendHeight > 0);
            for (var y = 0; y < plotTop; y++)
            for (var x = 0; x < control.Width; x++)
                Assert.Equal(withoutGuides.GetPixel(x, y), withGuides.GetPixel(x, y));
            Assert.Contains(Enumerable.Range(plotTop + 1, control.Height - plotTop - 22),
                y => Enumerable.Range(0, control.Width).Any(x => withoutGuides.GetPixel(x, y) != withGuides.GetPixel(x, y)));
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void IndependentAppearanceKeepsAllSignalPalettesAndReadableControlText(string appearance)
    {
        foreach (var name in BarColorThemes.Names)
        {
            var palette = BarColorThemes.Resolve(name);
            var theme = BarColorThemes.ApplyAppearance(palette, appearance);
            Assert.Equal(palette.IntensityCurve, theme.IntensityCurve);
            foreach (var signal in new[] { theme.Low, theme.Mid, theme.High, theme.Peak })
                Assert.True(VisualizationReadabilityTests.Contrast(signal, theme.Ui.VisualizationSurface) >= 3,
                    $"{name} / {appearance} has insufficient signal contrast.");
            foreach (var surface in new[] { theme.Ui.Canvas, theme.Ui.ControlSurface, theme.Ui.Surface })
            {
                Assert.True(VisualizationReadabilityTests.Contrast(theme.Ui.PrimaryText, surface) >= 4.5);
                Assert.True(VisualizationReadabilityTests.Contrast(theme.Ui.SecondaryText, surface) >= 4.5);
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(70, 42)]
    [InlineData(900, 280)]
    public void CompositeViewsPreserveMeasuredChannelIdentityAndExplicitMonoAvailability(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            foreach (var channels in new[] { 1, 2 })
            {
                var data = new AudioVisualizationData();
                data.Update(Enumerable.Repeat(0.25f, 32 * channels).ToArray(), 32, channels, 48000);
                var left = Enumerable.Repeat(0.01, 83).ToArray();
                var right = Enumerable.Repeat(0.0001, 83).ToArray();
                data.SetChannelSpectrum(left, channels == 2 ? right : null);
                var expectedLeft = (byte[])data.LeftSpectrum.Clone();
                var expectedRight = (byte[])data.RightSpectrum.Clone();
                foreach (var mode in new[] { AdvancedVisualizationMode.Overview, AdvancedVisualizationMode.StereoCompare })
                foreach (var appearance in new[] { "Palette", "Dark", "Light", "High Contrast" })
                {
                    using var control = new AdvancedVisualizationControl { Mode = mode, Size = new Size(width, height), ShowGuides = true };
                    control.ApplyTheme(BarColorThemes.ApplyAppearance(BarColorThemes.Studio, appearance));
                    control.SetSpectrum(expectedLeft);
                    control.SetAudioData(data);
                    using var bitmap = VisualizationReadabilityTests.Render(control);
                    var copied = Field<AudioVisualizationData>(control, "_audio");
                    Assert.Equal(expectedLeft, copied.LeftSpectrum);
                    Assert.Equal(expectedRight, copied.RightSpectrum);
                    Assert.Equal(channels == 2, copied.HasStereo);
                    if (channels == 1) Assert.All(copied.RightSpectrum, sample => Assert.Equal((byte)0, sample));
                    Assert.Equal(expectedLeft, data.LeftSpectrum);
                    Assert.Equal(expectedRight, data.RightSpectrum);
                }
            }
        });
    }

    [Theory]
    [InlineData("Overview")]
    [InlineData("Stereo Compare")]
    public void CompositeViewsHideTheFullWindowFrequencyAxisThatWouldMislabelTheirInternalPlots(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
            form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
            form.ShowInTaskbar = false;
            form.Show();
            Field<ComboBox>(form, "_modeSelector").SelectedItem = mode;
            Application.DoEvents();
            Assert.True(Field<AdvancedVisualizationControl>(form, "_advancedVisualizer").Visible);
            Assert.All(Field<Label[]>(form, "_axisLabels"), label => Assert.False(label.Visible,
                $"{mode} must label its own plot coordinates; the full-window {label.Text} label is misleading."));
            form.Close();
        });
    }

    [Theory]
    [InlineData(320, 180)]
    [InlineData(900, 280)]
    [InlineData(1200, 400)]
    public void OverviewPlacesItsTwentyKilohertzGridAndLabelAtTheSpectrumPaneEdge(int width, int height)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.Overview, ThemeName = "Studio", Size = new Size(width, height)
            };
            control.SetSpectrum(new byte[83]);
            using var actual = VisualizationReadabilityTests.Render(control);
            var plot = control.OverviewSpectrumPlotBounds;
            var subplotWidth = width >= 400 ? (int)(width * .70f) - 5 : width;
            Assert.Equal(0f, plot.Left);
            Assert.Equal(subplotWidth - 1, plot.Right);
            Assert.True(plot.Height > 50 && plot.Bottom < height - 10, "The frequency labels need their own row below the plotted spectrum.");
            if (width >= 400) Assert.True(plot.Right < width * .71f, "The spectrum axis must end inside the left pane.");
            var gridTop = (int)Math.Ceiling(plot.Top) + 5;
            var gridRows = (int)Math.Floor(plot.Bottom) - gridTop - 5;
            var endpointGridPixels = Enumerable.Range(gridTop, gridRows)
                .Count(y => actual.GetPixel((int)plot.Right, y).ToArgb() != control.BackColor.ToArgb());
            Assert.True(endpointGridPixels >= gridRows * .8, "The 20 kHz vertical grid must align with the spectrum pane endpoint.");
            var font = Field<Font>(control, "_analysisBodyFont");
            using var graphics = Graphics.FromImage(actual);
            var bodyHeight = font.GetHeight(graphics);
            var footer = (int)Math.Ceiling(bodyHeight + 7);
            var text = SpectrumAxis.FrequencyLabels[SpectrumAxis.Frequencies.Length - 1];
            Assert.Equal(20000, SpectrumAxis.Frequencies.Last());
            var textWidth = graphics.MeasureString(text, font).Width;
            var textLeft = Math.Min(subplotWidth - textWidth, plot.Right - textWidth / 2);
            AssertRenderedText(actual, text, font, BarColorThemes.Studio.Ui.SecondaryText, control.BackColor,
                new RectangleF(textLeft, height - footer, textWidth, bodyHeight + 2), Field<StringFormat>(control, "_centeredText"));
        });
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);

    internal static void AssertRenderedText(Bitmap actual, string text, Font font, Color foreground, Color background,
        RectangleF bounds, StringFormat format)
    {
        using var reference = new Bitmap(actual.Width, actual.Height);
        using (var graphics = Graphics.FromImage(reference))
        using (var brush = new SolidBrush(foreground))
        {
            graphics.Clear(background);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString(text, font, brush, bounds, format);
        }
        var pixels = 0;
        var rectangle = Rectangle.Intersect(new Rectangle(0, 0, actual.Width, actual.Height), Rectangle.Ceiling(bounds));
        for (var y = rectangle.Top; y < rectangle.Bottom; y++)
        for (var x = rectangle.Left; x < rectangle.Right; x++)
        {
            var expected = reference.GetPixel(x, y);
            if (expected.ToArgb() == background.ToArgb()) continue;
            pixels++;
            Assert.Equal(expected, actual.GetPixel(x, y));
        }
        Assert.True(pixels > 4, $"The compact {text} label must have visible readable glyphs.");
    }

    private static double Luminance(Color color)
    {
        double Linear(byte value) { var unit = value / 255d; return unit <= 0.04045 ? unit / 12.92 : Math.Pow((unit + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private sealed class MemoryProvider : SettingsProvider
    {
        private readonly Dictionary<string, object> _values;
        internal bool WasWritten { get; private set; }
        internal MemoryProvider(Dictionary<string, object> values)
        {
            _values = values;
            Initialize("GUI revision isolated preferences", new NameValueCollection());
        }
        public override string ApplicationName { get; set; } = "Spectrum.GuiRevisionTests";
        public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection properties)
        {
            var result = new SettingsPropertyValueCollection();
            foreach (SettingsProperty property in properties)
                result.Add(new SettingsPropertyValue(property)
                {
                    PropertyValue = _values.TryGetValue(property.Name, out var value) ? value
                        : Convert.ChangeType(property.DefaultValue, property.PropertyType, CultureInfo.InvariantCulture)
                });
            return result;
        }
        public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection values) => WasWritten = true;
    }
}
