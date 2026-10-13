using System;
using System.IO;
using System.Reflection;
using Spectrum;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

/// <summary>Explicit local verification helper; requires an authorized MP3 path and the existing native BASS library.</summary>
public static class RecordedMusicVerification
{
    public static string Verify(string path)
    {
        var data = Analyze(path, out var plan, out var frames, out var observed, out var combinedDisplay);
        Assert.True(data.HasSamples);
        Assert.True(data.HasChannelSpectrum);
        Assert.True(observed > 100);
        Assert.True(data.ObservationCount > 100);
        var renderCount = 0;
        CombinedRandomizationTests.RunOnSta(() =>
        {
            using var form = CombinedRandomizationTests.CreateForm();
            var change = typeof(FormAudioSpectrum).GetMethod("Spectrum_Change", BindingFlags.Instance | BindingFlags.NonPublic);
            change.Invoke(form, new object[] { null, new OnChangeEventArgs(combinedDisplay, data) { Plan = plan } });
            CombinedRandomizationTests.SetField(form, "_applyingRandomSelection", true);
            var selector = CombinedRandomizationTests.GetField<System.Windows.Forms.ComboBox>(form, "_modeSelector");
            var control = CombinedRandomizationTests.GetField<AdvancedVisualizationControl>(form, "_advancedVisualizer");
            foreach (var mode in new[] { "Stereo Spectrum", "Dynamics History", "Oscilloscope", "Stereo Levels", "Band Matrix" })
            {
                selector.SelectedItem = mode;
                using var bitmap = VisualizationReadabilityTests.Render(control);
                renderCount++;
                var actual = CombinedRandomizationTests.GetField<AudioVisualizationData>(control, "_audio");
                Assert.Equal(data.ObservationCount, actual.ObservationCount);
                Assert.Equal(data.LeftPeak, actual.LeftPeak);
                Assert.Equal(data.EndFrame, actual.EndFrame);
            }
        });
        return $"Offline recorded MP3 PCM: {plan.SampleRate} Hz, {data.Channels} channels; {frames} decoded frames; {observed} analysis windows; {data.ObservationCount} retained observations; {renderCount} snapshot mode switches rendered. Nonnegative channel power and mean consistency validated for every analyzed band. Live WASAPI endpoint not exercised.";
    }

    public static string Render(string path, int width = 900, int height = 280, int dpi = 96, string theme = "Studio")
    {
        var data = Analyze(path, out var plan, out _, out _, out _);
        return NewAnalyticalModeTests.RenderContactSheet(data, plan, width, height, dpi, theme);
    }

    private static AudioVisualizationData Analyze(string path, out BandPlan plan, out long decodedFrames, out int observed, out byte[] combinedDisplay)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Authorized music file not found.", path);
        var assemblyPath = Path.Combine(Path.GetDirectoryName(typeof(Analyzer).Assembly.Location), "Bass.Net.dll");
        var assembly = Assembly.LoadFrom(assemblyPath);
        var bass = assembly.GetType("Un4seen.Bass.Bass", true);
        var initType = assembly.GetType("Un4seen.Bass.BASSInit", true);
        var flagType = assembly.GetType("Un4seen.Bass.BASSFlag", true);
        var init = bass.GetMethod("BASS_Init", new[] { typeof(int), typeof(int), initType, typeof(IntPtr) });
        if (!(bool)init.Invoke(null, new[] { (object)0, 48000, Enum.ToObject(initType, 0), IntPtr.Zero }))
            throw new InvalidOperationException("BASS decode initialization failed.");
        var stream = 0;
        try
        {
            var create = bass.GetMethod("BASS_StreamCreateFile", new[] { typeof(string), typeof(long), typeof(long), flagType });
            stream = (int)create.Invoke(null, new[] { (object)path, 0L, 0L, Enum.Parse(flagType, "BASS_STREAM_DECODE, BASS_SAMPLE_FLOAT, BASS_UNICODE") });
            if (stream == 0) throw new InvalidOperationException("BASS MP3 decoder could not open the file.");
            var info = bass.GetMethod("BASS_ChannelGetInfo", new[] { typeof(int) }).Invoke(null, new object[] { stream });
            var rate = (int)info.GetType().GetField("freq").GetValue(info);
            var channels = (int)info.GetType().GetField("chans").GetValue(info);
            plan = BandPlan.CreateLogarithmic(83, 20, 20000, rate);
            var engine = new BandSpectrumAnalyzer(plan, channels);
            var history = new SampleHistory(channels, engine.RequiredFrames * 4);
            var chunk = new float[Math.Max(1, rate / 40) * channels];
            var snapshot = new float[engine.RequiredFrames * channels];
            var combined = new double[83]; var left = new double[83]; var right = new double[83];
            var data = new AudioVisualizationData();
            combinedDisplay = new byte[83];
            var read = bass.GetMethod("BASS_ChannelGetData", new[] { typeof(int), typeof(float[]), typeof(int) });
            var epoch = new object();
            long prior = 0;
            decodedFrames = 0; observed = 0;
            while (decodedFrames < rate * 30L)
            {
                var bytes = (int)read.Invoke(null, new object[] { stream, chunk, chunk.Length * sizeof(float) });
                if (bytes < 0) break;
                if (bytes == 0) throw new InvalidOperationException("MP3 decoder made no progress.");
                var count = bytes / (sizeof(float) * channels);
                history.Write(chunk, count);
                decodedFrames += count;
                if (!history.TryCopyLatest(snapshot, engine.RequiredFrames, out var end)) continue;
                engine.Analyze(snapshot, engine.RequiredFrames, combined, left, right);
                for (var band = 0; band < 83; band++)
                {
                    Assert.True(left[band] >= 0 && right[band] >= 0);
                    var mean = channels >= 2 ? (left[band] + right[band]) / 2 : left[band];
                    Assert.InRange(Math.Abs(mean - combined[band]), 0, Math.Max(1e-12, Math.Abs(mean) * 1e-10));
                    combinedDisplay[band] = LevelScale.ToDisplayByte(LevelScale.PowerToDb(combined[band]));
                }
                data.UpdateObservedWindow(snapshot, engine.RequiredFrames, channels, rate, end, prior, 0, epoch);
                data.SetChannelSpectrum(left, right);
                prior = end; observed++;
            }
            return data;
        }
        finally
        {
            if (stream != 0) bass.GetMethod("BASS_StreamFree", new[] { typeof(int) }).Invoke(null, new object[] { stream });
            bass.GetMethod("BASS_Free", Type.EmptyTypes).Invoke(null, null);
        }
    }
}
