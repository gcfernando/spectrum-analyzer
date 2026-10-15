// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Spectrum.Dsp;
using Un4seen.Bass;
using Un4seen.BassWasapi;
using ElapsedEventArgs = System.Timers.ElapsedEventArgs;
using Timer = System.Timers.Timer;

namespace Spectrum;

public delegate void OnChangeHandler(object obj, OnChangeEventArgs e);

// Event payload sent to the UI each tick: the byte levels plus the richer audio data used internally
public sealed class OnChangeEventArgs : EventArgs
{
    public IReadOnlyList<byte> Spectrumdata { get; }
    public OnChangeEventArgs(byte[] spectrumdata) => Spectrumdata = spectrumdata;
    internal AudioVisualizationData Audio { get; }
    internal BandPlan Plan { get; set; }
    internal OnChangeEventArgs(byte[] spectrumdata, AudioVisualizationData audio) : this(spectrumdata) => Audio = audio;
}

// Captures system output via WASAPI loopback (BASS), runs the band FFT on a timer and raises OnChange; also self-heals when devices change
public sealed class Analyzer : IDisposable
{
    public static event OnChangeHandler OnChange;

    // Number of bars we output
    private const int LINES = 83;

    // Bar centers are spread across the audible range
    private const double FIRST_CENTER_HZ = 20.0;
    private const double LAST_CENTER_HZ = 20000.0;

    // 25ms tick is roughly 40 fps; hang threshold/silence frames are used by the gate to detect stalled or dead streams
    private const int TIMER_INTERVAL_MS = 25;
    private const int HANG_THRESHOLD = 8;
    private const int SILENCE_FRAMES_REQUIRED = 4;

    private const float WASAPI_BUFFER_SECONDS = 1f;
    private const float WASAPI_PERIOD_SECONDS = 0.01f;

    private const int HISTORY_CAPACITY_FACTOR = 4;

    private readonly Timer _timer;
    private readonly byte[] _spectrumData;
    private readonly double[] _bandPower;
    private readonly double[] _leftBandPower = new double[LINES];
    private readonly double[] _sevenBandPower = new double[SevenBandAnalyzer.BandCount];
    private int _sevenBandEnabled;

    // The 7-band output is optional, toggled from another thread so it goes through Volatile
    public bool SevenBandEnabled
    {
        get => Volatile.Read(ref _sevenBandEnabled) != 0;
        set => Volatile.Write(ref _sevenBandEnabled, value ? 1 : 0);
    }
    private readonly double[] _rightBandPower = new double[LINES];

    private volatile CaptureStream _stream;

    private readonly CaptureGate _gate = new(SILENCE_FRAMES_REQUIRED, HANG_THRESHOLD);

    // Bookkeeping for the tick/teardown/recovery dance; the gates keep native frees from racing a running tick
    private int _tickActive;
    private int _tickThreadId;
    private bool _freeDeferred;
    private readonly object _lifecycleGate = new();
    private readonly object _nativeFreeGate = new();

    private readonly byte[] _fireData;
    private readonly OnChangeEventArgs _fireEventArgs;
    private readonly AudioVisualizationData _audioData = new();

    private readonly WASAPIPROC _process;
    private static readonly object _sender = new();

    private NAudio.CoreAudioApi.MMDeviceEnumerator _mmEnumerator;
    private DeviceNotificationClient _deviceNotificationClient;


    private volatile bool _initialized;
    private volatile bool _disposed;
    private volatile bool _recovering;
    private int _recoveryPending;
    private long _endpointChangeVersion;
    private long _handledEndpointChangeVersion;
    private bool _nativeTeardownPending;
    private bool _nativeHandlesFreed;
    private bool _wasapiInitialized;
    private bool _bassInitialized;
    private bool _stopPublished;
    private volatile bool _stopped;
    private readonly bool _managedOnly;
    private long _nextRecoveryTicks;
    private int _recoveryFailures;
    private Device _selectedDevice;
    // Starts out as "Starting" so the UI has something to show before the first real status
    private volatile CaptureStatus _currentStatus = new(CaptureState.Starting, "Starting audio capture", null, null, null, 0, 0);
    private int _lastNativeLevel = -1;

    private int _sampleRate = 48000;

    public int SelectIndex { get; set; } = -1;

    public CaptureStatus CurrentStatus => _currentStatus;
    public event Action<CaptureStatus> StatusChanged;
    public long CapturedFrames => _stream?.State.History.TotalFrames ?? 0;
    public int LastNativeLevel => Volatile.Read(ref _lastNativeLevel);

    internal BandPlan CurrentBandPlan => _stream?.State.Engine.Plan;

    // Public ctor does real native audio setup; the internal one lets tests run managed-only without touching BASS
    public Analyzer() : this(true) { }

    internal Analyzer(bool initializeNative)
    {
        _managedOnly = !initializeNative;
        _spectrumData = new byte[LINES];
        _bandPower = new double[LINES];

        _fireData = new byte[LINES];
        _fireEventArgs = new OnChangeEventArgs(_fireData, _audioData);

        _stream = new CaptureStream(AnalysisState.Create(_sampleRate, 2));

        _process = Process;

        // AutoReset off so ticks never overlap; each tick re-arms the timer itself
        _timer = new Timer { Interval = TIMER_INTERVAL_MS, AutoReset = false };
        _timer.Elapsed += TimerTick;

        _nativeHandlesFreed = true;
        if (!initializeNative) return;

        try
        {
            BassNet.Registration("buddyknox@usa.org", "2X11841782815");
            InitializeCapture();
        }
        catch (Exception ex)
        {
            CaptureFailed("Audio initialization failed: " + ex.Message);
        }

        // Endpoint notifications are a nice-to-have, so failing here just gets logged
        try
        {
            _mmEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            _deviceNotificationClient = new DeviceNotificationClient(OnAudioDeviceChanged);
            _ = _mmEnumerator.RegisterEndpointNotificationCallback(_deviceNotificationClient);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Endpoint notifications unavailable: " + ex.Message); }
        _timer.Start();
    }

    // Windows told us a device changed; bump a version and let the recovery logic rebuild the capture
    private void OnAudioDeviceChanged()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _stopped || _nativeTeardownPending) return;
            _endpointChangeVersion++;
        }
        RequestRecovery();
    }

    // True when a device change came in that we haven't dealt with yet
    private bool HasPendingEndpointChange
    {
        get
        {
            lock (_lifecycleGate)
                return !_disposed && !_stopped && _endpointChangeVersion != _handledEndpointChangeVersion;
        }
    }

    // We only want enabled loopback inputs, i.e. "what you hear" capture, not mics
    internal static bool IsEligibleLoopback(BASSWASAPIDeviceInfo flags)
    {
        const BASSWASAPIDeviceInfo required = BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED |
            BASSWASAPIDeviceInfo.BASS_DEVICE_INPUT | BASSWASAPIDeviceInfo.BASS_DEVICE_LOOPBACK;
        return (flags & required) == required;
    }

    // Prefer the loopback matching the Windows default output; with no default known just take the first one
    internal static Device SelectLoopback(IReadOnlyList<Device> devices, string defaultEndpointId)
        => string.IsNullOrEmpty(defaultEndpointId) ? devices.FirstOrDefault() :
            devices.FirstOrDefault(d => string.Equals(d.DeviceId, defaultEndpointId, StringComparison.OrdinalIgnoreCase));

    // Enumerates loopback devices and picks the one to capture, matching the default render endpoint
    private void DeviceList()
    {
        var count = BassWasapi.BASS_WASAPI_GetDeviceCount();
        var devices = new List<Device>(Math.Max(0, count));

        for (var i = 0; i < count; i++)
        {
            var di = BassWasapi.BASS_WASAPI_GetDeviceInfo(i);
            if (di != null && IsEligibleLoopback(di.flags))
            {
                devices.Add(new Device { Index = i, DeviceName = di.name, DeviceId = di.id });
            }
        }

        string defaultId = null;
        try
        {
            var mmEnum = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            NAudio.CoreAudioApi.MMDevice defaultDev = null;
            try
            {
                defaultDev = mmEnum.GetDefaultAudioEndpoint(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.Role.Multimedia);
                defaultId = defaultDev.ID;
            }
            finally
            {
                ReleaseComObject(defaultDev);
                ReleaseComObject(mmEnum);
            }
        }
        // Can't find the default endpoint? Fine, we just fall back to the first loopback device
        catch { }

        _selectedDevice = SelectLoopback(devices, defaultId);
        SelectIndex = _selectedDevice?.Index ?? -1;
    }

    // Brings up BASS and then WASAPI loopback; any failure gets reported through CaptureFailed instead of throwing
    private void InitializeCapture()
    {
        if (_disposed || _stopped) return;
        if (_managedOnly)
        {
            CaptureFailed("Native audio is disabled for this managed capture session");
            return;
        }
        // 0 update threads: we pull data ourselves from the timer instead
        _ = Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATETHREADS, 0);
        if (!_bassInitialized)
        {
            if (!Bass.BASS_Init(0, 48000, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero))
            {
                CaptureFailed("BASS initialization failed", (int)Bass.BASS_ErrorGetCode());
                return;
            }
            _bassInitialized = true;
            _nativeHandlesFreed = false;
        }
        DeviceList();
        var deviceInfo = SelectIndex < 0 ? null : BassWasapi.BASS_WASAPI_GetDeviceInfo(SelectIndex);
        if (deviceInfo == null || !IsEligibleLoopback(deviceInfo.flags))
        {
            CaptureFailed("No enabled system-output loopback endpoint is available");
            return;
        }
        // AUTOFORMAT lets WASAPI use the device's mix format; we read the real rate back afterwards
        var success = BassWasapi.BASS_WASAPI_Init(
                    SelectIndex,
                    0,
                    0,
                    BASSWASAPIInit.BASS_WASAPI_AUTOFORMAT | BASSWASAPIInit.BASS_WASAPI_BUFFER,
                    WASAPI_BUFFER_SECONDS,
                    WASAPI_PERIOD_SECONDS,
                    _process,
                    IntPtr.Zero);

        if (!success)
        {
            CaptureFailed("WASAPI initialization failed", (int)Bass.BASS_ErrorGetCode());
            return;
        }
        _wasapiInitialized = true;
        var info = BassWasapi.BASS_WASAPI_GetInfo();
        if (info == null || info.freq <= 0 || info.chans <= 0)
        {
            CaptureFailed("WASAPI returned an invalid capture format");
            return;
        }
        _sampleRate = info.freq;
        var channels = info.chans;

        // Reuse the existing state when format is unchanged so history/continuity survives a reconnect
        var state = _stream?.State;
        if (state == null || state.SampleRate != _sampleRate || state.History.Channels != channels)
            state = AnalysisState.Create(_sampleRate, channels);
        _stream = new CaptureStream(state);

        if (!BassWasapi.BASS_WASAPI_Start())
        {
            CaptureFailed("WASAPI start failed", (int)Bass.BASS_ErrorGetCode());
            return;
        }
        _initialized = true;
        _recoveryFailures = 0;
        SetStatus(CaptureState.Starting, "Waiting for captured PCM");
    }

    // WASAPI callback: just shove the raw PCM into the history ring buffer and get out quick
    private int Process(IntPtr buffer, int length, IntPtr user)
    {
        _stream?.State.History.Write(buffer, length);
        return length;
    }

    // Stops capture; if called from inside a tick we can't wait on ourselves, so the free is deferred until the tick ends
    public void Free()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _stopped = true;
            _handledEndpointChangeVersion = _endpointChangeVersion;
            _nativeTeardownPending = true;
            _timer.Stop();
            // Same-thread check: waiting for our own tick would deadlock
            if (_tickThreadId == Environment.CurrentManagedThreadId)
            {
                _freeDeferred = true;
                return;
            }
        }

        WaitForTickToFinish();
        try
        {
            lock (_nativeFreeGate)
            {
                if (!_disposed && !_stopPublished)
                {
                    FreeNativeHandles();
                    _stopPublished = true;
                    SetStatus(CaptureState.Stopped, "Capture stopped");
                    PublishUnavailable();
                }
            }
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _nativeTeardownPending = false;
            }
        }
    }

    // Blocks until any in-flight tick is done, so we never free native stuff under its feet
    private void WaitForTickToFinish()
    {
        lock (_lifecycleGate)
            while (_tickActive != 0)
                Monitor.Wait(_lifecycleGate);
    }

    // The heartbeat: read the native level, ask the gate what to do (analyze/silence/recover) and publish; always re-arms the timer at the end
    private void TimerTick(object sender, ElapsedEventArgs e)
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _stopped || _recovering || _nativeTeardownPending)
            {
                return;
            }

            // Re-entrancy guard, only one tick at a time
            if (Interlocked.CompareExchange(ref _tickActive, 1, 0) != 0)
            {
                return;
            }

            _tickThreadId = Environment.CurrentManagedThreadId;
        }

        try
        {
            // A device switch takes priority over everything else
            if (HasPendingEndpointChange && RequestRecovery())
                return;

            // Not initialised yet: keep telling the UI we're unavailable and retry on a backoff
            if (!_initialized)
            {
                PublishUnavailable();
                if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref _nextRecoveryTicks))
                    RequestRecovery();
                return;
            }
            var stream = _stream;
            var state = stream.State;
            var level = BassWasapi.BASS_WASAPI_GetLevel();
            Volatile.Write(ref _lastNativeLevel, level);
            var decision = _gate.Next(level, state.History.TotalFrames, stream, stream.StartFrame);

            // BASS returns -1 when the level can't be read
            if (level == -1)
            {
                SetStatus(CaptureState.Unavailable, "Native audio level unavailable", (int)Bass.BASS_ErrorGetCode());
                PublishUnavailable();
            }
            else
                switch (decision.Action)
                {
                    case CaptureAction.Analyze:
                        AnalyzeAndFire(state, stream);
                        break;
                    case CaptureAction.PublishSilence:
                        PublishSilence(state, true, stream);
                        break;
                }

            if (decision.RecoverDevice || _gate.NeedsRecovery)
            {
                if (RequestRecovery()) _gate.AcknowledgeRecovery();
            }
        }
        catch (Exception ex)
        {
            CaptureFailed("Capture interrupted: " + ex.Message);
            PublishUnavailable();
        }
        // Whatever happens, clear the active flag and restart the timer, unless we're shutting down or recovering
        finally
        {
            var freeDeferred = false;
            lock (_lifecycleGate)
            {
                Volatile.Write(ref _tickActive, 0);
                _tickThreadId = 0;
                Monitor.PulseAll(_lifecycleGate);
                freeDeferred = _freeDeferred;
                _freeDeferred = false;

                if (!freeDeferred && !_disposed && !_stopped && !_recovering && !_nativeTeardownPending)
                {
                    try
                    { _timer.Start(); }
                    catch (ObjectDisposedException) { }
                }
            }

            if (freeDeferred)
            {
                ThreadPool.QueueUserWorkItem(_ => Free());
            }
        }
    }

    // Zeroes the bars but still refreshes the observed window so peak/meter info stays truthful
    private void PublishSilence(AnalysisState state, bool validCaptureLevel, CaptureStream stream)
    {
        Array.Clear(_spectrumData, 0, LINES);
        var frames = state.Engine.RequiredFrames;
        if (validCaptureLevel && state.History.TryCopyLatest(state.Snapshot, frames, out var endFrame, _gate.GapEndFrame))
        {
            _audioData.UpdateObservedWindow(state.Snapshot, frames, state.History.Channels, state.SampleRate,
                endFrame, state.AudioEndFrame, _gate.GapEndFrame, stream);
            state.AudioEndFrame = endFrame;
            _audioData.SetChannelSpectrum(null, null);
            _audioData.SetSevenBandPower(null);
        }
        else
            _audioData.SetUnavailableWindow(state.History.TotalFrames, state.History.Channels, state.SampleRate, stream);
        // Distinguish real digital silence from "we have no PCM at all"
        var measuredSilence = _audioData.HasSamples && _audioData.LeftPeak == 0 && _audioData.RightPeak == 0;
        SetStatus(!_audioData.HasSamples ? CaptureState.Unavailable : measuredSilence ? CaptureState.Silent : CaptureState.Capturing,
            !_audioData.HasSamples ? "No current captured PCM" : measuredSilence ? "Captured digital silence" : "Capturing system output");
        _fireEventArgs.Plan = state.Engine.Plan;
        FireOnChange();
    }

    // Grab the latest window, run the FFT bands, convert to dB bytes and notify listeners
    private void AnalyzeAndFire(AnalysisState state, CaptureStream stream)
    {
        var frames = state.Engine.RequiredFrames;
        if (!state.History.TryCopyLatest(state.Snapshot, frames, out var endFrame, _gate.GapEndFrame))
        {
            return;
        }

        state.Engine.Analyze(state.Snapshot, frames, _bandPower, _leftBandPower, _rightBandPower);
        _audioData.UpdateObservedWindow(state.Snapshot, frames, state.History.Channels, state.SampleRate,
            endFrame, state.AudioEndFrame, _gate.GapEndFrame, stream);
        _audioData.SetChannelSpectrum(_leftBandPower, _rightBandPower);
        // Seven-band work is skipped unless someone turned it on
        if (state.SevenBand != null && Volatile.Read(ref _sevenBandEnabled) != 0)
        {
            state.SevenBand.Analyze(state.Snapshot, frames, _sevenBandPower);
            _audioData.SetSevenBandPower(_sevenBandPower);
        }
        state.AudioEndFrame = endFrame;
        _fireEventArgs.Plan = state.Engine.Plan;
        SetStatus(_audioData.HasSamples ? CaptureState.Capturing : CaptureState.Unavailable,
            _audioData.HasSamples ? "Capturing system output" : "Waiting for captured PCM");

        for (var i = 0; i < LINES; i++)
        {
            _spectrumData[i] = LevelScale.ToDisplayByte(LevelScale.PowerToDb(_bandPower[i]));
        }

        FireOnChange();
    }

    // Copy into a separate buffer first so subscribers never see half-written data, and one bad handler can't break the rest
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FireOnChange()
    {
        Buffer.BlockCopy(_spectrumData, 0, _fireData, 0, LINES);
        var handlers = OnChange;
        if (handlers == null)
        {
            return;
        }

        foreach (OnChangeHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(_sender, _fireEventArgs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Spectrum update subscriber failed: {ex}");
            }
        }
    }

    // Kicks off a background rebuild of the capture; throttled so a flaky device doesn't make us spin
    private bool RequestRecovery()
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _nextRecoveryTicks)) return false;
        if (!TryBeginRecovery())
        {
            return false;
        }

        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    WaitForTickToFinish();
                    lock (_nativeFreeGate)
                    {
                        if (_disposed || _stopped || IsNativeTeardownPending()) return;
                        // Remember which device-change version we started from, so changes during the rebuild trigger another pass
                        long endpointVersion;
                        lock (_lifecycleGate)
                            endpointVersion = _endpointChangeVersion;
                        FreeNativeHandles();
                        SetStatus(CaptureState.Recovering, "Reconnecting system-output loopback");
                        PublishUnavailable();
                        Interlocked.Exchange(ref _nextRecoveryTicks, DateTime.UtcNow.AddSeconds(1).Ticks);
                        InitializeCapture();
                        if (_initialized)
                        {
                            lock (_lifecycleGate)
                            {
                                _handledEndpointChangeVersion = _disposed || _stopped
                                    ? _endpointChangeVersion : endpointVersion;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_nativeFreeGate)
                    {
                        if (!_disposed && !_stopped)
                        {
                            CaptureFailed("Audio recovery failed: " + ex.Message);
                            PublishUnavailable();
                        }
                    }
                }
                finally
                {
                    CompleteRecovery();
                    StartTimerAfterRecovery();
                }
            });
        }
        catch (Exception ex)
        {
            CaptureFailed("Could not schedule audio recovery: " + ex.Message);
            CompleteRecovery();
            StartTimerAfterRecovery();
            return false;
        }
        return true;
    }

    // Only one recovery at a time; the CAS on _recoveryPending is what enforces that
    private bool TryBeginRecovery()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _stopped || _nativeTeardownPending ||
                Interlocked.CompareExchange(ref _recoveryPending, 1, 0) != 0)
            {
                return false;
            }

            _recovering = true;
            return true;
        }
    }

    private void CompleteRecovery()
    {
        _recovering = false;
        Volatile.Write(ref _recoveryPending, 0);
    }

    // Restarts the tick timer once recovery is done, unless we got stopped meanwhile
    private void StartTimerAfterRecovery()
    {
        if (_disposed || _stopped)
        {
            return;
        }

        try
        {
            _timer.Start();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool IsNativeTeardownPending()
    {
        lock (_lifecycleGate)
        {
            return _nativeTeardownPending;
        }
    }

    // Final teardown; if called from a tick thread we hop to the pool so we don't wait on ourselves
    public void Dispose()
    {
        var disposeOnWorker = false;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _handledEndpointChangeVersion = _endpointChangeVersion;
            _nativeTeardownPending = true;
            _timer.Stop();
            disposeOnWorker = _tickThreadId == Environment.CurrentManagedThreadId;
        }

        if (disposeOnWorker)
        {
            ThreadPool.QueueUserWorkItem(_ => DisposeAfterTick());
            return;
        }

        DisposeAfterTick();
    }

    private void DisposeAfterTick()
    {
        WaitForTickToFinish();
        lock (_nativeFreeGate)
        {
            if (_mmEnumerator != null)
            {
                try
                {
                    if (_deviceNotificationClient != null)
                        _ = _mmEnumerator.UnregisterEndpointNotificationCallback(_deviceNotificationClient);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                ReleaseComObject(_mmEnumerator);
                _mmEnumerator = null;
            }
            _timer.Elapsed -= TimerTick;
            _timer.Dispose();
            try { FreeNativeHandles(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Native cleanup failed: " + ex); }
            finally
            {
                SetStatus(CaptureState.Stopped, "Capture disposed");
                StatusChanged = null;
            }
        }
    }

    // COM objects from NAudio need an explicit release or the audio endpoints stay referenced
    private static void ReleaseComObject(object instance)
    {
        if (instance == null)
        {
            return;
        }

        if (Marshal.IsComObject(instance))
        {
            _ = Marshal.ReleaseComObject(instance);
            return;
        }

        // Not a COM object itself (NAudio wrapper), so release the COM objects hiding in its private fields
        var fields = instance.GetType().GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        foreach (var field in fields)
        {
            var resource = field.GetValue(instance);
            if (resource != null && Marshal.IsComObject(resource))
            {
                _ = Marshal.ReleaseComObject(resource);
            }
        }
    }

    // Releases WASAPI then BASS in that order; safe to call repeatedly
    private void FreeNativeHandles()
    {
        _initialized = false;
        if (_nativeHandlesFreed) return;
        if (_wasapiInitialized)
        {
            if (!BassWasapi.BASS_WASAPI_Free())
                throw new InvalidOperationException("WASAPI cleanup failed: " + Bass.BASS_ErrorGetCode());
            _wasapiInitialized = false;
        }
        if (_bassInitialized)
        {
            if (!Bass.BASS_Free())
                throw new InvalidOperationException("BASS cleanup failed: " + Bass.BASS_ErrorGetCode());
            _bassInitialized = false;
        }
        _nativeHandlesFreed = true;
    }

    // Exponential backoff for reconnect attempts: 1, 1, 2, 4, 8 seconds, capped at 8
    internal static int RecoveryDelaySeconds(int failureCount) => 1 << Math.Min(3, Math.Max(0, failureCount - 1));

    // Marks capture as down, schedules the next retry with backoff and tells the UI why
    private void CaptureFailed(string message, int? errorCode = null)
    {
        _initialized = false;
        _recoveryFailures = Math.Min(4, _recoveryFailures + 1);
        Interlocked.Exchange(ref _nextRecoveryTicks, DateTime.UtcNow.AddSeconds(RecoveryDelaySeconds(_recoveryFailures)).Ticks);
        SetStatus(CaptureState.Unavailable, message, errorCode);
    }

    // Flatline the bars and mark the audio window unavailable so the UI doesn't show stale data
    private void PublishUnavailable()
    {
        Array.Clear(_spectrumData, 0, LINES);
        var stream = _stream;
        _audioData.SetUnavailableWindow(stream.State.History.TotalFrames, stream.State.History.Channels, stream.State.SampleRate, stream);
        _fireEventArgs.Plan = stream.State.Engine.Plan;
        FireOnChange();
    }

    // Only raises StatusChanged when something actually changed, to avoid spamming every 25ms tick
    private void SetStatus(CaptureState state, string message, int? errorCode = null)
    {
        var device = _selectedDevice;
        var rate = _initialized ? _sampleRate : 0;
        var channels = _initialized ? _stream.State.History.Channels : 0;
        var old = _currentStatus;
        if (old.State == state && old.Message == message && old.ErrorCode == errorCode &&
            old.DeviceId == device?.DeviceId && old.DeviceName == device?.DeviceName &&
            old.SampleRate == rate && old.Channels == channels) return;
        var status = new CaptureStatus(state, message, device?.DeviceId, device?.DeviceName, errorCode, rate, channels);
        _currentStatus = status;
        var handlers = StatusChanged;
        if (handlers == null) return;
        foreach (Action<CaptureStatus> handler in handlers.GetInvocationList())
        {
            try { handler(status); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Capture status subscriber failed: " + ex); }
        }
    }

    // One capture session; StartFrame marks where in the history this stream began so older frames can be ignored
    private sealed class CaptureStream
    {
        public CaptureStream(AnalysisState state)
        {
            State = state;
            StartFrame = state.History.TotalFrames;
        }

        public AnalysisState State { get; }
        public long StartFrame { get; }
    }

    // Everything tied to one sample rate / channel layout: engines, ring buffer and a reusable snapshot buffer
    private sealed class AnalysisState
    {
        private AnalysisState(BandSpectrumAnalyzer engine, SevenBandAnalyzer sevenBand, SampleHistory history)
        {
            Engine = engine;
            SevenBand = sevenBand;
            History = history;
            Snapshot = new float[engine.RequiredFrames * history.Channels];
        }

        public BandSpectrumAnalyzer Engine { get; }
        public SevenBandAnalyzer SevenBand { get; }
        public SampleHistory History { get; }
        public int SampleRate => Engine.Plan.SampleRate;

        public float[] Snapshot { get; }
        public long AudioEndFrame { get; set; }

        public static AnalysisState Create(int sampleRate, int channels)
        {
            var plan = BandPlan.CreateLogarithmic(LINES, FIRST_CENTER_HZ, LAST_CENTER_HZ, sampleRate);
            var engine = new BandSpectrumAnalyzer(plan, channels);
            // History holds a few analysis windows' worth so we can still grab a full window if a tick runs late
        var history = new SampleHistory(channels, engine.RequiredFrames * HISTORY_CAPACITY_FACTOR);
            var sevenBand = new SevenBandAnalyzer(sampleRate, channels);
            return new AnalysisState(engine, sevenBand, history);
        }
    }

    // Listens for Windows audio device changes and just pokes the analyzer to reconnect
    private sealed class DeviceNotificationClient : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        private readonly Action _onDefaultChanged;

        public DeviceNotificationClient(Action onDefaultChanged)
            => _onDefaultChanged = onDefaultChanged;

        public void OnDefaultDeviceChanged(
            NAudio.CoreAudioApi.DataFlow flow,
            NAudio.CoreAudioApi.Role role,
            string defaultDeviceId)
        {
            // We only care about the default playback device, which is what loopback follows
            if (flow == NAudio.CoreAudioApi.DataFlow.Render &&
                role == NAudio.CoreAudioApi.Role.Multimedia)
            {
                _onDefaultChanged();
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) => _onDefaultChanged();
        public void OnDeviceRemoved(string deviceId) => _onDefaultChanged();
        public void OnDeviceStateChanged(string deviceId, NAudio.CoreAudioApi.DeviceState newState) => _onDefaultChanged();
        public void OnPropertyValueChanged(string pwstrDeviceId, NAudio.CoreAudioApi.PropertyKey key) { }
    }
}

// Latest measurements (wave, peaks, RMS, spectra) plus a ring of past observations, shared with the advanced visualizations
internal sealed class AudioVisualizationData
{
    internal const int MaximumWaveSamples = 2048;
    internal const int MaximumObservations = 1024;
    internal readonly float[] Wave = new float[MaximumWaveSamples];
    internal readonly byte[] LeftSpectrum = new byte[AdvancedVisualizationControl.BandCount];
    internal readonly byte[] RightSpectrum = new byte[AdvancedVisualizationControl.BandCount];
    private readonly AudioLevelObservation[] _observations = new AudioLevelObservation[MaximumObservations];
    private int _observationWrite;
    private int _historyRate;
    private int _historyChannels;
    internal int ObservationCount { get; private set; }
    internal object SourceEpoch { get; private set; }
    internal long EndFrame { get; private set; }
    internal bool HasChannelSpectrum { get; private set; }
    internal int WaveCount { get; private set; }
    internal int SampleRate { get; private set; }
    internal int Channels { get; private set; }
    internal int WindowFrames { get; private set; }
    internal double LeftPeak { get; private set; }
    internal double RightPeak { get; private set; }
    internal double LeftRms { get; private set; }
    internal double RightRms { get; private set; }
    internal bool HasSamples => WindowFrames > 0;
    internal bool HasStereo => HasSamples && Channels >= 2;

    // Feeds a window of PCM from the live capture; only frames newer than what we already measured count as "new"
    internal void UpdateObservedWindow(float[] samples, int frames, int channels, int sampleRate,
        long endFrame, long previousFrame, long gapEndFrame, object sourceEpoch = null)
    {
        var newFrames = (int)Math.Min(frames, Math.Max(0, endFrame - Math.Max(previousFrame, gapEndFrame)));
        UpdateWindow(samples, frames, channels, sampleRate, newFrames, endFrame, sourceEpoch);
    }

    // Simpler entry point for callers that just hand over samples (tests, offline use)
    internal void Update(float[] samples, int frames, int channels, int sampleRate, int windowFrames = -1)
    {
        var count = windowFrames == -1 ? frames : windowFrames;
        UpdateWindow(samples, frames, channels, sampleRate, count, EndFrame + Math.Max(0, count), null);
    }

    // Validates input, then measures peak/RMS for both channels and grabs a mono wave snippet for the scope
    private void UpdateWindow(float[] samples, int frames, int channels, int sampleRate, int count,
        long endFrame, object sourceEpoch)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (frames < 0 || (long)frames * channels > samples.Length) throw new ArgumentOutOfRangeException(nameof(frames));
        if (count < 0 || count > frames) throw new ArgumentOutOfRangeException("windowFrames");
        PrepareHistory(sampleRate, channels, sourceEpoch, endFrame);
        ClearMeasurements();
        SampleRate = sampleRate;
        Channels = channels;
        WindowFrames = count;
        if (count == 0) { AppendObservation(endFrame, 0); return; }
        double leftPower = 0, rightPower = 0;
        // Wave only keeps the tail of the window, capped at the max we draw
        WaveCount = Math.Min(count, MaximumWaveSamples);
        var waveStart = frames - WaveCount;
        for (var frame = frames - count; frame < frames; frame++)
        {
            var left = Finite(samples[frame * channels]);
            var right = channels >= 2 ? Finite(samples[frame * channels + 1]) : 0;
            LeftPeak = Math.Max(LeftPeak, Math.Abs((double)left));
            RightPeak = Math.Max(RightPeak, Math.Abs((double)right));
            leftPower += (double)left * left;
            rightPower += (double)right * right;
            if (frame >= waveStart) Wave[frame - waveStart] = left;
        }
        LeftRms = Math.Sqrt(leftPower / count);
        RightRms = Math.Sqrt(rightPower / count);
        AppendObservation(endFrame, count);
    }

    // Observations live in a ring buffer; index 0 is the oldest one still kept
    internal AudioLevelObservation ObservationAt(int index)
    {
        if (index < 0 || index >= ObservationCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _observations[(_observationWrite - ObservationCount + index + MaximumObservations) % MaximumObservations];
    }

    // Throws away old observations when the format, source or timeline changes (time went backwards) since they're not comparable anymore
    private void PrepareHistory(int sampleRate, int channels, object sourceEpoch, long endFrame)
    {
        if (_historyRate != sampleRate || _historyChannels != channels ||
            !ReferenceEquals(SourceEpoch, sourceEpoch) || endFrame < EndFrame)
        {
            ObservationCount = _observationWrite = 0;
            _historyRate = sampleRate;
            _historyChannels = channels;
            SourceEpoch = sourceEpoch;
        }
    }

    // Pushes one observation into the ring, flagging a discontinuity if there's a gap or availability flipped
    private void AppendObservation(long endFrame, int frames)
    {
        var start = endFrame - frames;
        var previous = ObservationCount > 0 ? ObservationAt(ObservationCount - 1) : default;
        EndFrame = endFrame;
        // Don't pile up duplicate "unavailable" entries for the same spot
        if (ObservationCount > 0 && !HasSamples && !previous.Available && previous.EndFrame == endFrame) return;
        _observations[_observationWrite] = new AudioLevelObservation(start, endFrame, LeftRms, LeftPeak,
            RightRms, RightPeak, HasSamples, HasStereo,
            ObservationCount > 0 && (!previous.Available || !HasSamples || start != previous.EndFrame));
        _observationWrite = (_observationWrite + 1) % MaximumObservations;
        ObservationCount = Math.Min(MaximumObservations, ObservationCount + 1);
    }

    // No usable audio right now, but we still log a gap so the timeline stays honest
    internal void SetUnavailableWindow(long endFrame, int channels, int sampleRate, object sourceEpoch)
    {
        PrepareHistory(sampleRate, channels, sourceEpoch, endFrame);
        ClearMeasurements();
        SampleRate = sampleRate;
        Channels = channels;
        AppendObservation(endFrame, 0);
    }

    internal readonly double[] SevenBandPower = new double[SevenBandAnalyzer.BandCount];
    internal bool HasSevenBand { get; private set; }

    // Stores the 7-band result, or clears it when null
    internal void SetSevenBandPower(double[] power)
    {
        HasSevenBand = HasSamples;
        if (power == null) { Array.Clear(SevenBandPower, 0, SevenBandPower.Length); return; }
        if (power.Length < SevenBandPower.Length) throw new ArgumentException("Expected seven bands.", nameof(power));
        Array.Copy(power, SevenBandPower, SevenBandPower.Length);
    }

    // Converts per-channel band power into display bytes; right side stays zero for mono sources
    internal void SetChannelSpectrum(double[] leftPower, double[] rightPower)
    {
        if (leftPower != null && leftPower.Length < LeftSpectrum.Length) throw new ArgumentException("Expected 83 channel bands.", nameof(leftPower));
        if (rightPower != null && rightPower.Length < RightSpectrum.Length) throw new ArgumentException("Expected 83 channel bands.", nameof(rightPower));
        HasChannelSpectrum = HasSamples && leftPower != null && (!HasStereo || rightPower != null);
        for (var band = 0; band < LeftSpectrum.Length; band++)
        {
            LeftSpectrum[band] = leftPower == null ? (byte)0 : LevelScale.ToDisplayByte(LevelScale.PowerToDb(leftPower[band]));
            RightSpectrum[band] = rightPower == null || !HasStereo ? (byte)0 : LevelScale.ToDisplayByte(LevelScale.PowerToDb(rightPower[band]));
        }
    }

    // Deep copy so the UI thread gets a stable snapshot while the analyzer keeps writing to its own instance
    internal void CopyFrom(AudioVisualizationData source)
    {
        if (source == null) { Clear(); return; }
        WaveCount = source.WaveCount;
        SampleRate = source.SampleRate;
        Channels = source.Channels;
        WindowFrames = source.WindowFrames;
        LeftPeak = source.LeftPeak;
        RightPeak = source.RightPeak;
        LeftRms = source.LeftRms;
        RightRms = source.RightRms;
        HasChannelSpectrum = source.HasChannelSpectrum;
        HasSevenBand = source.HasSevenBand;
        Array.Copy(source.SevenBandPower, SevenBandPower, SevenBandPower.Length);
        SourceEpoch = source.SourceEpoch;
        EndFrame = source.EndFrame;
        ObservationCount = source.ObservationCount;
        _observationWrite = source._observationWrite;
        _historyRate = source._historyRate;
        _historyChannels = source._historyChannels;
        Array.Copy(source._observations, _observations, MaximumObservations);
        Array.Copy(source.LeftSpectrum, LeftSpectrum, LeftSpectrum.Length);
        Array.Copy(source.RightSpectrum, RightSpectrum, RightSpectrum.Length);
        Array.Copy(source.Wave, Wave, WaveCount);
        Array.Clear(Wave, WaveCount, Wave.Length - WaveCount);
    }

    // Full reset including history
    internal void Clear()
    {
        ClearMeasurements();
        ObservationCount = _observationWrite = _historyRate = _historyChannels = 0;
        SourceEpoch = null;
        EndFrame = 0;
        Array.Clear(_observations, 0, MaximumObservations);
    }

    // Resets just the current measurements; observation history is kept
    private void ClearMeasurements()
    {
        WaveCount = 0;
        WindowFrames = 0;
        Channels = 0;
        SampleRate = 0;
        LeftPeak = RightPeak = LeftRms = RightRms = 0;
        HasChannelSpectrum = false;
        HasSevenBand = false;
        Array.Clear(SevenBandPower, 0, SevenBandPower.Length);
        Array.Clear(LeftSpectrum, 0, LeftSpectrum.Length);
        Array.Clear(RightSpectrum, 0, RightSpectrum.Length);
        Array.Clear(Wave, 0, Wave.Length);
    }

    // Amplitude (not power) to dB, so 20*log10; zero/inf map to the silence sentinel
    internal static double AmplitudeToDb(double amplitude) =>
        amplitude > 0 && !double.IsInfinity(amplitude) ? 20 * Math.Log10(amplitude) : LevelScale.SilenceDb;

    // Bad samples (NaN/Inf) would poison the meters, so treat them as silence
    private static float Finite(float sample) => float.IsNaN(sample) || float.IsInfinity(sample) ? 0 : sample;
}

// One snapshot of levels over a span of frames; Discontinuity means there was a gap before it
internal readonly struct AudioLevelObservation
{
    internal AudioLevelObservation(long startFrame, long endFrame, double leftRms, double leftPeak,
        double rightRms, double rightPeak, bool available, bool stereo, bool discontinuity)
    {
        StartFrame = startFrame; EndFrame = endFrame;
        LeftRms = leftRms; LeftPeak = leftPeak; RightRms = rightRms; RightPeak = rightPeak;
        Available = available; Stereo = stereo; Discontinuity = discontinuity;
    }
    internal long StartFrame { get; }
    internal long EndFrame { get; }
    internal double LeftRms { get; }
    internal double LeftPeak { get; }
    internal double RightRms { get; }
    internal double RightPeak { get; }
    internal bool Available { get; }
    internal bool Stereo { get; }
    internal bool Discontinuity { get; }
    // Peak-to-RMS ratio in dB, NaN if there's nothing meaningful to measure
    internal double CrestDb(int channel)
    {
        var rms = channel == 0 ? LeftRms : RightRms;
        var peak = channel == 0 ? LeftPeak : RightPeak;
        return Available && (channel == 0 || Stereo) && rms > 0 ? 20 * Math.Log10(peak / rms) : double.NaN;
    }
}
