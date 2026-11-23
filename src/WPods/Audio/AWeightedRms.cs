using System.Numerics;

namespace WPods.Audio;

internal sealed class AWeightedRms
{
    // Bilinear transform of the standard analogue A-weighting poles. Separate
    // stereo histories and an explicit 1 kHz normalization at the actual sample rate.
    private sealed class Section(double b0, double b1, double b2, double a1, double a2)
    {
        private double z1, z2;
        internal double Run(double x)
        {
            double y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
        internal Complex Response(Complex z) => (b0 + b1 * z + b2 * z * z) / (1 + a1 * z + a2 * z * z);
    }

    private static Section[] Filter(int rate)
    {
        double Pole(double hz) => (rate - Math.PI * hz) / (rate + Math.PI * hz);
        Section Pair(double hz1, double hz2, double zero) =>
            new(1, -2 * zero, 1, -Pole(hz1) - Pole(hz2), Pole(hz1) * Pole(hz2));
        return [Pair(20.598997, 20.598997, 1), Pair(107.65265, 737.86223, 1), Pair(12194.217, 12194.217, -1)];
    }

    private readonly Section[] left, right;
    private readonly double gain;
    private readonly object gate = new();
    private double sumLeft, sumRight;
    private long frames;
    internal AWeightedRms(int rate)
    {
        left = Filter(rate);
        right = Filter(rate);
        Complex z = Complex.FromPolarCoordinates(1, -2 * Math.PI * 1000 / rate);
        gain = 1 / left.Aggregate(Complex.One, (acc, stage) => acc * stage.Response(z)).Magnitude;
    }

    internal void Add(ReadOnlySpan<float> samples)
    {
        double l = 0, r = 0;
        for (int i = 0; i + 1 < samples.Length; i += 2)
        {
            double x = float.IsFinite(samples[i]) ? samples[i] * gain : 0;
            double y = float.IsFinite(samples[i + 1]) ? samples[i + 1] * gain : 0;
            l += Power(left, x);
            r += Power(right, y);
        }
        Accumulate(l, r, samples.Length / 2);
    }

    internal (long Frames, double Left, double Right) Take()
    {
        lock (gate)
        {
            double Db(double sum) => sum > 0 ? 10 * Math.Log10(sum / frames) : double.NegativeInfinity;
            var reading = (frames, Db(sumLeft), Db(sumRight));
            frames = 0;
            sumLeft = sumRight = 0;
            return reading;
        }
    }

    internal void AddSilence(uint count)
    {
        // Let the weighting filters decay while the loopback reports a silent packet.
        double l = 0, r = 0;
        for (uint i = 0; i < count; i++)
        {
            l += Power(left, 0);
            r += Power(right, 0);
        }
        Accumulate(l, r, count);
    }

    private static double Power(Section[] stages, double sample)
    {
        foreach (var stage in stages) sample = stage.Run(sample);
        return sample * sample;
    }

    private void Accumulate(double leftPower, double rightPower, long count)
    {
        lock (gate)
        {
            sumLeft += leftPower;
            sumRight += rightPower;
            frames += count;
        }
    }
}
