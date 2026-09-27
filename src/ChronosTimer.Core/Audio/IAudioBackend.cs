namespace ChronosTimer.Audio;

/// <summary>An audio device as listed by a backend.</summary>
/// <param name="Id">Identifier passed back to <see cref="IAudioBackend.OpenOutput"/> / <see cref="IAudioBackend.OpenInput"/>.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Channels">Channel count, if known (0 = unknown).</param>
/// <param name="IsDefault">True for the system default device.</param>
public sealed record AudioDeviceInfo(string Id, string Name, int Channels, bool IsDefault)
{
    public override string ToString() => $"{Id,-12} {Name}{(Channels > 0 ? $" ({Channels} ch)" : "")}{(IsDefault ? "  [default]" : "")}";
}

/// <summary>Fills an interleaved float buffer (−1…1) for output.</summary>
public delegate void AudioRenderCallback(Span<float> interleaved, int channels);

/// <summary>Receives an interleaved float buffer (−1…1) from input.</summary>
public delegate void AudioCaptureCallback(ReadOnlySpan<float> interleaved, int channels);

/// <summary>An open audio stream. Dispose to close it.</summary>
public interface IAudioStream : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Approximate samples between the callback and the converter (buffered audio).</summary>
    int LatencySamples { get; }

    /// <summary>Human-readable device name.</summary>
    string DeviceName { get; }

    /// <summary>Raised on the audio thread if the stream fails (device removed, driver error).</summary>
    event Action<Exception>? Failed;
}

/// <summary>A platform audio API.</summary>
public interface IAudioBackend
{
    /// <summary>Backend name, e.g. "WinMM", "CoreAudio", "ALSA", "Android".</summary>
    string Name { get; }

    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    /// <summary>Opens an output stream. <paramref name="deviceId"/> null or "default" = the default device.</summary>
    IAudioStream OpenOutput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioRenderCallback render);

    /// <summary>Opens an input stream.</summary>
    IAudioStream OpenInput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioCaptureCallback capture);
}

/// <summary>No audio: lists nothing, opens nothing. Used when no platform backend is available.</summary>
public sealed class NullAudioBackend : IAudioBackend
{
    public static NullAudioBackend Instance { get; } = new();

    public string Name => "None";
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [];
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [];

    public IAudioStream OpenOutput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioRenderCallback render) =>
        throw new NotSupportedException("No audio backend is available on this platform.");

    public IAudioStream OpenInput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioCaptureCallback capture) =>
        throw new NotSupportedException("No audio backend is available on this platform.");
}
