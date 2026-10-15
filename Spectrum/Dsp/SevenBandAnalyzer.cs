// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;

namespace Spectrum.Dsp;

// Simple immutable description of one of the classic mix-engineer frequency bands
internal readonly struct SevenBandDefinition
{
    public SevenBandDefinition(string name, string rangeLabel, double lowerHz, double upperHz)
    {
        Name = name;
        RangeLabel = rangeLabel;
        LowerHz = lowerHz;
        UpperHz = upperHz;
    }

    public string Name { get; }
    public string RangeLabel { get; }
    public double LowerHz { get; }
    public double UpperHz { get; }
}

// Wraps the generic band engine with fixed 7 bands (sub-bass to brilliance) for the simple view
internal sealed class SevenBandAnalyzer
{
    public const int BandCount = 7;

    // Standard band edges; the last one tops out at 20 kHz
    private static readonly SevenBandDefinition[] s_definitions =
    {
        new("Sub-Bass", "20–60 Hz", 20, 60),
        new("Bass", "60–250 Hz", 60, 250),
        new("Low-Mids", "250–500 Hz", 250, 500),
        new("Center Mids", "500 Hz–2 kHz", 500, 2000),
        new("Presence", "2–6 kHz", 2000, 6000),
        new("Treble", "6–10 kHz", 6000, 10000),
        new("Brilliance", "10–20 kHz", 10000, 20000),
    };

    private readonly BandSpectrumAnalyzer _engine;
    private readonly double[] _enginePower;
    private readonly int _activeBands;

    // Builds the engine, dropping/trimming bands that sit above Nyquist for low sample rates
    public SevenBandAnalyzer(int sampleRate, int channels)
    {
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var nyquist = sampleRate / 2.0;
        var edges = new List<double> { s_definitions[0].LowerHz };
        foreach (var band in s_definitions)
        {
            // Band starts past Nyquist, so nothing useful left to measure
            if (band.LowerHz >= nyquist) break;
            edges.Add(Math.Min(band.UpperHz, nyquist));
        }

        _activeBands = edges.Count - 1;
        if (_activeBands < 1)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate cannot represent any analysis band.");

        var plan = BandPlan.CreateFromEdges(edges.ToArray(), sampleRate);
        // Only one window size is used here, picking the second default as a decent time/frequency tradeoff
        var defaults = BandSpectrumAnalyzer.DefaultWindowLengths(sampleRate);
        _engine = new BandSpectrumAnalyzer(plan, channels, new[] { defaults[1] });
        _enginePower = new double[_activeBands];
        SampleRate = sampleRate;
    }

    // Handy pass-throughs so callers don't need to reach into the engine
    public static IReadOnlyList<SevenBandDefinition> Definitions => s_definitions;
    public int SampleRate { get; }
    public int RequiredFrames => _engine.RequiredFrames;
    public int WindowLength => _engine.GetWindowLength(0);
    public BandPlan Plan => _engine.Plan;

    // Always fills 7 outputs; bands the sample rate can't reach come back as NaN
    public void Analyze(float[] interleaved, int frameCount, double[] relativePower)
    {
        if (relativePower == null || relativePower.Length < BandCount)
            throw new ArgumentException("Output must hold seven values.", nameof(relativePower));
        _engine.Analyze(interleaved, frameCount, _enginePower);
        for (var band = 0; band < BandCount; band++)
            relativePower[band] = band < _activeBands ? _enginePower[band] : double.NaN;
    }
}
