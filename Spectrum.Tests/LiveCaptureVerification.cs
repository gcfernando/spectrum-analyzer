// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Spectrum.Tests;

// Explicit Windows/x86 integration helper, deliberately outside automatic unit-test discovery.
// Stores aggregate numbers only: no captured PCM, waveform, endpoint IDs or registration material.
public static class LiveCaptureVerification
{
    public static string Run(int seconds = 120, string outputJsonPath = null, bool playQuietSignal = false)
    {
        if (seconds < 5 || seconds > 3600) throw new ArgumentOutOfRangeException(nameof(seconds), "Use 5–3600 seconds.");
        if (IntPtr.Size != 4) throw new InvalidOperationException("Run this helper in x86 Windows PowerShell; bundled BASS libraries are x86.");
        var sync = new object();
        var clock = Stopwatch.StartNew();
        var samples = new List<string>();
        var statuses = new List<string>();
        var formats = new HashSet<string>(StringComparer.Ordinal);
        long windows = 0, validWindows = 0, zeroWindows = 0, unavailableWindows = 0, spectralWindows = 0;
        long maximumEndFrame = 0;
        double maximumLeftPeak = 0, maximumRightPeak = 0;
        string numericFailure = null;
        var stopped = false;
        Analyzer analyzer = null;
        QuietPlayback playback = null;
        OnChangeHandler observe = (_, args) =>
        {
            // Analyzer invokes subscribers synchronously; copy scalar values before it reuses its payload.
            var audio = args.Audio;
            lock (sync)
            {
                windows++;
                if (audio == null || !audio.HasSamples) { unavailableWindows++; return; }
                validWindows++;
                if (audio.LeftPeak == 0 && audio.RightPeak == 0) zeroWindows++;
                if (audio.HasChannelSpectrum) spectralWindows++;
                maximumEndFrame = Math.Max(maximumEndFrame, audio.EndFrame);
                maximumLeftPeak = Math.Max(maximumLeftPeak, audio.LeftPeak);
                maximumRightPeak = Math.Max(maximumRightPeak, audio.RightPeak);
                if (!Finite(audio.LeftPeak) || !Finite(audio.RightPeak) || !Finite(audio.LeftRms) || !Finite(audio.RightRms))
                    numericFailure = "Captured scalar measurements contain NaN or infinity.";
                formats.Add(audio.SampleRate + " Hz / " + audio.Channels + " ch");
            }
        };
        Action<CaptureStatus> statusChanged = status =>
        {
            lock (sync)
            {
                stopped |= status.State == CaptureState.Stopped;
                if (statuses.Count < 512)
                    statuses.Add("{\"seconds\":" + Number(clock.Elapsed.TotalSeconds) + ",\"state\":" + Quote(status.State.ToString()) +
                        ",\"message\":" + Quote(status.Message) + ",\"sampleRate\":" + status.SampleRate + ",\"channels\":" + status.Channels +
                        ",\"errorCode\":" + (status.ErrorCode.HasValue ? status.ErrorCode.Value.ToString(CultureInfo.InvariantCulture) : "null") + "}");
            }
        };
        var disposalMilliseconds = 0.0;
        string failure = null;
        using (var process = Process.GetCurrentProcess())
        {
            var priorCpu = process.TotalProcessorTime;
            var priorSeconds = clock.Elapsed.TotalSeconds;
            try
            {
                Analyzer.OnChange += observe;
                analyzer = new Analyzer();
                analyzer.StatusChanged += statusChanged;
                statusChanged(analyzer.CurrentStatus);
                if (playQuietSignal) playback = new QuietPlayback();
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    Thread.Sleep(Math.Min(1000, Math.Max(1, (int)((seconds - clock.Elapsed.TotalSeconds) * 1000))));
                    process.Refresh();
                    var now = clock.Elapsed.TotalSeconds;
                    var cpu = process.TotalProcessorTime;
                    var corePercent = (cpu - priorCpu).TotalSeconds / Math.Max(0.001, now - priorSeconds) * 100;
                    lock (sync)
                        samples.Add("{\"seconds\":" + Number(now) + ",\"cpuPercentOfOneCore\":" + Number(corePercent) +
                            ",\"cpuPercentOfMachine\":" + Number(corePercent / Environment.ProcessorCount) +
                            ",\"privateBytes\":" + process.PrivateMemorySize64 + ",\"workingSetBytes\":" + process.WorkingSet64 +
                            ",\"gdiHandles\":" + GetGuiResources(process.Handle, 0) + ",\"userHandles\":" + GetGuiResources(process.Handle, 1) +
                            ",\"capturedFrames\":" + analyzer.CapturedFrames + ",\"validWindows\":" + validWindows + "}");
                    priorCpu = cpu; priorSeconds = now;
                }
            }
            catch (Exception ex) { failure = ex.GetBaseException().Message; }
            finally
            {
                try { playback?.Dispose(); }
                catch (Exception ex) { failure = failure ?? "Playback cleanup failed: " + ex.GetBaseException().Message; }
                var disposal = Stopwatch.StartNew();
                try { analyzer?.Dispose(); }
                catch (Exception ex) { failure = failure ?? "Capture cleanup failed: " + ex.GetBaseException().Message; }
                disposalMilliseconds = disposal.Elapsed.TotalMilliseconds;
                if (analyzer != null) analyzer.StatusChanged -= statusChanged;
                Analyzer.OnChange -= observe;
            }
        }
        string json;
        lock (sync)
        {
            if (failure == null && validWindows == 0) failure = "No real captured PCM windows were observed. Inspect capture status messages and ensure the default output is active.";
            if (failure == null && numericFailure != null) failure = numericFailure;
            if (failure == null && !stopped) failure = "Capture did not publish orderly Stopped status.";
            var formatJson = new List<string>();
            foreach (var format in formats) formatJson.Add(Quote(format));
            formatJson.Sort(StringComparer.Ordinal);
            json = "{\"helper\":\"LiveCaptureVerification\",\"utcStarted\":" + Quote(DateTime.UtcNow.Subtract(clock.Elapsed).ToString("o", CultureInfo.InvariantCulture)) +
                ",\"durationSeconds\":" + Number(clock.Elapsed.TotalSeconds) + ",\"requestedSeconds\":" + seconds +
                ",\"quietSignalRequested\":" + (playQuietSignal ? "true" : "false") + ",\"logicalProcessors\":" + Environment.ProcessorCount +
                ",\"windows\":" + windows + ",\"validPcmWindows\":" + validWindows + ",\"digitalZeroWindows\":" + zeroWindows +
                ",\"unavailableWindows\":" + unavailableWindows + ",\"channelSpectrumWindows\":" + spectralWindows +
                ",\"maximumEndFrameWithinAnEpoch\":" + maximumEndFrame + ",\"maximumLeftSamplePeak\":" + Number(maximumLeftPeak) +
                ",\"maximumRightSamplePeak\":" + Number(maximumRightPeak) + ",\"formats\":[" + string.Join(",", formatJson) +
                "],\"disposeMilliseconds\":" + Number(disposalMilliseconds) + ",\"orderlyStop\":" + (stopped ? "true" : "false") +
                ",\"failure\":" + (failure == null ? "null" : Quote(failure)) + ",\"statuses\":[" + string.Join(",", statuses) +
                "],\"resourceSamples\":[" + string.Join(",", samples) + "]}";
        }
        if (!string.IsNullOrWhiteSpace(outputJsonPath)) File.WriteAllText(outputJsonPath, json, new UTF8Encoding(false));
        if (failure != null) throw new InvalidOperationException(failure + (string.IsNullOrWhiteSpace(outputJsonPath) ? "\n" + json : " Report: " + Path.GetFullPath(outputJsonPath)));
        return json;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static string Number(double value) => Finite(value) ? value.ToString("0.######", CultureInfo.InvariantCulture) : "null";
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var character in value ?? string.Empty)
        {
            if (character == '\\' || character == '"') result.Append('\\').Append(character);
            else if (character < 32) result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else result.Append(character);
        }
        return result.Append('"').ToString();
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    // Opt-in six-second PCM segment: 1 s zero, 3 s 997 Hz at -36 dBFS peak, 2 s zero.
    // Other Windows audio can mix with it; this smoke test does not assert calibrated loopback amplitude.
    private sealed class QuietPlayback : IDisposable
    {
        private readonly MemoryStream _wav;
        private readonly IDisposable _reader;
        private readonly IDisposable _output;
        private readonly MethodInfo _stop;

        internal QuietPlayback()
        {
            const int rate = 44100, duration = 6, channels = 2;
            _wav = new MemoryStream();
            using (var writer = new BinaryWriter(_wav, Encoding.ASCII, true))
            {
                var byteCount = rate * duration * channels * sizeof(short);
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + byteCount);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
                writer.Write((short)channels); writer.Write(rate); writer.Write(rate * channels * sizeof(short));
                writer.Write((short)(channels * sizeof(short))); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(byteCount);
                for (var frame = 0; frame < rate * duration; frame++)
                {
                    var sample = frame >= rate && frame < rate * 4
                        ? (short)Math.Round(short.MaxValue * Math.Pow(10, -36.0 / 20) * Math.Sin(2 * Math.PI * 997 * frame / rate)) : (short)0;
                    writer.Write(sample); writer.Write(sample);
                }
            }
            _wav.Position = 0;
            try
            {
                var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Analyzer).Assembly.Location), "NAudio.dll"));
                var readerType = assembly.GetType("NAudio.Wave.WaveFileReader", true);
                var outputType = assembly.GetType("NAudio.Wave.WaveOutEvent", false) ?? assembly.GetType("NAudio.Wave.WaveOut", true);
                _reader = (IDisposable)Activator.CreateInstance(readerType, _wav);
                _output = (IDisposable)Activator.CreateInstance(outputType);
                _stop = outputType.GetMethod("Stop", Type.EmptyTypes);
                outputType.GetMethod("Init", new[] { assembly.GetType("NAudio.Wave.IWaveProvider", true) }).Invoke(_output, new object[] { _reader });
                outputType.GetMethod("Play", Type.EmptyTypes).Invoke(_output, null);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            try { if (_output != null) _stop?.Invoke(_output, null); }
            finally
            {
                try { _output?.Dispose(); }
                finally { try { _reader?.Dispose(); } finally { _wav.Dispose(); } }
            }
        }
    }
}
