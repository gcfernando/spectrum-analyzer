// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;

namespace Spectrum.Dsp;

// Current chunk levels and session-held peaks/loudness. NaN means not yet available,
// rather than digital silence. The dBFS RMS reference is digital amplitude full scale.
internal sealed class AnalysisMeasurementResult
{
    public double LeftRmsDb { get; internal set; }
    public double RightRmsDb { get; internal set; }
    public double SamplePeakDb { get; internal set; }
    public double TruePeakDb { get; internal set; }
    public double Correlation { get; internal set; }
    public double MidRmsDb { get; internal set; }
    public double SideRmsDb { get; internal set; }
    public double MomentaryLufs { get; internal set; }
    public double ShortTermLufs { get; internal set; }
    public double IntegratedLufs { get; internal set; }
    public int ClippedSamples { get; internal set; }
    public bool HasStereo { get; internal set; }
    public bool LoudnessSupported { get; internal set; }
    public long TotalFrames { get; internal set; }
    public int GateBlocks { get; internal set; }
    public int TruePeakOversampling { get; internal set; }
    public double DurationSeconds { get; internal set; }
}

// Feed each captured PCM frame exactly once, in sample order. Overlapping FFT windows
// must be trimmed by the caller. This separate analysis path does not change legacy DSP.
//
// Loudness: ITU-R BS.1770-5 Annex 1, mono/stereo, two-stage K weighting, 400 ms
// gating blocks/100 ms hop, absolute -70 LUFS and relative -10 LU gates. Short-term
// and momentary use 3 s/400 ms ungated sample windows. Multichannel loudness is
// explicitly unavailable: the front pair is not a substitute for programme loudness.
//
// True peak: streaming polyphase FIR from Annex 2 (4 phases, 12 taps per phase).
// Extra cascaded interpolation stages ensure >=192 kHz at lower input sample rates.
// Values are estimates; synthetic regression tests do not constitute BS.2217 meter
// certification. Complete() flushes the FIR tail at an offline end of stream.
// Algorithm source: https://www.itu.int/rec/R-REC-BS.1770
internal sealed class AudioMeasurementAnalyzer
{
    public const double SilenceDb = -300.0;
    public const string TruePeakDescription = "Oversampled FIR true-peak estimate (ITU Annex 2 filter; not a certified meter)";
    private const double LoudnessOffset = -0.691;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _analyzedChannels;
    private readonly bool _loudnessSupported;
    private readonly int _momentaryFrames;
    private readonly int _shortTermFrames;
    private readonly int _hopFrames;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;
    private readonly double[] _momentaryEnergy;
    private readonly double[] _shortTermEnergy;
    private readonly List<double> _gatingEnergies = new();
    private readonly TruePeakInterpolator[] _truePeak;
    private readonly int _truePeakOversampling;
    private int _momentaryWrite;
    private int _shortTermWrite;
    private int _momentaryCount;
    private int _shortTermCount;
    private int _clippedSamples;
    private long _totalFrames;
    private double _momentarySum;
    private double _shortTermSum;
    private double _samplePeak;
    private double _truePeakValue;
    private double _integratedLufs = double.NaN;
    private bool _truePeakPending;
    private AnalysisMeasurementResult _last;

    public AudioMeasurementAnalyzer(int sampleRate, int channels)
    {
        if (sampleRate < 8000 || sampleRate > 384000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Use an 8–384 kHz sample rate.");
        if (channels < 1 || channels > 64) throw new ArgumentOutOfRangeException(nameof(channels));
        _sampleRate = sampleRate;
        _channels = channels;
        _analyzedChannels = Math.Min(channels, 2);
        _loudnessSupported = channels <= 2;
        _momentaryFrames = Math.Max(1, (int)Math.Round(sampleRate * .4));
        _shortTermFrames = sampleRate * 3;
        _hopFrames = Math.Max(1, (int)Math.Round(sampleRate * .1));
        _momentaryEnergy = new double[_momentaryFrames];
        _shortTermEnergy = new double[_shortTermFrames];
        _shelf = new Biquad[_analyzedChannels];
        _highPass = new Biquad[_analyzedChannels];
        _truePeak = new TruePeakInterpolator[_analyzedChannels];
        var stages = 1;
        _truePeakOversampling = 4;
        while ((long)sampleRate * _truePeakOversampling < 192000)
        {
            stages++;
            _truePeakOversampling *= 4;
        }
        for (var channel = 0; channel < _analyzedChannels; channel++)
        {
            // Original 48 kHz coefficients from BS.1770-5 Tables 1 and 2.
            _shelf[channel] = Biquad.AtSampleRate(sampleRate,
                1.53512485958697, -2.69169618940638, 1.19839281085285,
                -1.69065929318241, .73248077421585);
            _highPass[channel] = Biquad.AtSampleRate(sampleRate,
                1.0, -2.0, 1.0, -1.99004745483398, .99007225036621);
            _truePeak[channel] = new TruePeakInterpolator(stages);
        }
        _last = EmptyResult();
    }

    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    public AnalysisMeasurementResult Process(float[] interleaved, int frames)
    {
        if (interleaved == null) throw new ArgumentNullException(nameof(interleaved));
        if (frames < 0 || (long)frames * _channels > interleaved.Length)
            throw new ArgumentOutOfRangeException(nameof(frames));
        if (frames == 0) return _last;

        double leftSquare = 0, rightSquare = 0, cross = 0, midSquare = 0, sideSquare = 0;
        var hadGatingBlock = false;
        for (var frame = 0; frame < frames; frame++)
        {
            var left = Finite(interleaved[frame * _channels]);
            var right = _analyzedChannels == 2 ? Finite(interleaved[frame * _channels + 1]) : 0.0;
            leftSquare += left * left;
            rightSquare += right * right;
            cross += left * right;
            var mid = (left + right) * .5;
            var side = (left - right) * .5;
            midSquare += mid * mid;
            sideSquare += side * side;
            double weightedPower = 0;
            for (var channel = 0; channel < _analyzedChannels; channel++)
            {
                var sample = channel == 0 ? left : right;
                var magnitude = Math.Abs(sample);
                _samplePeak = Math.Max(_samplePeak, magnitude);
                if (magnitude >= 1.0 && _clippedSamples < int.MaxValue) _clippedSamples++;
                _truePeakValue = Math.Max(_truePeakValue, _truePeak[channel].Push(sample));
                if (_loudnessSupported)
                {
                    var weighted = _highPass[channel].Apply(_shelf[channel].Apply(sample));
                    weightedPower += weighted * weighted;
                }
            }
            _totalFrames++;
            if (_loudnessSupported) hadGatingBlock |= PushEnergy(weightedPower);
        }
        _truePeakPending = true;
        _truePeakValue = Math.Max(_truePeakValue, _samplePeak);
        if (hadGatingBlock) _integratedLufs = CalculateIntegrated();
        _last = new AnalysisMeasurementResult
        {
            LeftRmsDb = PowerDb(leftSquare / frames),
            RightRmsDb = _analyzedChannels == 2 ? PowerDb(rightSquare / frames) : double.NaN,
            SamplePeakDb = AmplitudeDb(_samplePeak),
            TruePeakDb = AmplitudeDb(_truePeakValue),
            Correlation = _analyzedChannels == 2 && leftSquare > 0 && rightSquare > 0
                ? Math.Max(-1, Math.Min(1, cross / Math.Sqrt(leftSquare * rightSquare))) : double.NaN,
            MidRmsDb = _analyzedChannels == 2 ? PowerDb(midSquare / frames) : double.NaN,
            SideRmsDb = _analyzedChannels == 2 ? PowerDb(sideSquare / frames) : double.NaN,
            MomentaryLufs = _loudnessSupported && _momentaryCount == _momentaryFrames
                ? Loudness(Math.Max(0, _momentarySum) / _momentaryFrames) : double.NaN,
            ShortTermLufs = _loudnessSupported && _shortTermCount == _shortTermFrames
                ? Loudness(Math.Max(0, _shortTermSum) / _shortTermFrames) : double.NaN,
            IntegratedLufs = _loudnessSupported ? _integratedLufs : double.NaN,
            ClippedSamples = _clippedSamples,
            HasStereo = _analyzedChannels == 2,
            LoudnessSupported = _loudnessSupported,
            TotalFrames = _totalFrames,
            GateBlocks = _gatingEnergies.Count,
            TruePeakOversampling = _truePeakOversampling,
            DurationSeconds = _totalFrames / (double)_sampleRate,
        };
        return _last;
    }

    // A finite audio file ends before the interpolation filter's delayed output does.
    // This does not add silence to loudness, RMS, duration or clipping statistics.
    public AnalysisMeasurementResult Complete()
    {
        if (!_truePeakPending) return _last;
        for (var channel = 0; channel < _analyzedChannels; channel++)
            for (var tail = 0; tail < 24; tail++)
                _truePeakValue = Math.Max(_truePeakValue, _truePeak[channel].Push(0));
        _truePeakPending = false;
        // Return a new result: previously returned measurements are stable snapshots.
        _last = CopyWithTruePeak(_last, AmplitudeDb(_truePeakValue));
        return _last;
    }

    public void Reset()
    {
        _momentaryWrite = _shortTermWrite = _momentaryCount = _shortTermCount = _clippedSamples = 0;
        _totalFrames = 0;
        _momentarySum = _shortTermSum = _samplePeak = _truePeakValue = 0;
        _integratedLufs = double.NaN;
        _truePeakPending = false;
        _gatingEnergies.Clear();
        Array.Clear(_momentaryEnergy, 0, _momentaryEnergy.Length);
        Array.Clear(_shortTermEnergy, 0, _shortTermEnergy.Length);
        for (var channel = 0; channel < _analyzedChannels; channel++)
        {
            _shelf[channel].Reset();
            _highPass[channel].Reset();
            _truePeak[channel].Reset();
        }
        _last = EmptyResult();
    }

    private bool PushEnergy(double energy)
    {
        _momentarySum += energy - _momentaryEnergy[_momentaryWrite];
        _momentaryEnergy[_momentaryWrite] = energy;
        if (++_momentaryWrite == _momentaryFrames) _momentaryWrite = 0;
        if (_momentaryCount < _momentaryFrames) _momentaryCount++;
        _shortTermSum += energy - _shortTermEnergy[_shortTermWrite];
        _shortTermEnergy[_shortTermWrite] = energy;
        if (++_shortTermWrite == _shortTermFrames) _shortTermWrite = 0;
        if (_shortTermCount < _shortTermFrames) _shortTermCount++;
        if (_momentaryCount < _momentaryFrames || (_totalFrames - _momentaryFrames) % _hopFrames != 0) return false;
        var blockPower = Math.Max(0, _momentarySum) / _momentaryFrames;
        // Below-absolute-gate blocks cannot contribute to either gate, so do not retain them.
        if (Loudness(blockPower) > -70.0) _gatingEnergies.Add(blockPower);
        return true;
    }

    private double CalculateIntegrated()
    {
        if (_gatingEnergies.Count == 0) return SilenceDb;
        double sum = 0;
        for (var i = 0; i < _gatingEnergies.Count; i++) sum += _gatingEnergies[i];
        var relativePowerThreshold = sum / _gatingEnergies.Count * .1;
        double includedSum = 0;
        var includedCount = 0;
        for (var i = 0; i < _gatingEnergies.Count; i++)
        {
            var energy = _gatingEnergies[i];
            if (energy <= relativePowerThreshold) continue;
            includedSum += energy;
            includedCount++;
        }
        return includedCount == 0 ? SilenceDb : Loudness(includedSum / includedCount);
    }

    private AnalysisMeasurementResult EmptyResult() => new()
    {
        LeftRmsDb = double.NaN, RightRmsDb = double.NaN, SamplePeakDb = double.NaN,
        TruePeakDb = double.NaN, Correlation = double.NaN, MidRmsDb = double.NaN,
        SideRmsDb = double.NaN, MomentaryLufs = double.NaN, ShortTermLufs = double.NaN,
        IntegratedLufs = double.NaN, HasStereo = _analyzedChannels == 2,
        LoudnessSupported = _loudnessSupported, TruePeakOversampling = _truePeakOversampling,
    };

    private static AnalysisMeasurementResult CopyWithTruePeak(AnalysisMeasurementResult source, double truePeakDb) => new()
    {
        LeftRmsDb = source.LeftRmsDb, RightRmsDb = source.RightRmsDb,
        SamplePeakDb = source.SamplePeakDb, TruePeakDb = truePeakDb,
        Correlation = source.Correlation, MidRmsDb = source.MidRmsDb, SideRmsDb = source.SideRmsDb,
        MomentaryLufs = source.MomentaryLufs, ShortTermLufs = source.ShortTermLufs,
        IntegratedLufs = source.IntegratedLufs, ClippedSamples = source.ClippedSamples,
        HasStereo = source.HasStereo, LoudnessSupported = source.LoudnessSupported,
        TotalFrames = source.TotalFrames, GateBlocks = source.GateBlocks,
        TruePeakOversampling = source.TruePeakOversampling, DurationSeconds = source.DurationSeconds,
    };

    private static double Finite(float sample) => float.IsNaN(sample) || float.IsInfinity(sample) ? 0.0 : sample;
    private static double PowerDb(double power) => power > 0 ? 10 * Math.Log10(power) : SilenceDb;
    private static double AmplitudeDb(double amplitude) => amplitude > 0 ? 20 * Math.Log10(amplitude) : SilenceDb;
    private static double Loudness(double power) => power > 0 ? LoudnessOffset + 10 * Math.Log10(power) : SilenceDb;

    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;
        private Biquad(double b0, double b1, double b2, double a1, double a2)
        { _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2; }

        public static Biquad AtSampleRate(int sampleRate, double b0, double b1, double b2, double a1, double a2)
        {
            if (sampleRate == 48000) return new Biquad(b0, b1, b2, a1, a2);
            // Invert the reference bilinear transform, then apply it at the new rate.
            // r=1 reproduces the reference coefficients; both stages retain the same
            // underlying analogue response, rather than using 48 kHz poles at all rates.
            var r = sampleRate / 48000.0;
            var r2 = r * r;
            var n2 = b0 - b1 + b2;
            var n1 = 2 * (b0 - b2);
            var n0 = b0 + b1 + b2;
            var d2 = 1 - a1 + a2;
            var d1 = 2 * (1 - a2);
            var d0 = 1 + a1 + a2;
            var denominator = d2 * r2 + d1 * r + d0;
            return new Biquad((n2 * r2 + n1 * r + n0) / denominator,
                (-2 * n2 * r2 + 2 * n0) / denominator,
                (n2 * r2 - n1 * r + n0) / denominator,
                (-2 * d2 * r2 + 2 * d0) / denominator,
                (d2 * r2 - d1 * r + d0) / denominator);
        }

        public double Apply(double sample)
        {
            var output = _b0 * sample + _z1;
            _z1 = _b1 * sample - _a1 * output + _z2;
            _z2 = _b2 * sample - _a2 * output;
            return output;
        }
        public void Reset() { _z1 = _z2 = 0; }
    }

    private sealed class TruePeakInterpolator
    {
        // Mathematical filter coefficients from ITU-R BS.1770-5 Annex 2.
        private static readonly double[][] Coefficients =
        {
            new[] { .0017089843750, .0109863281250, -.0196533203125, .0332031250000,
                -.0594482421875, .1373291015625, .9721679687500, -.1022949218750,
                .0476074218750, -.0266113281250, .0148925781250, -.0083007812500 },
            new[] { -.0291748046875, .0292968750000, -.0517578125000, .0891113281250,
                -.1665039062500, .4650878906250, .7797851562500, -.2003173828125,
                .1015625000000, -.0582275390625, .0330810546875, -.0189208984375 },
            new[] { -.0189208984375, .0330810546875, -.0582275390625, .1015625000000,
                -.2003173828125, .7797851562500, .4650878906250, -.1665039062500,
                .0891113281250, -.0517578125000, .0292968750000, -.0291748046875 },
            new[] { -.0083007812500, .0148925781250, -.0266113281250, .0476074218750,
                -.1022949218750, .9721679687500, .1373291015625, -.0594482421875,
                .0332031250000, -.0196533203125, .0109863281250, .0017089843750 },
        };
        private readonly double[] _history = new double[24];
        private readonly TruePeakInterpolator _next;
        private int _write;

        public TruePeakInterpolator(int stages) { if (stages > 1) _next = new TruePeakInterpolator(stages - 1); }
        public double Push(double sample)
        {
            _history[_write] = _history[_write + 12] = sample;
            double maximum = 0;
            for (var phase = 0; phase < 4; phase++)
            {
                var coefficients = Coefficients[phase];
                double output = 0;
                for (var tap = 0; tap < 12; tap++) output += coefficients[tap] * _history[_write + 12 - tap];
                maximum = Math.Max(maximum, _next == null ? Math.Abs(output) : _next.Push(output));
            }
            if (++_write == 12) _write = 0;
            return maximum;
        }
        public void Reset() { Array.Clear(_history, 0, _history.Length); _write = 0; _next?.Reset(); }
    }
}
