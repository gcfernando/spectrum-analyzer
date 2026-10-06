using System;
using System.Threading;
using System.Threading.Tasks;
using Spectrum.Dsp;
using Xunit;

namespace Spectrum.Tests;

public class SampleHistoryTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public SampleHistoryTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static float[] Ramp(long startFrame, int frames, int channels)
        => Signals.Interleaved(frames, channels, (c, n) => ((startFrame + n) * 10) + c);

    [Fact]
    public void ReturnsLatestFramesOldestFirst_AcrossTheWrap()
    {
        var h = new SampleHistory(2, 100);
        long written = 0;
        foreach (var chunk in new[] { 30, 45, 60, 17 })
        {
            h.Write(Ramp(written, chunk, 2), chunk);
            written += chunk;
        }

        var dest = new float[80 * 2];
        Assert.True(h.TryCopyLatest(dest, 80, out var end));
        Assert.Equal(written, end);
        for (var i = 0; i < 80; i++)
        {
            Assert.Equal(((written - 80 + i) * 10) + 0, dest[2 * i]);
            Assert.Equal(((written - 80 + i) * 10) + 1, dest[(2 * i) + 1]);
        }
    }

    [Fact]
    public void ZeroFillsFramesThatWereNeverWritten()
    {
        var h = new SampleHistory(1, 64);
        h.Write(Ramp(0, 10, 1), 10);
        var dest = new float[32];
        dest.FillWith(99);
        Assert.True(h.TryCopyLatest(dest, 32, out _));
        for (var i = 0; i < 22; i++)
        {
            Assert.Equal(0f, dest[i]);
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(i * 10f, dest[22 + i]);
        }
    }

    [Fact]
    public void FramesBeforeAGap_AreExcludedFromTheSnapshot()
    {
        // 50 frames before the pause; 10 frames after resume.
        var h = new SampleHistory(1, 64);
        h.Write(Ramp(0, 50, 1), 50);
        h.Write(Ramp(50, 10, 1), 10);
        var dest = new float[32];
        dest.FillWith(99);

        Assert.True(h.TryCopyLatest(dest, 32, out var end, discardBeforeFrame: 50));
        Assert.Equal(60, end);
        for (var i = 0; i < 22; i++)
        {
            Assert.Equal(0f, dest[i]); // Pre-gap audio is replaced by silence.
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal((50 + i) * 10f, dest[22 + i]);
        }

        // Once enough new audio arrives, the gap no longer affects the window.
        h.Write(Ramp(60, 40, 1), 40);
        Assert.True(h.TryCopyLatest(dest, 32, out _, discardBeforeFrame: 50));
        Assert.Equal(68 * 10f, dest[0]);
    }

    [Fact]
    public void OversizedWrite_KeepsOnlyTheNewestFrames()
    {
        var h = new SampleHistory(1, 16);
        h.Write(Ramp(0, 40, 1), 40);
        var dest = new float[16];
        Assert.True(h.TryCopyLatest(dest, 16, out var end));
        Assert.Equal(40, end); // Frame accounting remains absolute even when only the tail can be retained.
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal((24 + i) * 10f, dest[i]);
        }
    }

    [Fact]
    public void OversizedWrite_PreservesAbsoluteFramePositionsForGapTracking()
    {
        var h = new SampleHistory(1, 16);
        h.Write(Ramp(0, 40, 1), 40);
        h.Write(Ramp(40, 4, 1), 4);
        var dest = new float[16];

        Assert.True(h.TryCopyLatest(dest, 16, out var end, discardBeforeFrame: 40));
        Assert.Equal(44, end);
        for (var i = 0; i < 12; i++)
            Assert.Equal(0f, dest[i]);
        for (var i = 0; i < 4; i++)
            Assert.Equal((40 + i) * 10f, dest[12 + i]);
    }

    [Fact]
    public async Task ConcurrentProducer_NeverYieldsATornSnapshot()
    {
        // Producer writes a strictly increasing frame counter; snapshots must be contiguous runs of frames.
        const int channels = 2, window = 4096, chunk = 480;
        // Capacity is window + one in-flight chunk; snapshots only copy while the producer stays behind.
        var h = new SampleHistory(channels, window + chunk);
        using var stop = new CancellationTokenSource();

        // Ramp keeps producer writes memcpy-fast.
        const int cycle = chunk * 136;
        var ramp = new float[cycle * channels];
        for (var i = 0; i < cycle; i++)
        {
            ramp[i * channels] = i;
            ramp[(i * channels) + 1] = -i;
        }

        var producer = Task.Run(() =>
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(ramp, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var basePtr = handle.AddrOfPinnedObject();
                var offset = 0;
                while (!stop.IsCancellationRequested)
                {
                    h.Write(IntPtr.Add(basePtr, offset * channels * sizeof(float)), chunk * channels * sizeof(float));
                    offset = (offset + chunk) % cycle;
                }
            }
            finally
            {
                handle.Free();
            }
        });

        var dest = new float[window * channels];
        int accepted = 0, rejected = 0;
        try
        {
            // Run for at least 2 s or until evidence is clear; loaded machines just wait a bit longer.
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started < TimeSpan.FromSeconds(2) || accepted < 200 || rejected == 0)
                   && DateTime.UtcNow - started < TimeSpan.FromSeconds(60))
            {
                if (!h.TryCopyLatest(dest, window, out var end))
                {
                    rejected++;
                    continue;
                }

                if (end < window)
                {
                    continue;
                }

                accepted++;
                for (var i = 1; i < window; i++)
                {
                    var prev = dest[(i - 1) * channels];
                    var cur = dest[i * channels];
                    if (!(cur == prev + 1 || (prev == cycle - 1 && cur == 0)) || dest[(i * channels) + 1] != -cur)
                    {
                        Assert.Fail($"torn snapshot at {i}: {prev} -> {cur}, right {dest[(i * channels) + 1]}");
                    }
                }
            }
        }
        finally
        {
            stop.Cancel(); // Never leave the producer spinning on failure.
        }

        await producer;
        _out.WriteLine($"accepted {accepted}, rejected {rejected}");
        Assert.True(accepted >= 200, $"only {accepted} snapshots accepted ({rejected} rejected) in 60 s");
        Assert.True(rejected > 0, "the producer never lapped the reader, so torn-read detection was not exercised");
    }
}

internal static class ArrayTestExtensions
{
    public static void FillWith(this float[] a, float value)
    {
        for (var i = 0; i < a.Length; i++)
        {
            a[i] = value;
        }
    }
}
