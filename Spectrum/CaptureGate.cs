namespace Spectrum;

internal enum CaptureAction
{
    /// <summary>Analyze the latest samples and publish a frame.</summary>
    Analyze,

    /// <summary>Publish the floor after confirming silence or a gap.</summary>
    PublishSilence,

    /// <summary>Wait when evidence is inconclusive, such as after a transient BASS error.</summary>
    Wait,
}

internal readonly struct CaptureDecision
{
    public CaptureDecision(CaptureAction action, bool recoverDevice)
    {
        Action = action;
        RecoverDevice = recoverDevice;
    }

    public CaptureAction Action { get; }

    /// <summary>Indicates that the capture appears hung and needs reinitialization.</summary>
    public bool RecoverDevice { get; }
}

/// <summary>Tracks silence, stream gaps, and device hangs per timer tick without depending on BASS.</summary>
internal sealed class CaptureGate
{
    private readonly int _silenceTicksRequired;
    private readonly int _hangTicksThreshold;
    private object _stream;
    private long _lastTotalFrames;
    private int _quietTicks;
    private int _stallTicks;
    private int _lastLevel;
    private int _hangTicks;
    private bool _recoveryRequestedThisStall;

    public CaptureGate(int silenceTicksRequired, int hangTicksThreshold = int.MaxValue)
    {
        _silenceTicksRequired = silenceTicksRequired;
        _hangTicksThreshold = hangTicksThreshold;
    }

    /// <summary>Absolute frame index before which captured audio is excluded from analysis (0 = none).</summary>
    public long GapEndFrame { get; private set; }

    /// <param name="level">The BASS_WASAPI_GetLevel result, or −1 on error.</param>
    /// <param name="totalFrames">Frames captured so far by <paramref name="stream"/>.</param>
    /// <param name="stream">Current capture stream; a new instance resets stall tracking.</param>
    /// <param name="streamStartFrame">Starting frame index; earlier audio belongs to a previous stream.</param>
    public CaptureDecision Next(int level, long totalFrames, object stream, long streamStartFrame)
    {
        var sameStream = ReferenceEquals(stream, _stream);
        var stalled = sameStream && totalFrames == _lastTotalFrames;
        if (!sameStream)
        {
            GapEndFrame = streamStartFrame;
        }

        _stream = stream;
        _lastTotalFrames = totalFrames;

        var recover = UpdateHangDetection(level, stalled, sameStream);

        // Track gaps by consecutive ticks without new frames, regardless of the reported level.
        _stallTicks = stalled ? _stallTicks + 1 : 0;
        if (_stallTicks >= _silenceTicksRequired)
        {
            GapEndFrame = totalFrames;
        }

        if (level < 0)
        {
            _quietTicks++;
            return new CaptureDecision(_quietTicks >= _silenceTicksRequired ? CaptureAction.PublishSilence : CaptureAction.Wait, recover);
        }

        if (level == 0 || stalled)
        {
            _quietTicks++;
            if (_quietTicks >= _silenceTicksRequired)
            {
                return new CaptureDecision(CaptureAction.PublishSilence, recover);
            }
        }
        else
        {
            _quietTicks = 0;
        }

        return new CaptureDecision(CaptureAction.Analyze, recover);
    }

    private bool UpdateHangDetection(int level, bool stalled, bool sameStream)
    {
        // New frames on this stream end the stall and re-arm recovery.
        if (sameStream && !stalled)
        {
            _recoveryRequestedThisStall = false;
        }

        _hangTicks = stalled && level > 0 && level == _lastLevel ? _hangTicks + 1 : 0;
        _lastLevel = level;

        if (_hangTicks > _hangTicksThreshold && !_recoveryRequestedThisStall)
        {
            _hangTicks = 0;
            _quietTicks = 0;
            _recoveryRequestedThisStall = true;
            return true;
        }

        return false;
    }
}
