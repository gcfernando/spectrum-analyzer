// Developer :: > Gehan Fernando
using Xunit;

namespace Spectrum.Tests;

// Walks the capture gate through silence, stalls, pauses and stream swaps to confirm it only recovers when it truly should.
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
        var d = gate.Next(5000, 1500, StreamA, 0);
        Assert.Equal(CaptureAction.Analyze, d.Action);
        Assert.Equal(CaptureAction.Analyze, gate.Next(5000, 3000, StreamA, 0).Action);
        Assert.Equal(0, gate.GapEndFrame);
    }

    [Fact]
    public void ConfirmedStall_PublishesSilenceAndMarksTheGap()
    {
        var gate = new CaptureGate(Required);
        gate.Next(5000, 9000, StreamA, 0);
        CaptureDecision d = default;
        for (var t = 0; t < Required; t++)
        {
            d = gate.Next(5000, 9000, StreamA, 0);
        }

        Assert.Equal(CaptureAction.PublishSilence, d.Action);
        Assert.Equal(9000, gate.GapEndFrame);

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

        var d = gate.Next(5000, 20000, StreamB, 20000);
        Assert.Equal(CaptureAction.Analyze, d.Action);
        Assert.Equal(20000, gate.GapEndFrame);
    }

    private const int HangThreshold = 8;

    [Fact]
    public void SteadySignal_WithConstantLevel_NeverTriggersRecovery()
    {
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

        Assert.Equal(1, recoveries);

        var replacementRecoveries = 0;
        for (var t = 0; t < 50; t++)
        {
            if (gate.Next(5000, 1520, StreamB, 1520).RecoverDevice)
                replacementRecoveries++;
        }
        Assert.Equal(1, replacementRecoveries);

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
            Assert.False(gate.Next(0, 1520, StreamA, 0).RecoverDevice);
        }

        for (var t = 0; t < 50; t++)
        {
            Assert.False(gate.Next(1000 + t, 1520, StreamA, 0).RecoverDevice);
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
        Assert.Equal(CaptureAction.Analyze, gate.Next(0, 10500, StreamA, 0).Action);
    }
}
