// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;

namespace Spectrum.Dsp;

// Snapshot of what one band is doing internally, mostly for debugging/diagnostic views
internal sealed class BandDiagnostic
{
    public int Index { get; set; }
    public double LowerHz { get; set; }
    public double CenterHz { get; set; }
    public double UpperHz { get; set; }
    public int WindowLength { get; set; }
    public int FftLength { get; set; }
    public double ResolutionHz { get; set; }
    public double BinWidthHz { get; set; }
    public int FirstBin { get; set; }
    public int LastBin { get; set; }
    public double[] Weights { get; set; }
    public double RelativePower { get; set; }
    public double LevelDb { get; set; }
    public double Normalized { get; set; }
}

// FFT based band analyzer: picks a window size per band, sums bin power into bands, gives power relative to a full-scale sine
internal sealed class BandSpectrumAnalyzer
{
    // Each band needs at least this many FFT bins across it or it gets a longer window
    public const double MinBinsPerBand = 4.0;

    // A full-scale sine has mean power 0.5, so dividing by this makes 1.0 == 0 dBFS
    public const double FullScaleSinePower = 0.5;

    public static readonly IReadOnlyList<int> ReferenceWindowLengths = new[] { 4096, 8192, 16384 };

    public const int ReferenceSampleRate = 48000;

    // Zero padding is capped so the FFT doesn't get silly big
    public const int MaxZeroPadding = 4;

    private readonly Resolution[] _resolutions;
    private readonly int[] _bandResolution;
    private readonly int[] _bandFirstBin;
    private readonly double[][] _bandWeights;
    private readonly double[] _lastRelativePower;

    // Does all the one-time planning here: validates windows, assigns each band a resolution, precomputes bin weights
    public BandSpectrumAnalyzer(BandPlan plan, int channels, IReadOnlyList<int> windowLengths = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (channels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "At least one channel is required.");
        }

        Channels = channels;
        // Only stereo is really analysed; extra channels are just skipped over in the interleaved data
        AnalysedChannels = Math.Min(channels, 2);

        var lengths = new List<int>(windowLengths ?? DefaultWindowLengths(plan.SampleRate));
        if (lengths.Count == 0)
        {
            throw new ArgumentException("At least one window length is required.", nameof(windowLengths));
        }

        foreach (var length in lengths)
        {
            // Power of two check via the classic x & (x-1) trick
            if (length < 4 || (length & (length - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(windowLengths), length, "Window lengths must be powers of two >= 4.");
            }
        }

        lengths.Sort();

        var fs = plan.SampleRate;
        _bandResolution = new int[plan.Count];
        _bandFirstBin = new int[plan.Count];
        _bandWeights = new double[plan.Count][];
        _lastRelativePower = new double[plan.Count];

        // Pick the shortest window that still gives the band enough bins; fall back to the longest
        var used = new bool[lengths.Count];
        for (var b = 0; b < plan.Count; b++)
        {
            var r = lengths.Count - 1;
            for (var i = 0; i < lengths.Count; i++)
            {
                if (plan[b].WidthHz >= MinBinsPerBand * fs / lengths[i])
                {
                    r = i;
                    break;
                }
            }

            _bandResolution[b] = r;
            used[r] = true;
        }

        // Build one Resolution per window length, zero-padding when the narrowest band is thinner than a bin
        _resolutions = new Resolution[lengths.Count];
        for (var i = 0; i < lengths.Count; i++)
        {
            var narrowest = double.MaxValue;
            for (var b = 0; b < plan.Count; b++)
            {
                if (_bandResolution[b] == i)
                {
                    narrowest = Math.Min(narrowest, plan[b].WidthHz);
                }
            }

            var padding = 1;
            while (padding < MaxZeroPadding && (double)fs / (lengths[i] * padding) > narrowest)
            {
                padding *= 2;
            }

            _resolutions[i] = new Resolution(lengths[i], lengths[i] * padding, used[i]);
        }

        for (var b = 0; b < plan.Count; b++)
        {
            BuildWeights(b, plan[b], _resolutions[_bandResolution[b]].FftLength, fs);
        }

        // Caller must hand us at least the longest window's worth of frames
        RequiredFrames = lengths[lengths.Count - 1];
    }

    public BandPlan Plan { get; }
    public int Channels { get; }
    public int AnalysedChannels { get; }

    public int RequiredFrames { get; }

    // Window length a given band ended up using
    public int GetWindowLength(int band) => _resolutions[_bandResolution[band]].WindowLength;

    // Scale the 48k reference windows by the nearest power of two so time resolution stays similar at other rates
    public static int[] DefaultWindowLengths(int sampleRate)
    {
        var scale = Math.Pow(2, Math.Round(Math.Log((double)sampleRate / ReferenceSampleRate, 2)));
        var lengths = new int[ReferenceWindowLengths.Count];
        for (var i = 0; i < lengths.Length; i++)
        {
            lengths[i] = Math.Max(256, (int)(ReferenceWindowLengths[i] * scale));
        }

        return lengths;
    }

    // Mix-only overload for callers that don't care about per-channel levels
    public void Analyze(float[] interleaved, int frameCount, double[] relativeBandPower)
        => Analyze(interleaved, frameCount, relativeBandPower, null, null);

    // Main entry point: FFT the newest frames and fill per-band power (and optionally left/right)
    public void Analyze(float[] interleaved, int frameCount, double[] relativeBandPower,
        double[] leftRelativeBandPower, double[] rightRelativeBandPower)
    {
        if (interleaved == null)
        {
            throw new ArgumentNullException(nameof(interleaved));
        }

        if (relativeBandPower == null || relativeBandPower.Length < Plan.Count)
        {
            throw new ArgumentException("Output must hold one value per band.", nameof(relativeBandPower));
        }

        if (leftRelativeBandPower != null && leftRelativeBandPower.Length < Plan.Count)
            throw new ArgumentException("Output must hold one value per band.", nameof(leftRelativeBandPower));
        if (rightRelativeBandPower != null && rightRelativeBandPower.Length < Plan.Count)
            throw new ArgumentException("Output must hold one value per band.", nameof(rightRelativeBandPower));
        // Output buffers can't alias each other or we'd overwrite results mid-loop
        if (leftRelativeBandPower != null && ReferenceEquals(relativeBandPower, leftRelativeBandPower))
            throw new ArgumentException("Output buffers must be distinct.", nameof(leftRelativeBandPower));
        if (rightRelativeBandPower != null && (ReferenceEquals(relativeBandPower, rightRelativeBandPower) ||
            ReferenceEquals(leftRelativeBandPower, rightRelativeBandPower)))
            throw new ArgumentException("Output buffers must be distinct.", nameof(rightRelativeBandPower));

        if (frameCount < RequiredFrames || (long)frameCount * Channels > interleaved.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "Not enough frames for the longest FFT.");
        }

        // Skip FFTs for window sizes no band actually uses
        for (var r = 0; r < _resolutions.Length; r++)
        {
            if (_resolutions[r].InUse)
            {
                ComputeBinPower(_resolutions[r], interleaved, frameCount,
                    leftRelativeBandPower != null || rightRelativeBandPower != null);
            }
        }

        for (var b = 0; b < Plan.Count; b++)
        {
            var power = _resolutions[_bandResolution[b]].Power;
            var weights = _bandWeights[b];
            var first = _bandFirstBin[b];

            // Band power = weighted sum of bin power; weights handle bins only partly inside the band
            var sum = 0.0;
            for (var i = 0; i < weights.Length; i++)
            {
                sum += weights[i] * power[first + i];
            }

            var relative = sum / FullScaleSinePower;
            _lastRelativePower[b] = relative;
            relativeBandPower[b] = relative;

            if (AnalysedChannels == 1)
            {
                if (leftRelativeBandPower != null) leftRelativeBandPower[b] = relative;
                if (rightRelativeBandPower != null) rightRelativeBandPower[b] = 0;
            }
            else if (leftRelativeBandPower != null || rightRelativeBandPower != null)
            {
                var resolution = _resolutions[_bandResolution[b]];
                double leftSum = 0, rightSum = 0;
                for (var i = 0; i < weights.Length; i++)
                {
                    if (leftRelativeBandPower != null) leftSum += weights[i] * resolution.LeftPower[first + i];
                    if (rightRelativeBandPower != null) rightSum += weights[i] * resolution.RightPower[first + i];
                }
                if (leftRelativeBandPower != null) leftRelativeBandPower[b] = leftSum / FullScaleSinePower;
                if (rightRelativeBandPower != null) rightRelativeBandPower[b] = rightSum / FullScaleSinePower;
            }
        }
    }

    // Copies the numbers out for display; weights are cloned so callers can't mutate our state
    public BandDiagnostic GetDiagnostic(int band)
    {
        var res = _resolutions[_bandResolution[band]];
        var weights = (double[])_bandWeights[band].Clone();
        var db = LevelScale.PowerToDb(_lastRelativePower[band]);
        return new BandDiagnostic
        {
            Index = band,
            LowerHz = Plan[band].LowerHz,
            CenterHz = Plan[band].CenterHz,
            UpperHz = Plan[band].UpperHz,
            WindowLength = res.WindowLength,
            FftLength = res.FftLength,
            ResolutionHz = (double)Plan.SampleRate / res.WindowLength,
            BinWidthHz = (double)Plan.SampleRate / res.FftLength,
            FirstBin = _bandFirstBin[band],
            LastBin = _bandFirstBin[band] + weights.Length - 1,
            Weights = weights,
            RelativePower = _lastRelativePower[band],
            LevelDb = db,
            Normalized = LevelScale.Normalize(db),
        };
    }

    // For each band works out which FFT bins it covers and how much of each edge bin counts (fractional overlap)
    private void BuildWeights(int band, FrequencyBand fb, int n, int fs)
    {
        var df = (double)fs / n;
        var half = n / 2;

        var first = Math.Max(1, (int)Math.Floor((fb.LowerHz / df) + 0.5));
        var last = Math.Min(half, (int)Math.Ceiling((fb.UpperHz / df) - 0.5));
        // Always take at least one bin, even for a band narrower than a bin
        if (last < first)
        {
            last = first;
        }

        var weights = new double[last - first + 1];
        for (var k = first; k <= last; k++)
        {
            var binLo = (k - 0.5) * df;
            // Nyquist bin is only half a bin wide, hence the special case
            var binHi = k == half ? k * df : (k + 0.5) * df;
            var overlap = Math.Min(fb.UpperHz, binHi) - Math.Max(fb.LowerHz, binLo);
            weights[k - first] = overlap > 0 ? overlap / (binHi - binLo) : 0.0;
        }

        _bandFirstBin[band] = first;
        _bandWeights[band] = weights;
    }

    // Windows the newest samples, runs the FFT and converts to per-bin power (stereo packed into one complex FFT)
    private void ComputeBinPower(Resolution res, float[] interleaved, int frameCount, bool independentChannels)
    {
        var n = res.FftLength;
        var ch = Channels;
        var start = frameCount - res.WindowLength;
        var window = res.Window;
        var re = res.Re;
        var im = res.Im;

        // Left goes in the real part, right in the imaginary part so one FFT does both channels
        LoadWindowed(interleaved, start, ch, 0, window, res.WindowSum, re);
        if (AnalysedChannels == 2)
        {
            LoadWindowed(interleaved, start, ch, 1, window, res.WindowSum, im);
        }
        else
        {
            Array.Clear(im, 0, n);
        }

        // Zero padding past the window gives finer bin spacing without changing the window itself
        if (n > res.WindowLength)
        {
            Array.Clear(re, res.WindowLength, n - res.WindowLength);
            Array.Clear(im, res.WindowLength, n - res.WindowLength);
        }

        res.Fft.Forward(re, im);

        var power = res.Power;
        // Normalise by window energy so a full-scale sine reads the same whatever the window/FFT size
        var scale = 1.0 / (n * res.WindowSumOfSquares);
        var half = n / 2;
        power[0] = 0.0;
        res.LeftPower[0] = res.RightPower[0] = 0.0;

        if (AnalysedChannels == 2)
        {
            for (var k = 1; k <= half; k++)
            {
                // Split the two channels back apart using the conjugate symmetry of a real-input FFT
                var a = re[k];
                var bIm = im[k];
                var c = re[n - k];
                var d = im[n - k];

                var left = (((a + c) * (a + c)) + ((bIm - d) * (bIm - d))) * 0.25;
                var right = (((bIm + d) * (bIm + d)) + ((a - c) * (a - c))) * 0.25;

                // Fold negative freqs into positive (x2) except Nyquist, which has no mirror
                var oneSided = k == half ? 1.0 : 2.0;
                power[k] = oneSided * 0.5 * (left + right) * scale;
                if (independentChannels)
                {
                    res.LeftPower[k] = oneSided * left * scale;
                    res.RightPower[k] = oneSided * right * scale;
                }
            }
        }
        else
        {
            for (var k = 1; k <= half; k++)
            {
                var oneSided = k == half ? 1.0 : 2.0;
                power[k] = oneSided * ((re[k] * re[k]) + (im[k] * im[k])) * scale;
            }
        }
    }

    // Pulls one channel out of the interleaved buffer, scrubs NaN/Inf, removes DC and applies the window
    private static void LoadWindowed(float[] interleaved, int startFrame, int channels, int channel, double[] window, double windowSum, double[] dest)
    {
        var n = window.Length;
        var idx = (startFrame * channels) + channel;

        var weightedSum = 0.0;
        for (var i = 0; i < n; i++, idx += channels)
        {
            var s = interleaved[idx];
            var x = float.IsNaN(s) || float.IsInfinity(s) ? 0.0 : s;
            dest[i] = x;
            weightedSum += window[i] * x;
        }

        // Window-weighted mean is subtracted so DC offset doesn't leak into the lowest band
        var mean = weightedSum / windowSum;
        for (var i = 0; i < n; i++)
        {
            dest[i] = (dest[i] - mean) * window[i];
        }
    }

    // Per window-size scratch buffers plus the precomputed Hann window, allocated once so Analyze doesn't allocate
    private sealed class Resolution
    {
        public Resolution(int length, int fftLength, bool inUse)
        {
            WindowLength = length;
            FftLength = fftLength;
            InUse = inUse;
            Fft = new RadixTwoFft(fftLength);
            Window = new double[length];
            Re = new double[fftLength];
            Im = new double[fftLength];
            Power = new double[(fftLength / 2) + 1];
            LeftPower = new double[Power.Length];
            RightPower = new double[Power.Length];

            for (var i = 0; i < length; i++)
            {
                // Hann window; sums are kept for DC removal and power normalisation
                var w = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / length));
                Window[i] = w;
                WindowSum += w;
                WindowSumOfSquares += w * w;
            }
        }

        public int WindowLength { get; }
        public int FftLength { get; }
        public bool InUse { get; }
        public RadixTwoFft Fft { get; }
        public double[] Window { get; }
        public double WindowSum { get; }
        public double WindowSumOfSquares { get; }
        public double[] Re { get; }
        public double[] Im { get; }
        public double[] Power { get; }
        public double[] LeftPower { get; }
        public double[] RightPower { get; }
    }
}
