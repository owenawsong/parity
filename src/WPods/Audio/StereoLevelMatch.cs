#if PAIRSIDE_LIVE
using System.Runtime.CompilerServices;

namespace WPods.Audio;

// Compare the same program before and after binaural rendering. Both ears receive
// the same gain so timing, left/right level differences and stereo balance survive.
internal sealed class StereoLevelMatch
{
    private const int Window = SpatialStereo.Rate / 50; // 20 ms
    private const double Gate = 6.3e-8 * Window; // summed K-weighted power, about -72 dBFS
    private KWeight _inputLeft, _inputRight, _outputLeft, _outputRight;
    private double _inputSum, _outputSum, _inputAverage, _outputAverage;
    private int _frames;
    private float _gain = 1, _target = 1, _step;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal float Measure(float inLeft, float inRight, float outLeft, float outRight)
    {
        _inputSum += _inputLeft.Power(inLeft) + _inputRight.Power(inRight);
        _outputSum += _outputLeft.Power(outLeft) + _outputRight.Power(outRight);
        _gain += _step;
        if (++_frames == Window) Update();
        return _gain;
    }

    private void Update()
    {
        _gain = _target;
        if (_inputSum > Gate && _outputSum > Gate * .1)
        {
            // Average powers before taking their ratio. A transient split across
            // windows by the impulse response must not act like a volume change.
            const double weight = .026; // about 750 ms of active material
            _inputAverage += weight * (_inputSum - _inputAverage);
            _outputAverage += weight * (_outputSum - _outputAverage);
            float measured = (float)Math.Clamp(Math.Sqrt(_inputAverage / _outputAverage), .4, 2.5);
            // Approach over multiple windows; interpolate every sample to avoid steps.
            _target += Math.Clamp(.22f * (measured - _target), -.045f, .045f);
        }
        _step = (_target - _gain) / Window;
        _frames = 0;
        _inputSum = _outputSum = 0;
    }

    internal void Drain()
    {
        // Keep the learned trim through a rest. No silent samples can drive gain up.
        _inputLeft = _inputRight = _outputLeft = _outputRight = default;
        _inputSum = _outputSum = 0;
        _frames = 0;
        _target = _gain;
        _step = 0;
    }

    // ITU-R BS.1770 K-weighting at the renderer's fixed 48 kHz sample rate.
    // Transposed direct form II keeps the high-pass states small at low frequencies.
    private struct KWeight
    {
        private double _s1, _s2, _h1, _h2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal double Power(float sample)
        {
            double shelf = 1.53512485958697 * sample + _s1;
            _s1 = -2.69169618940638 * sample + 1.69065929318241 * shelf + _s2;
            _s2 = 1.19839281085285 * sample - .73248077421585 * shelf;
            double value = shelf + _h1;
            _h1 = -2 * shelf + 1.99004745483398 * value + _h2;
            _h2 = shelf - .99007225036621 * value;
            return value * value;
        }
    }
}
#endif
