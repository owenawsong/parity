using WPods.Audio;

const int rate = SpatialStereo.Rate;
float[] pair = new float[rate * 2];
for (int i = 0; i < rate; i++)
{
    pair[i * 2] = (float)(0.12 * Math.Sin(2 * Math.PI * i * 1000 / rate));
    pair[i * 2 + 1] = (float)(0.12 * Math.Sin(2 * Math.PI * i * 4000 / rate));
}

var eq = new PcEqualizer();
float[] block = (float[])pair.Clone();
eq.Process(block);
Require(block.AsSpan().SequenceEqual(pair), "Off must preserve the original samples.");
int[] curve = new int[PcEqualizer.BandCount];
curve[5] = 6;
eq.SetCurve(curve);
eq.SetMode(true, compareFlat: false);
for (int i = 0; i < 7; i++) Render();
var wet = Rms(block);
double spread = Decibels(wet.Left / wet.Right);
Require(spread is > 5 and < 6.5, $"1 kHz boost relative to 4 kHz was {spread:F2} dB.");

eq.SetMode(true, compareFlat: true);
for (int i = 0; i < 3; i++) Render();
var dry = Rms(block);
Require(Math.Abs(Decibels(dry.Left / dry.Right)) < .05, "Flat must restore the relative frequency balance.");
double dryChange = Decibels(Combined(dry) / Combined(Rms(pair)));
Require(Math.Abs(dryChange) < .01, $"Comparing flat must preserve playback level. Change: {dryChange:F2} dB.");
Require(Math.Abs(Decibels(Combined(wet) / Combined(dry))) < 2, "A/B should remain close in level.");

eq.SetMode(true, compareFlat: false);
int[] deepBass = new int[PcEqualizer.BandCount];
deepBass[0] = 12;
eq.SetCurve(deepBass);
for (int i = 0; i < 7; i++) Render();
double offBandChange = Decibels(Combined(Rms(block)) / Combined(Rms(pair)));
Require(Math.Abs(offBandChange) < .1, $"A bass boost must not duck unrelated frequencies. Change: {offBandChange:F2} dB.");

// Repeated bypass while a continuous stereo stream stays open.
eq.SetCurve([0, 0, 0, 0, 0, 6, 0, 0, 0, 0]);
for (int i = 0; i < 4; i++) Render();
pair.CopyTo(block, 0);
for (int offset = 0; offset < block.Length; offset += 480)
{
    eq.SetMode(offset / 480 % 2 == 0, compareFlat: false);
    var segment = block.AsSpan(offset, 480);
    eq.Process(segment);
    Require(segment.ContainsAnyExcept(0f), "EQ toggles must preserve continuous playback.");
}

Array.Fill(curve, 12);
eq.SetCurve(curve);
eq.SetMode(true, compareFlat: false);
for (int i = 0; i < block.Length; i++) block[i] = i % 5 == 0 ? .99f : -.99f;
eq.Process(block);
Require(block.All(x => float.IsFinite(x) && MathF.Abs(x) <= 1f), "Full-scale playback must remain finite and within the ceiling.");
eq.SetMode(false, compareFlat: false);
Render();
Render();
Require(block.AsSpan().SequenceEqual(pair), "Bypass must settle back to untouched stereo.");

eq.SetMode(true, compareFlat: false);
eq.SetCurve(new int[PcEqualizer.BandCount]);
Render();
Require(block.AsSpan().SequenceEqual(pair), "Enabling a flat EQ must preserve the original samples.");
long before = GC.GetAllocatedBytesForCurrentThread();
Render();
Require(GC.GetAllocatedBytesForCurrentThread() == before, "Renderer must allocate no managed memory during processing.");
Console.WriteLine($"PC EQ passed filter accuracy ({spread:F2} dB), flat comparison ({dryChange:F2} dB change), off-band gain ({offBandChange:F2} dB change), bypass, clipping and allocation checks.");

void Render()
{
    pair.CopyTo(block, 0);
    eq.Process(block);
}

static (double Left, double Right) Rms(float[] samples)
{
    double left = 0, right = 0;
    // Ignore the transition into the block and compare settled audio.
    for (int i = samples.Length / 2; i < samples.Length; i += 2)
    {
        left += samples[i] * samples[i];
        right += samples[i + 1] * samples[i + 1];
    }
    return (Math.Sqrt(left / (samples.Length / 4)), Math.Sqrt(right / (samples.Length / 4)));
}

static double Combined((double Left, double Right) sides) =>
    Math.Sqrt((sides.Left * sides.Left + sides.Right * sides.Right) / 2);
static double Decibels(double ratio) => 20 * Math.Log10(ratio);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

namespace WPods.Audio
{
    internal static class SpatialStereo
    {
        internal const int Rate = 48000;
    }
}
