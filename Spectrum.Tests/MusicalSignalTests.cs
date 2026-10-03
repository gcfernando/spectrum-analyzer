using System;
using System.Linq;
using Spectrum.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace Spectrum.Tests;

/// <summary>Synthetic musical material checks harmonics, pitch motion, and rhythm in measurement output.</summary>
public class MusicalSignalTests
{
    private const int Fs = 48000;
    private const int Hop = 1200;
    private readonly ITestOutputHelper _out;

    public MusicalSignalTests(ITestOutputHelper output) => _out = output;

    private static int BarOf(BandPlan plan, double hz)
        => Enumerable.Range(0, plan.Count).First(i => plan[i].UpperHz > hz);

    [Fact]
    public void HarmonicTone_ShowsFundamentalAndResolvableOvertonesAsSeparatePeaks()
    {
        // A3 220 Hz with 1/k harmonics; bright string/brass tone at −12 dBFS.
        var engine = Signals.DefaultEngine(Fs);
        const double f0 = 220;
        var buf = Signals.Interleaved(engine.RequiredFrames, 2, (_, n) =>
            Enumerable.Range(1, 16).Sum(k => Signals.Sine(f0 * k, 0.25 / k, n, Fs)));
        var db = Signals.AnalyzeDb(engine, buf);

        var peaks = new double[7];
        for (var k = 1; k <= 6; k++)
        {
            var bar = BarOf(engine.Plan, f0 * k);
            peaks[k] = Math.Max(db[bar], Math.Max(db[bar - 1], db[bar + 1]));
            _out.WriteLine($"harmonic {k} ({f0 * k} Hz) -> bar {bar}: {db[bar]:F1} dB (neighbours {db[bar - 1]:F1} / {db[bar + 1]:F1})");

            // A 0.25/k partial is 20·log10(0.25/k); allow for edge-sharing.
            Assert.InRange(peaks[k], (20 * Math.Log10(0.25 / k)) - 3.2, (20 * Math.Log10(0.25 / k)) + 0.1);
        }

        for (var k = 1; k <= 5; k++)
        {
            // Consecutive harmonics should stay as separate peaks.
            var from = BarOf(engine.Plan, f0 * k) + 1;
            var to = BarOf(engine.Plan, f0 * (k + 1)) - 1;
            var dip = Enumerable.Range(from, to - from + 1).Min(i => db[i]);
            Assert.True(dip < Math.Min(peaks[k], peaks[k + 1]) - 3, $"harmonics {k} and {k + 1} merge (dip {dip:F1} dB)");
        }
    }

    [Fact]
    public void BassLine_PitchMovementMovesTheFundamentalBar()
    {
        // Chromatic bass walk E1→E2, three harmonics per note.
        var engine = Signals.DefaultEngine(Fs);
        var previous = -1;
        var bars = new int[13];
        for (var semitone = 0; semitone <= 12; semitone++)
        {
            var f = 41.2034 * Math.Pow(2, semitone / 12.0);
            var buf = Signals.Interleaved(engine.RequiredFrames, 2, (_, n) =>
                Signals.Sine(f, 0.5, n, Fs) + Signals.Sine(2 * f, 0.25, n, Fs) + Signals.Sine(3 * f, 0.12, n, Fs));
            var db = Signals.AnalyzeDb(engine, buf);

            // Strongest bar at or below 1.5f is the fundamental.
            var limit = BarOf(engine.Plan, 1.5 * f);
            var bar = Enumerable.Range(0, limit).OrderByDescending(i => db[i]).First();
            bars[semitone] = bar;

            Assert.True(bar >= previous, $"semitone {semitone}: bar went down {previous}→{bar}");
            var band = engine.Plan[bar];
            Assert.True(f >= band.LowerHz - (0.5 * engine.GetDiagnostic(bar).ResolutionHz) &&
                        f <= band.UpperHz + (0.5 * engine.GetDiagnostic(bar).ResolutionHz), $"{f:F1} Hz shown at bar {bar}");
            previous = bar;
        }

        _out.WriteLine("E1→E2 fundamental bars: " + string.Join(" ", bars));
        Assert.True(bars[12] - bars[0] >= 7, "an octave spans ~8.3 bars");
    }

    [Fact]
    public void KickDrum_EnergyLandsInTheBassBarsNotTheHighs()
    {
        // Kick: 120→45 Hz sweep, 90 ms decay, every 500 ms.
        var engine = Signals.DefaultEngine(Fs);
        double Kick(long n)
        {
            var t = (n % (Fs / 2)) / (double)Fs;
            var phase = 2 * Math.PI * ((45 * t) + (75 * 0.03 * (1 - Math.Exp(-t / 0.03))));
            return 0.7 * Math.Exp(-t / 0.09) * Math.Sin(phase);
        }

        var maxDb = new double[engine.Plan.Count];
        for (var i = 0; i < maxDb.Length; i++) maxDb[i] = double.NegativeInfinity;

        for (long end = engine.RequiredFrames; end < Fs * 2; end += Hop)
        {
            var start = end - engine.RequiredFrames;
            var db = Signals.AnalyzeDb(engine, Signals.Interleaved(engine.RequiredFrames, 2, (_, i) => Kick(start + i)));
            for (var i = 0; i < db.Length; i++) maxDb[i] = Math.Max(maxDb[i], db[i]);
        }

        var peakBar = Signals.ArgMax(maxDb);
        _out.WriteLine($"kick peak bar {peakBar} ({engine.Plan[peakBar].CenterHz:F0} Hz) at {maxDb[peakBar]:F1} dB; max above 2 kHz {maxDb.Skip(BarOf(engine.Plan, 2000)).Max():F1} dB");
        Assert.InRange(engine.Plan[peakBar].CenterHz, 40, 130);
        Assert.True(maxDb.Skip(BarOf(engine.Plan, 2000)).Max() < maxDb[peakBar] - 40, "kick should not light the highs");
    }

    [Fact]
    public void HiHat_EnergyLandsInTheHighBarsNotTheBass()
    {
        // Hi-hat: 40 ms high-passed noise bursts at 8/s.
        var engine = Signals.DefaultEngine(Fs);
        var white = Signals.WhiteNoise(Fs * 2, 0.3, 5);
        double Hat(long n)
        {
            if (n < 2) return 0;
            var t = (n % (Fs / 8)) / (double)Fs;
            return Math.Exp(-t / 0.04) * (white[n] - (2 * white[n - 1]) + white[n - 2]) * 0.25;
        }

        var sum = new double[engine.Plan.Count];
        for (long end = engine.RequiredFrames; end < Fs * 2; end += Hop)
        {
            var start = end - engine.RequiredFrames;
            var p = Signals.AnalyzePower(engine, Signals.Interleaved(engine.RequiredFrames, 2, (_, i) => Hat(start + i)));
            for (var i = 0; i < p.Length; i++) sum[i] += p[i];
        }

        var db = sum.Select(LevelScale.PowerToDb).ToArray();
        var high = db.Skip(BarOf(engine.Plan, 6000)).Average();
        var bass = db.Take(BarOf(engine.Plan, 200)).Average();
        _out.WriteLine($"hi-hat: mean bar level above 6 kHz {high:F1} dB, below 200 Hz {bass:F1} dB");
        Assert.True(high - bass > 40, "hi-hat must energize the high bars far more than the bass bars");
    }
}
