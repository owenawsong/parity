#if PAIRSIDE_LIVE
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WPods.Audio;

// Two screen-relative speakers, convolved with measured binaural impulse responses.
internal sealed class SpatialStereo
{
    internal const int Rate = 48000;
    private static readonly float Boost = MathF.Pow(10, .75f / 20);
    private static readonly float Soft = MathF.Pow(10, -1.5f / 20);
    private const int Taps = 144;
    private const int Ring = 256;
    private const int Block = 64;
    // The packed grid includes delay-aligned intermediate responses. Keeping
    // the resampling offline leaves the audio thread with its usual bilinear mix.
    private const float AzimuthStep = 1.25f;
    private const int Azimuths = 145;
    private const int Elevations = 14;
    private const float Degrees = MathF.PI / 180;
    private static readonly float[] Measurements = ReadMeasurements();
    private readonly float[][] _history = [new float[Ring * 2], new float[Ring * 2]];
    private float[][] _current = NewFilters(), _previous = NewFilters();
    // The diffuse-field recordings roll off together below 200 Hz. A matched bass
    // shelf restores that band without changing interaural timing or level cues.
    private const float BassCoefficient = .00847232f; // 65 Hz at 48 kHz
    private float _bassLeft, _bassRight;
    private int _write;
    private int _tailFrames;
    private long _target;
    private long _lastTarget;
    private Quaternion _pose = Quaternion.Identity, _goal = Quaternion.Identity;
    private bool _settled = true;
    private bool _moving;
    private int _targetSpeakerAngle = 30;
    private float _speakerAngle = 30;
    private float _targetLevel = Boost, _level = Boost;
    private float _limiter = 1;
    private readonly StereoLevelMatch _levelMatch = new();

    internal SpatialStereo() => BuildFilters();

    internal static int SpeakerAngle(int degrees) => degrees is 20 or 55 ? degrees : 30;
    internal void SetSpeakerAngle(int degrees) => Volatile.Write(ref _targetSpeakerAngle, SpeakerAngle(degrees));

    internal static string LevelChoice(string? value) => value is "soft" or "matched" ? value : "boost";
    internal void SetLevel(string? value) =>
        Volatile.Write(ref _targetLevel, LevelChoice(value) switch { "soft" => Soft, "matched" => 1f, _ => Boost });

    // Fixed-point angles at 1/128 degree let the audio thread read the complete
    // sensor sample atomically without taking a lock or allocating on either thread.
    internal void Pose(double yaw, double pitch = 0, double roll = 0)
    {
        if (!double.IsFinite(yaw) || !double.IsFinite(pitch) || !double.IsFinite(roll)) return;
        static long Pack(double angle) => (ushort)(short)Math.Round(Math.IEEERemainder(angle, 360) * 128);
        Interlocked.Exchange(ref _target, (Pack(yaw) << 32) | (Pack(pitch) << 16) | Pack(roll));
    }

    private static float[][] NewFilters() => [new float[Taps], new float[Taps], new float[Taps], new float[Taps]];

    private static float[] ReadMeasurements()
    {
        using var stream = typeof(SpatialStereo).Assembly.GetManifestResourceStream("Pairside.Kemar48")
            ?? throw new InvalidDataException("Missing spatial response data.");
        float[] data = new float[Elevations * Azimuths * 2 * Taps];
        // The packed resource and the Windows audio pipeline both use little-endian PCM.
        // Decode blocks without issuing a stream read for every tap on first playback.
        Span<short> packed = stackalloc short[2048];
        for (int offset = 0; offset < data.Length; offset += packed.Length)
        {
            var block = packed[..Math.Min(packed.Length, data.Length - offset)];
            stream.ReadExactly(MemoryMarshal.AsBytes(block));
            for (int i = 0; i < block.Length; i++) data[offset + i] = block[i] * (.55f / 32768);
        }
        return data;
    }

    internal void Process(Span<byte> bytes)
    {
        Span<float> data = MemoryMarshal.Cast<byte, float>(bytes);
        float targetLevel = Volatile.Read(ref _targetLevel);
        bool silent = !data.ContainsAnyExcept(0f);
        if (silent)
        {
            if (_tailFrames == 0)
            {
                // A paused player still pulls output buffers. Its next audible frame
                // should start at the latest pose without convolving empty buffers.
                UpdatePose(immediate: true);
                _moving = false;
                _write = 0;
                _level = targetLevel;
                return;
            }
            int tail = Math.Min(data.Length / 2, _tailFrames);
            data = data[..(tail * 2)];
            _tailFrames -= tail;
        }
        else _tailFrames = 3072; // More than 24 bass-filter time constants, plus the HRIR.
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            int phase = _write & (Block - 1);
            if (phase == 0) UpdatePose();
            _bassLeft += BassCoefficient * (data[i] - _bassLeft);
            _bassRight += BassCoefficient * (data[i + 1] - _bassRight);
            _history[0][_write] = _history[0][_write + Ring] = data[i] + 2 * _bassLeft;
            _history[1][_write] = _history[1][_write + Ring] = data[i + 1] + 2 * _bassRight;
            int start = _write + Ring - Taps + 1;
            (float left, float right) = Convolve(_current, start);
            if (_moving)
            {
                var prior = Convolve(_previous, start);
                float mix = (phase + 1f) / Block;
                left = float.Lerp(prior.left, left, mix);
                right = float.Lerp(prior.right, right, mix);
            }
            // Ramp changes after matching so the trim keeps its effect and shares the peak limiter.
            _level += (targetLevel - _level) * .0015f;
            float gain = _levelMatch.Measure(data[i], data[i + 1], left, right) * _level;
            left *= gain;
            right *= gain;
            // Share headroom across ears to preserve the interaural level difference.
            float peak = MathF.Max(MathF.Abs(left), MathF.Abs(right));
            _limiter += (1 - _limiter) * .00035f;
            if (peak * _limiter > .99f) _limiter = .99f / peak;
            data[i] = left * _limiter;
            data[i + 1] = right * _limiter;
            _write = (_write + 1) & (Ring - 1);
        }
        if (silent && _tailFrames == 0)
        {
            _bassLeft = _bassRight = 0;
            _limiter = 1;
            _levelMatch.Drain();
            Array.Clear(_history[0]);
            Array.Clear(_history[1]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private (float left, float right) Convolve(float[][] filters, int start)
    {
        var left = Vector<float>.Zero;
        var right = Vector<float>.Zero;
        for (int tap = 0; tap < Taps; tap += Vector<float>.Count)
        {
            var a = new Vector<float>(_history[0], start + tap);
            var b = new Vector<float>(_history[1], start + tap);
            left += a * new Vector<float>(filters[0], tap) + b * new Vector<float>(filters[2], tap);
            right += a * new Vector<float>(filters[1], tap) + b * new Vector<float>(filters[3], tap);
        }
        return (Vector.Sum(left), Vector.Sum(right));
    }

    private void UpdatePose(bool immediate = false)
    {
        long sample = Interlocked.Read(ref _target);
        if (sample != _lastTarget)
        {
            _lastTarget = sample;
            const float unit = Degrees / 128;
            // Inverse listener rotation maps world directions into the ear frame.
            _goal = Quaternion.Conjugate(Quaternion.CreateFromYawPitchRoll(
                (short)(sample >> 32) * unit, -(short)(sample >> 16) * unit, -(short)sample * unit));
            if (Quaternion.Dot(_pose, _goal) < 0) _goal = -_goal;
            _settled = false;
        }
        int targetAngle = Volatile.Read(ref _targetSpeakerAngle);
        _moving = !_settled || _speakerAngle != targetAngle;
        if (!_moving) return;
        // Interpolate the orientation as a whole. At either vertical pole a tiny
        // real motion can flip both yaw and roll by 180 degrees in one report.
        _settled = immediate || (_pose - _goal).LengthSquared() < 3e-8f; // ~0.02 degrees
        _pose = _settled ? _goal : Quaternion.Slerp(_pose, _goal, .105f);
        _speakerAngle = immediate || Math.Abs(targetAngle - _speakerAngle) < .025f
            ? targetAngle : float.Lerp(_speakerAngle, targetAngle, .105f);
        (_current, _previous) = (_previous, _current);
        BuildFilters();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void BuildFilters()
    {
        var (side, forward) = MathF.SinCos(_speakerAngle * Degrees);
        for (int speaker = 0; speaker < 2; speaker++)
        {
            var direction = Vector3.Transform(new Vector3(speaker == 0 ? -side : side, 0, forward), _pose);
            float azimuth = MathF.Atan2(direction.X, direction.Z) / Degrees;
            float elevation = MathF.Asin(Math.Clamp(direction.Y, -1, 1)) / Degrees;
            float col = Math.Clamp(MathF.Abs(azimuth) / AzimuthStep, 0, Azimuths - 1);
            float row = Math.Clamp((elevation + 40) / 10, 0, Elevations - 1);
            int a = (int)col, b = Math.Min(a + 1, Azimuths - 1);
            int lo = (int)row, hi = Math.Min(lo + 1, Elevations - 1);
            for (int ear = 0; ear < 2; ear++)
            {
                int measuredEar = azimuth < 0 ? 1 - ear : ear;
                int Offset(int e, int angle) => ((e * Azimuths + angle) * 2 + measuredEar) * Taps;
                int la = Offset(lo, a), lb = Offset(lo, b), ha = Offset(hi, a), hb = Offset(hi, b);
                float[] output = _current[speaker * 2 + ear];
                for (int t = 0; t < Taps; t++)
                {
                    float low = float.Lerp(Measurements[la + t], Measurements[lb + t], col - a);
                    float high = float.Lerp(Measurements[ha + t], Measurements[hb + t], col - a);
                    output[t] = float.Lerp(low, high, row - lo);
                }
            }
        }
    }
}
#endif
