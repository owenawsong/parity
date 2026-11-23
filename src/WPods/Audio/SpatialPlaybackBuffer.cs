#if PAIRSIDE_LIVE
namespace WPods.Audio;

// One loopback writer and one render reader at 48 kHz. A stalled reader drops
// the oldest audio so recovering playback cannot get progressively further behind.
internal sealed class SpatialPlaybackBuffer(int capacityFrames)
{
    private const int Channels = 2;
    private const int RampFrames = 48;
    private readonly float[] _samples = new float[capacityFrames * Channels];
    private readonly Lock _gate = new();
    private int _head, _count;
    private bool _jump;
    // Only the render thread uses the ramp state.
    private bool _starved = true;
    private int _ramp;
    private float _lastLeft, _lastRight, _rampLeft, _rampRight;

    internal void Push(ReadOnlySpan<float> data)
    {
        data = data[..(data.Length / Channels * Channels)];
        lock (_gate)
        {
            int overflow = Math.Max(0, _count + data.Length - _samples.Length);
            if (overflow > 0)
            {
                int discard = Math.Min(_count, overflow);
                _head = (_head + discard) % _samples.Length;
                _count -= discard;
                data = data[(overflow - discard)..];
                _jump = true;
            }
            int tail = (_head + _count) % _samples.Length;
            int first = Math.Min(data.Length, _samples.Length - tail);
            data[..first].CopyTo(_samples.AsSpan(tail));
            data[first..].CopyTo(_samples);
            _count += data.Length;
        }
    }

    internal void Read(Span<float> destination)
    {
        bool jump;
        int available;
        lock (_gate)
        {
            jump = _jump;
            _jump = false;
            available = Math.Min(destination.Length / Channels * Channels, _count);
            int first = Math.Min(available, _samples.Length - _head);
            _samples.AsSpan(_head, first).CopyTo(destination);
            _samples.AsSpan(0, available - first).CopyTo(destination[first..]);
            _head = (_head + available) % _samples.Length;
            _count -= available;
        }
        destination[available..].Clear();
        if (available > 0 && (jump || _starved))
        {
            _starved = false;
            BeginRamp();
        }
        Blend(destination[..available]);
        if (available < destination.Length)
        {
            if (available > 0) BeginRamp();
            _starved = true;
            Blend(destination[available..]);
        }
    }

    private void BeginRamp()
    {
        _ramp = RampFrames;
        (_rampLeft, _rampRight) = (_lastLeft, _lastRight);
    }

    private void Blend(Span<float> samples)
    {
        int frames = samples.Length / Channels;
        for (int i = 0; i < frames && _ramp > 0; i++, _ramp--)
        {
            float mix = _ramp / (float)RampFrames;
            samples[i * Channels] = float.Lerp(samples[i * Channels], _rampLeft, mix);
            samples[i * Channels + 1] = float.Lerp(samples[i * Channels + 1], _rampRight, mix);
        }
        if (frames > 0) (_lastLeft, _lastRight) = (samples[frames * Channels - 2], samples[frames * Channels - 1]);
    }
}
#endif
