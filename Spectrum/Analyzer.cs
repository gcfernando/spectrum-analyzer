using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Spectrum.Dsp;
using Un4seen.Bass;
using Un4seen.BassWasapi;
using ElapsedEventArgs = System.Timers.ElapsedEventArgs;
using Timer = System.Timers.Timer;

namespace Spectrum;

public delegate void OnChangeHandler(object obj, OnChangeEventArgs e);

public sealed class OnChangeEventArgs : EventArgs
{
    public IReadOnlyList<byte> Spectrumdata { get; }
    public OnChangeEventArgs(byte[] spectrumdata) => Spectrumdata = spectrumdata;
}

public sealed class Analyzer : IDisposable
{
    public static event OnChangeHandler OnChange;

    private const int LINES = 83;

    // The 83 logarithmic band centres span 20 Hz to 20 kHz.
    private const double FIRST_CENTER_HZ = 20.0;
    private const double LAST_CENTER_HZ = 20000.0;

    // Each timer tick analyzes one STFT frame; its hop stays within half the shortest window to cover every sample.
    private const int TIMER_INTERVAL_MS = 25;
    private const int HANG_THRESHOLD = 8;
    private const int SILENCE_FRAMES_REQUIRED = 4;

    // The 10 ms WASAPI period supplies fresh audio between 25 ms analysis ticks and preserves transient timing.
    private const float WASAPI_BUFFER_SECONDS = 1f;
    private const float WASAPI_PERIOD_SECONDS = 0.01f;

    // History capacity is a multiple of the longest FFT to allow lock-free snapshots.
    private const int HISTORY_CAPACITY_FACTOR = 4;

    private readonly Timer _timer;
    private readonly byte[] _spectrumData;
    private readonly double[] _bandPower;

    // Replaced atomically whenever capture restarts; records analysis state and its starting frame count.
    private volatile CaptureStream _stream;

    // Timer-thread state for silence, stall, and gap decisions.
    private readonly CaptureGate _gate = new(SILENCE_FRAMES_REQUIRED, HANG_THRESHOLD);

    // Prevents device recovery from starting a second tick while shared buffers are in use.
    private int _tickActive;

    private readonly byte[] _fireData;
    private readonly OnChangeEventArgs _fireEventArgs;

    private readonly WASAPIPROC _process;
    private static readonly object _sender = new();

    private readonly SynchronizationContext _syncContext;

    private NAudio.CoreAudioApi.MMDeviceEnumerator _mmEnumerator;
    private readonly DeviceNotificationClient _deviceNotificationClient;


    private bool _initialized;
    private volatile bool _disposed;
    private volatile bool _recovering; // True while device recovery is pending on the UI thread.

    private int _sampleRate = 48000; // Updated from WASAPI after initialization.

    public int SelectIndex { get; set; }

    /// <summary>Band plan currently used for analysis (depends on the device sample rate).</summary>
    internal BandPlan CurrentBandPlan => _stream?.State.Engine.Plan;

    public Analyzer()
    {
        BassNet.Registration("buddyknox@usa.org", "2X11841782815");

        _syncContext = SynchronizationContext.Current;

        _spectrumData = new byte[LINES];
        _bandPower = new double[LINES];

        _fireData = new byte[LINES];
        _fireEventArgs = new OnChangeEventArgs(_fireData);

        _stream = new CaptureStream(AnalysisState.Create(_sampleRate, 2));

        _process = Process;

        _timer = new Timer { Interval = TIMER_INTERVAL_MS, AutoReset = false };
        _timer.Elapsed += TimerTick;

        _ = Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATETHREADS, 0);
        _ = Bass.BASS_Init(0, 48000, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);

        _ = DeviceList();
        Enable(true);

        try
        {
            _mmEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            _deviceNotificationClient = new DeviceNotificationClient(OnAudioDeviceChanged);
            _ = _mmEnumerator.RegisterEndpointNotificationCallback(_deviceNotificationClient);
        }
        catch { }
    }

    private void OnAudioDeviceChanged()
    {
        if (_disposed || _recovering)
        {
            return;
        }

        _recovering = true;

        var ctx = _syncContext;
        if (ctx != null)
        {
            ctx.Post(_ =>
            {
                if (_disposed)
                { _recovering = false; return; }
                _timer.Stop();
                Free();
                _ = Bass.BASS_Init(0, 48000, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);
                _initialized = false;
                _ = DeviceList();
                _recovering = false;
                Enable(true);
            }, null);
        }
        else
        {
            _recovering = false;
        }
    }

    private List<Device> DeviceList()
    {
        var count = BassWasapi.BASS_WASAPI_GetDeviceCount();
        var devices = new List<Device>(Math.Max(0, count));

        for (var i = 0; i < count; i++)
        {
            var di = BassWasapi.BASS_WASAPI_GetDeviceInfo(i);
            var flags = di.flags;

            if ((flags & BASSWASAPIDeviceInfo.BASS_DEVICE_ENABLED) != 0 &&
                (flags & BASSWASAPIDeviceInfo.BASS_DEVICE_INPUT) != 0 &&
                (flags & BASSWASAPIDeviceInfo.BASS_DEVICE_LOOPBACK) != 0)
            {
                devices.Add(new Device { Index = i, DeviceName = di.name });
            }
        }

        // Prefer the Windows default render endpoint.
        Device device = null;
        try
        {
            var mmEnum = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var defaultDev = mmEnum.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.Role.Multimedia);
            var defaultName = defaultDev.FriendlyName;
            device = devices.FirstOrDefault(d =>
                string.Equals(d.DeviceName, defaultName, StringComparison.OrdinalIgnoreCase)
                || d.DeviceName.IndexOf(defaultName, StringComparison.OrdinalIgnoreCase) >= 0
                || defaultName.IndexOf(d.DeviceName, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch { }

        // Fall back to matching common output device names.
        device ??=
            devices.FirstOrDefault(d => d.DeviceName.IndexOf("Headphones", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? devices.FirstOrDefault(d => d.DeviceName.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? devices.FirstOrDefault(d => d.DeviceName.IndexOf("Speakers", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? devices.FirstOrDefault();

        if (device != null)
        {
            SelectIndex = device.Index;
        }

        return devices;
    }

    private void Enable(bool value)
    {
        if (_disposed)
        {
            return;
        }

        if (value)
        {
            if (!_initialized)
            {
                _ = BassWasapi.BASS_WASAPI_GetDeviceInfo(SelectIndex);

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
                    var error = Bass.BASS_ErrorGetCode();
                    System.Diagnostics.Debug.WriteLine($"WASAPI Init failed: {error}");
                    return;
                }

                var channels = 2;
                try
                {
                    var info = BassWasapi.BASS_WASAPI_GetInfo();
                    if (info.freq > 0)
                    {
                        _sampleRate = info.freq;
                    }

                    if (info.chans > 0)
                    {
                        channels = info.chans;
                    }
                }
                catch
                {
                    _sampleRate = 48000;
                }

                // Reuse history unless the device format changed, avoiding large allocations during recovery.
                var state = _stream?.State;
                if (state == null || state.SampleRate != _sampleRate || state.History.Channels != channels)
                {
                    state = AnalysisState.Create(_sampleRate, channels);
                }

                _stream = new CaptureStream(state);

                _initialized = true;
            }

            _ = BassWasapi.BASS_WASAPI_Start();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _ = BassWasapi.BASS_WASAPI_Stop(true);
            _initialized = false;
            Free();
        }
    }

    // Copy captured frames into history; keep this callback allocation-free and free of analysis or locks.
    private int Process(IntPtr buffer, int length, IntPtr user)
    {
        _stream?.State.History.Write(buffer, length);
        return length;
    }

    public void Free()
    {
        if (_disposed)
        {
            return;
        }

        WaitForTickToFinish();

        _ = BassWasapi.BASS_WASAPI_Free();
        _ = Bass.BASS_Free();
    }

    // Wait briefly for analysis to finish before freeing BASS handles used by the timer thread.
    private void WaitForTickToFinish()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _tickActive) != 0 && sw.ElapsedMilliseconds < 200)
        {
            Thread.Sleep(1);
        }
    }

    private void TimerTick(object sender, ElapsedEventArgs e)
    {
        if (_disposed || _recovering)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _tickActive, 1, 0) != 0)
        {
            return; // The active tick restarts the timer in its finally block.
        }

        try
        {
            var stream = _stream;
            var state = stream.State;
            var level = BassWasapi.BASS_WASAPI_GetLevel();
            var decision = _gate.Next(level, state.History.TotalFrames, stream, stream.StartFrame);

            switch (decision.Action)
            {
                case CaptureAction.Analyze:
                    AnalyzeAndFire(state);
                    break;
                case CaptureAction.PublishSilence:
                    PublishSilence();
                    break;
            }

            if (decision.RecoverDevice)
            {
                RecoverHungDevice();
            }
        }
        finally
        {
            Volatile.Write(ref _tickActive, 0);

            if (!_disposed && !_recovering)
            {
                try
                { _timer.Start(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    // Publish the floor and let presentation ballistics decay the bars.
    private void PublishSilence()
    {
        Array.Clear(_spectrumData, 0, LINES);
        FireOnChange();
    }

    private void AnalyzeAndFire(AnalysisState state)
    {
        var frames = state.Engine.RequiredFrames;
        if (!state.History.TryCopyLatest(state.Snapshot, frames, out _, _gate.GapEndFrame))
        {
            return; // Keep the previous complete frame if the producer overwrote this snapshot.
        }

        state.Engine.Analyze(state.Snapshot, frames, _bandPower);

        for (var i = 0; i < LINES; i++)
        {
            _spectrumData[i] = LevelScale.ToDisplayByte(LevelScale.PowerToDb(_bandPower[i]));
        }

        FireOnChange();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FireOnChange()
    {
        Buffer.BlockCopy(_spectrumData, 0, _fireData, 0, LINES);
        OnChange?.Invoke(_sender, _fireEventArgs);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // CaptureGate requires stalled frames and a frozen non-zero level to detect a device hang.
    private void RecoverHungDevice()
    {
        _recovering = true;

        var ctx = _syncContext;
        if (ctx != null)
        {
            ctx.Post(_ =>
            {
                if (_disposed)
                { _recovering = false; return; }
                Free();
                _ = Bass.BASS_Init(0, 48000, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);
                _initialized = false;
                _recovering = false;
                Enable(true);
            }, null);
        }
        else
        {
            // Run recovery inline and keep _tickActive set so concurrent disposal waits for teardown to finish.
            _ = BassWasapi.BASS_WASAPI_Free();
            _ = Bass.BASS_Free();
            _ = Bass.BASS_Init(0, 48000, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);
            _initialized = false;
            _recovering = false;
            Enable(true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mmEnumerator != null)
        {
            try
            {
                if (_deviceNotificationClient != null)
                {
                    _ = _mmEnumerator.UnregisterEndpointNotificationCallback(_deviceNotificationClient);
                }
            }
            catch { }
            _mmEnumerator = null;
        }

        _timer.Stop();
        _timer.Elapsed -= TimerTick;
        _timer.Dispose();

        // Free() returns when disposed, so clean up BASS directly.
        WaitForTickToFinish();
        _ = BassWasapi.BASS_WASAPI_Free();
        _ = Bass.BASS_Free();

        OnChange = null;
    }

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

    private sealed class AnalysisState
    {
        private AnalysisState(BandSpectrumAnalyzer engine, SampleHistory history)
        {
            Engine = engine;
            History = history;
            Snapshot = new float[engine.RequiredFrames * history.Channels];
        }

        public BandSpectrumAnalyzer Engine { get; }
        public SampleHistory History { get; }
        public int SampleRate => Engine.Plan.SampleRate;

        // Used only by the analysis thread.
        public float[] Snapshot { get; }

        public static AnalysisState Create(int sampleRate, int channels)
        {
            var plan = BandPlan.CreateLogarithmic(LINES, FIRST_CENTER_HZ, LAST_CENTER_HZ, sampleRate);
            var engine = new BandSpectrumAnalyzer(plan, channels);
            var history = new SampleHistory(channels, engine.RequiredFrames * HISTORY_CAPACITY_FACTOR);
            return new AnalysisState(engine, history);
        }
    }

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
            if (flow == NAudio.CoreAudioApi.DataFlow.Render &&
                role == NAudio.CoreAudioApi.Role.Multimedia)
            {
                _onDefaultChanged();
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, NAudio.CoreAudioApi.DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, NAudio.CoreAudioApi.PropertyKey key) { }
    }
}