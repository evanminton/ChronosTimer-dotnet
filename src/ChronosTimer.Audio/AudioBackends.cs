namespace ChronosTimer.Audio;

/// <summary>Picks the audio backend for the current platform.</summary>
public static class AudioBackends
{
    /// <summary>WinMM on Windows, CoreAudio on macOS / Mac Catalyst / iOS, ALSA on Linux; <see cref="NullAudioBackend"/> otherwise.</summary>
    public static IAudioBackend CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WinMmBackend();
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS()) return new CoreAudioBackend();
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() && AlsaBackend.IsAvailable) return new AlsaBackend();
        return NullAudioBackend.Instance;
    }
}
