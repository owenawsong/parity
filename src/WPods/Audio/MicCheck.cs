using System.Buffers.Binary;
using System.Runtime.InteropServices;
using static WPods.Audio.AudioEndpoints;

namespace WPods.Audio;

/// <summary>Records the same Windows input endpoint other applications select, including the virtual USB path.</summary>
internal static class MicCheck
{
    private const int Rate = 48_000;
    internal const int DurationMs = 120_000;
    private const int MinimumSampleFrames = Rate / 4;
    private const int Silent = 2;
    private const int ConvertPcm = unchecked((int)0x88000000);

    internal sealed record Recording(byte[] Wave, int PeakDb, int[] Envelope, string Level);
    internal sealed class MutedInputException : Exception { }
    internal sealed class InputChangedException : Exception { }
    internal sealed class ShortSampleException : Exception { }
    internal sealed class NoFramesException : Exception { }

    internal static unsafe Recording Capture(string endpoint, bool verifyCallsDefault, CancellationToken ct,
        Action<int, int>? progress = null, Func<bool>? finished = null)
    {
        ct.ThrowIfCancellationRequested();
        if (finished?.Invoke() == true) throw new ShortSampleException();
        var enumerator = CreateEnumerator();
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        bool started = false;
        try
        {
            if (verifyCallsDefault && !IsDefaultCallsInput(enumerator, endpoint)) throw new InputChangedException();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpoint, out device));
            if (ReadMute(device) == true) throw new MutedInputException();
            Guid id = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref id, 0x17, 0, out object instance));
            client = (IAudioClient)instance;
            WaveFormat format = new() { Tag = 1, Channels = 1, SamplesPerSecond = Rate, BytesPerSecond = Rate * 2, Alignment = 2, Bits = 16 };
            Marshal.ThrowExceptionForHR(client.Initialize(0, ConvertPcm, 1_000_000, 0, (nint)(&format), 0));
            id = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref id, out object stream));
            capture = (IAudioCaptureClient)stream;
            ct.ThrowIfCancellationRequested();
            if (finished?.Invoke() == true) throw new ShortSampleException();
            if (verifyCallsDefault && !IsDefaultCallsInput(enumerator, endpoint)) throw new InputChangedException();
            Marshal.ThrowExceptionForHR(client.Start());
            started = true;

            int limit = Rate * (DurationMs / 1000);
            short[] pcm = new short[Rate];
            int copied = 0;
            int nextReport = 0, peak = 0;
            // Keep the test bounded even if the endpoint stops delivering frames.
            long deadline = Environment.TickCount64 + DurationMs + 1700;
            long lastPacket = Environment.TickCount64;
            while (copied < limit && Environment.TickCount64 < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (finished?.Invoke() == true) break;
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint next));
                if (next == 0)
                {
                    if (Environment.TickCount64 - lastPacket > 2500) throw new NoFramesException();
                    ct.WaitHandle.WaitOne(8);
                    continue;
                }
                lastPacket = Environment.TickCount64;
                Marshal.ThrowExceptionForHR(capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _));
                try
                {
                    int count = (int)Math.Min(frames, (uint)(limit - copied));
                    if (copied + count > pcm.Length) Array.Resize(ref pcm, Math.Min(limit, Math.Max(pcm.Length * 2, copied + count)));
                    if ((flags & Silent) == 0 && data != 0)
                    {
                        var packet = new ReadOnlySpan<short>((void*)data, count);
                        packet.CopyTo(pcm.AsSpan(copied));
                        foreach (short sample in packet) peak = Math.Max(peak, Math.Abs((int)sample));
                    }
                    copied += count;
                    if (copied >= nextReport)
                    {
                        progress?.Invoke((int)((long)copied * 1000 / Rate), PeakDb(peak / 32768d));
                        nextReport = copied + Rate / 12;
                        peak = 0;
                    }
                }
                finally
                {
                    Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames));
                }
            }
            client.Stop();
            started = false;
            ct.ThrowIfCancellationRequested();
            if (verifyCallsDefault && !IsDefaultCallsInput(enumerator, endpoint)) throw new InputChangedException();
            if (ReadMute(device) == true) throw new MutedInputException();
            if (copied < limit && finished?.Invoke() != true) throw new NoFramesException();
            if (copied < MinimumSampleFrames) throw new ShortSampleException();
            return Format(pcm.AsSpan(0, copied));
        }
        finally
        {
            if (started) client!.Stop();
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    internal static Recording Format(ReadOnlySpan<short> samples)
    {
        const int bars = 32;
        int[] envelope = new int[bars];
        // Count 20 ms windows with measurable energy. A pop can have a high peak in an otherwise silent clip.
        const int window = Rate / 50;
        int activeWindows = 0, clipped = 0;
        double energy = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            int bar = i * bars / samples.Length;
            int amplitude = Math.Abs((int)samples[i]);
            envelope[bar] = Math.Max(envelope[bar], amplitude);
            if (amplitude >= 32760) clipped++;
            energy += (double)amplitude * amplitude;
            if ((i + 1) % window == 0)
            {
                if (energy >= window * 130d * 130d) activeWindows++; // about -48 dBFS RMS
                energy = 0;
            }
        }
        int peakDb = PeakDb(envelope.Max() / 32768d);
        for (int i = 0; i < bars; i++) envelope[i] = PeakDb(envelope[i] / 32768d);
        byte[] wave = new byte[44 + samples.Length * 2];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), samples.Length * 2);
        MemoryMarshal.AsBytes(samples).CopyTo(wave.AsSpan(44));
        string level = peakDb <= -54 ? "silent" : clipped > samples.Length / 1000 ? "clipped" :
            activeWindows < 8 ? "quiet" : "ready";
        return new Recording(wave, peakDb, envelope, level);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        internal ushort Tag, Channels;
        internal uint SamplesPerSecond, BytesPerSecond;
        internal ushort Alignment, Bits, ExtraBytes;
    }

}
