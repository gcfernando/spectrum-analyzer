using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spectrum.Dsp;

/// <summary>A lock-free single-producer/single-consumer ring for recent interleaved audio frames.</summary>
internal sealed class SampleHistory
{
    private const int MaxSnapshotAttempts = 4;

    private readonly float[] _ring;
    private readonly int _capacityFrames;

    // Number of fully written frames visible to the consumer.
    private long _committedFrames;

    // Exclusive upper bound of frames the producer may currently be writing.
    private long _reservedFrames;

    public SampleHistory(int channels, int capacityFrames)
    {
        if (channels < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        if (capacityFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        }

        Channels = channels;
        _capacityFrames = capacityFrames;
        _ring = new float[checked(capacityFrames * channels)];
    }

    public int Channels { get; }
    public int CapacityFrames => _capacityFrames;

    /// <summary>Total committed frames, used to detect a stalled stream.</summary>
    public long TotalFrames => Interlocked.Read(ref _committedFrames);

    /// <summary>Appends interleaved 32-bit float samples from an unmanaged buffer.</summary>
    public void Write(IntPtr buffer, int byteLength)
    {
        if (buffer == IntPtr.Zero || byteLength <= 0)
        {
            return;
        }

        var samples = byteLength / sizeof(float);
        var frames = samples / Channels;
        if (frames <= 0)
        {
            return;
        }

        var sourceOffsetSamples = 0;
        if (frames > _capacityFrames)
        {
            // Skip samples that would be overwritten immediately.
            sourceOffsetSamples = (frames - _capacityFrames) * Channels;
            frames = _capacityFrames;
        }

        var committed = Interlocked.Read(ref _committedFrames); // The producer is the only writer.
        Interlocked.Exchange(ref _reservedFrames, committed + frames); // Publish the reservation before overwriting samples.

        var ringFrame = (int)(committed % _capacityFrames);
        var firstFrames = Math.Min(frames, _capacityFrames - ringFrame);
        var src = IntPtr.Add(buffer, sourceOffsetSamples * sizeof(float));

        Marshal.Copy(src, _ring, ringFrame * Channels, firstFrames * Channels);
        if (frames > firstFrames)
        {
            Marshal.Copy(IntPtr.Add(src, firstFrames * Channels * sizeof(float)), _ring, 0, (frames - firstFrames) * Channels);
        }

        Interlocked.Exchange(ref _committedFrames, committed + frames); // Publish samples before advancing the committed count.
    }

    /// <summary>Appends interleaved samples from a managed array.</summary>
    public void Write(float[] interleaved, int frameCount)
    {
        var handle = GCHandle.Alloc(interleaved, GCHandleType.Pinned);
        try
        {
            Write(handle.AddrOfPinnedObject(), frameCount * Channels * sizeof(float));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Copies the latest frames oldest-first, zero-filling missing or discarded frames; returns false if overwritten during the copy.</summary>
    public bool TryCopyLatest(float[] destination, int frames, out long endFrame, long discardBeforeFrame = 0)
    {
        if (frames < 1 || frames > _capacityFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), frames, "Must be between 1 and the capacity.");
        }

        if (destination == null || destination.Length < frames * Channels)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
        {
            var end = Interlocked.Read(ref _committedFrames);
            var available = (int)Math.Min(end - Math.Min(end, Math.Max(0, discardBeforeFrame)), frames);
            var missing = frames - available;

            if (missing > 0)
            {
                Array.Clear(destination, 0, missing * Channels);
            }

            var startFrame = end - available;
            var ringFrame = (int)(startFrame % _capacityFrames);
            var firstFrames = Math.Min(available, _capacityFrames - ringFrame);

            Array.Copy(_ring, ringFrame * Channels, destination, missing * Channels, firstFrames * Channels);
            if (available > firstFrames)
            {
                Array.Copy(_ring, 0, destination, (missing + firstFrames) * Channels, (available - firstFrames) * Channels);
            }

            Interlocked.MemoryBarrier(); // Complete sample reads before checking for overwritten data.
            var reserved = Interlocked.Read(ref _reservedFrames);

            // The snapshot is safe if the producer has not advanced beyond its end plus ring capacity.
            if (reserved - startFrame <= _capacityFrames)
            {
                endFrame = end;
                return true;
            }
        }

        endFrame = 0;
        return false;
    }
}
