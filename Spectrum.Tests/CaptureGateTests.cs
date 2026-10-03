using Xunit;

namespace Spectrum.Tests;

/// <summary>Analysis-timer decisions for silence, stalls, BASS errors, gaps, and stream changes.</summary>
public class CaptureGateTests
{
    private const int Required = 4;
    private static readonly object StreamA = new();
    private static readonly object StreamB = new();

    [Fact]
    public void FlowingAudio_IsAnalysedAndFeedsTheHangDetector()
    {
        var gate = new CaptureGate(Required);
        for (var t = 1; t <= 20; t++)
        {
            var d = gate.Next(5000, t * 1500, StreamA, 0);
            Assert.Equal(CaptureAction.Analyze, d.Action);
            Assert.False(d.RecoverDevice);
        }

        Assert.Equal(0, gate.GapEndFrame);
    }

    [Fact]
    public void BassError_NeverReachesTheHangDetector_AndSettlesToSilence()
    {
        // A constant −1 level used to look like a stall and trigger a reset.
        var gate = new CaptureGate(Required);
        for (var t = 1; t <= 50; t++)
        {
            var d = gate.Next(-1, 0, StreamA, 0);
            Assert.False(d.RecoverDevice);
            Assert.Equal(t >= Required ? CaptureAction.PublishSilence : CaptureAction.Wait, d.Action);
        }
    }

    [Fact]
    public void SingleTickWithoutNewFrames_IsJitterNotAGap()
    {
        var gate = new CaptureGate(Required);
        gate.Next(5000, 1500, StreamA, 0);
        var d = gate.Next(5000, 1500, StreamA, 0); // No callback ran between these ticks.
        Assert.Equal(CaptureAction.Analyze, d.Action);
        Assert.Equal(CaptureAction.Analyze, gate.Next(5000, 3000, StreamA, 0).Action);
        Assert.Equal(0, gate.GapEndFrame);
    }

    [Fact]
    public void ConfirmedStall_PublishesSilenceAndMarksTheGap()
    {
        // Gap handling must keep pre-pause audio separate from resumed audio.
        var gate = new CaptureGate(Required);
        gate.Next(5000, 9000, StreamA, 0);
        CaptureDecision d = default;
        for (var t = 0; t < Required; t++)
        {
            d = gate.Next(5000, 9000, StreamA, 0); // No frames while paused.
        }

        Assert.Equal(CaptureAction.PublishSilence, d.Action);
        Assert.Equal(9000, gate.GapEndFrame);

        // Playback resumes; the earlier gap remains in force.
        Assert.Equal(CaptureAction.Analyze, gate.Next(5000, 10500, StreamA, 0).Action);
        Assert.Equal(9000, gate.GapEndFrame);
    }

    [Fact]
    public void StallDuringBassErrors_StillMarksTheGap()
    {
        var gate = new CaptureGate(Required);
        gate.Next(5000, 9000, StreamA, 0);
        for (var t = 0; t < Required; t++)
        {
            Assert.False(gate.Next(-1, 9000, StreamA, 0).RecoverDevice);
        }

        Assert.Equal(CaptureAction.Analyze, gate.Next(5000, 10500, StreamA, 0).Action);
        Assert.Equal(9000, gate.GapEndFrame);
    }

    [Fact]
    public void QuietTicksFollowedByOneLateCallback_AreNotAGap()
    {
        // One late callback with flowing audio is not a gap.
        var gate = new CaptureGate(Required);
        for (var t = 1; t < Required; t++)
        {
            gate.Next(0, t * 1500, StreamA, 0);
        }

        gate.Next(0, (Required - 1) * 1500, StreamA, 0);
        Assert.Equal(0, gate.GapEndFrame);
    }

    [Fact]
    public void ZeroLevelWithFramesFlowing_IsSilenceWithoutAGap()
    {
        // Silence is valid; continuous history means no gap.
        var gate = new CaptureGate(Required);
        CaptureDecision d = default;
        for (var t = 1; t <= Required; t++)
        {
            d = gate.Next(0, t * 1500, StreamA, 0);
        }

        Assert.Equal(CaptureAction.PublishSilence, d.Action);
        Assert.False(d.RecoverDevice);
        Assert.Equal(0, gate.GapEndFrame);
    }

    [Fact]
    public void NewStream_ExcludesAudioFromThePreviousStream_AndIsNotTreatedAsStalled()
    {
        var gate = new CaptureGate(Required);
        gate.Next(5000, 20000, StreamA, 0);

        // Recovery reuses the same history; capture restarts at 20000.
        var d = gate.Next(5000, 20000, StreamB, 20000);
        Assert.Equal(CaptureAction.Analyze, d.Action);
        Assert.Equal(20000, gate.GapEndFrame);
    }

    private const int HangThreshold = 8;

    [Fact]
    public void SteadySignal_WithConstantLevel_NeverTriggersRecovery()
    {
        // A steady tone with flowing frames is healthy, not a hang.
        var gate = new CaptureGate(Required, HangThreshold);
        for (var t = 1; t <= 500; t++)
        {
            var d = gate.Next(214895823, t * 1520L, StreamA, 0);
            Assert.False(d.RecoverDevice, $"tick {t}");
            Assert.Equal(CaptureAction.Analyze, d.Action);
        }
    }

    [Fact]
    public void FrozenLevelWithNoFrames_TriggersOneRecoveryPerStall()
    {
        var gate = new CaptureGate(Required, HangThreshold);
        gate.Next(5000, 1520, StreamA, 0);
        var recoveries = 0;
        for (var t = 0; t < 100; t++)
        {
            if (gate.Next(5000, 1520, StreamA, 0).RecoverDevice)
            {
                recoveries++;
                Assert.True(t >= HangThreshold, $"recovery too early at stalled tick {t + 1}");
            }
        }

        Assert.Equal(1, recoveries); // A stale level during a long pause cannot restart recovery.

        // Recovery created another stalled stream; no second reset until frames flow.
        for (var t = 0; t < 50; t++)
        {
            Assert.False(gate.Next(5000, 1520, StreamB, 1520).RecoverDevice);
        }

        // Frames resume; a new frozen stall re-arms recovery.
        gate.Next(5000, 3040, StreamB, 1520);
        var rearmed = false;
        for (var t = 0; t < 20; t++)
        {
            rearmed |= gate.Next(5000, 3040, StreamB, 1520).RecoverDevice;
        }

        Assert.True(rearmed);
    }

    [Fact]
    public void StallWithZeroOrChangingLevel_IsNotAHang()
    {
        var gate = new CaptureGate(Required, HangThreshold);
        gate.Next(0, 1520, StreamA, 0);
        for (var t = 0; t < 50; t++)
        {
            Assert.False(gate.Next(0, 1520, StreamA, 0).RecoverDevice); // Silence is not a hang.
        }

        for (var t = 0; t < 50; t++)
        {
            Assert.False(gate.Next(1000 + t, 1520, StreamA, 0).RecoverDevice); // Level is still changing.
        }
    }

    [Fact]
    public void SoundAfterSilence_ResetsTheQuietCount()
    {
        var gate = new CaptureGate(Required);
        for (var t = 1; t < Required; t++)
        {
            gate.Next(0, t * 1500, StreamA, 0);
        }

        Assert.Equal(CaptureAction.Analyze, gate.Next(5000, 9000, StreamA, 0).Action);
        Assert.Equal(CaptureAction.Analyze, gate.Next(0, 10500, StreamA, 0).Action); // Quiet-count reset.
    }
}
