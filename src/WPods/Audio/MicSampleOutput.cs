using System.Runtime.InteropServices;
using static WPods.Audio.AudioEndpoints;

namespace WPods.Audio;

internal static class MicSampleOutput
{
    private const int Rate = 48_000;
    private const int BytesPerFrame = 4;
    private const int NoPersist = 0x00080000;
    private const int ConvertPcm = unchecked((int)0x88000000);
    private static readonly Guid ReplaySession = new("2814EEF8-C8D3-42C3-A3D2-E751269AE24D");

    internal readonly record struct PlaybackStart(bool NativeStereo);

    // Playback ownership lives on the UI thread. The worker keeps its own token
    // until it reports completion, even if a new recording replaced the sample.
    internal sealed class PlaybackOwner
    {
        private CancellationTokenSource? _current;

        internal CancellationTokenSource Begin() => _current = new CancellationTokenSource();
        internal bool IsCurrent(CancellationTokenSource source) => _current == source;

        internal void Cancel()
        {
            CancellationTokenSource? source = _current;
            _current = null;
            source?.Cancel();
        }

        internal bool Complete(CancellationTokenSource source)
        {
            bool current = IsCurrent(source);
            if (current) _current = null;
            source.Dispose();
            return current;
        }
    }

    internal static unsafe void Play(byte[] wave, int startMs, CancellationToken token, Action<int> progress,
        string? endpoint = null, Action<PlaybackStart>? onStart = null)
    {
        token.ThrowIfCancellationRequested();
        ReadOnlySpan<short> mono = MemoryMarshal.Cast<byte, short>(wave.AsSpan(44));
        int position = Math.Clamp(startMs * (Rate / 1000), 0, mono.Length);
        if (position == mono.Length) return;
        var enumerator = CreateEnumerator();
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioRenderClient? stream = null;
        bool started = false;
        try
        {
            int result = endpoint is null
                ? enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device)
                : enumerator.GetDevice(endpoint, out device);
            Marshal.ThrowExceptionForHR(result);
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17, 0, out object instance));
            client = (IAudioClient)instance;
            bool nativeStereo = Initialize(client);
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out int capacity));
            iid = typeof(IAudioRenderClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out object render));
            stream = (IAudioRenderClient)render;

            int first = Math.Min(capacity, mono.Length - position);
            Queue(stream, mono.Slice(position, first), nativeStereo);
            position += first;
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(client.Start());
            started = true;
            onStart?.Invoke(new PlaybackStart(nativeStereo));
            progress(startMs);
            long reportAfter = Environment.TickCount64 + 80;
            long deadline = Environment.TickCount64 + 2000 + (long)mono.Length * 1000 / Rate - startMs;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Environment.TickCount64 > deadline) throw new InvalidOperationException("The playback endpoint stopped consuming audio.");
                Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out int queued));
                if (position == mono.Length && queued == 0) break;
                if (Environment.TickCount64 >= reportAfter)
                {
                    progress(Math.Max(startMs, (int)((long)(position - queued) * 1000 / Rate)));
                    reportAfter = Environment.TickCount64 + 80;
                }
                int frames = Math.Min(capacity - queued, mono.Length - position);
                if (frames > 0)
                {
                    Queue(stream, mono.Slice(position, frames), nativeStereo);
                    position += frames;
                }
                token.WaitHandle.WaitOne(12);
            }
        }
        finally
        {
            if (started) client!.Stop();
            if (stream is not null) Marshal.ReleaseComObject(stream);
            if (client is not null) Marshal.ReleaseComObject(client);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static unsafe bool Initialize(IAudioClient client)
    {
        Guid session = ReplaySession;
        Marshal.ThrowExceptionForHR(client.GetMixFormat(out nint mix));
        try
        {
            if (ReadStereoFloatRate(mix) == Rate)
            {
                Marshal.ThrowExceptionForHR(client.Initialize(0, NoPersist, 800_000, 0, mix, (nint)(&session)));
                return true;
            }
        }
        finally { Marshal.FreeCoTaskMem(mix); }

        // Other outputs can still rely on Windows for rate or channel conversion.
        WaveFormat format = new() { Tag = 1, Channels = 2, Rate = Rate, BytesPerSecond = Rate * BytesPerFrame, Alignment = BytesPerFrame, Bits = 16 };
        Marshal.ThrowExceptionForHR(client.Initialize(0, NoPersist | ConvertPcm, 800_000, 0, (nint)(&format), (nint)(&session)));
        return false;
    }

    private static unsafe void Queue(IAudioRenderClient stream, ReadOnlySpan<short> mono, bool nativeStereo)
    {
        Marshal.ThrowExceptionForHR(stream.GetBuffer(mono.Length, out nint data));
        if (nativeStereo) CopyStereoFloat(mono, new Span<float>((void*)data, mono.Length * 2));
        else CopyStereo(mono, new Span<short>((void*)data, mono.Length * 2));
        Marshal.ThrowExceptionForHR(stream.ReleaseBuffer(mono.Length, 0));
    }

    internal static void CopyStereo(ReadOnlySpan<short> mono, Span<short> stereo)
    {
        for (int i = 0; i < mono.Length; i++)
            stereo[i * 2] = stereo[i * 2 + 1] = mono[i];
    }

    internal static void CopyStereoFloat(ReadOnlySpan<short> mono, Span<float> stereo)
    {
        for (int i = 0; i < mono.Length; i++)
            stereo[i * 2] = stereo[i * 2 + 1] = mono[i] / 32768f;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        internal ushort Tag, Channels;
        internal int Rate, BytesPerSecond;
        internal ushort Alignment, Bits, ExtraBytes;
    }
}
