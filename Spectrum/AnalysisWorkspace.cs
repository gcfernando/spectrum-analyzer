using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Spectrum.Dsp;

namespace Spectrum;

// Owned copies leave the existing reused display buffers and audio callback untouched.
internal sealed class AnalysisFrame
{
    public AnalysisFrame(BandPlan plan, int channels, int frameCount, long endFrame, float[] samples,
        double[] combined, double[] left, double[] right, object sourceEpoch = null, long firstValidFrame = 0,
        string sourceId = null, string sourceName = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (channels < 1 || frameCount < 0 || samples == null || (long)frameCount * channels > samples.Length)
            throw new ArgumentException("Invalid analysis PCM dimensions.");
        if (endFrame < 0 || firstValidFrame < 0 || firstValidFrame > endFrame || combined == null || combined.Length != plan.Count ||
            left == null || left.Length != plan.Count || right == null || right.Length != plan.Count)
            throw new ArgumentException("Invalid analysis spectrum dimensions.");
        Channels = channels; FrameCount = frameCount; EndFrame = endFrame; SourceEpoch = sourceEpoch; FirstValidFrame = firstValidFrame;
        Samples = (float[])samples.Clone(); Power = (double[])combined.Clone(); LeftPower = (double[])left.Clone(); RightPower = (double[])right.Clone();
        SourceId = sourceId ?? ""; SourceName = sourceName ?? "";
        ValidateMeasurements();
        CapturedUtc = DateTime.UtcNow;
    }
    // Missing/invalid measurements must never silently become measured zero. The separate
    // seven-band display's unsupported-band NaN contract does not enter these band arrays.
    internal void ValidateMeasurements()
    {
        ValidatePcm(Samples, FrameCount, Channels);
        foreach (var values in new[] { Power, LeftPower, RightPower })
            for (var i = 0; i < values.Length; i++)
                if (double.IsNaN(values[i]) || double.IsInfinity(values[i]) || values[i] < 0)
                    throw new ArgumentException("Analysis band power must be finite and nonnegative.");
    }
    internal static void ValidatePcm(float[] samples, int frames, int channels)
    {
        if (samples == null || frames < 0 || channels < 1 || (long)frames * channels > samples.Length)
            throw new ArgumentException("Invalid analysis PCM dimensions.");
        var count = checked(frames * channels);
        for (var i = 0; i < count; i++)
            if (float.IsNaN(samples[i]) || float.IsInfinity(samples[i]))
                throw new ArgumentException("Captured audio contains a non-finite sample; analysis is unavailable.");
    }
    public BandPlan Plan { get; }
    public int SampleRate => Plan.SampleRate;
    public int Channels { get; }
    public int FrameCount { get; }
    public long EndFrame { get; }
    public long FirstValidFrame { get; }
    public DateTime CapturedUtc { get; internal set; }
    public object SourceEpoch { get; }
    public string SourceId { get; }
    public string SourceName { get; }
    public float[] Samples { get; }
    public double[] Power { get; }
    public double[] LeftPower { get; }
    public double[] RightPower { get; }
}

internal sealed class AnalysisSettings
{
    public double FloorDb { get; set; } = -72;
    public double CeilingDb { get; set; }
    public double MinHz { get; set; } = 20;
    public double MaxHz { get; set; } = 20000;
    public string Response { get; set; } = "Balanced";
    public string Channel { get; set; } = "Combined";
    public int AveragingMilliseconds { get; set; } = 200;
    public int SmoothingBands { get; set; } = 1;
    public int PeakHoldMilliseconds { get; set; } = 500;
    public int HistorySeconds { get; set; } = 30;
    public AnalysisSettings Clone() => (AnalysisSettings)MemberwiseClone();
    public void Validate()
    {
        if (!Finite(FloorDb) || !Finite(CeilingDb) || FloorDb < -180 || CeilingDb > 24 || CeilingDb - FloorDb < 6)
            throw new ArgumentException("Use a level range of at least 6 dB, within -180 to +24 dBFS.");
        if (!Finite(MinHz) || !Finite(MaxHz) || MinHz < 1 || MaxHz > 384000 || MaxHz <= MinHz)
            throw new ArgumentException("Frequency limits must be ascending, between 1 and 384,000 Hz.");
        if (!new[] { "Fast", "Balanced", "Detailed" }.Contains(Response) ||
            !new[] { "Combined", "Left", "Right", "Mid", "Side" }.Contains(Channel))
            throw new ArgumentException("Unknown response or channel selection.");
        if (AveragingMilliseconds < 0 || AveragingMilliseconds > 10000 || SmoothingBands < 0 || SmoothingBands > 8 ||
            PeakHoldMilliseconds < 0 || PeakHoldMilliseconds > 60000 || HistorySeconds < 1 || HistorySeconds > 120)
            throw new ArgumentException("Analysis timing or smoothing is outside its supported range.");
    }
    private static bool Finite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
}

internal sealed class AnalysisHistoryRow
{
    public AnalysisHistoryRow(double seconds, double[] power, bool gap = false)
    { Seconds = seconds; Power = (double[])power.Clone(); Gap = gap; }
    public double Seconds { get; }
    public double[] Power { get; }
    public bool Gap { get; }
}

internal sealed class AnalysisWorkspace
{
    private readonly object _sync = new();
    private readonly List<AnalysisHistoryRow> _history = new();
    private AnalysisSettings _active = new();
    private AnalysisFrame _latest;
    private AnalysisFrame _resetBoundary;
    private double[] _display = Array.Empty<double>(), _peaks = Array.Empty<double>(), _reference;
    private double[] _rawSelected = Array.Empty<double>();
    private BandPlan _referencePlan;
    private string _referenceName = "";
    private DateTime? _referenceCapturedUtc;
    private double[] _sessionSelectedRaw;
    private string _sessionResponse, _sessionChannel;
    private int[] _phaseDensity = Array.Empty<int>();
    private double _phaseExtent;
    private double _phaseWindowSeconds;
    private double[] _holdUntil = Array.Empty<double>();
    private AudioMeasurementAnalyzer _meter;
    private AnalysisMeasurementResult _measurements;
    private FixedHopHistory _spectrogram;
    private object _epoch;
    private long _lastEnd;
    private double _time, _offset;
    private string _sourceKind = "Live capture", _sourceName = "", _sourceId = "";
    private double _selectionStartSeconds;
    private string _status = "Waiting for captured audio";
    private volatile bool _offline;
    private bool _unavailable = true;
    private PendingAnalysis _pending;
    private int _workerActive;
    private BandSpectrumAnalyzer _selectedEngine;
    private string _engineKey;
    public event Action Changed;
    public AnalysisSettings Settings { get; private set; } = new();
    public string WorkspaceName { get; set; } = "My analysis";
    public string PreferredView { get; set; } = "Spectrum";
    public int WindowWidth { get; set; } = 1200;
    public int WindowHeight { get; set; } = 800;
    public bool ReferenceVisible { get; set; } = true;
    public string LayoutName { get; set; } = "Studio";
    public int SettingsWidth { get; set; } = 238;
    public bool SettingsVisible { get; set; } = true;
    public bool DetailsVisible { get; set; }
    public AnalysisFrame Latest { get { lock (_sync) return _latest; } }
    public double[] DisplayPower { get { lock (_sync) return _display; } }
    public double[] RawSelectedPower { get { lock (_sync) return _rawSelected; } }
    public double[] PeakPower { get { lock (_sync) return _peaks; } }
    public double[] ReferencePower { get { lock (_sync) return _reference; } }
    public string ReferenceName { get { lock (_sync) return _reference == null ? "" : _referenceName; } }
    public DateTime? ReferenceCapturedUtc { get { lock (_sync) return _reference == null ? null : _referenceCapturedUtc; } }
    public IReadOnlyList<AnalysisHistoryRow> History { get { lock (_sync) return _history.ToArray(); } }
    public AnalysisMeasurementResult Measurements { get { lock (_sync) return _measurements; } }
    public string Status { get { lock (_sync) return _status; } }
    public bool IsOffline { get { lock (_sync) return _offline; } }
    public bool IsSavedSession { get { lock (_sync) return _offline && _latest != null && _latest.FrameCount == 0; } }
    public string SourceKind { get { lock (_sync) return _sourceKind; } }
    public string SourceName { get { lock (_sync) return _sourceName; } }
    public string SourceId { get { lock (_sync) return _sourceId; } }
    public double SelectionStartSeconds { get { lock (_sync) return _selectionStartSeconds; } }
    public bool IsUnavailable { get { lock (_sync) return _unavailable; } }
    public double CurrentSeconds { get { lock (_sync) return _time; } }
    public int[] PhaseDensity { get { lock (_sync) return _phaseDensity; } }
    public double PhaseExtent { get { lock (_sync) return _phaseExtent; } }
    public double PhaseWindowSeconds { get { lock (_sync) return _phaseWindowSeconds; } }

    public void UpdateSettings()
    {
        try { Settings.Validate(); }
        catch { lock (_sync) Settings = _active.Clone(); throw; }
        lock (_sync)
        {
            var spectrumSettingsChanged = _active.Channel != Settings.Channel || _active.Response != Settings.Response || _active.SmoothingBands != Settings.SmoothingBands;
            if (_latest != null && _latest.FrameCount == 0 &&
                (Settings.Channel != _active.Channel || Settings.Response != _active.Response))
            {
                Settings = _active.Clone();
                throw new InvalidOperationException("Saved sessions contain measurements, not PCM. Open the original audio or resume live capture to change the channel or analysis response.");
            }
            if (_active.Channel != Settings.Channel || _active.Response != Settings.Response)
            {
                _selectedEngine = null; _spectrogram = null; _reference = null;
                _display = Array.Empty<double>(); _peaks = Array.Empty<double>(); _history.Clear();
            }
            _active = Settings.Clone();
            TrimHistory();
            if (_latest != null && (spectrumSettingsChanged || _display.Length == 0)) UpdateSpectrum(_latest, 0);
        }
        Changed?.Invoke();
    }

    public void Accept(AnalysisFrame frame)
    {
        if (frame == null) return;
        lock (_sync) { if (_offline) return; frame.ValidateMeasurements(); AcceptCore(frame); }
        Changed?.Invoke();
    }

    // The capture timer never waits for measurements, decoding or session disk I/O.
    // Keep at most the newest frame, on one worker per workspace; gaps are measured explicitly.
    public void Post(AnalysisFrame frame)
    {
        if (frame == null || _offline) return;
        Interlocked.Exchange(ref _pending, new PendingAnalysis { Frame = frame });
        StartWorker();
    }
    public void PostUnavailable(string message)
    {
        if (_offline) return;
        Interlocked.Exchange(ref _pending, new PendingAnalysis { Message = message ?? "Audio unavailable" });
        StartWorker();
    }
    private void StartWorker()
    {
        if (Interlocked.CompareExchange(ref _workerActive, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                PendingAnalysis next;
                while ((next = Interlocked.Exchange(ref _pending, null)) != null)
                {
                    try { if (next.Frame != null) Accept(next.Frame); else SetUnavailable(next.Message); }
                    catch (Exception error)
                    {
                        System.Diagnostics.Trace.TraceError("Analysis workbench: {0}", error);
                        try { SetUnavailable("Analysis failed: " + error.Message); }
                        catch (Exception notificationError) { System.Diagnostics.Trace.TraceError("Analysis error notification: {0}", notificationError); }
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _workerActive, 0);
                if (Volatile.Read(ref _pending) != null) StartWorker();
            }
        });
    }
    private sealed class PendingAnalysis { internal AnalysisFrame Frame; internal string Message; }

    private void AcceptCore(AnalysisFrame frame)
    {
        var changedSource = _latest == null || _latest.SampleRate != frame.SampleRate ||
            _latest.Channels != frame.Channels || !ReferenceEquals(_epoch, frame.SourceEpoch) || frame.EndFrame < _lastEnd;
        var continuingAfterReset = _latest == null && _resetBoundary != null &&
            ReferenceEquals(_resetBoundary.SourceEpoch, frame.SourceEpoch) && _resetBoundary.SampleRate == frame.SampleRate &&
            _resetBoundary.Channels == frame.Channels && frame.EndFrame >= _resetBoundary.EndFrame;
        var firstValid = continuingAfterReset ? Math.Max(frame.FirstValidFrame, _resetBoundary.EndFrame) : frame.FirstValidFrame;
        var validFrames = frame.EndFrame - firstValid;
        var newFrames = changedSource ? Math.Min(frame.FrameCount, validFrames) : frame.EndFrame - _lastEnd;
        if (newFrames <= 0) return;
        var gap = _unavailable || (!changedSource && (newFrames > frame.FrameCount || firstValid > _lastEnd));
        if (changedSource || gap)
        {
            if (_latest != null && !CompatiblePlan(_latest.Plan, frame.Plan)) _history.Clear();
            if (_latest != null) _history.Add(new AnalysisHistoryRow(_time, new double[frame.Plan.Count], true));
            if (changedSource)
            {
                _offset = _latest == null ? continuingAfterReset ? -firstValid / (double)frame.SampleRate : 0
                    : _time - (frame.EndFrame - Math.Min(frame.FrameCount, validFrames)) / (double)frame.SampleRate;
                if (_referencePlan != null && !SameBandEdges(_referencePlan, frame.Plan)) { _reference = null; _referencePlan = null; }
                _selectedEngine = null;
                _display = Array.Empty<double>(); _peaks = Array.Empty<double>();
            }
            _meter = new AudioMeasurementAnalyzer(frame.SampleRate, frame.Channels);
            _spectrogram = null;
        }
        var priorTime = _time;
        _time = _offset + frame.EndFrame / (double)frame.SampleRate;
        _latest = frame; _epoch = frame.SourceEpoch;
        _resetBoundary = null;
        UpdatePhaseDensity(frame);
        var available = (int)Math.Min(frame.FrameCount, Math.Min(validFrames, Math.Max(0, newFrames)));
        if (available > 0)
        {
            var pcm = new float[available * frame.Channels];
            Array.Copy(frame.Samples, (frame.FrameCount - available) * frame.Channels, pcm, 0, pcm.Length);
            _measurements = _meter.Process(pcm, available);
            if (_spectrogram == null) _spectrogram = new FixedHopHistory(frame.Plan, frame.Channels, _active.Channel);
            _spectrogram.Push(pcm, available, _offset + (frame.EndFrame - available) / (double)frame.SampleRate, _history);
        }
        _lastEnd = frame.EndFrame; _unavailable = false;
        _sourceKind = _offline ? "Audio file" : "Live capture";
        _sourceName = frame.SourceName; _sourceId = frame.SourceId; _selectionStartSeconds = 0;
        UpdateSpectrum(frame, Math.Max(0, _time - priorTime));
        TrimHistory();
        _status = $"Live · {frame.SampleRate:N0} Hz / {frame.Channels} ch · {_active.Channel} · unquantized band power";
    }

    private void UpdatePhaseDensity(AnalysisFrame frame)
    {
        _phaseDensity = Array.Empty<int>(); _phaseExtent = 0; _phaseWindowSeconds = 0;
        if (frame.Channels < 2 || frame.FrameCount == 0) return;
        var frames = (int)Math.Min(frame.FrameCount, frame.EndFrame - frame.FirstValidFrame);
        if (frames <= 0) return;
        var first = frame.FrameCount - frames;
        _phaseWindowSeconds = frames / (double)frame.SampleRate;
        double Read(int f, int c)
        {
            var sample = frame.Samples[f * frame.Channels + c];
            return float.IsNaN(sample) || float.IsInfinity(sample) ? 0 : sample;
        }
        for (var f = first; f < frame.FrameCount; f++)
        {
            var left = Read(f, 0); var right = Read(f, 1);
            _phaseExtent = Math.Max(_phaseExtent, Math.Max(Math.Abs((left + right) * .5), Math.Abs((left - right) * .5)));
        }
        var density = new int[64 * 64];
        if (_phaseExtent > 0)
            for (var f = first; f < frame.FrameCount; f++)
            {
                var left = Read(f, 0); var right = Read(f, 1);
                var x = (int)Math.Round(31.5 * (1 + (left - right) * .5 / _phaseExtent));
                var y = (int)Math.Round(31.5 * (1 - (left + right) * .5 / _phaseExtent));
                density[Math.Max(0, Math.Min(63, y)) * 64 + Math.Max(0, Math.Min(63, x))]++;
            }
        _phaseDensity = density;
    }

    private void UpdateSpectrum(AnalysisFrame frame, double elapsed)
    {
        var target = SelectPower(frame);
        _rawSelected = (double[])target.Clone();
        if (_active.SmoothingBands > 0)
        {
            var smoothed = new double[target.Length];
            for (var b = 0; b < target.Length; b++)
            {
                var first = Math.Max(0, b - _active.SmoothingBands);
                var last = Math.Min(target.Length - 1, b + _active.SmoothingBands);
                for (var i = first; i <= last; i++) smoothed[b] += target[i];
                smoothed[b] /= last - first + 1;
            }
            target = smoothed;
        }
        var old = _display;
        var current = new double[target.Length];
        var peak = _peaks.Length == target.Length ? (double[])_peaks.Clone() : new double[target.Length];
        if (_holdUntil.Length != target.Length) _holdUntil = new double[target.Length];
        var alpha = _active.AveragingMilliseconds == 0 || old.Length != target.Length || elapsed == 0
            ? 1 : 1 - Math.Exp(-elapsed * 1000 / _active.AveragingMilliseconds);
        for (var b = 0; b < target.Length; b++)
        {
            current[b] = old.Length == target.Length ? old[b] + alpha * (target[b] - old[b]) : target[b];
            if (target[b] >= peak[b]) { peak[b] = target[b]; _holdUntil[b] = _time + _active.PeakHoldMilliseconds / 1000d; }
            else if (_time > _holdUntil[b]) peak[b] = Math.Max(current[b], peak[b] * Math.Pow(10, -12 * elapsed / 10));
        }
        _display = current; _peaks = peak;
    }

    private double[] SelectPower(AnalysisFrame frame)
    {
        if (frame.FrameCount == 0 && _sessionSelectedRaw != null && _sessionResponse == _active.Response && _sessionChannel == _active.Channel)
            return (double[])_sessionSelectedRaw.Clone();
        if (_active.Channel == "Right" && frame.Channels < 2 || _active.Channel == "Side" && frame.Channels < 2)
            return new double[frame.Plan.Count];
        if (_active.Response == "Balanced" && _active.Channel == "Combined") return (double[])frame.Power.Clone();
        if (_active.Response == "Balanced" && _active.Channel == "Left") return (double[])frame.LeftPower.Clone();
        if (_active.Response == "Balanced" && _active.Channel == "Right") return (double[])frame.RightPower.Clone();
        if (frame.FrameCount == 0) throw new InvalidOperationException("The saved session has no PCM for this analysis selection.");
        var combined = _active.Channel == "Combined";
        var key = frame.SampleRate + ":" + frame.Channels + ":" + _active.Response + ":" + _active.Channel;
        if (_selectedEngine == null || key != _engineKey)
        {
            var defaults = BandSpectrumAnalyzer.DefaultWindowLengths(frame.SampleRate);
            var lengths = _active.Response == "Fast" ? new[] { defaults[0] }
                : _active.Response == "Detailed" ? new[] { defaults[defaults.Length - 1] } : defaults;
            _selectedEngine = new BandSpectrumAnalyzer(frame.Plan, combined ? frame.Channels : 1, lengths); _engineKey = key;
        }
        if (frame.FrameCount < _selectedEngine.RequiredFrames) return (double[])frame.Power.Clone();
        var input = combined ? frame.Samples : ChannelSamples(frame.Samples, frame.FrameCount, frame.Channels, _active.Channel);
        var output = new double[frame.Plan.Count];
        _selectedEngine.Analyze(input, frame.FrameCount, output);
        return output;
    }

    internal static float[] ChannelSamples(float[] input, int frames, int channels, string channel)
    {
        var output = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var l = input[i * channels]; var r = channels > 1 ? input[i * channels + 1] : 0;
            output[i] = channel == "Left" ? l : channel == "Right" ? r : channel == "Mid" ? channels > 1 ? (float)(((double)l + r) * .5) : l
                : channels > 1 ? (float)(((double)l - r) * .5) : 0;
        }
        return output;
    }

    private void TrimHistory()
    {
        var first = _history.FindIndex(r => r.Seconds >= _time - _active.HistorySeconds);
        if (first > 0) _history.RemoveRange(0, first);
        if (_history.Count > 12000) _history.RemoveRange(0, _history.Count - 12000);
    }
    public void CaptureReference()
    {
        lock (_sync)
        {
            if (_latest == null || _unavailable) throw new InvalidOperationException("Capture a spectrum before saving a reference.");
            _reference = (double[])_display.Clone();
            _referencePlan = _latest.Plan;
            _referenceName = !string.IsNullOrEmpty(_sourceName) ? _sourceName : _offline ? "Saved analysis" : "Live capture";
            _referenceCapturedUtc = DateTime.UtcNow; ReferenceVisible = true;
        }
        Changed?.Invoke();
    }
    public void ClearReference() { lock (_sync) { _reference = null; _referencePlan = null; _referenceName = ""; _referenceCapturedUtc = null; } Changed?.Invoke(); }
    public void ApplyStoredReference(BandPlan plan, double[] power, string name, DateTime? capturedUtc, string channel, string response)
    {
        if (plan == null || power == null || power.Length != plan.Count ||
            power.Any(value => double.IsNaN(value) || double.IsInfinity(value) || value < 0))
            throw new ArgumentException("The saved reference has invalid band power.");
        var copy = (double[])power.Clone();
        lock (_sync)
        {
            if (_latest == null) throw new InvalidOperationException("Measure live audio or open a session before applying a reference.");
            if (!SameBandEdges(plan, _latest.Plan)) throw new InvalidOperationException("The reference frequency bands differ from the current measurement.");
            if (channel != _active.Channel || response != _active.Response)
                throw new InvalidOperationException("Use the same channel and analysis response as the saved reference (" + channel + ", " + response + ").");
            _reference = copy; _referencePlan = plan; _referenceName = name ?? "Saved reference";
            _referenceCapturedUtc = capturedUtc; ReferenceVisible = true;
        }
        Changed?.Invoke();
    }
    public void SetUnavailable(string message)
    {
        lock (_sync)
        {
            if (_offline) return;
            if (!_unavailable && _latest != null) _history.Add(new AnalysisHistoryRow(_time, new double[_latest.Plan.Count], true));
            _unavailable = true; _status = message ?? "Audio unavailable";
        }
        Changed?.Invoke();
    }
    public void Reset()
    {
        lock (_sync)
        {
            if (_latest != null) _resetBoundary = !_offline && _latest.FrameCount > 0 ? _latest : null;
            _latest = null; _history.Clear(); _display = _peaks = _holdUntil = Array.Empty<double>();
            _reference = null; _referencePlan = null; _rawSelected = Array.Empty<double>(); _meter = null; _measurements = null; _spectrogram = null; _selectedEngine = null;
            _referenceName = ""; _referenceCapturedUtc = null; ReferenceVisible = true;
            _sessionSelectedRaw = null; _sessionResponse = _sessionChannel = null;
            _phaseDensity = Array.Empty<int>(); _phaseExtent = 0; _phaseWindowSeconds = 0;
            _epoch = null; _lastEnd = 0; _time = _offset = 0; _unavailable = true; _status = "Waiting for captured audio";
            _sourceKind = "Live capture"; _sourceName = ""; _sourceId = ""; _selectionStartSeconds = 0;
        }
        Changed?.Invoke();
    }
    public void ResumeLive() { lock (_sync) { Reset(); _offline = false; } }

    public void AnalyzeFile(string path, double startSeconds, double durationSeconds)
    {
        if (double.IsNaN(startSeconds) || double.IsInfinity(startSeconds) || startSeconds < 0 ||
            double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || durationSeconds <= 0 || durationSeconds > 600)
            throw new ArgumentException("Select a positive duration up to 600 seconds and a nonnegative start.");
        var decoded = AnalysisAudioFile.Read(path);
        var first = (long)Math.Round(startSeconds * decoded.SampleRate);
        var total = decoded.Samples.Length / decoded.Channels;
        if (first >= total) throw new ArgumentException("The selected range starts after the end of the audio file.");
        var last = Math.Min(total, first + (long)Math.Round(durationSeconds * decoded.SampleRate));
        if (last <= first) throw new ArgumentException("The selected duration must contain at least one audio sample.");
        var plan = BandPlan.CreateLogarithmic(83, 20, 20000, decoded.SampleRate);
        var engine = new BandSpectrumAnalyzer(plan, decoded.Channels);
        var history = new SampleHistory(decoded.Channels, engine.RequiredFrames * 2);
        var snapshot = new float[engine.RequiredFrames * decoded.Channels];
        var hop = Math.Max(1, decoded.SampleRate / 20);
        var epoch = new object();
        AnalysisWorkspace analysis;
        lock (_sync)
        {
            _offline = true;
            _status = "Analyzing " + Path.GetFileName(path) + " · live capture continues";
            analysis = new AnalysisWorkspace { Settings = _active.Clone(), _active = _active.Clone(), _offline = true };
        }
        Changed?.Invoke();
        // Build the offline result separately, so even a long selection never holds a UI-read lock.
        for (var frame = first; frame < last; frame += hop)
        {
            var count = (int)Math.Min(hop, last - frame);
            var chunk = new float[count * decoded.Channels];
            Array.Copy(decoded.Samples, frame * decoded.Channels, chunk, 0, chunk.Length);
            history.Write(chunk, count);
            history.TryCopyLatest(snapshot, engine.RequiredFrames, out var end);
            var combined = new double[83]; var left = new double[83]; var right = new double[83];
            engine.Analyze(snapshot, engine.RequiredFrames, combined, left, right);
            analysis.AcceptCore(new AnalysisFrame(plan, decoded.Channels, engine.RequiredFrames, end, snapshot, combined, left, right, epoch));
        }
        if (analysis._meter != null) analysis._measurements = analysis._meter.Complete();
        lock (_sync)
        {
            _latest = analysis._latest; _display = analysis._display; _rawSelected = analysis._rawSelected; _peaks = analysis._peaks;
            _sessionSelectedRaw = null; _sessionResponse = _sessionChannel = null;
            _holdUntil = analysis._holdUntil; _measurements = analysis._measurements;
            if (_referencePlan == null || !SameBandEdges(_referencePlan, analysis._latest.Plan)) { _reference = null; _referencePlan = null; }
            _phaseDensity = analysis._phaseDensity; _phaseExtent = analysis._phaseExtent; _phaseWindowSeconds = analysis._phaseWindowSeconds;
            _meter = analysis._meter; _spectrogram = analysis._spectrogram; _epoch = analysis._epoch; _lastEnd = analysis._lastEnd;
            _time = analysis._time; _offset = analysis._offset; _unavailable = analysis._unavailable;
            _sourceKind = "Audio file"; _sourceName = Path.GetFileName(path); _sourceId = ""; _selectionStartSeconds = first / (double)decoded.SampleRate;
            _history.Clear(); _history.AddRange(analysis._history);
            _status = $"Offline · {Path.GetFileName(path)} · {startSeconds:F2}–{last / (double)decoded.SampleRate:F2} s · {decoded.SampleRate:N0} Hz / {decoded.Channels} ch";
        }
        Changed?.Invoke();
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double ReadNumber(XAttribute attribute, double fallback = 0) => attribute == null ? fallback : double.Parse(attribute.Value, CultureInfo.InvariantCulture);
    private static string Values(double[] values) => string.Join(" ", values.Select(Number));
    private static double[] ReadValues(string text, int count)
    {
        var tokens = (text ?? "").Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != count) throw new InvalidDataException("The saved spectrum has an invalid band count.");
        var result = tokens.Select(t => double.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        if (result.Any(v => double.IsNaN(v) || double.IsInfinity(v) || v < 0)) throw new InvalidDataException("Invalid saved band power.");
        return result;
    }
    private XElement SettingsXml() => new("settings", new XAttribute("name", WorkspaceName ?? "My analysis"),
        new XAttribute("referenceVisible", ReferenceVisible),
        new XAttribute("layout", ValidLayout(LayoutName)), new XAttribute("settingsWidth", Math.Max(170, Math.Min(600, SettingsWidth))),
        new XAttribute("settingsVisible", SettingsVisible), new XAttribute("detailsVisible", DetailsVisible),
        new XAttribute("view", PreferredView == "Spectrogram" || PreferredView == "Stereo phase" ? PreferredView : "Spectrum"),
        new XAttribute("width", Math.Max(900, Math.Min(7680, WindowWidth))), new XAttribute("height", Math.Max(600, Math.Min(4320, WindowHeight))),
        new XAttribute("floor", Number(_active.FloorDb)), new XAttribute("ceiling", Number(_active.CeilingDb)),
        new XAttribute("minHz", Number(_active.MinHz)), new XAttribute("maxHz", Number(_active.MaxHz)),
        new XAttribute("response", _active.Response), new XAttribute("channel", _active.Channel),
        new XAttribute("averagingMs", _active.AveragingMilliseconds), new XAttribute("smoothing", _active.SmoothingBands),
        new XAttribute("holdMs", _active.PeakHoldMilliseconds), new XAttribute("historySeconds", _active.HistorySeconds));
    private static AnalysisSettings ParseSettings(XElement xml)
    {
        if (xml == null) throw new InvalidDataException("Saved settings are missing.");
        var settings = new AnalysisSettings { FloorDb = ReadNumber(xml.Attribute("floor"), -72), CeilingDb = ReadNumber(xml.Attribute("ceiling")),
            MinHz = ReadNumber(xml.Attribute("minHz"), 20), MaxHz = ReadNumber(xml.Attribute("maxHz"), 20000),
            Response = (string)xml.Attribute("response") ?? "Balanced", Channel = (string)xml.Attribute("channel") ?? "Combined",
            AveragingMilliseconds = (int)ReadNumber(xml.Attribute("averagingMs"), 200), SmoothingBands = (int)ReadNumber(xml.Attribute("smoothing"), 1),
            PeakHoldMilliseconds = (int)ReadNumber(xml.Attribute("holdMs"), 500), HistorySeconds = (int)ReadNumber(xml.Attribute("historySeconds"), 30) };
        _ = (bool?)xml.Attribute("referenceVisible"); // Validate optional presentation metadata before replacing current settings.
        _ = (bool?)xml.Attribute("settingsVisible"); _ = (bool?)xml.Attribute("detailsVisible");
        _ = (int?)xml.Attribute("settingsWidth");
        settings.Validate(); return settings;
    }
    private static XElement ReadDocument(string path, string expected)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16000000 });
        var root = XElement.Load(reader);
        if (root.Name != expected || (string)root.Attribute("version") != "1") throw new InvalidDataException("Unsupported Spectrum file format.");
        return root;
    }
    private static bool CompatiblePlan(BandPlan a, BandPlan b) => a.SampleRate == b.SampleRate && SameBandEdges(a, b);
    private static bool SameBandEdges(BandPlan a, BandPlan b) => a.Count == b.Count &&
        Enumerable.Range(0, a.Count).All(i => Math.Abs(a[i].LowerHz - b[i].LowerHz) < 1e-6 && Math.Abs(a[i].UpperHz - b[i].UpperHz) < 1e-6);
    private void ReadLayout(XElement settings)
    {
        var view = (string)settings.Attribute("view");
        PreferredView = view == "Spectrogram" || view == "Stereo phase" ? view : "Spectrum";
        ReferenceVisible = (bool?)settings.Attribute("referenceVisible") ?? true;
        LayoutName = ValidLayout((string)settings.Attribute("layout"));
        SettingsWidth = Math.Max(170, Math.Min(600, (int?)settings.Attribute("settingsWidth") ?? 238));
        SettingsVisible = (bool?)settings.Attribute("settingsVisible") ?? true;
        DetailsVisible = (bool?)settings.Attribute("detailsVisible") ?? false;
        WindowWidth = Math.Max(900, Math.Min(7680, (int)ReadNumber(settings.Attribute("width"), 1200)));
        WindowHeight = Math.Max(600, Math.Min(4320, (int)ReadNumber(settings.Attribute("height"), 800)));
    }
    private static string ValidLayout(string name) => name == "Compact" || name == "Measurement" ? name : "Studio";
    public void SaveWorkspace(string path)
    {
        lock (_sync)
        {
            var root = new XElement("spectrum-workspace", new XAttribute("version", 1), SettingsXml());
            if (_reference != null && _referencePlan != null)
                root.Add(new XElement("reference", new XAttribute("rate", _referencePlan.SampleRate),
                    new XAttribute("name", _referenceName), _referenceCapturedUtc.HasValue ? new XAttribute("capturedUtc", _referenceCapturedUtc.Value.ToString("o")) : null,
                    new XElement("edges", Values(Enumerable.Range(0, _referencePlan.Count).Select(i => _referencePlan[i].LowerHz).Concat(new[] { _referencePlan.MaxHz }).ToArray())),
                    new XElement("power", Values(_reference))));
            root.Save(path);
        }
    }
    public void LoadWorkspace(string path)
    {
        var root = ReadDocument(path, "spectrum-workspace");
        var xml = root.Element("settings");
        var settings = ParseSettings(xml);
        BandPlan referencePlan = null; double[] reference = null;
        if (root.Element("reference") is XElement r)
        {
            var tokens = ((string)r.Element("edges") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3 || tokens.Length > 257) throw new InvalidDataException("Invalid reference band count.");
            referencePlan = BandPlan.CreateFromEdges(ReadValues(string.Join(" ", tokens), tokens.Length), checked((int)ReadNumber(r.Attribute("rate"))));
            reference = ReadValues((string)r.Element("power"), referencePlan.Count);
        }
        lock (_sync) { Settings = settings; WorkspaceName = (string)xml.Attribute("name") ?? "My analysis"; ReadLayout(xml); }
        UpdateSettings();
        lock (_sync)
        {
            if (referencePlan != null && (_latest == null || SameBandEdges(referencePlan, _latest.Plan)))
            {
                _reference = reference; _referencePlan = referencePlan;
                _referenceName = (string)root.Element("reference")?.Attribute("name") ?? "Saved reference";
                _referenceCapturedUtc = ReadReferenceTime(root.Element("reference"));
            }
            else { _reference = null; _referencePlan = null; }
        }
        Changed?.Invoke();
    }
    public void SaveSession(string path)
    {
        lock (_sync)
        {
            if (_latest == null) throw new InvalidOperationException("There is no analysis to save.");
            var plan = _latest.Plan;
            var edges = Enumerable.Range(0, plan.Count).Select(i => plan[i].LowerHz).Concat(new[] { plan.MaxHz }).ToArray();
            var xml = new XElement("spectrum-session", new XAttribute("version", 1), SettingsXml(),
                new XElement("capture", new XAttribute("rate", plan.SampleRate), new XAttribute("channels", _latest.Channels),
                    new XAttribute("endFrame", _latest.EndFrame), new XAttribute("seconds", Number(_time)),
                    new XAttribute("sourceKind", _sourceKind), new XAttribute("sourceName", _sourceName), new XAttribute("sourceId", _sourceId),
                    new XAttribute("firstValidFrame", _latest.FirstValidFrame), new XAttribute("selectionStartSeconds", Number(_selectionStartSeconds)),
                    new XAttribute("utc", _latest.CapturedUtc.ToString("o")), new XAttribute("note", "Spectral measurements only; no audio recording"),
                    new XElement("edges", Values(edges)), new XElement("combined", Values(_latest.Power)),
                    new XElement("left", Values(_latest.LeftPower)), new XElement("right", Values(_latest.RightPower)),
                    new XElement("display", Values(_display)), new XElement("peaks", Values(_peaks)),
                    new XElement("selectedRaw", Values(_rawSelected)),
                    new XElement("phaseDensity", new XAttribute("extent", Number(_phaseExtent)), new XAttribute("windowSeconds", Number(_phaseWindowSeconds)), string.Join(" ", _phaseDensity)),
                    _reference == null ? null : new XElement("reference", new XAttribute("name", _referenceName),
                        _referenceCapturedUtc.HasValue ? new XAttribute("capturedUtc", _referenceCapturedUtc.Value.ToString("o")) : null, Values(_reference))),
                new XElement("history", _history.Select(row => new XElement("row", new XAttribute("seconds", Number(row.Seconds)),
                    new XAttribute("gap", row.Gap), Values(row.Power)))));
            if (_measurements != null)
                xml.Add(new XElement("measurements", typeof(AnalysisMeasurementResult).GetProperties()
                    .Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(int) || p.PropertyType == typeof(long) || p.PropertyType == typeof(bool))
                    .Select(p => new XAttribute(p.Name, Convert.ToString(p.GetValue(_measurements), CultureInfo.InvariantCulture)))));
            xml.Save(path);
        }
    }
    public void LoadSession(string path)
    {
        var xml = ReadDocument(path, "spectrum-session"); var settings = ParseSettings(xml.Element("settings"));
        var capture = xml.Element("capture") ?? throw new InvalidDataException("Saved capture is missing.");
        var rate = checked((int)ReadNumber(capture.Attribute("rate"))); var channels = checked((int)ReadNumber(capture.Attribute("channels")));
        if (rate < 8000 || rate > 384000 || channels < 1 || channels > 32) throw new InvalidDataException("Unsupported saved audio format.");
        var edgeText = ((string)capture.Element("edges") ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (edgeText.Length < 3 || edgeText.Length > 257) throw new InvalidDataException("Unsupported saved band count.");
        var plan = BandPlan.CreateFromEdges(ReadValues(string.Join(" ", edgeText), edgeText.Length), rate);
        var count = plan.Count;
        var end = checked((long)ReadNumber(capture.Attribute("endFrame")));
        var frame = new AnalysisFrame(plan, channels, 0, end, Array.Empty<float>(), ReadValues((string)capture.Element("combined"), count),
            ReadValues((string)capture.Element("left"), count), ReadValues((string)capture.Element("right"), count),
            firstValidFrame: checked((long)ReadNumber(capture.Attribute("firstValidFrame"))),
            sourceId: (string)capture.Attribute("sourceId"), sourceName: (string)capture.Attribute("sourceName"));
        if (DateTime.TryParse((string)capture.Attribute("utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var capturedUtc)) frame.CapturedUtc = capturedUtc;
        var display = ReadValues((string)capture.Element("display"), count); var peaks = ReadValues((string)capture.Element("peaks"), count);
        var selectedRaw = capture.Element("selectedRaw") == null
            ? settings.Response == "Balanced" && settings.Channel == "Combined" ? (double[])frame.Power.Clone()
                : settings.Response == "Balanced" && settings.Channel == "Left" ? (double[])frame.LeftPower.Clone()
                : settings.Response == "Balanced" && settings.Channel == "Right" ? (double[])frame.RightPower.Clone()
                : throw new InvalidDataException("The saved session is missing its original selected-channel measurements.")
            : ReadValues((string)capture.Element("selectedRaw"), count);
        var phaseXml = capture.Element("phaseDensity");
        var phaseExtent = ReadNumber(phaseXml?.Attribute("extent"));
        var phaseWindowSeconds = ReadNumber(phaseXml?.Attribute("windowSeconds"));
        if (double.IsNaN(phaseExtent) || double.IsInfinity(phaseExtent) || phaseExtent < 0) throw new InvalidDataException("Invalid saved stereo phase range.");
        if (double.IsNaN(phaseWindowSeconds) || double.IsInfinity(phaseWindowSeconds) || phaseWindowSeconds < 0 || phaseWindowSeconds > 60) throw new InvalidDataException("Invalid saved stereo phase window.");
        var phaseDensity = phaseXml == null || string.IsNullOrWhiteSpace(phaseXml.Value) ? Array.Empty<int>()
            : ReadValues(phaseXml.Value, 4096).Select(value => checked((int)value)).ToArray();
        var reference = capture.Element("reference") == null ? null : ReadValues((string)capture.Element("reference"), count);
        var rows = (xml.Element("history")?.Elements("row") ?? Enumerable.Empty<XElement>()).Take(12001)
            .Select(r => new AnalysisHistoryRow(ReadNumber(r.Attribute("seconds")), ReadValues(r.Value, count), (bool?)r.Attribute("gap") ?? false)).ToList();
        if (rows.Count > 12000 || rows.Any(r => double.IsNaN(r.Seconds) || double.IsInfinity(r.Seconds) || r.Seconds < 0) ||
            rows.Zip(rows.Skip(1), (a, b) => a.Seconds > b.Seconds).Any(v => v)) throw new InvalidDataException("Invalid saved time history.");
        var seconds = ReadNumber(capture.Attribute("seconds"));
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) throw new InvalidDataException("Invalid saved duration.");
        var selectionStart = ReadNumber(capture.Attribute("selectionStartSeconds"));
        if (double.IsNaN(selectionStart) || double.IsInfinity(selectionStart) || selectionStart < 0) throw new InvalidDataException("Invalid saved selection start.");
        AnalysisMeasurementResult measurements = null;
        if (xml.Element("measurements") is XElement meterXml)
        {
            measurements = new AnalysisMeasurementResult();
            foreach (var property in typeof(AnalysisMeasurementResult).GetProperties().Where(p => p.CanWrite))
            {
                var value = meterXml.Attribute(property.Name);
                if (value != null && (property.PropertyType == typeof(double) || property.PropertyType == typeof(int) || property.PropertyType == typeof(long) || property.PropertyType == typeof(bool)))
                    property.SetValue(measurements, Convert.ChangeType(value.Value, property.PropertyType, CultureInfo.InvariantCulture));
            }
        }
        lock (_sync)
        {
            Settings = settings; _active = settings.Clone(); WorkspaceName = (string)xml.Element("settings")?.Attribute("name") ?? "My analysis"; ReadLayout(xml.Element("settings"));
            _offline = true; _latest = frame; _display = display; _peaks = peaks; _reference = reference; _time = seconds;
            _sourceKind = (string)capture.Attribute("sourceKind") ?? "Saved measurements";
            _sourceName = _sourceKind == "Audio file" ? Path.GetFileName(frame.SourceName) : frame.SourceName;
            _sourceId = frame.SourceId; _selectionStartSeconds = selectionStart;
            _referencePlan = reference == null ? null : plan; _rawSelected = selectedRaw;
            _referenceName = reference == null ? "" : (string)capture.Element("reference")?.Attribute("name") ?? "Saved reference";
            _referenceCapturedUtc = reference == null ? null : ReadReferenceTime(capture.Element("reference"));
            _sessionSelectedRaw = (double[])selectedRaw.Clone(); _sessionResponse = settings.Response; _sessionChannel = settings.Channel;
            _phaseDensity = phaseDensity; _phaseExtent = phaseExtent; _phaseWindowSeconds = phaseWindowSeconds;
            _history.Clear(); _history.AddRange(rows); _measurements = measurements; _unavailable = false;
            _status = "Saved analysis · " + Path.GetFileName(path) + " · no audio recording";
        }
        Changed?.Invoke();
    }
    private static DateTime? ReadReferenceTime(XElement reference)
        => DateTime.TryParse((string)reference?.Attribute("capturedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value.ToUniversalTime() : (DateTime?)null;
    public void ExportCsv(string path)
    {
        lock (_sync)
        {
            if (_latest == null) throw new InvalidOperationException("There is no spectrum to export.");
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine("utc,seconds,end_frame,sample_rate,channels,band,lower_hz,center_hz,upper_hz,raw_power,left_power,right_power,raw_dbfs,display_power,reference_power,delta_db,floor_dbfs,ceiling_dbfs,response,channel,averaging_ms,smoothing_bands,peak_hold_ms,selected_raw_power,selected_raw_dbfs,left_rms_dbfs,right_rms_dbfs,sample_peak_dbfs,estimated_true_peak_dbtp,correlation,mid_rms_dbfs,side_rms_dbfs,momentary_lufs,short_term_lufs,integrated_lufs,overload_samples,measured_frames,programme_loudness_supported,source_kind,source_name,selection_start_seconds,minimum_hz,maximum_hz,history_seconds,source_id,first_valid_frame,measurement_state");
            var m = _measurements;
            string Stat(double? value) => !value.HasValue || double.IsNaN(value.Value) ? "" : Number(value.Value);
            string CsvText(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var statistics = string.Join(",", Stat(m?.LeftRmsDb), Stat(m != null && m.HasStereo ? m.RightRmsDb : (double?)null), Stat(m?.SamplePeakDb),
                Stat(m?.TruePeakDb), Stat(m?.Correlation), Stat(m?.MidRmsDb), Stat(m?.SideRmsDb), Stat(m?.MomentaryLufs), Stat(m?.ShortTermLufs), Stat(m?.IntegratedLufs),
                m?.ClippedSamples.ToString(CultureInfo.InvariantCulture) ?? "", m?.TotalFrames.ToString(CultureInfo.InvariantCulture) ?? "", m?.LoudnessSupported.ToString() ?? "");
            for (var b = 0; b < _latest.Plan.Count; b++)
            {
                var band = _latest.Plan[b]; var p = _latest.Power[b];
                var refValue = _reference == null ? "" : Number(_reference[b]);
                var delta = _reference == null || _reference[b] <= 0 || _display[b] <= 0 ? "" : Number(10 * Math.Log10(_display[b] / _reference[b]));
                writer.WriteLine(string.Join(",", _latest.CapturedUtc.ToString("o"), Number(_time), _latest.EndFrame.ToString(CultureInfo.InvariantCulture),
                    _latest.SampleRate.ToString(CultureInfo.InvariantCulture), _latest.Channels.ToString(CultureInfo.InvariantCulture), (b + 1).ToString(CultureInfo.InvariantCulture),
                    Number(band.LowerHz), Number(band.CenterHz), Number(band.UpperHz), Number(p), Number(_latest.LeftPower[b]), Number(_latest.RightPower[b]),
                    p > 0 ? Number(10 * Math.Log10(p)) : "-Infinity", Number(_display[b]), refValue, delta, Number(_active.FloorDb), Number(_active.CeilingDb),
                    _active.Response, _active.Channel, _active.AveragingMilliseconds.ToString(CultureInfo.InvariantCulture), _active.SmoothingBands.ToString(CultureInfo.InvariantCulture),
                    _active.PeakHoldMilliseconds.ToString(CultureInfo.InvariantCulture), Number(_rawSelected[b]),
                    _rawSelected[b] > 0 ? Number(10 * Math.Log10(_rawSelected[b])) : "-Infinity", statistics,
                    CsvText(_sourceKind), CsvText(_sourceName), Number(_selectionStartSeconds), Number(_active.MinHz), Number(_active.MaxHz),
                    _active.HistorySeconds.ToString(CultureInfo.InvariantCulture), CsvText(_sourceId),
                    _latest.FirstValidFrame.ToString(CultureInfo.InvariantCulture),
                    CsvText(_unavailable ? "Unavailable (last valid measurement)" : IsSavedSession ? "Saved measurement" : _offline ? "Audio file" : "Live capture")));
            }
        }
    }

    // Fixed sample hops, independent of timer/UI cadence. Each row has its actual audio time.
    private sealed class FixedHopHistory
    {
        private readonly int _rate, _channels, _inputChannels, _window, _hop;
        private readonly string _channel;
        private readonly float[] _ring, _snapshot;
        private readonly BandSpectrumAnalyzer _engine;
        private long _frames;
        private double? _startTime;
        public FixedHopHistory(BandPlan plan, int channels, string channel)
        {
            _rate = plan.SampleRate; _inputChannels = channels; _channel = channel;
            _channels = channel == "Combined" ? channels : 1;
            _window = Math.Max(512, BandSpectrumAnalyzer.DefaultWindowLengths(_rate)[0] / 2); _hop = _window / 2;
            _ring = new float[_window * _channels]; _snapshot = new float[_ring.Length];
            _engine = new BandSpectrumAnalyzer(plan, _channels, new[] { _window });
        }
        public void Push(float[] input, int frames, double startTime, List<AnalysisHistoryRow> rows)
        {
            if (!_startTime.HasValue) _startTime = startTime;
            var mono = _channel == "Combined" ? input : ChannelSamples(input, frames, _inputChannels, _channel);
            for (var i = 0; i < frames; i++)
            {
                var slot = (int)(_frames % _window);
                for (var c = 0; c < _channels; c++) _ring[slot * _channels + c] = mono[i * _channels + c];
                _frames++;
                if (_frames < _window || (_frames - _window) % _hop != 0) continue;
                var first = (int)(_frames % _window);
                Array.Copy(_ring, first * _channels, _snapshot, 0, (_window - first) * _channels);
                Array.Copy(_ring, 0, _snapshot, (_window - first) * _channels, first * _channels);
                var power = new double[_engine.Plan.Count]; _engine.Analyze(_snapshot, _window, power);
                rows.Add(new AnalysisHistoryRow(_startTime.Value + _frames / (double)_rate, power));
            }
        }
    }
}
