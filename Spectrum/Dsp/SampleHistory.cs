// Developer :: > Gehan Fernando
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spectrum.Dsp;

// Ring buffer shared between the WASAPI callback and the analysis timer, so no locks allowed on the audio thread.
internal sealed class SampleHistory
{
    private const int MaxSnapshotAttempts = 4;

    private readonly float[] _ring;
    private readonly int _capacityFrames;

    private long _committedFrames;

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

    public long TotalFrames => Interlocked.Read(ref _committedFrames);

    public void Write(IntPtr buffer, int byteLength)
    {
        if (buffer == IntPtr.Zero || byteLength <= 0)
        {
            return;
        }

        var samples = byteLength / sizeof(float);
        var inputFrames = samples / Channels;
        if (inputFrames <= 0)
        {
            return;
        }

        var frames = inputFrames;
        var sourceOffsetSamples = 0;
        if (frames > _capacityFrames)
        {
            sourceOffsetSamples = (frames - _capacityFrames) * Channels;
            frames = _capacityFrames;
        }

        var committed = Interlocked.Read(ref _committedFrames);
        var endFrame = committed + inputFrames;
        Interlocked.Exchange(ref _reservedFrames, endFrame);

        var firstStoredFrame = endFrame - frames;
        var ringFrame = (int)(firstStoredFrame % _capacityFrames);
        var firstFrames = Math.Min(frames, _capacityFrames - ringFrame);
        var src = IntPtr.Add(buffer, sourceOffsetSamples * sizeof(float));

        Marshal.Copy(src, _ring, ringFrame * Channels, firstFrames * Channels);
        if (frames > firstFrames)
        {
            Marshal.Copy(IntPtr.Add(src, firstFrames * Channels * sizeof(float)), _ring, 0, (frames - firstFrames) * Channels);
        }

        Interlocked.Exchange(ref _committedFrames, endFrame);
    }

    public void Write(float[] interleaved, int frameCount)
    {
        if (interleaved == null)
        {
            throw new ArgumentNullException(nameof(interleaved));
        }

        if (frameCount < 0 || frameCount > interleaved.Length / Channels)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), frameCount, "Source must contain all requested frames.");
        }

        if (frameCount == 0)
        {
            return;
        }

        var frames = Math.Min(frameCount, _capacityFrames);
        var sourceOffset = (frameCount - frames) * Channels;
        var endFrame = Interlocked.Read(ref _committedFrames) + frameCount;
        Interlocked.Exchange(ref _reservedFrames, endFrame);

        var ringFrame = (int)((endFrame - frames) % _capacityFrames);
        var firstFrames = Math.Min(frames, _capacityFrames - ringFrame);
        Array.Copy(interleaved, sourceOffset, _ring, ringFrame * Channels, firstFrames * Channels);
        if (frames > firstFrames)
        {
            Array.Copy(interleaved, sourceOffset + firstFrames * Channels, _ring, 0, (frames - firstFrames) * Channels);
        }

        Interlocked.Exchange(ref _committedFrames, endFrame);
    }

    // Reader never blocks the writer; it just copies and then checks afterwards whether the writer lapped it.
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

            Interlocked.MemoryBarrier();
            var reserved = Interlocked.Read(ref _reservedFrames);

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
