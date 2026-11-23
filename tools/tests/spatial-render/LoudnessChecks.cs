using System.Runtime.InteropServices;
using WPods.Audio;

internal static class LoudnessChecks
{
    private const int Rate = SpatialStereo.Rate;
    private const int Frames = Rate * 5;

    private static float[] Fixture(string kind)
    {
        var pcm = new float[2 * Frames];
        var rng = new Random(823);
        double l = 0, r = 0;
        for (int i = 0; i < Frames; i++)
        {
            double t = i / (double)Rate;
            l += .14 * (rng.NextDouble() - .5 - l);
            r += .14 * (rng.NextDouble() - .5 - r);
            double mono = .08 * Math.Sin(2 * Math.PI * 160 * t) +
                .04 * Math.Sin(2 * Math.PI * 830 * t) + .018 * Math.Sin(2 * Math.PI * 2330 * t);
            double beat = .07 * Math.Exp(-7 * (t % .43)) * Math.Sin(2 * Math.PI * 70 * t);
            (double left, double right) = kind switch
            {
                "voice" => (mono, mono),
                "noise" => (l, r),
                _ => (.42 * l + mono + beat, .42 * r + .75 * mono + beat),
            };
            double envelope = .55 + .45 * Math.Sin(2 * Math.PI * 1.7 * t);
            pcm[2 * i] = (float)(envelope * left);
            pcm[2 * i + 1] = (float)(envelope * right);
        }
        return pcm;
    }

    // BS.1770 K-weighted, summed channel power. The fixtures stay above the gate.
    private static double Power(float[] samples, int firstFrame, int endFrame)
    {
        double sum = 0;
        for (int c = 0; c < 2; c++)
        {
            double a1 = 0, a2 = 0, b1 = 0, b2 = 0;
            for (int i = 0; i < endFrame; i++)
            {
                double a = samples[2 * i + c] + 1.69065929318241 * a1 - .73248077421585 * a2;
                double shelf = 1.53512485958697 * a - 2.69169618940638 * a1 + 1.19839281085285 * a2;
                a2 = a1; a1 = a;
                double b = shelf + 1.99004745483398 * b1 - .99007225036621 * b2;
                double k = b - 2 * b1 + b2;
                b2 = b1; b1 = b;
                if (i >= firstFrame) sum += k * k;
            }
        }
        return sum;
    }

    internal static void Run()
    {
        const double targetLu = .75;
        foreach (string source in new[] { "voice", "noise", "music" })
        {
            float[] dry = Fixture(source);
            foreach (double yaw in new double[] { 0, 60, 120, 180, -70 })
            {
                var dsp = new SpatialStereo();
                dsp.Pose(yaw, yaw == -70 ? 25 : 0);
                dsp.Process(new byte[8]);
                var wet = (float[])dry.Clone();
                dsp.Process(MemoryMarshal.AsBytes(wet.AsSpan()));
                double delta = 10 * Math.Log10(Power(wet, 0, Frames) / Power(dry, 0, Frames));
                double settled = 10 * Math.Log10(Power(wet, Rate, Frames) / Power(dry, Rate, Frames));
                Console.WriteLine($"{source} at {yaw:F0}°: {delta:F2} LU, settled {settled:F2} LU");
                if (Math.Abs(delta - targetLu) > .85 || Math.Abs(settled - targetLu) > .15)
                    throw new InvalidOperationException("Spatial output missed its target lift over stereo.");
            }
        }

        // Exercise the gain controller with real-time callback sizes while both
        // angular interpolation and the level of the source change.
        var song = Fixture("music");
        for (int i = Rate * 4; i < song.Length; i++) song[i] *= .25f;
        var moving = (float[])song.Clone();
        var renderer = new SpatialStereo();
        const int chunk = 480;
        for (int start = 0; start < Frames; start += chunk)
        {
            double phase = 2 * Math.PI * start / Frames;
            renderer.Pose(180 * Math.Sin(phase), 23 * Math.Cos(phase));
            renderer.Process(MemoryMarshal.AsBytes(moving.AsSpan(2 * start, 2 * chunk)));
        }
        // Change the trim mid-stream with the same renderer and matching history.
        var adjusted = Fixture("music");
        var unadjusted = (float[])adjusted.Clone();
        var signal = new SpatialStereo();
        string[] levels = ["boost", "matched", "soft"];
        double[] targets = [.75, 0, -1.5];
        for (int second = 0; second < levels.Length; second++)
        {
            signal.SetLevel(levels[second]);
            signal.Process(MemoryMarshal.AsBytes(adjusted.AsSpan(2 * second * Rate, 2 * Rate)));
            int from = second * Rate + Rate / 2, to = (second + 1) * Rate;
            double trim = 10 * Math.Log10(Power(adjusted, from, to) / Power(unadjusted, from, to));
            if (Math.Abs(trim - targets[second]) > .2)
                throw new InvalidOperationException($"Spatial volume {levels[second]} missed its target at {trim:F2} LU.");
        }
        Console.WriteLine("Live spatial volume targets passed at +0.75, 0, and -1.5 LU.");

        double worst = 0;
        for (int start = 0; start + Rate / 2 <= Frames; start += Rate / 2)
        {
            double delta = 10 * Math.Log10(Power(moving, start, start + Rate / 2) / Power(song, start, start + Rate / 2));
            worst = Math.Max(worst, Math.Abs(delta - targetLu));
        }
        Console.WriteLine($"Moving music, largest half-second deviation from target: {worst:F2} LU.");
        if (worst > 1.5) throw new InvalidOperationException("Head motion or a change in song volume caused a level jump.");
    }
}
