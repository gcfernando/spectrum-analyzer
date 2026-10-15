// Developer :: > Gehan Fernando
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Un4seen.BassWasapi;
using Xunit;

namespace Spectrum.Tests;

[CollectionDefinition("Capture lifecycle", DisableParallelization = true)]
public sealed class CaptureLifecycleCollection { }

[Collection("Capture lifecycle")]
// Regression net around capture start/stop and device recovery; these run serialized because they share native state.
public sealed class CaptureRegressionTests
{
    [Theory]
    [InlineData(unchecked((int)0x80000000))]
    [InlineData(unchecked((int)0x80008000))]
    public void PackedFullScaleRightChannelIsNotAnError(int level)
    {
        var gate = new CaptureGate(4, 8);
        var stream = new object();
        for (var tick = 1; tick <= 50; tick++)
        {
            var decision = gate.Next(level, tick * 480, stream, 0);
            Assert.Equal(CaptureAction.Analyze, decision.Action);
            Assert.False(decision.RecoverDevice);
        }
    }

    [Fact]
    public void PersistentNativeFailureRequestsOneRecoveryEvenWhenFramesFlow()
    {
        var gate = new CaptureGate(4, 8);
        var stream = new object();
        var recoveries = 0;
        for (var tick = 1; tick <= 100; tick++)
            if (gate.Next(-1, tick * 480, stream, 0).RecoverDevice) recoveries++;
        Assert.Equal(1, recoveries);
        gate.Next(5000, 50000, stream, 0);
        for (var tick = 1; tick <= 20; tick++)
            if (gate.Next(-1, 50000, stream, 0).RecoverDevice) recoveries++;
        Assert.Equal(2, recoveries);
    }

    [Fact]
    public void IdentityWinsOverDuplicateAndOverlappingFriendlyNames()
    {
        var devices = new[]
        {
            new Device { Index = 2, DeviceName = "Speakers", DeviceId = "endpoint-a" },
            new Device { Index = 4, DeviceName = "Speakers", DeviceId = "endpoint-b" },
            new Device { Index = 6, DeviceName = "Speakers (USB)", DeviceId = "endpoint-c" }
        };
        Assert.Same(devices[1], Analyzer.SelectLoopback(devices, "ENDPOINT-B"));
        Assert.Same(devices[2], Analyzer.SelectLoopback(devices, "endpoint-c"));
        Assert.Null(Analyzer.SelectLoopback(Array.Empty<Device>(), "endpoint-b"));
        Assert.Null(Analyzer.SelectLoopback(devices, "missing-default"));
        Assert.Same(devices[0], Analyzer.SelectLoopback(devices, null));
    }

    [Fact]
    public void OnlyEnabledLoopbackInputsAreEligible()
    {
        var enabled = BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED;
        var input = BASSWASAPIDeviceInfo.BASS_DEVICE_INPUT;
        var loopback = BASSWASAPIDeviceInfo.BASS_DEVICE_LOOPBACK;
        Assert.False(Analyzer.IsEligibleLoopback(enabled));
        Assert.False(Analyzer.IsEligibleLoopback(enabled | input));
        Assert.False(Analyzer.IsEligibleLoopback(input | loopback));
        Assert.True(Analyzer.IsEligibleLoopback(enabled | input | loopback));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(100, 8)]
    public void RetryBackoffIsBounded(int failures, int seconds)
        => Assert.Equal(seconds, Analyzer.RecoveryDelaySeconds(failures));

    [Fact]
    public void StatusIsImmutableAndStopPublishesUnavailableRatherThanFakeSilence()
    {
        using var analyzer = new Analyzer(false);
        var starting = analyzer.CurrentStatus;
        Assert.Equal(-1, analyzer.SelectIndex);
        Assert.Equal(0, analyzer.CapturedFrames);
        Assert.Equal(-1, analyzer.LastNativeLevel);
        Assert.All(typeof(CaptureStatus).GetProperties(), property => Assert.False(property.CanWrite));
        var updates = 0;
        var frameWasUnavailable = false;
        var frameWasZero = false;
        var frameState = CaptureState.Starting;
        OnChangeHandler handler = (_, frame) =>
        {
            updates++;
            frameState = analyzer.CurrentStatus.State;
            frameWasZero = frame.Spectrumdata.All(value => value == 0);
            frameWasUnavailable = !frame.Audio.HasSamples;
        };
        Analyzer.OnChange += handler;
        try
        {
            analyzer.Free();
            Assert.Equal(1, updates);
            Assert.Equal(CaptureState.Stopped, frameState);
            Assert.True(frameWasUnavailable);
            Assert.True(frameWasZero);
            Assert.Equal(CaptureState.Starting, starting.State);
            Assert.Equal(CaptureState.Stopped, analyzer.CurrentStatus.State);
        }
        finally { Analyzer.OnChange -= handler; }
    }

    [Fact]
    public void FaultyStatusSubscriberCannotBreakCleanup()
    {
        var analyzer = new Analyzer(false);
        var observed = false;
        analyzer.StatusChanged += _ => throw new InvalidOperationException("subscriber failure");
        analyzer.StatusChanged += status => observed = status.State == CaptureState.Stopped;
        analyzer.Dispose();
        Assert.True(observed);
        analyzer.Dispose();
        analyzer.Free();
    }

    [Fact]
    public async Task RecoveryWorksWithoutAUiContextAndCompletesAfterFailure()
    {
        using var analyzer = new Analyzer(false);
        var failed = new TaskCompletionSource<CaptureStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.StatusChanged += status =>
        {
            if (status.State == CaptureState.Unavailable) failed.TrySetResult(status);
        };
        Invoke(analyzer, "OnAudioDeviceChanged");
        Assert.Same(failed.Task, await Task.WhenAny(failed.Task, Task.Delay(5000)));
        Assert.Contains("disabled", (await failed.Task).Message);
        Assert.Equal(0, analyzer.CapturedFrames);
        Assert.False(analyzer.CurrentStatus.State == CaptureState.Capturing);
    }

    [Fact]
    public async Task EndpointNotificationsDuringCooldownAreCoalescedAndServicedByAHealthyTick()
    {
        using var analyzer = new Analyzer(false);
        var deadline = DateTime.UtcNow.AddMinutes(1).Ticks;
        SetField(analyzer, "_nextRecoveryTicks", deadline);
        var attempts = 0;
        var failed = new TaskCompletionSource<CaptureStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.StatusChanged += status =>
        {
            if (status.State == CaptureState.Recovering) Interlocked.Increment(ref attempts);
            if (status.State == CaptureState.Unavailable) failed.TrySetResult(status);
        };

        Invoke(analyzer, "OnAudioDeviceChanged");
        Invoke(analyzer, "OnAudioDeviceChanged");
        Assert.True(HasPendingEndpointChange(analyzer));
        Assert.Equal(2L, GetField<long>(analyzer, "_endpointChangeVersion"));
        Assert.Equal(deadline, GetField<long>(analyzer, "_nextRecoveryTicks"));
        Assert.Equal(0, Volatile.Read(ref attempts));
        Assert.Equal(CaptureState.Starting, analyzer.CurrentStatus.State);

        SetField(analyzer, "_nextRecoveryTicks", 0L);
        var earliestRetry = DateTime.UtcNow.AddSeconds(1).Ticks;
        // Recovery has to get serviced even when the old source looked healthy, before any native level call.
        SetField(analyzer, "_initialized", true);
        Invoke(analyzer, "TimerTick", null, null);
        Assert.Same(failed.Task, await Task.WhenAny(failed.Task, Task.Delay(5000)));
        Assert.Contains("disabled", (await failed.Task).Message);
        WaitForRecoveryCompletion(analyzer);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.True(HasPendingEndpointChange(analyzer));
        Assert.True(GetField<long>(analyzer, "_nextRecoveryTicks") >= earliestRetry);
        Assert.Equal(-1, analyzer.SelectIndex);
        Assert.Null(analyzer.CurrentStatus.DeviceId);
    }

    [Fact]
    public async Task EndpointNotificationInsideRecoveryWorkerRetainsNewerSourceSelectionIntent()
    {
        using var analyzer = new Analyzer(false);
        var attempts = 0;
        var failures = 0;
        var firstFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFailure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.StatusChanged += status =>
        {
            if (status.State == CaptureState.Recovering && Interlocked.Increment(ref attempts) == 1)
            {
                Invoke(analyzer, "OnAudioDeviceChanged");
                Invoke(analyzer, "OnAudioDeviceChanged");
            }
            if (status.State == CaptureState.Unavailable)
            {
                if (Interlocked.Increment(ref failures) == 1) firstFailure.TrySetResult(true);
                else secondFailure.TrySetResult(true);
            }
        };

        var earliestRetry = DateTime.UtcNow.AddSeconds(1).Ticks;
        Invoke(analyzer, "OnAudioDeviceChanged");
        Assert.Same(firstFailure.Task, await Task.WhenAny(firstFailure.Task, Task.Delay(5000)));
        WaitForRecoveryCompletion(analyzer);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Equal(3L, GetField<long>(analyzer, "_endpointChangeVersion"));
        Assert.True(HasPendingEndpointChange(analyzer));
        Assert.True(GetField<long>(analyzer, "_nextRecoveryTicks") >= earliestRetry);
        var deadline = DateTime.UtcNow.AddMinutes(1).Ticks;
        SetField(analyzer, "_nextRecoveryTicks", deadline);

        Invoke(analyzer, "TimerTick", null, null);
        Assert.Equal(1, Volatile.Read(ref attempts));
        Assert.Equal(deadline, GetField<long>(analyzer, "_nextRecoveryTicks"));
        SetField(analyzer, "_nextRecoveryTicks", 0L);
        SetField(analyzer, "_initialized", true);
        Invoke(analyzer, "TimerTick", null, null);
        Assert.Same(secondFailure.Task, await Task.WhenAny(secondFailure.Task, Task.Delay(5000)));
        WaitForRecoveryCompletion(analyzer);
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.True(HasPendingEndpointChange(analyzer));
        Assert.Equal(0L, GetField<long>(analyzer, "_handledEndpointChangeVersion"));
        Assert.Equal(-1, analyzer.SelectIndex);
        Assert.Equal(CaptureState.Unavailable, analyzer.CurrentStatus.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulRecoveryAcknowledgesOnlyItsSourceSelectionSnapshot(bool newerNotification)
    {
        using var analyzer = new Analyzer(false);
        // Only simulating the acknowledgement here, so park the timer to keep it off the native level calls.
        GetField<System.Timers.Timer>(analyzer, "_timer").Interval = 60000;
        var simulatedInitialization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.StatusChanged += status =>
        {
            if (status.State != CaptureState.Unavailable || !status.Message.Contains("disabled")) return;
            if (newerNotification) Invoke(analyzer, "OnAudioDeviceChanged");
            SetField(analyzer, "_initialized", true);
            simulatedInitialization.TrySetResult(true);
        };

        Invoke(analyzer, "OnAudioDeviceChanged");
        Assert.Same(simulatedInitialization.Task,
            await Task.WhenAny(simulatedInitialization.Task, Task.Delay(5000)));
        WaitForRecoveryCompletion(analyzer);

        Assert.True(GetField<bool>(analyzer, "_initialized"));
        Assert.Equal(1L, GetField<long>(analyzer, "_handledEndpointChangeVersion"));
        Assert.Equal(newerNotification ? 2L : 1L, GetField<long>(analyzer, "_endpointChangeVersion"));
        Assert.Equal(newerNotification, HasPendingEndpointChange(analyzer));
        Assert.Equal(-1, analyzer.SelectIndex);
        Assert.Null(analyzer.CurrentStatus.DeviceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopOrDisposeCancelsDeferredEndpointSelectionAndRejectsLaterNotifications(bool dispose)
    {
        using var analyzer = new Analyzer(false);
        SetField(analyzer, "_nextRecoveryTicks", DateTime.UtcNow.AddMinutes(1).Ticks);
        Invoke(analyzer, "OnAudioDeviceChanged");
        Assert.True(HasPendingEndpointChange(analyzer));
        var changes = 0;
        analyzer.StatusChanged += _ => changes++;

        if (dispose) analyzer.Dispose();
        else analyzer.Free();
        Assert.False(HasPendingEndpointChange(analyzer));
        Assert.Equal(GetField<long>(analyzer, "_endpointChangeVersion"),
            GetField<long>(analyzer, "_handledEndpointChangeVersion"));
        Assert.Equal(1, changes);
        SetField(analyzer, "_nextRecoveryTicks", 0L);
        Invoke(analyzer, "OnAudioDeviceChanged");
        Invoke(analyzer, "TimerTick", null, null);

        Assert.False(HasPendingEndpointChange(analyzer));
        Assert.Equal(1L, GetField<long>(analyzer, "_endpointChangeVersion"));
        Assert.Equal(0, GetField<int>(analyzer, "_recoveryPending"));
        Assert.Equal(1, changes);
        Assert.Equal(CaptureState.Stopped, analyzer.CurrentStatus.State);
        Assert.Equal(-1, analyzer.SelectIndex);
    }

    [Fact]
    public void ThrottledRecoveryRemainsPendingUntilAcknowledged()
    {
        var gate = new CaptureGate(4, 8);
        var stream = new object();
        for (var tick = 0; tick < 20; tick++) gate.Next(-1, 0, stream, 0);
        Assert.True(gate.NeedsRecovery);
        gate.Next(-1, 0, stream, 0);
        Assert.True(gate.NeedsRecovery);
        gate.AcknowledgeRecovery();
        Assert.False(gate.NeedsRecovery);
        gate.Next(0, 0, new object(), 0);
        Assert.False(gate.NeedsRecovery);
    }

    [Fact]
    public void EachReplacementStreamCanRecoverFromPersistentErrorsWithoutAnyPcm()
    {
        var gate = new CaptureGate(4, 8);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stream = new object();
            var recoveries = 0;
            for (var tick = 0; tick < 20; tick++)
                if (gate.Next(-1, 0, stream, 0).RecoverDevice) recoveries++;
            Assert.Equal(1, recoveries);
            Assert.True(gate.NeedsRecovery);
            gate.AcknowledgeRecovery();
            Assert.False(gate.NeedsRecovery);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80000000))]
    [InlineData(unchecked((int)0x80008000))]
    public void FrozenNegativePackedLevelRecoversOncePerStream(int level)
    {
        var gate = new CaptureGate(4, 8);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stream = new object();
            gate.Next(level, 480, stream, 0);
            var recoveries = 0;
            for (var tick = 0; tick < 50; tick++)
                if (gate.Next(level, 480, stream, 0).RecoverDevice) recoveries++;
            Assert.Equal(1, recoveries);
            gate.AcknowledgeRecovery();
        }
    }

    [Fact]
    public async Task ConcurrentFreeAndDisposeAreIdempotentAndDoNotWaitOnDisposedHandles()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var analyzer = new Analyzer(false);
            using var start = new ManualResetEventSlim();
            var free = Task.Run(() => { start.Wait(); analyzer.Free(); });
            var dispose = Task.Run(() => { start.Wait(); analyzer.Dispose(); });
            start.Set();
            var both = Task.WhenAll(free, dispose);
            Assert.Same(both, await Task.WhenAny(both, Task.Delay(5000)));
            await both;
            Assert.Equal(CaptureState.Stopped, analyzer.CurrentStatus.State);
        }
    }

    [Fact]
    public async Task DisposalFromAnAudioSubscriberDoesNotDeadlockTheAnalysisTick()
    {
        var analyzer = new Analyzer(false);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.StatusChanged += status =>
        {
            if (status.State == CaptureState.Stopped) stopped.TrySetResult(true);
        };
        OnChangeHandler handler = (_, _) => analyzer.Dispose();
        Analyzer.OnChange += handler;
        try
        {
            Invoke(analyzer, "TimerTick", null, null);
            Assert.Same(stopped.Task, await Task.WhenAny(stopped.Task, Task.Delay(5000)));
            await stopped.Task;
        }
        finally
        {
            Analyzer.OnChange -= handler;
            analyzer.Dispose();
        }
    }

    private static void Invoke(Analyzer analyzer, string method, params object[] arguments)
        => typeof(Analyzer).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(analyzer, arguments);

    private static T GetField<T>(Analyzer analyzer, string name)
        => (T)typeof(Analyzer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(analyzer);

    private static void SetField<T>(Analyzer analyzer, string name, T value)
        => typeof(Analyzer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(analyzer, value);

    private static bool HasPendingEndpointChange(Analyzer analyzer)
        => (bool)typeof(Analyzer).GetProperty("HasPendingEndpointChange",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(analyzer);

    private static void WaitForRecoveryCompletion(Analyzer analyzer)
    {
        Assert.True(SpinWait.SpinUntil(() => GetField<int>(analyzer, "_recoveryPending") == 0,
            TimeSpan.FromSeconds(5)), "The managed recovery worker did not finish.");
        GetField<System.Timers.Timer>(analyzer, "_timer").Stop();
    }
}
