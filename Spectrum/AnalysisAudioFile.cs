// Developer :: > Gehan Fernando
using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Spectrum;

// Endpoint IDs remain stable when the native device list changes order.
internal sealed class AnalysisAudioSource
{
    internal static readonly AnalysisAudioSource Default = new AnalysisAudioSource(
        "@default-render", "Follow Windows default output", true, true, -1);

    internal AnalysisAudioSource(string id, string name, bool isDefault, bool isLoopback, int deviceIndex)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A source needs an endpoint identity.", nameof(id));
        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? "Audio endpoint" : name;
        IsDefault = isDefault;
        IsLoopback = isLoopback;
        DeviceIndex = deviceIndex;
    }

    internal string Id { get; }
    internal string Name { get; }
    internal bool IsDefault { get; }
    internal bool IsLoopback { get; }
    internal int DeviceIndex { get; }
    public override string ToString() => IsDefault ? Name : Name + (IsLoopback ? " (system output)" : " (input)");
}

internal sealed class AnalysisAudioFileData
{
    internal AnalysisAudioFileData(float[] samples, int sampleRate, int channels)
    {
        if (samples == null) throw new ArgumentNullException(nameof(samples));
        if (channels < 1 || channels > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Length % channels != 0) throw new ArgumentException("Audio must contain complete frames.", nameof(samples));
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    internal float[] Samples { get; }
    internal int SampleRate { get; }
    internal int Channels { get; }
    internal int FrameCount => Samples.Length / Channels;
    internal double DurationSeconds => (double)FrameCount / SampleRate;
}

// File decoding is deliberately independent of BASS's shared capture initialization/device lifetime.
// WAV/MP3/AIFF use the bundled NAudio readers; other supported containers use Windows Media Foundation.
internal static class AnalysisAudioFile
{
    internal const int MaximumSamples = 16 * 1024 * 1024; // 64 MiB decoded float PCM, appropriate for this x86 app.
    internal const double MaximumDurationSeconds = 600;
    internal const string FileFilter = "Audio files|*.wav;*.mp3;*.flac;*.aif;*.aiff;*.wma;*.m4a|All files|*.*";

    internal static AnalysisAudioFileData Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose an audio file.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected audio file does not exist.", fullPath);
        try
        {
            // Own WAV/MP3 input streams before parsing their headers. The bundled reader's path
            // constructor can throw before it becomes disposable when a malformed header is read.
            var extension = Path.GetExtension(fullPath);
            var isWave = string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase);
            var isMp3 = string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase);
            using var fileInput = isWave || isMp3
                ? File.OpenRead(fullPath) : null;
            using WaveStream reader = isWave ? new WaveFileReader(fileInput) : isMp3 ?
                new Mp3FileReader(fileInput) : (WaveStream)new AudioFileReader(fullPath);
            ISampleProvider provider = reader is AudioFileReader audioReader ? audioReader : new SampleChannel(reader, false);
            var rate = provider.WaveFormat.SampleRate;
            var channels = provider.WaveFormat.Channels;
            if (rate < 1 || rate > 384000 || channels < 1 || channels > 32)
                throw new InvalidDataException("This audio format is outside the supported sample-rate/channel limits.");
            var decodedSamples = reader is AudioFileReader ? reader.Length / sizeof(float) :
                reader.Length / reader.WaveFormat.BlockAlign * channels;
            if (reader.TotalTime.TotalSeconds > MaximumDurationSeconds || decodedSamples > MaximumSamples)
                throw new InvalidDataException("Choose a shorter audio section: file analysis supports up to 10 minutes and 64 MiB of decoded PCM.");

            var capacity = (int)Math.Min(MaximumSamples, Math.Max(0, decodedSamples));
            var samples = new List<float>(capacity);
            var buffer = new float[8192 * channels];
            var maximumDurationSamples = (long)(MaximumDurationSeconds * rate) * channels;
            while (true)
            {
                var count = provider.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                if (count < 0 || count > buffer.Length) throw new InvalidDataException("The audio decoder returned an invalid sample count.");
                if ((long)samples.Count + count > MaximumSamples || (long)samples.Count + count > maximumDurationSamples)
                    throw new InvalidDataException("Choose a shorter audio section: file analysis supports up to 10 minutes and 64 MiB of decoded PCM.");
                for (var index = 0; index < count; index++)
                {
                    var sample = buffer[index];
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                        throw new InvalidDataException("The decoded audio contains a non-finite sample and cannot be measured.");
                    samples.Add(sample);
                }
            }

            if (samples.Count == 0) throw new InvalidDataException("The selected audio file contains no decoded samples.");
            if (samples.Count % channels != 0) throw new InvalidDataException("The audio decoder returned an incomplete final frame.");
            return new AnalysisAudioFileData(samples.ToArray(), rate, channels);
        }
        catch (InvalidDataException) { throw; }
        catch (IOException) { throw; }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception exception) when (exception is ArgumentException || exception is FormatException ||
            exception is InvalidOperationException || exception is System.Runtime.InteropServices.COMException ||
            exception is NotSupportedException || exception is DllNotFoundException)
        {
            throw new InvalidDataException("The file could not be decoded. Try PCM WAV or MP3; FLAC and other formats require a supported Windows Media Foundation decoder. " +
                exception.Message, exception);
        }
    }
}
