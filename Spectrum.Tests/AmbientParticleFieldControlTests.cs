using System;
using System.Threading;
using Spectrum;
using Xunit;

namespace Spectrum.Tests;

public class AmbientParticleFieldControlTests
{
    [Fact]
    public void CopiesTheUiDisplayFrameInsteadOfRetainingCallerBuffer()
    {
        RunOnSta(() =>
        {
            using var field = new AmbientParticleFieldControl();
            var frame = new byte[AmbientParticleFieldControl.BandCount];
            Fill(frame, 255);

            field.SetSpectrum(frame);
            Array.Clear(frame, 0, frame.Length);

            Assert.True(field.ActiveParticleCount > 0);
        });
    }

    [Fact]
    public void ZeroFrameLeavesNoActiveParticles()
    {
        RunOnSta(() =>
        {
            using var field = new AmbientParticleFieldControl();
            var activeFrame = new byte[AmbientParticleFieldControl.BandCount];
            activeFrame[0] = 255;
            field.SetSpectrum(activeFrame);
            Assert.True(field.ActiveParticleCount > 0);

            field.SetSpectrum(new byte[AmbientParticleFieldControl.BandCount]);

            Assert.Equal(0, field.ActiveParticleCount);
        });
    }

    [Fact]
    public void ParticleStateIsDeterministicAndBounded()
    {
        RunOnSta(() =>
        {
            using var first = new AmbientParticleFieldControl();
            using var second = new AmbientParticleFieldControl();
            var frame = new byte[AmbientParticleFieldControl.BandCount];
            Fill(frame, 255);

            for (var i = 0; i < 10; i++)
            {
                first.SetSpectrum(frame);
                second.SetSpectrum(frame);
                Assert.InRange(first.ActiveParticleCount, 0, AmbientParticleFieldControl.MaximumParticles);
                Assert.Equal(first.ActiveParticleCount, second.ActiveParticleCount);
                Assert.Equal(first.GetParticleStateHashForTesting(), second.GetParticleStateHashForTesting());
            }
        });
    }

    [Fact]
    public void DisposalClearsParticleStateAndIgnoresLaterFrames()
    {
        RunOnSta(() =>
        {
            var field = new AmbientParticleFieldControl();
            field.Dispose();
            var frame = new byte[AmbientParticleFieldControl.BandCount];
            frame[0] = 255;

            field.SetSpectrum(frame);

            Assert.True(field.IsDisposedForTesting);
            Assert.Equal(0, field.ActiveParticleCount);
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw failure;
    }

    private static void Fill(byte[] values, byte value)
    {
        for (var i = 0; i < values.Length; i++)
            values[i] = value;
    }
}
