// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class AuditCompletenessRegressionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static IEnumerable<object[]> Modes() => FormAudioSpectrum.VisualModeNames.Select(mode => new object[] { mode });

    public static IEnumerable<object[]> RadialReadoutCases()
    {
        foreach (var size in new[] { new Size(320, 180), new Size(900, 280), new Size(1200, 400) })
        foreach (var scale in new[] { 1f, 1.5f, 2f })
            yield return new object[] { size.Width, size.Height, scale };
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryModeConsumesMatchedSilenceQuietAndStereoFramesWithoutChangingTheirMeasurements(string mode)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm(mode: mode);
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var evidenceDirectory = Environment.GetEnvironmentVariable("SPECTRUM_RENDER_EVIDENCE_DIR");
            if (!string.IsNullOrWhiteSpace(evidenceDirectory))
            {
                Directory.CreateDirectory(evidenceDirectory);
                var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
                form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
                form.ShowInTaskbar = false;
                form.Show();
                form.ClientSize = new Size(1200, 480);
                Application.DoEvents();
            }
            var control = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            var bars = Field<VerticalProgressBar[]>(form, "_progressBars");
            var sourceEpoch = new object();
            long fixtureEndFrame = 0;
            foreach (var fixture in new[] { "silence", "quiet", "stereo" })
            {
                var level = fixture == "silence" ? (byte)0 : fixture == "quiet" ? (byte)12 : (byte)190;
                var frame = Enumerable.Repeat(level, 83).ToArray();
                // Each fixture is a new observed window, rather than three
                // conflicting payloads labelled with the same source position.
                fixtureEndFrame += 512;
                var audio = CreateAudio(fixture == "silence" ? 0f : fixture == "quiet" ? .0002f : .5f, 2, 48000, fixtureEndFrame, sourceEpoch);
                var inputLeft = (byte[])audio.LeftSpectrum.Clone();
                var inputRight = (byte[])audio.RightSpectrum.Clone();
                typeof(FormAudioSpectrum).GetMethod("Spectrum_Change", PrivateInstance).Invoke(form,
                    new object[] { null, new OnChangeEventArgs(frame, audio) });
                typeof(FormAudioSpectrum).GetMethod("ApplySpectrumToUI", PrivateInstance).Invoke(form, null);
                Assert.Equal(frame, Field<byte[]>(form, "_applyBuffer"));
                var copied = Field<AudioVisualizationData>(form, "_audioApplyBuffer");
                Assert.Equal(audio.LeftRms, copied.LeftRms);
                Assert.Equal(audio.RightPeak, copied.RightPeak);
                Assert.Equal(inputLeft, copied.LeftSpectrum);
                Assert.Equal(inputRight, copied.RightSpectrum);
                if (mode == "Spectrum" || mode == "Bricks" || mode == "LED")
                {
                    foreach (var bar in bars)
                    {
                        Assert.Equal((int)level, Field<int>(bar, "_targetValue"));
                        // Render the same settled frame in each meter presentation.
                        CombinedRandomizationTests.SetField(bar, "_displayValue", (float)level);
                        bar.DisplayFrozen = true;
                    }
                    using var image = VisualizationReadabilityTests.Render(bars[40]);
                    if (level > 0) Assert.False(bars[40].RenderedActiveBounds.IsEmpty);
                    foreach (var bar in bars) bar.DisplayFrozen = false;
                }
                else
                {
                    Assert.Equal(frame, Field<byte[]>(control, "_spectrum"));
                    using var image = VisualizationReadabilityTests.Render(control);
                    var displayed = Field<AudioVisualizationData>(control, "_audio");
                    Assert.Equal(inputLeft, displayed.LeftSpectrum);
                    Assert.Equal(inputRight, displayed.RightSpectrum);
                    Assert.Equal(audio.SevenBandPower, displayed.SevenBandPower);
                    Assert.Equal(audio.LeftRms, displayed.LeftRms);
                    Assert.Equal(audio.EndFrame, displayed.EndFrame);
                }
                Assert.Equal(inputLeft, audio.LeftSpectrum);
                Assert.Equal(inputRight, audio.RightSpectrum);
                Assert.All(frame, sample => Assert.Equal(level, sample));
                if (!string.IsNullOrWhiteSpace(evidenceDirectory))
                {
                    form.SetDisplayFrozen(true);
                    using var image = VisualizationReadabilityTests.Render(form);
                    image.Save(Path.Combine(evidenceDirectory, mode.Replace(' ', '-') + "-" + fixture + ".png"), ImageFormat.Png);
                    form.SetDisplayFrozen(false);
                }
            }
            if (!string.IsNullOrWhiteSpace(evidenceDirectory)) form.Close();
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void MonitoringChannelAndRelativeGroupIdentitiesRemainStableAcrossAllDecorativePalettes(string appearance)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            Color[] expected = null;
            var levels = Enumerable.Repeat((byte)180, 83).ToArray();
            var audio = CreateAudio(.5f, 2, 48000);
            using var control = new AdvancedVisualizationControl { Size = new Size(900, 280) };
            foreach (var palette in BarColorThemes.Names)
            {
                control.ApplyTheme(BarColorThemes.ApplyAppearance(BarColorThemes.Resolve(palette), appearance));
                control.SetSpectrum(levels);
                control.SetAudioData(audio);
                var colors = new[] { Field<Color>(control, "_thresholdBelowColor"), Field<Color>(control, "_thresholdAboveColor"),
                    Field<Color>(control, "_leftChannelColor"), Field<Color>(control, "_rightChannelColor") }
                    .Concat(Field<Color[]>(control, "_balanceColors")).ToArray();
                if (expected == null) expected = colors;
                else Assert.Equal(expected, colors);
                Assert.NotEqual(colors[0], colors[1]);
                Assert.NotEqual(colors[2], colors[3]);
                Assert.Equal(3, colors.Skip(4).Distinct().Count());
                foreach (var color in colors)
                    Assert.True(VisualizationReadabilityTests.Contrast(color, control.BackColor) >= 3,
                        $"{palette}/{appearance}: semantic colors must remain distinguishable from the canvas.");
                foreach (var mode in new[] { AdvancedVisualizationMode.ThresholdMonitor, AdvancedVisualizationMode.SpectralBalance,
                    AdvancedVisualizationMode.StereoLevels, AdvancedVisualizationMode.StereoCompare })
                {
                    control.Mode = mode;
                    using var bitmap = VisualizationReadabilityTests.Render(control);
                    Assert.Equal(levels, Field<byte[]>(control, "_spectrum"));
                    Assert.Equal(audio.LeftRms, Field<AudioVisualizationData>(control, "_audio").LeftRms);
                    if (mode == AdvancedVisualizationMode.SpectralBalance)
                    {
                        Assert.InRange(Field<double[]>(control, "_balance").Sum(), .99999999, 1.00000001);
                        var captionHeight = (int)typeof(AdvancedVisualizationControl).GetMethod("GetCaptionHeight", PrivateInstance)
                            .Invoke(control, new object[] { control.Width, control.Height });
                        var top = Math.Max(30f, captionHeight + 8f);
                        var rowHeight = (control.Height - top - 3) / 3;
                        for (var group = 0; group < 3; group++)
                            Assert.Equal(colors[4 + group].ToArgb(), bitmap.GetPixel(200, (int)(top + (group + .5f) * rowHeight)).ToArgb());
                    }
                    if (mode == AdvancedVisualizationMode.ThresholdMonitor)
                    {
                        var plot = control.SpectrumPlotBounds;
                        Assert.Equal(colors[1].ToArgb(), bitmap.GetPixel(403, (int)(plot.Bottom - plot.Height * .3f)).ToArgb());
                    }
                }
            }
        });
    }

    [Theory]
    [InlineData(-12, 212)]
    [InlineData(-18, 191)]
    [InlineData(-24, 170)]
    [InlineData(-36, 128)]
    [InlineData(-48, 85)]
    public void AdjustableMonitoringRuleUsesTheDocumentedQuantizedBoundaryWithoutChangingMeasurements(int threshold, int quantized)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl { Mode = AdvancedVisualizationMode.ThresholdMonitor, Size = new Size(900, 280) };
            Assert.Equal(-24, control.ThresholdDb);
            Assert.Equal((byte)170, Field<byte>(control, "_thresholdByte"));
            var levels = Enumerable.Range(0, 83).Select(index => (byte)(index * 3)).ToArray();
            control.SetSpectrum(levels);
            var prior = (byte[])Field<byte[]>(control, "_previousSpectrum").Clone();
            control.ThresholdDb = threshold;
            using var bitmap = VisualizationReadabilityTests.Render(control);
            Assert.Equal((byte)quantized, Field<byte>(control, "_thresholdByte"));
            Assert.Contains(Math.Abs(threshold) + " dBFS", Field<string>(control, "_thresholdBelowLabel"));
            Assert.Contains(Math.Abs(threshold) + " dBFS", Field<string>(control, "_thresholdAboveLabel"));
            Assert.Equal(levels, Field<byte[]>(control, "_spectrum"));
            Assert.Equal(prior, Field<byte[]>(control, "_previousSpectrum"));
            Assert.Throws<ArgumentOutOfRangeException>(() => control.ThresholdDb = -6);
            Assert.Throws<ArgumentOutOfRangeException>(() => control.ThresholdDb = 0);
        });
    }

    [Fact]
    public void BrickAndLedRhythmsDifferWhileTheirActiveLevelsAndSeparatedPeaksStayCalibrated()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var selector = Field<ComboBox>(form, "_modeSelector");
            var bar = Field<VerticalProgressBar[]>(form, "_progressBars")[40];
            bar.Size = new Size(24, 240);
            var rhythms = new List<Size>();
            foreach (var mode in new[] { "Bricks", "LED" })
            {
                selector.SelectedItem = mode;
                bar.Size = new Size(24, 240);
                bar.DisplayFrozen = true;
                CombinedRandomizationTests.SetField(bar, "_displayValue", 150f);
                CombinedRandomizationTests.SetField(bar, "_peakValue", 220f);
                using var bitmap = VisualizationReadabilityTests.Render(bar);
                rhythms.Add(new Size(bar.BrickHeight, bar.BrickGap));
                Assert.Equal(150f, Field<float>(bar, "_displayValue"));
                Assert.Equal(220f, Field<float>(bar, "_peakValue"));
                Assert.False(bar.RenderedActiveBounds.IsEmpty);
                Assert.False(bar.RenderedPeakBounds.IsEmpty);
                Assert.True(bar.RenderedPeakBounds.Bottom + bar.PeakGapPixels <= bar.RenderedActiveBounds.Top,
                    $"{mode} must retain its peak clearance after the cell rhythm change.");
            }
            Assert.True(rhythms[0].Width > rhythms[1].Width, "Bricks must use visibly larger cells than LED.");
            Assert.NotEqual(rhythms[0], rhythms[1]);
        });
    }

    [Theory]
    [InlineData(24000)]
    [InlineData(48000)]
    public void ScopeReferencesFollowActualSampleTimeAmplitudeAndMonoChannelChanges(int sampleRate)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.Oscilloscope, ThemeName = "Studio", Size = new Size(900, 280)
            };
            foreach (var channels in new[] { 1, 2, 1 })
            {
                var data = CreateAudio(.125f, channels, sampleRate);
                control.SetAudioData(data);
                using var image = VisualizationReadabilityTests.Render(control);
                var plot = control.WaveformPlotBounds;
                Assert.True(plot.Left >= 58 && plot.Bottom < control.Height - 10, "Amplitude and time labels must have dedicated space.");
                var duration = 1000d * (Field<int>(control, "_waveDisplayCount") - 1) / sampleRate;
                var timeLabels = Field<string[]>(control, "_waveTimeLabels");
                var amplitudeLabels = Field<string[]>(control, "_waveAmplitudeLabels");
                Assert.Equal("0", timeLabels[0]);
                Assert.InRange(Math.Abs(double.Parse(timeLabels[4].Replace(" ms", ""), CultureInfo.CurrentCulture) - duration), 0, .0051);
                Assert.Equal("0 FS", amplitudeLabels[2]);
                var range = Field<double>(control, "_waveRange");
                Assert.Equal(range, double.Parse(amplitudeLabels[0].Replace(" FS", ""), CultureInfo.CurrentCulture), 6);
                Assert.Equal(-range, double.Parse(amplitudeLabels[4].Replace(" FS", ""), CultureInfo.CurrentCulture), 6);
                Assert.StartsWith(channels == 1 ? "Mono waveform" : "Front-left waveform", Field<string>(control, "_waveCaption"));
                var font = Field<Font>(control, "_analysisBodyFont");
                using var metrics = Graphics.FromImage(image);
                var lineHeight = font.GetHeight(metrics);
                GuiRevisionRegressionTests.AssertRenderedText(image, amplitudeLabels[2], font,
                    BarColorThemes.Studio.Ui.SecondaryText, control.BackColor,
                    new RectangleF(0, (plot.Top + plot.Bottom) / 2 - lineHeight / 2, plot.Left - 5, lineHeight),
                    Field<StringFormat>(control, "_rightText"));
                var textWidth = metrics.MeasureString(timeLabels[4], font).Width + 2;
                GuiRevisionRegressionTests.AssertRenderedText(image, timeLabels[4], font,
                    BarColorThemes.Studio.Ui.SecondaryText, control.BackColor,
                    new RectangleF(Math.Max(plot.Left, Math.Min(control.Width - textWidth, plot.Right - textWidth / 2)),
                        plot.Bottom + 3, textWidth, lineHeight), Field<StringFormat>(control, "_centeredText"));
                Assert.Equal(data.LeftRms, Field<AudioVisualizationData>(control, "_audio").LeftRms);
            }
        });
    }

    [Fact]
    public void OptionalOctaveNumbersHaveTheirOwnRowAndDoNotChangeEstimatedPower()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.OctaveSpectrum, ThemeName = "Studio", Size = new Size(900, 280)
            };
            Assert.False(control.ShowOctaveValues);
            control.SetSpectrum(Enumerable.Repeat((byte)130, 83).ToArray());
            var powers = (byte[])Field<byte[]>(control, "_octaveLevels").Clone();
            using var withoutValues = VisualizationReadabilityTests.Render(control);
            var previousBounds = control.OctaveSpectrumPlotBounds;
            Assert.True(previousBounds.Left >= 42, "The level scale must have a gutter outside the first octave bar.");
            control.ShowOctaveValues = true;
            using var withValues = VisualizationReadabilityTests.Render(control);
            var plot = control.OctaveSpectrumPlotBounds;
            Assert.Equal(previousBounds.Left, plot.Left);
            Assert.Equal(previousBounds.Bottom, plot.Bottom);
            Assert.True(plot.Top > previousBounds.Top, "Value labels must reserve a row above the bars.");
            Assert.Equal(powers, Field<byte[]>(control, "_octaveLevels"));
            var font = Field<Font>(control, "_analysisBodyFont");
            using var metrics = Graphics.FromImage(withValues);
            var height = font.GetHeight(metrics);
            var labels = (string[])typeof(AdvancedVisualizationControl).GetField("s_matrixValueLabels", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            GuiRevisionRegressionTests.AssertRenderedText(withValues, labels[powers[0]], font,
                BarColorThemes.Studio.Ui.PrimaryText, control.BackColor,
                new RectangleF(plot.Left, plot.Top - height - 3, plot.Width / 10, height), Field<StringFormat>(control, "_centeredText"));
        });
    }

    [Fact]
    public void EffectHelpAndFallbackRemainVisibleUntilAnExplicitCompatibleSelection()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            WithPreferenceSavesCanceled(() =>
            {
                using var form = CombinedRandomizationTests.CreateForm();
                var mode = Field<ComboBox>(form, "_modeSelector");
                var effect = Field<ComboBox>(form, "_styleSelector");
                foreach (var pair in new[] { ("Pulse", "not beat detection"), ("Trail", "400 ms"), ("Glow", "measured edge") })
                {
                    effect.SelectedItem = pair.Item1;
                    Assert.Contains(pair.Item2, effect.AccessibleDescription);
                }
                CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", false);
                mode.SelectedItem = "Stereo Levels";
                Assert.Equal("None", effect.SelectedItem);
                Assert.Contains("unavailable", effect.AccessibleDescription);
                Assert.Contains("None", effect.AccessibleDescription);
                var status = Field<Label>(form, "_captureStatusLabel");
                Assert.Contains("unavailable", status.Text);
                typeof(FormAudioSpectrum).GetMethod("UpdateStyleSelector", PrivateInstance).Invoke(form, null);
                typeof(FormAudioSpectrum).GetMethod("UpdateCaptureStatus", PrivateInstance).Invoke(form, null);
                Assert.Contains("unavailable", effect.AccessibleDescription);
                Assert.Contains("unavailable", status.Text);
                mode.SelectedItem = "Spectrum";
                effect.SelectedItem = "Outline";
                Assert.Null(Field<string>(form, "_effectFallbackMessage"));
                Assert.Contains("active signal", effect.AccessibleDescription);
            });
        });
    }

    [Fact]
    public void PresetBrowserExplainsAndRendersEachChoiceWithoutTouchingCaptureOrActiveSelection()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var mode = Field<ComboBox>(form, "_modeSelector").SelectedItem;
            var effect = Field<ComboBox>(form, "_styleSelector").SelectedItem;
            var palette = Field<ComboBox>(form, "_themeSelector").SelectedItem;
            var guides = Field<bool>(form, "_showGuides");
            var analyzer = Field<object>(form, "_analyzer");
            var frame = (byte[])Field<byte[]>(form, "_applyBuffer").Clone();
            using var browser = form.CreatePresetBrowser();
            var list = (ListBox)browser.Controls.Find("presetList", true).Single();
            var details = (Label)browser.Controls.Find("presetDetails", true).Single();
            var notice = (Label)browser.Controls.Find("previewNotice", true).Single();
            var preview = (PictureBox)browser.Controls.Find("presetPreview", true).Single();
            Assert.Equal(FormAudioSpectrum.PresetNames, list.Items.Cast<string>().ToArray());
            Assert.Contains("not live audio", notice.Text);
            Assert.NotNull(browser.AcceptButton);
            Assert.NotNull(browser.CancelButton);
            var fingerprints = new HashSet<string>();
            var evidenceDirectory = Environment.GetEnvironmentVariable("SPECTRUM_RENDER_EVIDENCE_DIR");
            foreach (var preset in FormAudioSpectrum.PresetNames)
            {
                list.SelectedItem = preset;
                Assert.Contains("Guides:", details.Text);
                Assert.Contains(" | ", details.Text);
                Assert.True(details.Text.IndexOf('\n') > 20, "Every preset needs a purpose before its combination metadata.");
                var image = Assert.IsType<Bitmap>(preview.Image);
                Assert.True(image.Width >= 320 && image.Height >= 160);
                var pixels = new List<int>();
                for (var y = 0; y < image.Height; y += 8)
                for (var x = 0; x < image.Width; x += 8) pixels.Add(image.GetPixel(x, y).ToArgb());
                Assert.True(pixels.Distinct().Count() > 5, $"{preset} needs a readable captured-snapshot or waiting-state preview.");
                Assert.True(fingerprints.Add(string.Join(",", pixels)), $"{preset} must show its own combination.");
                if (!string.IsNullOrWhiteSpace(evidenceDirectory))
                {
                    Directory.CreateDirectory(evidenceDirectory);
                    image.Save(Path.Combine(evidenceDirectory, "preset-" + preset.Replace(' ', '-') + ".png"), ImageFormat.Png);
                }
                Assert.Equal(mode, Field<ComboBox>(form, "_modeSelector").SelectedItem);
                Assert.Equal(effect, Field<ComboBox>(form, "_styleSelector").SelectedItem);
                Assert.Equal(palette, Field<ComboBox>(form, "_themeSelector").SelectedItem);
                Assert.Equal(guides, Field<bool>(form, "_showGuides"));
                Assert.Same(analyzer, Field<object>(form, "_analyzer"));
                Assert.Equal(frame, Field<byte[]>(form, "_applyBuffer"));
            }
        });
    }

    [Fact]
    public void ViewControlsExposeMonitoringValuesAndResolutionWithoutChangingTheCapturedFrame()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var load = typeof(FormAudioSpectrum).GetMethod("FormAudioSpectrum_Load", PrivateInstance);
            form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
            form.ShowInTaskbar = false;
            form.Show();
            var frame = Enumerable.Repeat((byte)140, 83).ToArray();
            typeof(FormAudioSpectrum).GetMethod("Spectrum_Change", PrivateInstance).Invoke(form,
                new object[] { null, new OnChangeEventArgs(frame, CreateAudio(.25f, 2, 48000)) });
            typeof(FormAudioSpectrum).GetMethod("ApplySpectrumToUI", PrivateInstance).Invoke(form, null);
            var analyzer = Field<object>(form, "_analyzer");
            typeof(FormAudioSpectrum).GetMethod("ShowViewMenu", PrivateInstance).Invoke(form, null);
            Application.DoEvents();
            var menu = Field<ContextMenuStrip>(form, "_viewMenu");
            var threshold = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Monitoring threshold");
            Assert.Equal(new[] { "-12 dBFS", "-18 dBFS", "-24 dBFS", "-36 dBFS", "-48 dBFS" },
                threshold.DropDownItems.Cast<ToolStripMenuItem>().Select(item => item.Text).ToArray());
            threshold.DropDownItems.Cast<ToolStripMenuItem>().Single(item => item.Text == "-18 dBFS").PerformClick();
            var control = Field<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            Assert.Equal(-18, control.ThresholdDb);
            var octave = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Octave spectrum values");
            octave.PerformClick();
            Assert.True(control.ShowOctaveValues);
            var resolution = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Waterfall resolution");
            foreach (var coarse in new[] { true, false })
            {
                resolution.DropDownItems.Cast<ToolStripMenuItem>().Single(item => item.Text.StartsWith(coarse ? "Coarse" : "Fine", StringComparison.Ordinal)).PerformClick();
                Assert.Equal(coarse ? "Octave Waterfall" : "Waterfall", Field<ComboBox>(form, "_modeSelector").SelectedItem);
                Assert.Equal(frame, Field<byte[]>(form, "_applyBuffer"));
                Assert.Equal(frame, Field<byte[]>(control, "_spectrum"));
                Assert.Same(analyzer, Field<object>(form, "_analyzer"));
            }
            menu.Close();
            form.Close();
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("High Contrast")]
    public void PalettePickerSwatchesShowTheActualAppearanceAdaptedSignalColors(string appearance)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            CombinedRandomizationTests.SetField(form, "_appearance", appearance);
            var selector = Field<ComboBox>(form, "_themeSelector");
            var draw = typeof(FormAudioSpectrum).GetMethod("DrawSelectorItem", PrivateInstance);
            foreach (var name in BarColorThemes.Names)
            {
                using var image = new Bitmap(240, 36);
                using var graphics = Graphics.FromImage(image);
                var args = new DrawItemEventArgs(graphics, selector.Font, new Rectangle(0, 0, 240, 36),
                    selector.Items.IndexOf(name), DrawItemState.None);
                draw.Invoke(form, new object[] { args, true, false });
                var theme = BarColorThemes.ApplyAppearance(BarColorThemes.Resolve(name), appearance);
                Assert.Equal(theme.Low.ToArgb(), image.GetPixel(10, 18).ToArgb());
                Assert.Equal(theme.Mid.ToArgb(), image.GetPixel(20, 18).ToArgb());
                Assert.Equal(theme.High.ToArgb(), image.GetPixel(30, 18).ToArgb());
            }
        });
    }

    [Fact]
    public void HighContrastMagnitudeStopsHaveOrderedLuminanceWhilePreservingTheBlackWorkspace()
    {
        var theme = BarColorThemes.HighContrast;
        Assert.Equal(Color.Black, theme.Ui.VisualizationSurface);
        var colors = new[] { theme.Low, theme.Mid, theme.High, theme.Peak };
        for (var index = 0; index < colors.Length; index++)
        {
            Assert.True(VisualizationReadabilityTests.Contrast(colors[index], Color.Black) >= 3);
            if (index > 0)
                Assert.True(VisualizationReadabilityTests.Contrast(colors[index], Color.Black) >
                    VisualizationReadabilityTests.Contrast(colors[index - 1], Color.Black),
                    "Magnitude must rise in luminance instead of changing between unrelated saturated categories.");
        }
    }

    [Fact]
    public void ImageExportWritesAnActualPngAtTheCurrentWindowSizeWithoutChangingCaptureOrSelection()
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var mode = Field<string>(form, "_visualMode");
            var analyzer = Field<object>(form, "_analyzer");
            var path = Path.Combine(Path.GetTempPath(), "Spectrum-export-regression-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                form.SaveVisualizationImageTo(path);
                Assert.True(new FileInfo(path).Length > 100);
                using var saved = Image.FromFile(path);
                Assert.Equal(ImageFormat.Png.Guid, saved.RawFormat.Guid);
                Assert.Equal(form.ClientSize, saved.Size);
                Assert.Equal(mode, Field<string>(form, "_visualMode"));
                Assert.Same(analyzer, Field<object>(form, "_analyzer"));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
    }

    [Theory]
    [MemberData(nameof(RadialReadoutCases))]
    public void RadialSideReadoutsRespectActualAngularLabelBoundsAndClearWhenTheViewportBecomesCompact(int width, int height, float scale)
    {
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var control = new AdvancedVisualizationControl
            {
                Mode = AdvancedVisualizationMode.RadialSpectrum, ThemeName = "Studio", Size = new Size(width, height)
            };
            using var font = new Font("Segoe UI", 9 * scale);
            control.Font = font;
            var frame = Enumerable.Repeat((byte)140, 83).ToArray();
            frame[60] = 210;
            control.SetSpectrum(frame);
            using var image = VisualizationReadabilityTests.Render(control);
            var left = control.RadialLeftKeyBounds;
            var right = control.RadialRightReadoutBounds;
            if (width == 320)
            {
                Assert.True(left.IsEmpty && right.IsEmpty, "Compact radial layouts must not paint side blocks over their frequency labels.");
            }
            else
            {
                Assert.False(left.IsEmpty, $"The {width}x{height} radial view at {scale:P0} must use its available side space.");
                Assert.False(right.IsEmpty);
                var viewport = new RectangleF(PointF.Empty, control.ClientSize);
                Assert.True(viewport.Contains(left));
                Assert.True(viewport.Contains(right));
                var protectedText = Field<RectangleF[]>(control, "_radialLabelBounds")
                    .Concat(new[] { Field<RectangleF>(control, "_radialCaptionBounds"), Field<RectangleF>(control, "_radialLegendBounds") })
                    .Where(bounds => !bounds.IsEmpty).ToArray();
                foreach (var bounds in protectedText)
                {
                    Assert.False(left.IntersectsWith(bounds), $"Left key {left} overlaps radial text {bounds} at {scale:P0}.");
                    Assert.False(right.IntersectsWith(bounds), $"Right readout {right} overlaps radial text {bounds} at {scale:P0}.");
                }
                var angular = Field<RectangleF[]>(control, "_radialLabelBounds").Where(bounds => !bounds.IsEmpty).ToArray();
                if (angular.Length > 0)
                {
                    Assert.True(angular.Min(bounds => bounds.Left) - left.Right >= 12, "The left key needs a readable gap from the angular labels.");
                    Assert.True(right.Left - angular.Max(bounds => bounds.Right) >= 12, "The strongest-band readout needs a readable gap from the angular labels.");
                }
                Assert.Equal(frame, Field<byte[]>(control, "_spectrum"));
            }
            control.Size = new Size(70, 42);
            using var compact = VisualizationReadabilityTests.Render(control);
            Assert.True(control.RadialLeftKeyBounds.IsEmpty);
            Assert.True(control.RadialRightReadoutBounds.IsEmpty);
        });
    }

    private static void WithPreferenceSavesCanceled(Action action)
    {
        var settings = VisualizerPreferences.Default;
        var original = settings.Properties.Cast<SettingsProperty>().ToDictionary(property => property.Name, property => settings[property.Name]);
        SettingsSavingEventHandler cancelSave = (sender, args) => args.Cancel = true;
        settings.SettingsSaving += cancelSave;
        try { action(); }
        finally
        {
            foreach (var pair in original) settings[pair.Key] = pair.Value;
            settings.SettingsSaving -= cancelSave;
        }
    }

    private static AudioVisualizationData CreateAudio(float amplitude, int channels, int sampleRate, long endFrame = 512, object sourceEpoch = null)
    {
        const int frames = 512;
        var samples = new float[frames * channels];
        for (var frame = 0; frame < frames; frame++)
        {
            samples[frame * channels] = (float)Math.Sin(2 * Math.PI * frame / 32) * amplitude;
            if (channels > 1) samples[frame * channels + 1] = samples[frame * channels] * .25f;
        }
        var data = new AudioVisualizationData();
        data.UpdateObservedWindow(samples, frames, channels, sampleRate, endFrame, Math.Max(0, endFrame - frames), 0, sourceEpoch);
        var power = amplitude * amplitude / 2d;
        data.SetSevenBandPower(Enumerable.Repeat(power, 7).ToArray());
        data.SetChannelSpectrum(Enumerable.Repeat(power, 83).ToArray(),
            channels > 1 ? Enumerable.Repeat(power / 16, 83).ToArray() : null);
        return data;
    }

    private static T Field<T>(object target, string name) => CombinedRandomizationTests.GetField<T>(target, name);
}
