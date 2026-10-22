// Developer :: > Gehan Fernando
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Spectrum.Dsp;
using Un4seen.BassWasapi;
using Xunit;

namespace Spectrum.Tests;

[Collection("Capture lifecycle")]
public sealed class AnalysisAudioSourceTests
{
    [Fact]
    public void AnalysisSourcesAcceptEnabledInputsWithoutChangingLegacyLoopbackEligibility()
    {
        var enabled = BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED;
        var input = BASSWASAPIDeviceInfo.BASS_DEVICE_INPUT;
        var loopback = BASSWASAPIDeviceInfo.BASS_DEVICE_LOOPBACK;
        Assert.True(Analyzer.IsEligibleAnalysisSource(enabled | input));
        Assert.True(Analyzer.IsEligibleAnalysisSource(enabled | input | loopback));
        Assert.False(Analyzer.IsEligibleAnalysisSource(input));
        Assert.False(Analyzer.IsEligibleAnalysisSource(enabled));
        Assert.False(Analyzer.IsEligibleLoopback(enabled | input));
        Assert.True(Analyzer.IsEligibleLoopback(enabled | input | loopback));
    }

    [Fact]
    public void ManagedCaptureKeepsDefaultBehaviorAndPinsExplicitSourcesByIdentity()
    {
        using var analyzer = new Analyzer(false);
        Assert.Same(AnalysisAudioSource.Default, analyzer.SelectedAnalysisSource);
        Assert.Single(analyzer.GetAnalysisSources());
        var microphone = new AnalysisAudioSource("microphone-id", "Microphone", false, false, 9);
        analyzer.SelectAnalysisSource(microphone);
        Assert.Same(microphone, analyzer.SelectedAnalysisSource);
        Assert.Equal(-1, analyzer.SelectIndex); // managed test must not touch native capture.
        Assert.Equal(0, analyzer.CapturedFrames);
        analyzer.SelectAnalysisSource(null);
        Assert.Same(AnalysisAudioSource.Default, analyzer.SelectedAnalysisSource);
        Assert.Equal(CaptureState.Starting, analyzer.CurrentStatus.State);
    }

    [Fact]
    public void SourceDescriptionsClearlyDistinguishInputAndSystemOutput()
    {
        Assert.Contains("default", AnalysisAudioSource.Default.ToString());
        Assert.Contains("system output", new AnalysisAudioSource("id-a", "Device", false, true, 1).ToString());
        Assert.Contains("input", new AnalysisAudioSource("id-b", "Device", false, false, 2).ToString());
        Assert.Throws<ArgumentException>(() => new AnalysisAudioSource(" ", "Device", false, true, 1));
    }

    [Fact]
    public void StoppedOrDisposedCaptureCannotBeReselected()
    {
        var analyzer = new Analyzer(false);
        analyzer.Free();
        Assert.Throws<InvalidOperationException>(() => analyzer.SelectAnalysisSource(AnalysisAudioSource.Default));
        analyzer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => analyzer.SelectAnalysisSource(AnalysisAudioSource.Default));
    }

    [Fact]
    public void FaultyAvailabilitySubscriberCannotBreakAnotherSubscriberOrLegacyStop()
    {
        using var analyzer = new Analyzer(false);
        var availableNotifications = 0;
        var legacyNotifications = 0;
        analyzer.AnalysisUnavailable += () => throw new InvalidOperationException("subscriber fault");
        analyzer.AnalysisUnavailable += () => availableNotifications++;
        OnChangeHandler legacy = (_, _) => legacyNotifications++;
        Analyzer.OnChange += legacy;
        try
        {
            analyzer.Free();
            Assert.Equal(1, availableNotifications);
            Assert.Equal(1, legacyNotifications);
            Assert.Equal(CaptureState.Stopped, analyzer.CurrentStatus.State);
        }
        finally { Analyzer.OnChange -= legacy; }
    }

    [Fact]
    public void RawAnalysisPreservesQuietPowerTimestampAndSampleOwnershipDespiteFaultySubscriber()
    {
        using var analyzer = new Analyzer(false);
        var stateType = typeof(Analyzer).GetNestedType("AnalysisState", BindingFlags.NonPublic);
        var state = stateType.GetMethod("Create").Invoke(null, new object[] { 48000, 2 });
        var streamType = typeof(Analyzer).GetNestedType("CaptureStream", BindingFlags.NonPublic);
        var stream = Activator.CreateInstance(streamType, new[] { state });
        typeof(Analyzer).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(analyzer, stream);
        var history = (SampleHistory)stateType.GetProperty("History").GetValue(state);
        var engine = (BandSpectrumAnalyzer)stateType.GetProperty("Engine").GetValue(state);
        var samples = Signals.StereoSine(engine.RequiredFrames, 48000, engine.Plan[80].CenterHz, 1e-5, 1e-5);
        history.Write(samples, engine.RequiredFrames);
        AnalysisFrame observed = null;
        byte[] display = null;
        analyzer.AnalysisFrameAvailable += _ => throw new InvalidOperationException("subscriber fault");
        analyzer.AnalysisFrameAvailable += frame => observed = frame;
        OnChangeHandler legacy = (_, frame) => display = frame.Spectrumdata.ToArray();
        Analyzer.OnChange += legacy;
        try
        {
            typeof(Analyzer).GetMethod("AnalyzeAndFire", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(analyzer, new[] { state, stream });
            Assert.NotNull(observed);
            Assert.NotNull(display);
            Assert.Equal(engine.RequiredFrames, observed.EndFrame);
            Assert.Same(stream, observed.SourceEpoch);
            Assert.InRange(LevelScale.PowerToDb(observed.Power[80]), -100.01, -99.99);
            Assert.Equal(0, display[80]); // The new feed must retain power already hidden by the old display.
            var retainedPower = observed.Power[80];
            var retainedSample = observed.Samples[2];
            ((double[])typeof(Analyzer).GetField("_bandPower", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(analyzer))[80] = 99;
            ((float[])stateType.GetProperty("Snapshot").GetValue(state))[2] = 99;
            Assert.Equal(retainedPower, observed.Power[80]);
            Assert.Equal(retainedSample, observed.Samples[2]);
        }
        finally { Analyzer.OnChange -= legacy; }
    }

    [Fact]
    public void ExplicitEndpointNotificationsDoNotFollowAnUnrelatedWindowsDefaultChange()
    {
        var sourceNotifications = 0;
        var defaultNotifications = 0;
        var status = new CaptureStatus(CaptureState.Capturing, "test", "selected", "selected", null, 48000, 2);
        var type = typeof(Analyzer).GetNestedType("DeviceNotificationClient", BindingFlags.NonPublic);
        var client = Activator.CreateInstance(type, new object[]
        {
            (Action)(() => sourceNotifications++), (Func<CaptureStatus>)(() => status), (Action)(() => defaultNotifications++)
        });
        var method = type.GetMethod("OnDefaultDeviceChanged");
        var parameters = method.GetParameters();
        method.Invoke(client, new[] { Enum.Parse(parameters[0].ParameterType, "Render"),
            Enum.Parse(parameters[1].ParameterType, "Multimedia"), "unrelated" });
        Assert.Equal(0, sourceNotifications);
        Assert.Equal(1, defaultNotifications);
        type.GetMethod("OnDeviceRemoved").Invoke(client, new object[] { "SELECTED" });
        Assert.Equal(1, sourceNotifications);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void OfflinePcmWavPreservesSampleRateChannelsAndInterleaving(int bits)
    {
        var path = Path.Combine(Path.GetTempPath(), "Spectrum-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        var expected = new[] { -.75f, .25f, -.5f, .5f, 0f, .75f };
        try
        {
            WriteWave(path, expected, 32000, 2, bits, false);
            var decoded = AnalysisAudioFile.Read(path);
            Assert.Equal(32000, decoded.SampleRate);
            Assert.Equal(2, decoded.Channels);
            Assert.Equal(3, decoded.FrameCount);
            Assert.Equal(expected.Length, decoded.Samples.Length);
            for (var index = 0; index < expected.Length; index++)
                Assert.InRange(Math.Abs(decoded.Samples[index] - expected[index]), 0, 1e-6);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfflineFloatWavRejectsNonFiniteSamplesWithoutClippingFiniteHeadroom()
    {
        var path = Path.Combine(Path.GetTempPath(), "Spectrum-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteWave(path, new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, .125f, 1.25f }, 48000, 1, 32, true);
            Assert.Throws<InvalidDataException>(() => AnalysisAudioFile.Read(path));
            WriteWave(path, new[] { .125f, 1.25f }, 48000, 1, 32, true);
            var decoded = AnalysisAudioFile.Read(path);
            Assert.Equal(new[] { .125f, 1.25f }, decoded.Samples);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfflineReaderRejectsMissingEmptyAndMalformedFiles()
    {
        Assert.Throws<ArgumentException>(() => AnalysisAudioFile.Read(" "));
        Assert.Throws<FileNotFoundException>(() => AnalysisAudioFile.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav")));
        var path = Path.Combine(Path.GetTempPath(), "Spectrum-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteWave(path, Array.Empty<float>(), 48000, 1, 16, false);
            Assert.Throws<InvalidDataException>(() => AnalysisAudioFile.Read(path));
            File.WriteAllText(path, "This is not a WAV header.");
            Assert.Throws<InvalidDataException>(() => AnalysisAudioFile.Read(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfflineReaderRejectsDurationBeyondItsDocumentedBoundBeforeAnalysis()
    {
        var path = Path.Combine(Path.GetTempPath(), "Spectrum-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteWave(path, new float[601], 1, 1, 16, false);
            var exception = Assert.Throws<InvalidDataException>(() => AnalysisAudioFile.Read(path));
            Assert.Contains("10 minutes", exception.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfflineDataRejectsPartialFramesAndInvalidFormatMetadata()
    {
        Assert.Throws<ArgumentException>(() => new AnalysisAudioFileData(new float[3], 48000, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalysisAudioFileData(new float[2], 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalysisAudioFileData(new float[2], 48000, 0));
    }

    private static void WriteWave(string path, float[] samples, int rate, int channels, int bits, bool ieeeFloat)
    {
        using var output = new BinaryWriter(File.Create(path), Encoding.ASCII);
        var bytesPerSample = bits / 8;
        var bytes = samples.Length * bytesPerSample;
        output.Write(Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + bytes);
        output.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16);
        output.Write((short)(ieeeFloat ? 3 : 1)); output.Write((short)channels);
        output.Write(rate); output.Write(rate * channels * bytesPerSample);
        output.Write((short)(channels * bytesPerSample)); output.Write((short)bits);
        output.Write(Encoding.ASCII.GetBytes("data")); output.Write(bytes);
        foreach (var sample in samples)
        {
            if (ieeeFloat) output.Write(sample);
            else if (bits == 8) output.Write((byte)Math.Round(128 + sample * 128));
            else if (bits == 16) output.Write((short)Math.Round(sample * 32768));
            else if (bits == 24)
            {
                var value = (int)Math.Round(sample * 8388608);
                output.Write((byte)value); output.Write((byte)(value >> 8)); output.Write((byte)(value >> 16));
            }
            else output.Write((int)Math.Round(sample * 2147483648.0));
        }
    }
}

// Opt-in native integration proof. Reports only counts/format; no PCM or endpoint identities are saved.
// Run in x86 with active system output. It switches this Analyzer's capture selection, never Windows defaults.
public static class AnalysisCaptureVerification
{
    public static string Run()
    {
        if (IntPtr.Size != 4) throw new InvalidOperationException("Run native capture verification in x86 Windows.");
        using var analyzer = new Analyzer();
        long windows = 0;
        object epoch = null;
        analyzer.AnalysisFrameAvailable += frame =>
        {
            Volatile.Write(ref epoch, frame.SourceEpoch);
            Interlocked.Increment(ref windows);
        };
        if (!WaitUntil(() => Interlocked.Read(ref windows) >= 3, 8))
            throw new InvalidOperationException("No native analysis frames: " + analyzer.CurrentStatus.Message);
        var sources = analyzer.GetAnalysisSources();
        var chosen = sources.FirstOrDefault(source => !source.IsDefault && source.IsLoopback &&
            string.Equals(source.Id, analyzer.CurrentStatus.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (chosen == null) throw new InvalidOperationException("The active default output was not available for explicit source selection.");
        var baselineWindows = Interlocked.Read(ref windows);
        var baselineEpoch = Volatile.Read(ref epoch);
        analyzer.SelectAnalysisSource(chosen);
        if (!WaitUntil(() => !ReferenceEquals(Volatile.Read(ref epoch), baselineEpoch) &&
            Interlocked.Read(ref windows) >= baselineWindows + 3 && analyzer.CurrentStatus.SampleRate > 0, 8))
            throw new InvalidOperationException("Explicit native source did not resume analysis: " + analyzer.CurrentStatus.Message);
        var explicitWindows = Interlocked.Read(ref windows) - baselineWindows;
        var explicitEpoch = Volatile.Read(ref epoch);
        var explicitTotal = Interlocked.Read(ref windows);
        analyzer.SelectAnalysisSource(AnalysisAudioSource.Default);
        if (!WaitUntil(() => !ReferenceEquals(Volatile.Read(ref epoch), explicitEpoch) &&
            Interlocked.Read(ref windows) >= explicitTotal + 3 && analyzer.CurrentStatus.SampleRate > 0, 8))
            throw new InvalidOperationException("Following the default output did not resume analysis: " + analyzer.CurrentStatus.Message);
        return "{\"nativeSourceProof\":true,\"sourceCount\":" + sources.Count +
            ",\"loopbackSourceCount\":" + sources.Count(source => !source.IsDefault && source.IsLoopback) +
            ",\"inputSourceCount\":" + sources.Count(source => !source.IsDefault && !source.IsLoopback) +
            ",\"defaultWindows\":" + baselineWindows + ",\"explicitWindows\":" + explicitWindows +
            ",\"restoredDefaultWindows\":" + (Interlocked.Read(ref windows) - explicitTotal) +
            ",\"sampleRate\":" + analyzer.CurrentStatus.SampleRate + ",\"channels\":" + analyzer.CurrentStatus.Channels + "}";
    }

    private static bool WaitUntil(Func<bool> condition, int seconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
