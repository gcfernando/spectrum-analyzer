// Developer :: > Gehan Fernando
namespace Spectrum;

internal enum CaptureAction
{
    Analyze,

    PublishSilence,

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

    public bool RecoverDevice { get; }
}

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
    private int _errorTicks;
    private bool _recoveryAcknowledged;
    public bool NeedsRecovery => _recoveryRequestedThisStall && !_recoveryAcknowledged;
    public void AcknowledgeRecovery() => _recoveryAcknowledged = true;

    public CaptureGate(int silenceTicksRequired, int hangTicksThreshold = int.MaxValue)
    {
        _silenceTicksRequired = silenceTicksRequired;
        _hangTicksThreshold = hangTicksThreshold;
    }

    public long GapEndFrame { get; private set; }

    // Called once per timer tick; decides between analyzing, showing silence, waiting, or recovering a hung device.
    public CaptureDecision Next(int level, long totalFrames, object stream, long streamStartFrame)
    {
        var sameStream = ReferenceEquals(stream, _stream);
        var stalled = sameStream && totalFrames == _lastTotalFrames;
        if (!sameStream)
        {
            GapEndFrame = streamStartFrame;
            _quietTicks = _stallTicks = _hangTicks = _errorTicks = 0;
            _lastLevel = 0;
            _recoveryRequestedThisStall = _recoveryAcknowledged = false;
        }

        _stream = stream;
        _lastTotalFrames = totalFrames;

        var recover = UpdateHangDetection(level, stalled, sameStream);
        _errorTicks = level == -1 ? (int)System.Math.Min((long)_errorTicks + 1, int.MaxValue) : 0;
        if (_errorTicks > _hangTicksThreshold && !_recoveryRequestedThisStall)
        {
            recover = _recoveryRequestedThisStall = true;
        }

        _stallTicks = stalled ? _stallTicks + 1 : 0;
        if (_stallTicks >= _silenceTicksRequired)
        {
            GapEndFrame = totalFrames;
        }

        if (level == -1)
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
        if (sameStream && !stalled && level != -1)
        {
            _recoveryRequestedThisStall = false;
            _recoveryAcknowledged = false;
        }

        _hangTicks = stalled && level != 0 && level != -1 && level == _lastLevel ? _hangTicks + 1 : 0;
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
