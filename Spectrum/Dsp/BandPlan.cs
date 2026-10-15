// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;

namespace Spectrum.Dsp;

internal readonly struct FrequencyBand
{
    public FrequencyBand(double lowerHz, double centerHz, double upperHz)
    {
        LowerHz = lowerHz;
        CenterHz = centerHz;
        UpperHz = upperHz;
    }

    public double LowerHz { get; }
    public double CenterHz { get; }
    public double UpperHz { get; }
    public double WidthHz => UpperHz - LowerHz;
}

internal sealed class BandPlan
{
    private readonly FrequencyBand[] _bands;

    private BandPlan(FrequencyBand[] bands, double ratio, int sampleRate)
    {
        _bands = bands;
        BandRatio = ratio;
        SampleRate = sampleRate;
    }

    public IReadOnlyList<FrequencyBand> Bands => _bands;
    public int Count => _bands.Length;
    public double BandRatio { get; }
    public int SampleRate { get; }
    public double MinHz => _bands[0].LowerHz;
    public double MaxHz => _bands[_bands.Length - 1].UpperHz;

    public FrequencyBand this[int index] => _bands[index];

    // Log spacing matches how we hear pitch, so low bands aren't squashed into one bar.
    public static BandPlan CreateLogarithmic(int count, double firstCenterHz, double lastCenterHz, int sampleRate)
    {
        if (count < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "At least two bands are required.");
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (!(firstCenterHz > 0) || !(lastCenterHz > firstCenterHz))
        {
            throw new ArgumentOutOfRangeException(nameof(lastCenterHz), "Require 0 < firstCenterHz < lastCenterHz.");
        }

        var nyquist = sampleRate / 2.0;
        var ratio = Math.Pow(lastCenterHz / firstCenterHz, 1.0 / (count - 1));

        if (lastCenterHz * Math.Sqrt(ratio) > nyquist)
        {
            var top = Math.Exp(((2.0 * (count - 1) * Math.Log(nyquist)) + Math.Log(firstCenterHz)) / ((2.0 * count) - 1));
            if (!(top > firstCenterHz))
            {
                throw new ArgumentException($"Sample rate {sampleRate} Hz cannot support bands starting at {firstCenterHz} Hz.");
            }

            ratio = Math.Pow(top / firstCenterHz, 1.0 / (count - 1));
        }

        var halfStep = Math.Sqrt(ratio);
        var bands = new FrequencyBand[count];
        var lower = firstCenterHz / halfStep;

        for (var i = 0; i < count; i++)
        {
            var center = firstCenterHz * Math.Pow(ratio, i);
            var upper = i == count - 1 ? Math.Min(center * halfStep, nyquist) : center * halfStep;
            bands[i] = new FrequencyBand(lower, center, upper);
            lower = upper;
        }

        return new BandPlan(bands, ratio, sampleRate);
    }

    public static BandPlan CreateFromEdges(double[] edgesHz, int sampleRate)
    {
        if (edgesHz == null) throw new ArgumentNullException(nameof(edgesHz));
        if (edgesHz.Length < 2) throw new ArgumentException("At least two edges are required.", nameof(edgesHz));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        if (!(edgesHz[0] > 0)) throw new ArgumentException("Edges must be positive.", nameof(edgesHz));
        for (var i = 1; i < edgesHz.Length; i++)
            if (!(edgesHz[i] > edgesHz[i - 1])) throw new ArgumentException("Edges must be strictly ascending.", nameof(edgesHz));
        if (edgesHz[edgesHz.Length - 1] > sampleRate / 2.0)
            throw new ArgumentException("The top edge exceeds Nyquist.", nameof(edgesHz));

        var bands = new FrequencyBand[edgesHz.Length - 1];
        for (var i = 0; i < bands.Length; i++)
            bands[i] = new FrequencyBand(edgesHz[i], Math.Sqrt(edgesHz[i] * edgesHz[i + 1]), edgesHz[i + 1]);
        var ratio = Math.Pow(edgesHz[edgesHz.Length - 1] / edgesHz[0], 1.0 / bands.Length);
        return new BandPlan(bands, ratio, sampleRate);
    }

    public double FrequencyToPosition(double hz)
    {
        if (!(hz > 0))
        {
            return 0;
        }

        return Math.Log(hz / MinHz) / Math.Log(BandRatio);
    }
}
