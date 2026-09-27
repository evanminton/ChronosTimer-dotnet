using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ChronosTimer.Audio;

/// <summary>
/// Apple audio through AudioToolbox Audio Queues (macOS, Mac Catalyst, iOS). Uses the system default input/output
/// device (choose it in System Settings › Sound, or Audio MIDI Setup).
/// </summary>
public sealed unsafe class CoreAudioBackend : IAudioBackend
{
    public string Name => "CoreAudio";

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [new("default", "System default output", 2, true)];
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [new("default", "System default input", 0, true)];

    private static void CheckDevice(string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) && !deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("On Apple platforms Chronos Timer uses the system default device; pick it in System Settings › Sound (or Audio MIDI Setup) and set the device to 'default'.");
    }

    public IAudioStream OpenOutput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioRenderCallback render)
    {
        CheckDevice(deviceId);
        return new QueueStream(true, sampleRate, channels, bufferFrames, render, null);
    }

    public IAudioStream OpenInput(string? deviceId, int sampleRate, int channels, int bufferFrames, AudioCaptureCallback capture)
    {
        CheckDevice(deviceId);
        return new QueueStream(false, sampleRate, channels, bufferFrames, null, capture);
    }

    private sealed class QueueStream : IAudioStream
    {
        private const int BufferCount = 3;
        private readonly bool _output;
        private readonly nint _queue;
        private GCHandle _self;
        private readonly AudioRenderCallback? _render;
        private readonly AudioCaptureCallback? _capture;
        private readonly int _bufferFrames;
        private float[] _scratch;
        private volatile bool _disposed;

        public QueueStream(bool output, int sampleRate, int channels, int bufferFrames, AudioRenderCallback? render, AudioCaptureCallback? capture)
        {
            _output = output;
            _render = render;
            _capture = capture;
            _bufferFrames = bufferFrames;
            SampleRate = sampleRate;
            Channels = channels;
            _scratch = new float[bufferFrames * channels];
            _self = GCHandle.Alloc(this);

            var fmt = new Native.AudioStreamBasicDescription
            {
                mSampleRate = sampleRate,
                mFormatID = Native.kAudioFormatLinearPCM,
                mFormatFlags = Native.kAudioFormatFlagIsFloat | Native.kAudioFormatFlagIsPacked,
                mBytesPerPacket = (uint)(4 * channels),
                mFramesPerPacket = 1,
                mBytesPerFrame = (uint)(4 * channels),
                mChannelsPerFrame = (uint)channels,
                mBitsPerChannel = 32,
            };

            nint q;
            int err = output
                ? Native.AudioQueueNewOutput(&fmt, &OutputCallback, GCHandle.ToIntPtr(_self), 0, 0, 0, &q)
                : Native.AudioQueueNewInput(&fmt, &InputCallback, GCHandle.ToIntPtr(_self), 0, 0, 0, &q);
            if (err != 0) { _self.Free(); throw new InvalidOperationException($"Could not open the default audio {(output ? "output" : "input")} ({sampleRate} Hz, {channels} ch): OSStatus {err}. {(output ? "" : "Allow microphone access for this app in System Settings › Privacy & Security.")}"); }
            _queue = q;

            uint bytes = (uint)(bufferFrames * channels * 4);
            for (int i = 0; i < BufferCount; i++)
            {
                nint buf;
                err = Native.AudioQueueAllocateBuffer(_queue, bytes, &buf);
                if (err != 0) { Dispose(); throw new InvalidOperationException($"AudioQueueAllocateBuffer failed: OSStatus {err}"); }
                if (output) Fill((Native.AudioQueueBuffer*)buf);
                else Native.AudioQueueEnqueueBuffer(_queue, buf, 0, 0);
                if (output) Native.AudioQueueEnqueueBuffer(_queue, buf, 0, 0);
            }
            err = Native.AudioQueueStart(_queue, 0);
            if (err != 0) { Dispose(); throw new InvalidOperationException($"AudioQueueStart failed: OSStatus {err}"); }
            LatencySamples = output ? (BufferCount - 1) * bufferFrames : bufferFrames;
        }

        public int SampleRate { get; }
        public int Channels { get; }
        public int LatencySamples { get; }
        public string DeviceName => _output ? "System default output" : "System default input";
        public event Action<Exception>? Failed;

        private void Fill(Native.AudioQueueBuffer* b)
        {
            int count = _bufferFrames * Channels;
            var span = new Span<float>((void*)b->mAudioData, count);
            try { _render!(span, Channels); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { span.Clear(); Failed?.Invoke(ex); }
            b->mAudioDataByteSize = (uint)(count * 4);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OutputCallback(nint user, nint queue, nint buffer)
        {
            if (GCHandle.FromIntPtr(user).Target is not QueueStream s || s._disposed) return;
            s.Fill((Native.AudioQueueBuffer*)buffer);
            Native.AudioQueueEnqueueBuffer(queue, buffer, 0, 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void InputCallback(nint user, nint queue, nint buffer, nint startTime, uint packets, nint descs)
        {
            if (GCHandle.FromIntPtr(user).Target is not QueueStream s || s._disposed) return;
            var b = (Native.AudioQueueBuffer*)buffer;
            int count = (int)(b->mAudioDataByteSize / 4);
            count -= count % s.Channels;
            try { s._capture!(new ReadOnlySpan<float>((void*)b->mAudioData, count), s.Channels); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { s.Failed?.Invoke(ex); }
            Native.AudioQueueEnqueueBuffer(queue, buffer, 0, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_queue != 0)
            {
                Native.AudioQueueStop(_queue, 1);
                Native.AudioQueueDispose(_queue, 1);
            }
            if (_self.IsAllocated) _self.Free();
            _scratch = [];
        }
    }

    private static class Native
    {
        private const string Lib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        public const uint kAudioFormatLinearPCM = 0x6C70636D; // 'lpcm'
        public const uint kAudioFormatFlagIsFloat = 1;
        public const uint kAudioFormatFlagIsPacked = 8;

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioStreamBasicDescription
        {
            public double mSampleRate;
            public uint mFormatID;
            public uint mFormatFlags;
            public uint mBytesPerPacket;
            public uint mFramesPerPacket;
            public uint mBytesPerFrame;
            public uint mChannelsPerFrame;
            public uint mBitsPerChannel;
            public uint mReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AudioQueueBuffer
        {
            public uint mAudioDataBytesCapacity;
            public nint mAudioData;
            public uint mAudioDataByteSize;
            public nint mUserData;
            public uint mPacketDescriptionCapacity;
            public nint mPacketDescriptions;
            public uint mPacketDescriptionCount;
        }

        [DllImport(Lib)]
        public static extern int AudioQueueNewOutput(AudioStreamBasicDescription* format, delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback,
            nint userData, nint runLoop, nint runLoopMode, uint flags, nint* queue);

        [DllImport(Lib)]
        public static extern int AudioQueueNewInput(AudioStreamBasicDescription* format, delegate* unmanaged[Cdecl]<nint, nint, nint, nint, uint, nint, void> callback,
            nint userData, nint runLoop, nint runLoopMode, uint flags, nint* queue);

        [DllImport(Lib)] public static extern int AudioQueueAllocateBuffer(nint queue, uint size, nint* buffer);
        [DllImport(Lib)] public static extern int AudioQueueEnqueueBuffer(nint queue, nint buffer, uint numPacketDescs, nint packetDescs);
        [DllImport(Lib)] public static extern int AudioQueueStart(nint queue, nint startTime);
        [DllImport(Lib)] public static extern int AudioQueueStop(nint queue, byte immediate);
        [DllImport(Lib)] public static extern int AudioQueueDispose(nint queue, byte immediate);
    }
}
