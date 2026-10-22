using System;
using System.Collections.Generic;
using System.Globalization;
using Spectrum.Dsp;

namespace Spectrum;

internal enum MainSpectrumGeometry { NotApplicable, LegacyBars, LogFrequency, RadialBands, MatrixCards, SevenBandCards, StereoPanels }

// Construct on UI apply, together with the displayed bytes/audio snapshot. The analyzer
// already owns and copies AnalysisFrame; optional presentation payloads are copied here.
internal sealed class MainSpectrumSnapshot
{
    internal MainSpectrumSnapshot(AnalysisFrame frame, IReadOnlyList<double> sevenBandPower = null,
        IReadOnlyList<double> selectedPower = null, string selectedChannel = "Combined", string sourceDeviceId = null)
    {
        Frame = frame;
        Channel = selectedChannel ?? "Combined";
        if (Channel != "Combined" && Channel != "Left" && Channel != "Right" && Channel != "Mid" && Channel != "Side")
            throw new ArgumentException("Unknown measured channel.", nameof(selectedChannel));
        if (sevenBandPower != null && sevenBandPower.Count != SevenBandAnalyzer.BandCount)
            throw new ArgumentException("Expected seven measured band powers.", nameof(sevenBandPower));
        if (selectedPower != null && (frame == null || selectedPower.Count != frame.Plan.Count))
            throw new ArgumentException("Selected power must match the displayed frame's plan.", nameof(selectedPower));
        SevenPower = ReadOnlyCopy(sevenBandPower); SelectedPower = ReadOnlyCopy(selectedPower);
        SourceDeviceId = sourceDeviceId;
        var valid = frame == null ? 0 : (int)Math.Min(frame.FrameCount, frame.EndFrame - frame.FirstValidFrame);
        if (valid <= 0) return; // A saved measurement cannot establish PCM silence.
        HasPcm = true; LeftSilent = RightSilent = MidSilent = SideSilent = true;
        for (var f = frame.FrameCount - valid; f < frame.FrameCount; f++)
        {
            var left = Clean(frame.Samples[f * frame.Channels]);
            var right = frame.Channels > 1 ? Clean(frame.Samples[f * frame.Channels + 1]) : 0;
            LeftSilent &= left == 0; RightSilent &= right == 0;
            MidSilent &= frame.Channels > 1 ? left + right == 0 : left == 0;
            SideSilent &= left - right == 0;
        }
    }
    internal AnalysisFrame Frame { get; }
    internal string Channel { get; }
    internal string SourceDeviceId { get; }
    internal IReadOnlyList<double> SevenPower { get; }
    internal IReadOnlyList<double> SelectedPower { get; }
    private bool HasPcm { get; }
    private bool LeftSilent { get; }
    private bool RightSilent { get; }
    private bool MidSilent { get; }
    private bool SideSilent { get; }
    internal bool IsSilent(string channel) => HasPcm && (channel == "Left" ? LeftSilent : channel == "Right" ? RightSilent
        : channel == "Mid" ? MidSilent : channel == "Side" ? SideSilent : LeftSilent && RightSilent);
    private static double Clean(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0 : value;
    private static IReadOnlyList<double> ReadOnlyCopy(IReadOnlyList<double> values)
    {
        if (values == null) return null;
        var result = new double[values.Count];
        for (var i = 0; i < result.Length; i++) result[i] = values[i];
        return Array.AsReadOnly(result);
    }
}

internal sealed class MainSpectrumReadout
{
    public int BandNumber { get; internal set; }
    public double LowerHz { get; internal set; } = double.NaN;
    public double CenterHz { get; internal set; } = double.NaN;
    public double UpperHz { get; internal set; } = double.NaN;
    public double Power { get; internal set; } = double.NaN;
    public double LevelDb { get; internal set; } = double.NaN;
    public double LeftDb { get; internal set; } = double.NaN;
    public double RightDb { get; internal set; } = double.NaN;
    public string Channel { get; internal set; }
    public string Units => "dBFS (full-scale sine)";
    public bool IsBandApplicable { get; internal set; }
    public bool IsBelowFloor { get; internal set; }
    public bool IsDigitalSilence { get; internal set; }
    public bool IsUnavailable { get; internal set; }
    public bool IsDisplayFrozen { get; internal set; }
    public bool SourceChanged { get; internal set; }
    public string Status { get; internal set; }
    public string Purpose { get; internal set; }
    public string CaptureText { get; internal set; }
    public string Text { get; internal set; }
}

internal static class MainSpectrumInspector
{
    internal static MainSpectrumReadout Inspect(MainSpectrumSnapshot snapshot, int selectedBand, string mode,
        CaptureStatus currentSourceStatus, bool displayFrozen = false, double displayFloorDb = LevelScale.FloorDb,
        string selectedChannel = null)
    {
        if (double.IsNaN(displayFloorDb) || double.IsInfinity(displayFloorDb))
            throw new ArgumentOutOfRangeException(nameof(displayFloorDb));
        var channel = selectedChannel ?? snapshot?.Channel ?? "Combined";
        if (channel != "Combined" && channel != "Left" && channel != "Right" && channel != "Mid" && channel != "Side")
            throw new ArgumentException("Unknown measured channel.", nameof(selectedChannel));
        var geometry = GetGeometry(mode);
        var frame = snapshot?.Frame;
        var changed = frame != null && currentSourceStatus != null &&
            ((!string.IsNullOrEmpty(snapshot.SourceDeviceId) && !string.IsNullOrEmpty(currentSourceStatus.DeviceId) &&
              !string.Equals(snapshot.SourceDeviceId, currentSourceStatus.DeviceId, StringComparison.OrdinalIgnoreCase)) ||
             (currentSourceStatus.SampleRate > 0 && currentSourceStatus.SampleRate != frame.SampleRate) ||
             (currentSourceStatus.Channels > 0 && currentSourceStatus.Channels != frame.Channels));
        var result = new MainSpectrumReadout
        {
            Channel = channel, IsBandApplicable = geometry != MainSpectrumGeometry.NotApplicable,
            IsDisplayFrozen = displayFrozen, SourceChanged = changed, CaptureText = CaptureText(currentSourceStatus),
            Purpose = Purpose(mode)
        };
        if (!result.IsBandApplicable) return Finish(result, "Mode purpose", false, result.Purpose);
        if (frame == null) return Finish(result, "No displayed measurement", true);
        // Current capture health must not invalidate an intentionally retained frozen frame.
        var healthy = currentSourceStatus != null && (currentSourceStatus.State == CaptureState.Capturing || currentSourceStatus.State == CaptureState.Silent);
        if (!displayFrozen && (!healthy || changed)) return Finish(result, changed ? "Capture source changed" : "Audio unavailable", true);
        var seven = geometry == MainSpectrumGeometry.SevenBandCards;
        var count = seven ? SevenBandAnalyzer.BandCount : frame.Plan.Count;
        if (selectedBand < 0 || selectedBand >= count) return Finish(result, "Point to a measured band", false);
        result.BandNumber = selectedBand + 1;
        if (seven)
        {
            var band = SevenBandAnalyzer.Definitions[selectedBand];
            result.LowerHz = band.LowerHz; result.UpperHz = Math.Min(band.UpperHz, frame.SampleRate / 2d);
            if (result.LowerHz >= result.UpperHz)
            { result.UpperHz = double.NaN; return Finish(result, "Above Nyquist · unavailable", true); }
            result.CenterHz = Math.Sqrt(result.LowerHz * result.UpperHz);
            if (snapshot.SevenPower == null) return Finish(result, "Seven-band measurement unavailable", true);
            // Existing seven-band payload is the combined front-channel analysis only.
            if (channel != "Combined") return Finish(result, "Selected seven-band channel unavailable", true);
            result.Power = snapshot.SevenPower[selectedBand];
        }
        else
        {
            var band = frame.Plan[selectedBand];
            result.LowerHz = band.LowerHz; result.CenterHz = band.CenterHz; result.UpperHz = band.UpperHz;
            result.LeftDb = Db(frame.LeftPower[selectedBand]);
            result.RightDb = frame.Channels > 1 ? Db(frame.RightPower[selectedBand]) : double.NaN;
            if (frame.Channels < 2 && (channel == "Right" || channel == "Side"))
                return Finish(result, "Channel unavailable · mono input", true);
            // Independent L/R powers omit phase, so cannot determine Mid/Side band power.
            var hasSelectedPower = snapshot.SelectedPower != null && snapshot.Channel == channel;
            if ((channel == "Mid" || channel == "Side") && !hasSelectedPower)
                return Finish(result, "Selected-channel band measurement unavailable", true);
            result.Power = hasSelectedPower ? snapshot.SelectedPower[selectedBand]
                : channel == "Left" ? frame.LeftPower[selectedBand]
                : channel == "Right" ? frame.RightPower[selectedBand] : frame.Power[selectedBand];
        }
        if (double.IsNaN(result.Power) || double.IsInfinity(result.Power) || result.Power < 0)
            return Finish(result, "Band measurement unavailable", true);
        result.LevelDb = Db(result.Power);
        result.IsDigitalSilence = result.Power == 0 && snapshot.IsSilent(channel);
        result.IsBelowFloor = result.Power > 0 && result.LevelDb < displayFloorDb;
        var state = result.IsDigitalSilence ? "Digital silence" : result.Power == 0 ? "Zero band power"
            : result.IsBelowFloor ? "Below display floor" : "Measured";
        var value = result.Power == 0 ? "−∞ dBFS" : result.LevelDb.ToString("0.00", CultureInfo.CurrentCulture) + " dBFS";
        var measurement = mode == "Stereo Compare" && channel == "Combined"
            ? "L " + FormatDb(result.LeftDb) + " / R " + FormatDb(result.RightDb)
            : result.Channel + " · " + value;
        return Finish(result, state, false, $"Band {result.BandNumber} · {result.LowerHz:N1}–{result.UpperHz:N1} Hz · {measurement} · {state}");
    }

    private static MainSpectrumReadout Finish(MainSpectrumReadout result, string state, bool unavailable, string text = null)
    {
        result.Status = state; result.IsUnavailable = unavailable;
        result.Text = (result.IsDisplayFrozen ? "Display frozen · " : "") + (text ?? state) +
            (result.SourceChanged ? " · current capture source differs" : "");
        return result;
    }
    private static double Db(double power) => power > 0 && !double.IsInfinity(power) ? 10 * Math.Log10(power)
        : power == 0 ? double.NegativeInfinity : double.NaN;
    private static string FormatDb(double value) => double.IsNaN(value) ? "unavailable"
        : double.IsNegativeInfinity(value) ? "−∞ dBFS" : value.ToString("0.00", CultureInfo.CurrentCulture) + " dBFS";
    internal static string CaptureText(CaptureStatus status) => status == null ? "Capture status unavailable"
        : status.State == CaptureState.Capturing ? "Capture active"
        : status.State == CaptureState.Silent ? "Capture active · digital silence"
        : status.State == CaptureState.Starting ? "Capture starting"
        : status.State == CaptureState.Recovering ? "Capture recovering"
        : status.State == CaptureState.Stopped ? "Capture stopped" : "Capture unavailable";

    internal static MainSpectrumGeometry GetGeometry(string mode) => mode switch
    {
        "Spectrum" or "Bricks" or "LED" => MainSpectrumGeometry.LegacyBars,
        "Contour" or "Peak Trace" or "Threshold Monitor" or "Overview" or "Stereo Compare" => MainSpectrumGeometry.LogFrequency,
        "Radial Spectrum" => MainSpectrumGeometry.RadialBands,
        "Band Matrix" => MainSpectrumGeometry.MatrixCards,
        "Advanced Spectral Balance" => MainSpectrumGeometry.SevenBandCards,
        "Stereo Spectrum" => MainSpectrumGeometry.StereoPanels,
        _ => MainSpectrumGeometry.NotApplicable
    };

    // Exact edges work for arbitrary imported plans as well as the logarithmic plan.
    // A shared edge belongs to the band on its right; the final upper edge is included.
    internal static int FindBandAtFrequency(BandPlan plan, double frequencyHz)
    {
        if (plan == null || double.IsNaN(frequencyHz) || frequencyHz < plan.MinHz || frequencyHz > plan.MaxHz) return -1;
        for (var i = 0; i < plan.Count; i++) if (frequencyHz < plan[i].UpperHz || i == plan.Count - 1) return i;
        return -1;
    }

    internal static string Purpose(string mode) => mode switch
    {
        "Oscilloscope" => "Front-left PCM waveform · time and FS amplitude; frequency-band inspection does not apply.",
        "Stereo Levels" => "Front-channel RMS and sample peaks · amplitude-relative dBFS; no frequency-band selection.",
        "Dynamics History" => "RMS and sample-peak history · missing captured audio remains a gap.",
        "Waterfall" or "Spectral History" => "Frequency intensity over received display frames · hover cannot identify a current measured band in this history.",
        "Octave Waterfall" => "Grouped frequency history reconstructed from quantized display levels.",
        "Octave Spectrum" => "Octave groups estimated from quantized display power · not an individual raw FFT band.",
        "Spectral Balance" => "Relative bass, mid and high estimates from quantized display power.",
        "Level Change" => "Signed frame-to-frame display-level change · not an absolute band-level measurement.",
        "Advanced Spectral Balance" => "Seven measured front-channel bands · full-scale-sine-relative dBFS.",
        "Stereo Spectrum" or "Stereo Compare" => "Independent front-left and front-right band powers · full-scale-sine-relative dBFS.",
        "Phase" or "Stereo Phase" => "Stereo phase relationship · frequency-band inspection does not apply.",
        _ => GetGeometry(mode) != MainSpectrumGeometry.NotApplicable ? "Measured frequency-band power · full-scale-sine-relative dBFS."
            : "No individual frequency-band measurement applies to this view."
    };
}
