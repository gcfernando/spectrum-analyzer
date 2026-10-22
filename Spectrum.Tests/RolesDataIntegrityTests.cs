using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

[Collection("Capture lifecycle")]
public sealed class RolesDataIntegrityTests
{
    private static AnalysisFrame Frame(long end = 8, string id = "endpoint-id", string name = "Actual endpoint", long firstValid = 0)
    {
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, 48000);
        return new AnalysisFrame(plan, 2, 8, end, Enumerable.Repeat(.125f, 16).ToArray(),
            Enumerable.Repeat(.02, 83).ToArray(), Enumerable.Repeat(.02, 83).ToArray(), Enumerable.Repeat(.02, 83).ToArray(),
            sourceEpoch: typeof(RolesDataIntegrityTests), firstValidFrame: firstValid, sourceId: id, sourceName: name);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void FrameRejectsNonFiniteCapturedPcmInsteadOfPublishingMeasuredSilence(float invalid)
    {
        var frame = Frame(); var samples = (float[])frame.Samples.Clone(); samples[15] = invalid;
        Assert.Throws<ArgumentException>(() => new AnalysisFrame(frame.Plan, 2, 8, 8, samples, frame.Power, frame.LeftPower, frame.RightPower));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)] [InlineData(-.1)]
    public void FrameRejectsInvalidBandPowerInsteadOfSubstitutingZero(double invalid)
    {
        var frame = Frame(); var power = (double[])frame.Power.Clone(); power[82] = invalid;
        Assert.Throws<ArgumentException>(() => new AnalysisFrame(frame.Plan, 2, 8, 8, frame.Samples, power, frame.LeftPower, frame.RightPower));
        Assert.Throws<ArgumentException>(() => new AnalysisFrame(frame.Plan, 2, 8, 8, frame.Samples, frame.Power, power, frame.RightPower));
        Assert.Throws<ArgumentException>(() => new AnalysisFrame(frame.Plan, 2, 8, 8, frame.Samples, frame.Power, frame.LeftPower, power));
    }

    [Fact]
    public void PcmValidationUsesOnlyTheDeclaredWindowAndRetainsFiniteHeadroom()
    {
        var samples = new[] { 1.25f, -2f, float.NaN };
        AnalysisFrame.ValidatePcm(samples, 1, 2);
        Assert.Throws<ArgumentException>(() => AnalysisFrame.ValidatePcm(samples, 3, 1));
        Assert.Equal(1.25f, samples[0]); Assert.Equal(-2f, samples[1]);
    }

    [Fact]
    public void RejectedMutatedFrameDoesNotReplaceTheLastValidAnalysis()
    {
        var workspace = new AnalysisWorkspace(); var valid = Frame(); workspace.Accept(valid);
        var invalid = Frame(16); invalid.Power[0] = double.NaN;
        Assert.Throws<ArgumentException>(() => workspace.Accept(invalid));
        Assert.Same(valid, workspace.Latest); Assert.Equal(8, workspace.Measurements.TotalFrames);
    }

    [Fact]
    public void WorkerFailureInvalidatesCurrentReadingsAndRecoversOnNextValidWindow()
    {
        var workspace = new AnalysisWorkspace(); var valid = Frame(); workspace.Accept(valid);
        var invalid = Frame(16); invalid.Samples[3] = float.NaN;
        using var failed = new ManualResetEventSlim(); using var recovered = new ManualResetEventSlim();
        workspace.Changed += () => { if (workspace.IsUnavailable) failed.Set(); else if (workspace.Latest.EndFrame == 24) recovered.Set(); };
        workspace.Post(invalid);
        Assert.True(failed.Wait(5000), "Invalid captured PCM must reach an explicit unavailable state.");
        Assert.True(workspace.IsUnavailable); Assert.Contains("Analysis failed", workspace.Status);
        Assert.Same(valid, workspace.Latest); Assert.Contains(workspace.History, row => row.Gap);
        Assert.Throws<InvalidOperationException>(() => workspace.CaptureReference());
        workspace.Post(Frame(24)); Assert.True(recovered.Wait(5000));
        Assert.False(workspace.IsUnavailable); Assert.Equal(8, workspace.Measurements.TotalFrames);
    }

    [Theory]
    [InlineData("AnalyzeAndFire")] [InlineData("PublishSilence")]
    public void NativeAnalysisRejectsInvalidPcmBeforePublishingAnyDisplayOrAnalysisFrame(string methodName)
    {
        using var analyzer = new Analyzer(false);
        var stateType = typeof(Analyzer).GetNestedType("AnalysisState", BindingFlags.NonPublic);
        var state = stateType.GetMethod("Create").Invoke(null, new object[] { 48000, 2 });
        var streamType = typeof(Analyzer).GetNestedType("CaptureStream", BindingFlags.NonPublic);
        var stream = Activator.CreateInstance(streamType, new[] { state });
        typeof(Analyzer).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(analyzer, stream);
        var engine = (BandSpectrumAnalyzer)stateType.GetProperty("Engine").GetValue(state);
        var history = (SampleHistory)stateType.GetProperty("History").GetValue(state);
        var samples = new float[engine.RequiredFrames * 2]; samples[samples.Length - 1] = float.NaN;
        history.Write(samples, engine.RequiredFrames);
        var published = 0; var displayPublished = 0;
        analyzer.AnalysisFrameAvailable += _ => published++;
        OnChangeHandler listener = (_, _) => displayPublished++;
        Analyzer.OnChange += listener;
        try
        {
            var args = methodName == "PublishSilence" ? new object[] { state, true, stream } : new[] { state, stream };
            var error = Assert.Throws<TargetInvocationException>(() => typeof(Analyzer).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(analyzer, args));
            Assert.IsType<ArgumentException>(error.InnerException);
            Assert.Equal(0, published); Assert.Equal(0, displayPublished);
        }
        finally { Analyzer.OnChange -= listener; }
    }

    [Fact]
    public void AnalyzerPublicationCarriesTheSelectedEndpointOnTheSameOwnedWindow()
    {
        using var analyzer = new Analyzer(false);
        var stateType = typeof(Analyzer).GetNestedType("AnalysisState", BindingFlags.NonPublic);
        var state = stateType.GetMethod("Create").Invoke(null, new object[] { 48000, 2 });
        var streamType = typeof(Analyzer).GetNestedType("CaptureStream", BindingFlags.NonPublic);
        var stream = Activator.CreateInstance(streamType, new[] { state });
        var engine = (BandSpectrumAnalyzer)stateType.GetProperty("Engine").GetValue(state);
        typeof(Analyzer).GetField("_selectedDevice", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(analyzer,
            new Device { DeviceId = "fixture-endpoint", DeviceName = "Explicitly identified test endpoint" });
        AnalysisFrame result = null; analyzer.AnalysisFrameAvailable += frame => result = frame;
        typeof(Analyzer).GetMethod("PublishAnalysisFrame", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(analyzer, new object[] { state, engine.RequiredFrames, (long)engine.RequiredFrames, stream });
        Assert.NotNull(result); Assert.Equal("fixture-endpoint", result.SourceId);
        Assert.Equal("Explicitly identified test endpoint", result.SourceName); Assert.Same(stream, result.SourceEpoch);
        Assert.Equal(engine.RequiredFrames, result.FrameCount); Assert.Equal(48000, result.SampleRate);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(float.NegativeInfinity)]
    public void RealFloatWaveDecoderRejectsEachInvalidSampleAndReleasesTheFile(float invalid)
    {
        var path = Path.Combine(Path.GetTempPath(), "Spectrum-invalid-float-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var writer = new BinaryWriter(File.Create(path), Encoding.ASCII))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(44);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)3); writer.Write((short)1); writer.Write(48000); writer.Write(192000);
                writer.Write((short)4); writer.Write((short)32);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(8); writer.Write(.125f); writer.Write(invalid);
            }
            var error = Assert.Throws<InvalidDataException>(() => AnalysisAudioFile.Read(path));
            Assert.Contains("non-finite", error.Message);
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.CanWrite);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LiveEndpointIdentityAndWindowBoundarySurviveSessionAndCsvRoundTrip()
    {
        var session = Path.GetTempFileName(); var csv = Path.GetTempFileName();
        try
        {
            var frame = Frame(48008, "{actual-endpoint-id}", "Actual, endpoint \\ name", 48000);
            var workspace = new AnalysisWorkspace(); workspace.Accept(frame);
            Assert.Equal(frame.SourceId, workspace.SourceId); Assert.Equal(frame.SourceName, workspace.SourceName);
            workspace.SaveSession(session); var loaded = new AnalysisWorkspace(); loaded.LoadSession(session);
            Assert.Equal(frame.SourceId, loaded.SourceId); Assert.Equal(frame.SourceName, loaded.SourceName);
            Assert.Equal(frame.SourceId, loaded.Latest.SourceId); Assert.Equal(48000, loaded.Latest.FirstValidFrame);
            Assert.Equal(frame.EndFrame, loaded.Latest.EndFrame); Assert.Empty(loaded.Latest.Samples);
            loaded.ExportCsv(csv); var savedCsv = File.ReadAllText(csv);
            Assert.Contains("source_id,first_valid_frame,measurement_state", savedCsv);
            Assert.Contains("\"{actual-endpoint-id}\",48000,\"Saved measurement\"", savedCsv);
            workspace.SetUnavailable("Disconnected"); workspace.ExportCsv(csv);
            Assert.Contains("Unavailable (last valid measurement)", File.ReadAllText(csv));
            Assert.Contains(frame.CapturedUtc.ToString("o"), File.ReadAllText(csv));
            var legacy = XDocument.Load(session); legacy.Root.Element("capture").Attribute("sourceId").Remove();
            legacy.Root.Element("capture").Attribute("firstValidFrame").Remove(); legacy.Save(session);
            loaded.LoadSession(session); Assert.Equal("", loaded.SourceId); Assert.Equal(0, loaded.Latest.FirstValidFrame);
        }
        finally { File.Delete(session); File.Delete(csv); }
    }
}
