using Android.Media;
using ChronosTimer.Audio;

namespace ChronosTimer.App;

/// <summary>Android audio: AudioTrack (float) for LTC output, AudioRecord (16-bit) for LTC input. Default devices only.</summary>
public sealed class AndroidAudioBackend : IAudioBackend
{
    public string Name => "Android";

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [new("default", "Default output (headphones / USB audio when connected)", 2, true)];
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [new("default", "Default input (mic / headset / USB audio)", 1, true)];

    public IAudioStream OpenOutput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioRenderCallback render) =>
        new TrackStream(sampleRate, Math.Clamp(channels, 1, 2), bufferFrames, render);

    public IAudioStream OpenInput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioCaptureCallback capture) =>
        new RecordStream(sampleRate, Math.Clamp(channels, 1, 2), bufferFrames, capture);

    private sealed class TrackStream : IAudioStream
    {
        private readonly AudioTrack _track;
        private readonly Thread _thread;
        private readonly AudioRenderCallback _render;
        private readonly int _bufferFrames;
        private volatile bool _stop;

        public TrackStream(int sampleRate, int channels, int bufferFrames, AudioRenderCallback render)
        {
            _render = render;
            _bufferFrames = bufferFrames;
            SampleRate = sampleRate;
            Channels = channels;
            var mask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
            int min = AudioTrack.GetMinBufferSize(sampleRate, mask, Encoding.PcmFloat);
            int bytes = Math.Max(min, bufferFrames * channels * 4 * 2);
            _track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)!
                .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.PcmFloat)!.SetSampleRate(sampleRate)!.SetChannelMask(mask)!.Build()!)!
                .SetBufferSizeInBytes(bytes)!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;
            LatencySamples = bytes / (channels * 4);
            _track.Play();
            _thread = new Thread(Run) { IsBackground = true, Name = "LTC output", Priority = System.Threading.ThreadPriority.Highest };
            _thread.Start();
        }

        public int SampleRate { get; }
        public int Channels { get; }
        public int LatencySamples { get; }
        public string DeviceName => "Android default output";
        public event Action<Exception>? Failed;

        private void Run()
        {
            var buf = new float[_bufferFrames * Channels];
            try
            {
                while (!_stop)
                {
                    _render(buf, Channels);
                    int off = 0;
                    while (off < buf.Length && !_stop)
                    {
                        int n = _track.Write(buf, off, buf.Length - off, WriteMode.Blocking);
                        if (n < 0) throw new InvalidOperationException($"AudioTrack.Write failed ({n}).");
                        off += n;
                    }
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
            try { _track.Pause(); _track.Flush(); _track.Stop(); } catch (Java.Lang.IllegalStateException) { }
            _thread.Join(1000);
            _track.Release();
            _track.Dispose();
        }
    }

    private sealed class RecordStream : IAudioStream
    {
        private readonly AudioRecord _rec;
        private readonly Thread _thread;
        private readonly AudioCaptureCallback _capture;
        private readonly int _bufferFrames;
        private volatile bool _stop;

        public RecordStream(int sampleRate, int channels, int bufferFrames, AudioCaptureCallback capture)
        {
            _capture = capture;
            _bufferFrames = bufferFrames;
            SampleRate = sampleRate;
            Channels = channels;
            var mask = channels == 1 ? ChannelIn.Mono : ChannelIn.Stereo;
            int min = AudioRecord.GetMinBufferSize(sampleRate, mask, Encoding.Pcm16bit);
            if (min <= 0) throw new InvalidOperationException($"The input can't record {sampleRate} Hz, {channels} ch.");
            _rec = new AudioRecord(AudioSource.Unprocessed, sampleRate, mask, Encoding.Pcm16bit, Math.Max(min, bufferFrames * channels * 2 * 2));
            if (_rec.State != State.Initialized)
            {
                _rec.Release();
                _rec = new AudioRecord(AudioSource.Mic, sampleRate, mask, Encoding.Pcm16bit, Math.Max(min, bufferFrames * channels * 2 * 2));
            }
            if (_rec.State != State.Initialized)
            {
                _rec.Release();
                throw new InvalidOperationException("Could not open the audio input. Allow microphone access for Chronos Timer in Android settings.");
            }
            LatencySamples = bufferFrames;
            _rec.StartRecording();
            _thread = new Thread(Run) { IsBackground = true, Name = "LTC input", Priority = System.Threading.ThreadPriority.Highest };
            _thread.Start();
        }

        public int SampleRate { get; }
        public int Channels { get; }
        public int LatencySamples { get; }
        public string DeviceName => "Android default input";
        public event Action<Exception>? Failed;

        private void Run()
        {
            var shorts = new short[_bufferFrames * Channels];
            var floats = new float[shorts.Length];
            try
            {
                while (!_stop)
                {
                    int n = _rec.Read(shorts, 0, shorts.Length);
                    if (n < 0) throw new InvalidOperationException($"AudioRecord.Read failed ({n}).");
                    n -= n % Channels;
                    for (int i = 0; i < n; i++) floats[i] = shorts[i] / 32768f;
                    if (n > 0) _capture(floats.AsSpan(0, n), Channels);
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
            try { _rec.Stop(); } catch (Java.Lang.IllegalStateException) { }
            _thread.Join(1000);
            _rec.Release();
            _rec.Dispose();
        }
    }
}
