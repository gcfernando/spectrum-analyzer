// Developer :: > Gehan Fernando
namespace Spectrum
{
    // Simple DTO for an audio endpoint we show in the device picker
    public class Device
    {
        public int Index { get; set; }
        public string DeviceName { get; set; }
        public string DeviceId { get; set; }
    }

    // Lifecycle of the capture pipeline, so the UI can show what's going on
    public enum CaptureState { Starting, Capturing, Silent, Unavailable, Recovering, Stopped }

    // Immutable snapshot of capture health, handed to the UI whenever the state changes
    public sealed class CaptureStatus
    {
        // Internal ctor: only the capture engine should create these
        internal CaptureStatus(CaptureState state, string message, string deviceId, string deviceName,
            int? errorCode, int sampleRate, int channels)
        {
            State = state; Message = message; DeviceId = deviceId; DeviceName = deviceName;
            ErrorCode = errorCode; SampleRate = sampleRate; Channels = channels;
        }
        public CaptureState State { get; }
        public string Message { get; }
        public string DeviceId { get; }
        public string DeviceName { get; }
        // Null when there's no error; otherwise the raw code from the audio API
        public int? ErrorCode { get; }
        public int SampleRate { get; }
        public int Channels { get; }
    }
}