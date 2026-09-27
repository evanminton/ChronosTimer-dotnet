using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ChronosTimer.Audio;

/// <summary>Linux audio through ALSA (libasound.so.2). Devices: "default", or "plughw:card,device" from /proc/asound/pcm.</summary>
public sealed unsafe class AlsaBackend : IAudioBackend
{
    public string Name => "ALSA";

    /// <summary>True when libasound can be loaded.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!NativeLibrary.TryLoad("libasound.so.2", out nint h)) return false;
            NativeLibrary.Free(h);
            return true;
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => List(playback: true);
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => List(playback: false);

    private static List<AudioDeviceInfo> List(bool playback)
    {
        var list = new List<AudioDeviceInfo> { new("default", "ALSA default (PulseAudio / PipeWire if present)", 0, true) };
        try
        {
            foreach (string line in File.ReadAllLines("/proc/asound/pcm"))
            {
                // "00-00: ALC892 Analog : ALC892 Analog : playback 1 : capture 1"
                string[] f = line.Split(':');
                if (f.Length < 3) continue;
                bool has = f.Skip(3).Any(x => x.Trim().StartsWith(playback ? "playback" : "capture", StringComparison.Ordinal));
                if (!has) continue;
                string[] cd = f[0].Trim().Split('-');
                if (cd.Length != 2 || !int.TryParse(cd[0], out int card) || !int.TryParse(cd[1], out int dev)) continue;
                list.Add(new AudioDeviceInfo(string.Create(CultureInfo.InvariantCulture, $"plughw:{card},{dev}"), f[1].Trim(), 0, false));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    public IAudioStream OpenOutput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioRenderCallback render) =>
        new PcmStream(true, Pick(deviceId), sampleRate, channels, bufferFrames, render, null);

    public IAudioStream OpenInput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioCaptureCallback capture) =>
        new PcmStream(false, Pick(deviceId), sampleRate, channels, bufferFrames, null, capture);

    private static string Pick(string? id) => string.IsNullOrWhiteSpace(id) ? "default" : id.Trim();

    private sealed class PcmStream : IAudioStream
    {
        private const int Periods = 3;
        private readonly bool _output;
        private readonly nint _pcm;
        private readonly Thread _thread;
        private readonly AudioRenderCallback? _render;
        private readonly AudioCaptureCallback? _capture;
        private readonly int _bufferFrames;
        private volatile bool _stop;

        public PcmStream(bool output, string device, int sampleRate, int channels, int bufferFrames, AudioRenderCallback? render, AudioCaptureCallback? capture)
        {
            _output = output;
            _render = render;
            _capture = capture;
            _bufferFrames = bufferFrames;
            SampleRate = sampleRate;
            Channels = channels;
            DeviceName = device;

            nint pcm;
            byte[] name = Encoding.UTF8.GetBytes(device + "\0");
            int err;
            fixed (byte* n = name) err = Native.snd_pcm_open(&pcm, n, output ? 0 : 1, 0);
            if (err < 0) throw new InvalidOperationException($"Could not open ALSA device '{device}': {Native.Error(err)}");
            _pcm = pcm;
            uint latencyUs = (uint)((long)bufferFrames * Periods * 1_000_000 / sampleRate);
            err = Native.snd_pcm_set_params(_pcm, Native.SND_PCM_FORMAT_FLOAT_LE, Native.SND_PCM_ACCESS_RW_INTERLEAVED, (uint)channels, (uint)sampleRate, 1, latencyUs);
            if (err < 0) { Native.snd_pcm_close(_pcm); throw new InvalidOperationException($"ALSA device '{device}' can't do {sampleRate} Hz, {channels} ch float: {Native.Error(err)}"); }
            LatencySamples = output ? (Periods - 1) * bufferFrames : bufferFrames;
            _thread = new Thread(Run) { IsBackground = true, Name = output ? "LTC output" : "LTC input", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        public int SampleRate { get; }
        public int Channels { get; }
        public int LatencySamples { get; }
        public string DeviceName { get; }
        public event Action<Exception>? Failed;

        private void Run()
        {
            var buf = new float[_bufferFrames * Channels];
            try
            {
                while (!_stop)
                {
                    if (_output) _render!(buf, Channels);
                    int done = 0;
                    while (done < _bufferFrames && !_stop)
                    {
                        nint r;
                        fixed (float* p = &buf[done * Channels])
                            r = _output ? Native.snd_pcm_writei(_pcm, p, (nuint)(_bufferFrames - done)) : Native.snd_pcm_readi(_pcm, p, (nuint)(_bufferFrames - done));
                        if (r < 0)
                        {
                            int rec = Native.snd_pcm_recover(_pcm, (int)r, 1);
                            if (rec < 0) throw new InvalidOperationException("ALSA: " + Native.Error((int)r));
                            continue;
                        }
                        done += (int)r;
                    }
                    if (!_output && done > 0) _capture!(buf.AsSpan(0, done * Channels), Channels);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_stop) Failed?.Invoke(ex);
            }
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            Native.snd_pcm_drop(_pcm);
            if (Thread.CurrentThread != _thread) _thread.Join(1000);
            Native.snd_pcm_close(_pcm);
        }
    }

    private static class Native
    {
        private const string Lib = "libasound.so.2";
        public const int SND_PCM_FORMAT_FLOAT_LE = 14;
        public const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;

        [DllImport(Lib)] public static extern int snd_pcm_open(nint* pcm, byte* name, int stream, int mode);
        [DllImport(Lib)] public static extern int snd_pcm_set_params(nint pcm, int format, int access, uint channels, uint rate, int softResample, uint latencyUs);
        [DllImport(Lib)] public static extern nint snd_pcm_writei(nint pcm, void* buffer, nuint frames);
        [DllImport(Lib)] public static extern nint snd_pcm_readi(nint pcm, void* buffer, nuint frames);
        [DllImport(Lib)] public static extern int snd_pcm_recover(nint pcm, int err, int silent);
        [DllImport(Lib)] public static extern int snd_pcm_drop(nint pcm);
        [DllImport(Lib)] public static extern int snd_pcm_close(nint pcm);
        [DllImport(Lib)] private static extern byte* snd_strerror(int errnum);

        public static string Error(int err) => Marshal.PtrToStringUTF8((nint)snd_strerror(err)) ?? $"error {err}";
    }
}
