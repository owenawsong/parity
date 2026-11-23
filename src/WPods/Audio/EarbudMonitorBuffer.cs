#if PAIRSIDE_LIVE
namespace WPods.Audio;

// Input is the decoded 48 kHz mono earbud mic, output is interleaved stereo.
internal sealed class EarbudMonitorBuffer
{
    internal const int Capacity = 48 * 45;
    private const int StartFrames = 48 * 15;
    private readonly float[] _samples = new float[Capacity];
    private readonly Lock _lock = new();
    private int _head, _count;
    private bool _playing;
    private long _lastPush, _received, _rendered, _dropped;
    internal long Received => Interlocked.Read(ref _received);
    internal long Rendered => Interlocked.Read(ref _rendered);
    internal long Dropped => Interlocked.Read(ref _dropped);

    internal void Push(ReadOnlySpan<float> mono)
    {
        lock (_lock)
        {
            Interlocked.Add(ref _received, mono.Length);
            long now = Environment.TickCount64;
            if (_count > 0 && now - _lastPush > 100) { _count = 0; _playing = false; }
            _lastPush = now;
            int excess = Math.Max(0, _count + mono.Length - Capacity);
            Interlocked.Add(ref _dropped, excess);
            int discard = Math.Min(_count, excess);
            _head = (_head + discard) % Capacity;
            _count -= discard;
            mono = mono[(excess - discard)..];
            int tail = (_head + _count) % Capacity;
            int first = Math.Min(mono.Length, Capacity - tail);
            mono[..first].CopyTo(_samples.AsSpan(tail));
            mono[first..].CopyTo(_samples);
            _count += mono.Length;
        }
    }

    internal void Read(Span<float> stereo, float gain)
    {
        stereo.Clear();
        lock (_lock)
        {
            if (!_playing && _count < StartFrames) return;
            _playing = true;
            int frames = Math.Min(_count, stereo.Length / 2);
            for (int i = 0; i < frames; ++i)
            {
                float value = Math.Clamp(_samples[_head] * gain, -0.25f, 0.25f);
                stereo[i * 2] = stereo[i * 2 + 1] = value;
                _head = (_head + 1) % Capacity;
            }
            Interlocked.Add(ref _rendered, frames);
            _count -= frames;
            if (frames < stereo.Length / 2) _playing = false;
        }
    }
}
#endif
