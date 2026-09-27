using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChronosTimer.Audio;

/// <summary>Windows audio through the WinMM wave API (every Windows version, no admin rights, no dependencies).</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WinMmBackend : IAudioBackend
{
    private const uint WaveMapper = 0xFFFFFFFF;

    public string Name => "WinMM";

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var list = new List<AudioDeviceInfo> { new("default", "Windows default output (sound mapper)", 0, true) };
        uint n = Native.waveOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            Native.WAVEOUTCAPSW caps;
            if (Native.waveOutGetDevCapsW(i, &caps, (uint)sizeof(Native.WAVEOUTCAPSW)) == 0)
                list.Add(new AudioDeviceInfo(i.ToString(System.Globalization.CultureInfo.InvariantCulture), new string(caps.szPname), caps.wChannels, false));
        }
        return list;
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        var list = new List<AudioDeviceInfo> { new("default", "Windows default input (sound mapper)", 0, true) };
        uint n = Native.waveInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            Native.WAVEINCAPSW caps;
            if (Native.waveInGetDevCapsW(i, &caps, (uint)sizeof(Native.WAVEINCAPSW)) == 0)
                list.Add(new AudioDeviceInfo(i.ToString(System.Globalization.CultureInfo.InvariantCulture), new string(caps.szPname), caps.wChannels, false));
        }
        return list;
    }

    private static (uint Id, string Name) Resolve(string? deviceId, IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase)) return (WaveMapper, devices[0].Name);
        var match = devices.FirstOrDefault(d => d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                    ?? devices.Skip(1).FirstOrDefault(d => d.Name.Contains(deviceId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"No audio device '{deviceId}'. Use 'devices' to list them.");
        return match.Id == "default" ? (WaveMapper, match.Name) : (uint.Parse(match.Id, System.Globalization.CultureInfo.InvariantCulture), match.Name);
    }

    public IAudioStream OpenOutput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioRenderCallback render)
    {
        var (id, name) = Resolve(deviceId, GetOutputDevices());
        return new WaveStream(output: true, id, name, sampleRate, channels, bufferFrames, render, null);
    }

    public IAudioStream OpenInput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioCaptureCallback capture)
    {
        var (id, name) = Resolve(deviceId, GetInputDevices());
        return new WaveStream(output: false, id, name, sampleRate, channels, bufferFrames, null, capture);
    }

    private sealed class WaveStream : IAudioStream
    {
        private const int BufferCount = 4;
        private readonly bool _output;
        private readonly nint _handle;
        private readonly Native.WAVEHDR*[] _headers = new Native.WAVEHDR*[BufferCount];
        private readonly AutoResetEvent _event = new(false);
        private readonly Thread _thread;
        private readonly AudioRenderCallback? _render;
        private readonly AudioCaptureCallback? _capture;
        private readonly int _bufferFrames;
        private volatile bool _stop;
        private float[] _floats;

        public WaveStream(bool output, uint id, string name, int sampleRate, int channels, int bufferFrames, AudioRenderCallback? render, AudioCaptureCallback? capture)
        {
            _output = output;
            _render = render;
            _capture = capture;
            _bufferFrames = bufferFrames;
            SampleRate = sampleRate;
            Channels = channels;
            DeviceName = name;
            _floats = new float[bufferFrames * channels];

            var fmt = new Native.WAVEFORMATEX
            {
                wFormatTag = 1, // PCM
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(channels * 2),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                cbSize = 0,
            };
            nint evt = _event.SafeWaitHandle.DangerousGetHandle();
            nint h;
            int err = output
                ? Native.waveOutOpen(&h, id, &fmt, evt, 0, Native.CALLBACK_EVENT)
                : Native.waveInOpen(&h, id, &fmt, evt, 0, Native.CALLBACK_EVENT);
            if (err != 0) throw new InvalidOperationException($"Could not open '{name}' ({sampleRate} Hz, {channels} ch): {Error(err, output)}");
            _handle = h;

            uint bytes = (uint)(bufferFrames * channels * 2);
            for (int i = 0; i < BufferCount; i++)
            {
                var hdr = (Native.WAVEHDR*)NativeMemory.AllocZeroed((nuint)sizeof(Native.WAVEHDR));
                hdr->lpData = (nint)NativeMemory.AllocZeroed(bytes);
                hdr->dwBufferLength = bytes;
                _headers[i] = hdr;
                err = output ? Native.waveOutPrepareHeader(_handle, hdr, (uint)sizeof(Native.WAVEHDR)) : Native.waveInPrepareHeader(_handle, hdr, (uint)sizeof(Native.WAVEHDR));
                if (err != 0) { Dispose(); throw new InvalidOperationException("Could not prepare audio buffers: " + Error(err, output)); }
            }

            LatencySamples = output ? (BufferCount - 1) * bufferFrames : bufferFrames;
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
            try
            {
                if (_output)
                {
                    for (int i = 0; i < BufferCount; i++) FillAndWrite(_headers[i]);
                }
                else
                {
                    for (int i = 0; i < BufferCount; i++) Check(Native.waveInAddBuffer(_handle, _headers[i], (uint)sizeof(Native.WAVEHDR)));
                    Check(Native.waveInStart(_handle));
                }
                int next = 0;
                while (!_stop)
                {
                    _event.WaitOne(200);
                    while (!_stop && (_headers[next]->dwFlags & Native.WHDR_DONE) != 0)
                    {
                        var hdr = _headers[next];
                        if (_output) FillAndWrite(hdr);
                        else
                        {
                            int samples = (int)(hdr->dwBytesRecorded / 2);
                            if (_floats.Length < samples) _floats = new float[samples];
                            short* src = (short*)hdr->lpData;
                            for (int k = 0; k < samples; k++) _floats[k] = src[k] / 32768f;
                            samples -= samples % Channels;
                            _capture!(_floats.AsSpan(0, samples), Channels);
                            hdr->dwFlags &= ~Native.WHDR_DONE;
                            Check(Native.waveInAddBuffer(_handle, hdr, (uint)sizeof(Native.WAVEHDR)));
                        }
                        next = (next + 1) % BufferCount;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!_stop) Failed?.Invoke(ex);
            }
        }

        private void FillAndWrite(Native.WAVEHDR* hdr)
        {
            var span = _floats.AsSpan(0, _bufferFrames * Channels);
            _render!(span, Channels);
            short* dst = (short*)hdr->lpData;
            for (int k = 0; k < span.Length; k++)
            {
                float v = Math.Clamp(span[k], -1f, 1f);
                dst[k] = (short)Math.Round(v * 32767f);
            }
            hdr->dwFlags &= ~Native.WHDR_DONE;
            Check(Native.waveOutWrite(_handle, hdr, (uint)sizeof(Native.WAVEHDR)));
        }

        private void Check(int err)
        {
            if (err != 0) throw new InvalidOperationException(Error(err, _output));
        }

        public void Dispose()
        {
            if (_stop) return;
            _stop = true;
            _event.Set();
            if (_thread is not null && _thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(1000);
            if (_handle != 0)
            {
                if (_output) Native.waveOutReset(_handle); else { Native.waveInStop(_handle); Native.waveInReset(_handle); }
                foreach (var hdr in _headers)
                {
                    if (hdr is null) continue;
                    if (_output) Native.waveOutUnprepareHeader(_handle, hdr, (uint)sizeof(Native.WAVEHDR));
                    else Native.waveInUnprepareHeader(_handle, hdr, (uint)sizeof(Native.WAVEHDR));
                }
                if (_output) Native.waveOutClose(_handle); else Native.waveInClose(_handle);
            }
            for (int i = 0; i < _headers.Length; i++)
            {
                if (_headers[i] is null) continue;
                NativeMemory.Free((void*)_headers[i]->lpData);
                NativeMemory.Free(_headers[i]);
                _headers[i] = null;
            }
            _event.Dispose();
        }

        private static string Error(int err, bool output)
        {
            char* buf = stackalloc char[256];
            int r = output ? Native.waveOutGetErrorTextW(err, buf, 256) : Native.waveInGetErrorTextW(err, buf, 256);
            return r == 0 ? new string(buf) + $" (MMRESULT {err})" : $"MMRESULT {err}";
        }
    }

    private static class Native
    {
        public const uint CALLBACK_EVENT = 0x00050000;
        public const uint WHDR_DONE = 0x00000001;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public nint lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public nint dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public nint lpNext;
            public nint reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            public fixed char szPname[32];
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEINCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            public fixed char szPname[32];
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll")] public static extern int waveOutGetDevCapsW(nuint uDeviceID, WAVEOUTCAPSW* pwoc, uint cbwoc);
        [DllImport("winmm.dll")] public static extern int waveOutOpen(nint* phwo, uint uDeviceID, WAVEFORMATEX* pwfx, nint dwCallback, nint dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(nint hwo, WAVEHDR* pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(nint hwo, WAVEHDR* pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(nint hwo, WAVEHDR* pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutReset(nint hwo);
        [DllImport("winmm.dll")] public static extern int waveOutClose(nint hwo);
        [DllImport("winmm.dll")] public static extern int waveOutGetErrorTextW(int mmrError, char* pszText, uint cchText);

        [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll")] public static extern int waveInGetDevCapsW(nuint uDeviceID, WAVEINCAPSW* pwic, uint cbwic);
        [DllImport("winmm.dll")] public static extern int waveInOpen(nint* phwi, uint uDeviceID, WAVEFORMATEX* pwfx, nint dwCallback, nint dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(nint hwi, WAVEHDR* pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(nint hwi, WAVEHDR* pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(nint hwi, WAVEHDR* pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveInStart(nint hwi);
        [DllImport("winmm.dll")] public static extern int waveInStop(nint hwi);
        [DllImport("winmm.dll")] public static extern int waveInReset(nint hwi);
        [DllImport("winmm.dll")] public static extern int waveInClose(nint hwi);
        [DllImport("winmm.dll")] public static extern int waveInGetErrorTextW(int mmrError, char* pszText, uint cchText);
    }
}
