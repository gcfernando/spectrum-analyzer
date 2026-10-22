using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public sealed class RolesPreviewTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void BrowserWithoutCapturedWindowNeverTurnsDisplayBytesIntoInventedReadings()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            using var main = CombinedRandomizationTests.CreateForm();
            using var empty = main.RenderModePreview("Bricks", new Size(560, 280));
            var bytes = Field<byte[]>(main, "_applyBuffer");
            for (var i = 0; i < bytes.Length; i++) bytes[i] = 255;
            using var unpaired = main.RenderModePreview("Bricks", empty.Size);
            Assert.Equal(Pixels(empty), Pixels(unpaired));
            using var browser = main.CreateVisualizationBrowser();
            Assert.Contains("not live audio", browser.Controls.OfType<Control>().SelectMany(Descendants).OfType<Label>()
                .Single(label => label.Text.StartsWith("PREVIEW")).Text);
            Assert.Contains("waiting for audio", browser.Controls.Find("visualizationPreview", true)[0].AccessibleName.ToLowerInvariant());
            Capture(browser, "preview-waiting.png");
        });

    [Fact]
    public void PreviewRequiresTheSameSourceEpochSampleEndAndFormatAndDoesNotChangeTheMeasurement()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            using var main = CombinedRandomizationTests.CreateForm();
            var epoch = new object();
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
            // Explicit isolated test input, never a production fallback signal.
            var pcm = Enumerable.Range(0, 512).SelectMany(i => new[] { (float)(.25 * Math.Sin(2 * Math.PI * i / 48)), 0f }).ToArray();
            var power = Enumerable.Repeat(.01, 83).ToArray();
            var frame = new AnalysisFrame(plan, 2, 512, 512, pcm, power, power, new double[83], epoch,
                sourceId: "qa-fixture", sourceName: "Isolated QA fixture");
            Set(main, "_displayedInspection", new MainSpectrumSnapshot(frame, sourceDeviceId: "Isolated QA fixture"));
            var audio = Field<AudioVisualizationData>(main, "_audioApplyBuffer");
            var bytes = Field<byte[]>(main, "_applyBuffer");
            for (var i = 0; i < bytes.Length; i++) bytes[i] = LevelScale.ToDisplayByte(-20);
            using var empty = main.RenderModePreview("Bricks", new Size(560, 280));
            audio.UpdateObservedWindow(pcm, 512, 2, 48000, 512, 0, 0, new object());
            using var wrongEpoch = main.RenderModePreview("Bricks", empty.Size);
            Assert.Equal(Pixels(empty), Pixels(wrongEpoch));
            audio.UpdateObservedWindow(pcm, 512, 2, 48000, 513, 1, 0, epoch);
            using var wrongEnd = main.RenderModePreview("Bricks", empty.Size);
            Assert.Equal(Pixels(empty), Pixels(wrongEnd));
            audio.UpdateObservedWindow(pcm, 512, 2, 44100, 512, 0, 0, epoch);
            using var wrongRate = main.RenderModePreview("Bricks", empty.Size);
            Assert.Equal(Pixels(empty), Pixels(wrongRate));
            audio.UpdateObservedWindow(pcm, 512, 2, 48000, 512, 0, 0, epoch);
            audio.SetChannelSpectrum(power, new double[83]);
            var unchanged = (byte[])bytes.Clone();
            using var measured = main.RenderModePreview("Bricks", empty.Size);
            Assert.NotEqual(Pixels(empty), Pixels(measured));
            using var repeat = main.RenderModePreview("Bricks", empty.Size);
            Assert.Equal(Pixels(measured), Pixels(repeat));
            Assert.Equal(unchanged, bytes); Assert.Equal(512, audio.EndFrame); Assert.Same(epoch, audio.SourceEpoch);
            Assert.Same(frame, Field<MainSpectrumSnapshot>(main, "_displayedInspection").Frame);
            using var browser = main.CreateVisualizationBrowser();
            Capture(browser, "preview-captured-test-input.png");
        });

    private static System.Collections.Generic.IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    [Fact]
    public void ChangePreviewDoesNotInventAPreviousSilentFrame()
        => CombinedRandomizationTests.RunOnSta(() =>
        {
            using var main = CombinedRandomizationTests.CreateForm();
            var epoch = new object(); var pcm = Enumerable.Repeat(.25f, 128).ToArray();
            var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
            var power = Enumerable.Repeat(.0625, 83).ToArray();
            var frame = new AnalysisFrame(plan, 2, 64, 64, pcm, power, power, power, epoch);
            Set(main, "_displayedInspection", new MainSpectrumSnapshot(frame));
            Field<AudioVisualizationData>(main, "_audioApplyBuffer").UpdateObservedWindow(pcm, 64, 2, 48000, 64, 0, 0, epoch);
            var bytes = Field<byte[]>(main, "_applyBuffer");
            for (var i = 0; i < bytes.Length; i++) bytes[i] = 30;
            using var quiet = main.RenderModePreview("Level Change", new Size(560, 280));
            for (var i = 0; i < bytes.Length; i++) bytes[i] = 240;
            using var loud = main.RenderModePreview("Level Change", quiet.Size);
            Assert.Equal(Pixels(quiet), Pixels(loud));
        });
    private static int[] Pixels(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) pixels[y * bitmap.Width + x] = bitmap.GetPixel(x, y).ToArgb();
        return pixels;
    }
    private static T Field<T>(object target, string name) => (T)typeof(FormAudioSpectrum).GetField(name, Hidden).GetValue(target);
    private static void Set(object target, string name, object value) => typeof(FormAudioSpectrum).GetField(name, Hidden).SetValue(target, value);
    private static void Capture(Form form, string file)
    {
        var directory = Environment.GetEnvironmentVariable("SPECTRUM_ROLES_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory); form.Show(); ModernWorkbenchProbe.Pump(100);
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(Path.Combine(directory, file)); form.Close();
    }
}

public static class RolesTruthVerification
{
    public static string Run(string outputDirectory)
    {
        Environment.SetEnvironmentVariable("SPECTRUM_ROLES_EVIDENCE", outputDirectory);
        var tests = new RolesPreviewTests();
        tests.BrowserWithoutCapturedWindowNeverTurnsDisplayBytesIntoInventedReadings();
        tests.PreviewRequiresTheSameSourceEpochSampleEndAndFormatAndDoesNotChangeTheMeasurement();
        tests.ChangePreviewDoesNotInventAPreviousSilentFrame();
        RolesUiStateVerification.Run(Path.Combine(outputDirectory, "ui-states"));
        foreach (var appearance in new[] { "Dark", "Light", "High Contrast" })
            new RolesSpectrogramTests().MissingTimeAndSampleGapsAreDistinctFromActualDigitalSilence(appearance);
        var json = "{\"passed\":true,\"shownPreviewWorkflows\":2,\"testInputExplicitlyIdentified\":true,\"sourceEpochEndAndFormatChecked\":true}";
        File.WriteAllText(Path.Combine(outputDirectory, "truthful-preview.json"), json);
        return json;
    }
}
