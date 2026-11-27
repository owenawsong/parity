#if PAIRSIDE_LIVE
using WPods.Audio.Dsp;

namespace WPods.Audio;

// The control thread builds each filter bank. The renderer owns the filter
// histories and transitions between banks without locks or allocations.
internal sealed class PcEqualizer
{
    internal const int BandCount = 10;
    internal const int LimitDb = 12;
    private const float Q = 1.4f;
    private const int FadeFrames = SpatialStereo.Rate / 50;
    private const int BlockSamples = 256;
    private Bank _target = Bank.Flat;
    private Bank _current = Bank.Flat;
    private Bank? _prior;
    private int[]? _controlCurve;
    private int _fade;
    private int _mode;
    private float _enabled, _audition, _limiter = 1;
    private readonly StereoLevelMatch _match = new();

    private sealed class Bank(Biquad[] left, Biquad[] right)
    {
        internal static readonly Bank Flat = new([], []);
        internal void Process(Span<float> stereo)
        {
            for (int i = 0; i < left.Length; i++)
            {
                left[i].ProcessChannel(stereo, 0, 2);
                right[i].ProcessChannel(stereo, 1, 2);
            }
        }
    }

    internal static bool Valid(int[]? values) =>
        values is { Length: BandCount } && Array.TrueForAll(values, db => db is >= -LimitDb and <= LimitDb);

    internal void SetCurve(int[]? values)
    {
        if (!Valid(values) || (_controlCurve != null && _controlCurve.AsSpan().SequenceEqual(values))) return;
        _controlCurve = (int[])values!.Clone();
        var left = new List<Biquad>(BandCount);
        var right = new List<Biquad>(BandCount);
        for (int i = 0; i < BandCount; i++)
        {
            if (values![i] == 0) continue;
            float frequency = 31.25f * (1 << i);
            left.Add(Biquad.Peaking(SpatialStereo.Rate, frequency, Q, values[i]));
            right.Add(Biquad.Peaking(SpatialStereo.Rate, frequency, Q, values[i]));
        }
        Volatile.Write(ref _target, left.Count == 0 ? Bank.Flat : new Bank(left.ToArray(), right.ToArray()));
    }

    internal void SetMode(bool enabled, bool compareFlat) =>
        Volatile.Write(ref _mode, enabled ? compareFlat ? 1 : 2 : 0);

    internal void Process(Span<float> stereo)
    {
        int mode = Volatile.Read(ref _mode);
        if (mode == 0 && _enabled == 0) return;
        Span<float> wetBuffer = stackalloc float[BlockSamples];
        Span<float> oldBuffer = stackalloc float[BlockSamples];
        for (int offset = 0; offset < stereo.Length; offset += BlockSamples)
        {
            var data = stereo.Slice(offset, Math.Min(BlockSamples, stereo.Length - offset));
            var target = Volatile.Read(ref _target);
            if (_prior is null && !ReferenceEquals(_current, target))
            {
                // No previous audio means the next audible block can begin with
                // the selected filter instead of fading in the startup default.
                if (_enabled > 0) { _prior = _current; _fade = 0; }
                _current = target;
            }
            if (ReferenceEquals(_current, Bank.Flat) && _prior is null)
            {
                _enabled = mode == 0 ? 0 : 1;
                _audition = mode == 2 ? 1 : 0;
                _limiter = 1;
                continue;
            }
            var wet = wetBuffer[..data.Length];
            data.CopyTo(wet);
            _current.Process(wet);
            var prior = _prior;
            var old = oldBuffer[..data.Length];
            if (prior is not null)
            {
                data.CopyTo(old);
                prior.Process(old);
            }
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                float left = wet[i], right = wet[i + 1];
                if (prior is not null && _fade < FadeFrames)
                {
                    float weight = ++_fade / (float)FadeFrames;
                    left = float.Lerp(old[i], left, weight);
                    right = float.Lerp(old[i + 1], right, weight);
                }
                float trim = _match.Measure(data[i], data[i + 1], left, right);
                left *= trim;
                right *= trim;
                // Protect only actual filtered peaks. The dry side keeps unity
                // gain so changing bands or enabling comparison can't duck it.
                float peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
                _limiter += (1 - _limiter) * .0005f;
                if (peak * _limiter > .999f) _limiter = .999f / peak;
                float step = 1f / FadeFrames;
                _enabled = Math.Clamp(_enabled + (mode == 0 ? -step : step), 0, 1);
                _audition = Math.Clamp(_audition + (mode == 2 ? step : -step), 0, 1);
                float mix = _enabled * _audition;
                data[i] = float.Lerp(data[i], left * _limiter, mix);
                data[i + 1] = float.Lerp(data[i + 1], right * _limiter, mix);
            }
            if (_fade >= FadeFrames) _prior = null;
        }
    }
}
#endif
