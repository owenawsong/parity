using System.Diagnostics;
using System.Runtime.InteropServices;
using WPods.Audio;

if (args.Contains("--cold-start"))
{
    var load = Stopwatch.StartNew();
    var renderer = new SpatialStereo();
    renderer.Process(new byte[512]);
    Console.WriteLine(load.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    return;
}

if (args.Length == 2 && args[0] == "--soak-minutes" && int.TryParse(args[1], out int minutes) && minutes is >= 1 and <= 180)
{
    var renderer = new SpatialStereo();
    var simulated = new float[3_840]; // 40 ms of stereo at 48 kHz
    var source = new Random(48);
    // Initialize the SIMD and silent-buffer paths before watching callback allocation.
    Render(renderer, simulated);
    renderer.Pose(20, 10, 3);
    for (int i = 0; i < 32; i++)
    {
        Array.Fill(simulated, .1f);
        Render(renderer, simulated);
    }
    var clock = Stopwatch.StartNew();
    long blocks = 0;
    int reportedMinute = 0;
    double maxRenderMs = 0;
    while (clock.Elapsed.TotalMinutes < minutes)
    {
        double now = clock.Elapsed.TotalSeconds;
        bool playing = now % 13 < 11;
        renderer.Pose(360 * Math.Sin(now * .84), 50 * Math.Sin(now * .31), 33 * Math.Sin(now * .44));
        renderer.SetSpeakerAngle(((int)now % 3) switch { 0 => 20, 1 => 55, _ => 30 });
        for (int i = 0; i < simulated.Length; i++) simulated[i] = playing ? source.NextSingle() * .42f - .21f : 0;
        long allocatedAtRead = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        Render(renderer, simulated);
        long sampleAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedAtRead;
        maxRenderMs = Math.Max(maxRenderMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (sampleAllocation != 0) throw new InvalidOperationException($"Audio thread allocated {sampleAllocation} bytes in buffer {blocks}.");
        foreach (float output in simulated)
            Require(float.IsFinite(output) && Math.Abs(output) <= .99001f, "Invalid output in soak test.");
        blocks++;
        int elapsedMinute = (int)clock.Elapsed.TotalMinutes;
        if (elapsedMinute > reportedMinute)
        {
            reportedMinute = elapsedMinute;
            Console.WriteLine($"Minute {reportedMinute}: {blocks} buffers, largest render {maxRenderMs:F2} ms.");
        }
        Thread.Sleep(39);
    }
    Console.WriteLine($"PASS {minutes} minutes: {blocks} blocks, zero audio thread allocations, all samples within the limiter.");
    return;
}

static void Require(bool ok, string reason)
{
    if (!ok) throw new InvalidOperationException(reason);
}

static void Render(SpatialStereo dsp, float[] samples) =>
    dsp.Process(MemoryMarshal.AsBytes(samples.AsSpan()));

static float[] Impulse(double yaw, double pitch = 0, bool mono = true, double roll = 0)
{
    var dsp = new SpatialStereo();
    dsp.Pose(yaw, pitch, roll);
    Render(dsp, new float[24_576]);
    var impulse = new float[4096];
    impulse[0] = .25f;
    if (mono) impulse[1] = impulse[0];
    Render(dsp, impulse);
    return impulse;
}

static double Balance(float[] samples)
{
    double left = 0, right = 0;
    for (int i = 0; i < samples.Length; i += 2)
    {
        left += samples[i] * samples[i];
        right += samples[i + 1] * samples[i + 1];
    }
    return 10 * Math.Log10(left / right);
}

static double Difference(float[] a, float[] b)
{
    double error = 0, reference = 0;
    for (int i = 0; i < a.Length; i++)
    {
        error += Math.Pow(a[i] - b[i], 2);
        reference += b[i] * b[i];
    }
    return Math.Sqrt(error / reference);
}

var front = Impulse(0);
var widthRenderer = new SpatialStereo();
float[] SpacedImpulse(int angle)
{
    widthRenderer.SetSpeakerAngle(angle);
    Render(widthRenderer, new float[24_576]);
    var pulse = new float[4096];
    pulse[0] = .25f;
    Render(widthRenderer, pulse);
    return pulse;
}
double narrow = Balance(SpacedImpulse(20)), standard = Balance(SpacedImpulse(30)), wide = Balance(SpacedImpulse(55));
Require(narrow < standard && standard < wide && wide - narrow > 1, "Speaker spacing must change the audible placement of a left source.");
Require(Math.Abs(Balance(SpacedImpulse(999)) - standard) < .05, "Unexpected spacing must restore standard placement.");
Console.WriteLine($"Left-channel separation at Narrow / Standard / Wide: {narrow:F1} / {standard:F1} / {wide:F1} dB.");
var rear = Impulse(180);
Require(Math.Abs(Balance(front)) < .01 && Math.Abs(Balance(rear)) < .01, "Centered mono should balance in front and behind.");
Require(Difference(rear, front) > .25, "Front and rear impulse responses should be distinct.");
Require(Difference(Impulse(0, 40), front) > .15, "Looking up should change the impulse response.");
Require(Balance(Impulse(65)) > 3 && Balance(Impulse(-65)) < -3, "A right turn should bring the front stage toward the left ear.");
Require(Balance(Impulse(0, mono: false)) > 3, "Left source should be strongest on the left.");
Require(Balance(Impulse(180, mono: false)) < -3, "The same source should be strongest on the right after a half turn.");
// Bass in a centered mix should retain its level through the spatializer.
var bass = new float[24_000];
for (int i = 0; i < bass.Length; i += 2)
    bass[i] = bass[i + 1] = .1f * MathF.Sin(i * MathF.PI * 60 / SpatialStereo.Rate);
Render(new SpatialStereo(), bass);
double bassDb = 10 * Math.Log10(bass.Skip(12000).Average(x => x * x) / .005);
Require(Math.Abs(bassDb) < 2, $"Centered bass level changed by {bassDb:F1} dB.");
double rearStep = Math.Abs(Balance(Impulse(179)) - Balance(Impulse(-179)));
Require(rearStep < 1.5, "The rear seam should have a small interaural change.");

var tracked = new SpatialStereo();
var held = new SpatialStereo();
foreach (var dsp in new[] { tracked, held })
{
    dsp.Pose(179);
    Render(dsp, new float[24_576]);
}
var noise = new float[8192];
var rng = new Random(25);
for (int i = 0; i < noise.Length; i++) noise[i] = (rng.NextSingle() - .5f) * .4f;
var moving = (float[])noise.Clone();
var stationary = (float[])noise.Clone();
tracked.Pose(-179);
Render(tracked, moving);
Render(held, stationary);
var endpoint = new SpatialStereo();
endpoint.Pose(-179);
Render(endpoint, new float[24_576]);
var final = (float[])noise.Clone();
Render(endpoint, final);
for (int i = 0; i < noise.Length; i += 256)
{
    var window = i..(i + 256);
    double endpointDifference = Difference(final[window], stationary[window]);
    Require(Difference(moving[window], stationary[window]) <= endpointDifference + .08,
        "A two-degree turn across the rear must stay near the endpoint responses for the entire transition.");
}
float[] loud = Enumerable.Repeat(8f, 2048).ToArray();
Render(tracked, loud);
Require(loud.All(sample => float.IsFinite(sample) && Math.Abs(sample) <= .99001f), "Output must remain bounded.");
Render(tracked, new float[8192]);
var pause = new float[8192];
tracked.Pose(20, 15);
Render(tracked, pause);
Require(pause.All(sample => sample == 0), "A paused and drained stream must produce digital silence.");
var resume = new float[4096];
resume[0] = resume[1] = .25f;
Render(tracked, resume);
var clean = Impulse(20, 15);
float trim = resume.Max() / clean.Max();
Require(trim is >= .39f and <= 2.51f, "Resume gain must stay bounded.");
for (int i = 0; i < clean.Length; i++) clean[i] *= trim;
Require(Difference(resume, clean) < .0001, "Resume must use the latest pose and a clean history.");

var timer = Stopwatch.StartNew();
var block = new float[960];
long before = GC.GetAllocatedBytesForCurrentThread();
for (int tick = 0; tick < 500; tick++)
{
    Array.Copy(noise, block, block.Length);
    tracked.Pose(tick * .72, 20 * Math.Sin(tick * .02));
    Render(tracked, block);
}
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
timer.Stop();
Require(allocated == 0, "Rendering allocated during a full turn.");
Console.WriteLine($"PASS. Rear 179/-179 balance difference {rearStep:F2} dB. " +
    $"Moving full-turn benchmark: {timer.Elapsed.TotalMilliseconds:F1} ms for 5 s, {allocated} B allocated.");

LoudnessChecks.Run();

var earDown = Impulse(0, mono: false, roll: 40);
var earUp = Impulse(0, mono: false, roll: -40);
Require(Difference(earDown, earUp) > .1, "Tilting the ears must change elevation cues from a side speaker.");
Require(Difference(Impulse(30, 45, roll: 20), Impulse(210, 135, roll: 200)) < .001,
    "Equivalent poses on either side of vertical must have the same impulse response.");
Require(Difference(Impulse(179.8, 15, roll: 31), Impulse(-179.8, 15, roll: 31)) < .08,
    "A tilted listener must have a smooth rear seam.");
Require(Difference(Impulse(0, 20, roll: 179.8), Impulse(0, 20, roll: -179.8)) < .08,
    "Rolling through the wrap must preserve neighboring impulse responses.");
Console.WriteLine("Signed head tilt, equivalent upright/inverted poses, and banked rear/roll seams passed.");

// The sensor's two Euler representations meet at vertical. A one-degree
// movement should produce the same *transition*, including intermediate audio.
foreach (int sign in new[] { -1, 1 })
{
    var continuous = new SpatialStereo();
    var wrapped = new SpatialStereo();
    foreach (var dsp in new[] { continuous, wrapped })
    {
        dsp.Pose(20, sign * 89.5, 10);
        Render(dsp, new float[24_576]);
    }
    continuous.Pose(20, sign * 90.5, 10);
    wrapped.Pose(200, sign * 89.5, 190);
    var nearPole = (float[])noise.Clone();
    var acrossPole = (float[])noise.Clone();
    Render(continuous, nearPole);
    Render(wrapped, acrossPole);
    var poleDifference = Difference(acrossPole, nearPole);
    Require(poleDifference < .001, $"Equivalent moves through {sign * 90} degrees diverged by {poleDifference:P1}.");
    Console.WriteLine($"Tracking transition through {sign * 90} degrees: {poleDifference:P3} difference.");
}

// With a single virtual speaker, sweeping its bearing across a measured interval
// should not introduce a comb notch halfway between its two recorded responses.
static double TrebleEnergy(float[] stereo, int ear)
{
    const int length = 512;
    double energy = 0;
    for (int bin = 65; bin <= 138; bin++) // 6–13 kHz at the renderer's 48 kHz
    {
        double coeff = 2 * Math.Cos(2 * Math.PI * bin / length);
        double last = 0, prior = 0;
        for (int frame = 0; frame < length; frame++)
        {
            double next = stereo[frame * 2 + ear] + coeff * last - prior;
            prior = last;
            last = next;
        }
        energy += last * last + prior * prior - coeff * last * prior;
    }
    return energy;
}

var dips = new List<double>();
var lower = Impulse(-30, mono: false);
for (int angle = 0; angle < 180; angle += 5)
{
    var mid = Impulse(-30 - angle - 2.5, mono: false);
    var upper = Impulse(-30 - angle - 5, mono: false);
    for (int ear = 0; ear < 2; ear++)
    {
        double endpointMean = (TrebleEnergy(lower, ear) + TrebleEnergy(upper, ear)) / 2;
        dips.Add(10 * Math.Log10(TrebleEnergy(mid, ear) / endpointMean));
    }
    lower = upper;
}
dips.Sort();
double median = (dips[35] + dips[36]) / 2;
Require(median > -.2 && dips[0] > -1.9, "Intermediate angles must preserve horizontal treble energy.");
Console.WriteLine($"Horizontal 6–13 kHz midpoint shortfall across 72 ear/bearing pairs: median {median:F2} dB; worst {dips[0]:F2} dB.");
