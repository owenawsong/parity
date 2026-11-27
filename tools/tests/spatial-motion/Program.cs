using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using WPods.Audio;
using WPods.Core;

var retry = new HeadTrackingRetry();
Require(retry.Due(100), "Track immediately on the first attempt");
retry.Failed(100);
Require(!retry.Due(1099) && retry.Due(1100), "Retry quickly after an interrupted feed");
retry.Failed(2100);
Require(!retry.Due(5099) && retry.Due(5100), "Back off after the next failure");
retry.Failed(6100);
Require(!retry.Due(14099) && retry.Due(14100), "Allow a final automatic retry");
retry.Failed(15100);
Require(retry.Exhausted && !retry.Due(long.MaxValue), "Stop retrying until wear changes or the user retries");
retry.Reset();
Require(!retry.Exhausted && retry.Due(15200), "A new wear state or valid heading re-arms tracking");

static HeadMotion TryGesture(string sensitivity, bool shake, int amplitude, bool changeMidway = false)
{
    var motion = new HeadMotion();
    motion.SetGestureSensitivity(sensitivity);
    long now = 1000;
    void Sample(int value, int repetitions)
    {
        for (int i = 0; i < repetitions; i++, now += 25)
            motion.ObserveAxes(shake ? 0 : value, shake ? value : 0, now);
    }
    Sample(0, 16);
    Sample(amplitude, 12);
    if (changeMidway) motion.SetGestureSensitivity("high");
    if (shake) Sample(-amplitude, 10);
    Sample(0, 12);
    return motion;
}

foreach (bool shake in new[] { false, true })
{
    int small = shake ? 8 : 6, ordinary = shake ? 11 : 9, deliberate = shake ? 16 : 13;
    Require(TryGesture("high", shake, small).Gestures == 1, "Small movement at High");
    Require(TryGesture("normal", shake, small).Gestures == 0, "Small movement at Normal");
    Require(TryGesture("normal", shake, ordinary).Gestures == 1, "Ordinary movement at Normal");
    Require(TryGesture("low", shake, ordinary).Gestures == 0, "Ordinary movement at Low");
    Require(TryGesture("low", shake, deliberate).Gestures == 1, "Deliberate movement at Low");
    Require(TryGesture("high", shake, 2).Gestures == 0, "Jitter caused a shortcut");
    Require(TryGesture("low", shake, deliberate, changeMidway:true).Gestures == 0, "Mid-gesture sensitivity change fired a shortcut");
}
Console.WriteLine("Low, Normal and High distinguish real gesture amplitudes for both axes; jitter and mid-gesture edits are suppressed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static byte[] Frame(Quaternion rotation, bool longSequence, byte service = 16)
{
    byte[] envelope = Convert.FromHexString(longSequence
        ? "04000400170000001000450008F50F10033A3E08101A3A"
        : "040004001700000010004400087F10033A3E08101A3A");
    envelope[^3] = service;
    byte[] frame = new byte[envelope.Length + 58];
    envelope.CopyTo(frame, 0);
    Span<byte> payload = frame.AsSpan(envelope.Length);
    payload[0] = 1;
    payload[9] = 3;
    short Pack(float value) => (short)Math.Clamp(MathF.Round(value * (rotation.W < 0 ? -32768 : 32768)), short.MinValue, short.MaxValue);
    BinaryPrimitives.WriteInt16LittleEndian(payload[20..], Pack(rotation.X));
    BinaryPrimitives.WriteInt16LittleEndian(payload[22..], Pack(rotation.Y));
    BinaryPrimitives.WriteInt16LittleEndian(payload[24..], Pack(rotation.Z));
    return frame;
}

// In a level test mount, forward is -X, right is +Y and up is +Z.
foreach (byte service in new byte[] { 14, 16 })
{
    foreach (int direction in new[] { -1, 1 })
    {
        foreach (bool pro3 in new[] { false, true })
        {
            var gesture = new HeadMotion(invertPracticePitch: pro3);
            long time = 1000;
            for (int i = 0; i < 16; i++)
                gesture.Observe(Frame(Quaternion.Identity, i % 2 == 0, service), time += 25);
            Require(gesture.Calibrated && gesture.GesturePitchUp == 0, "practice center");
            var signedAxis = pro3 ? -Vector3.UnitY : Vector3.UnitY;
            var nod = Quaternion.CreateFromAxisAngle(signedAxis, direction * MathF.PI / 7);
            for (int i = 0; i < 12; i++)
                gesture.Observe(Frame(nod, i % 2 == 0, service), time += 25);
            Require(Math.Sign(gesture.GesturePitchUp) == direction,
                $"Practice pitch disagreed with the calibrated sign for service {service}, Pro 3={pro3}");
            gesture.ResetGesture();
            for (int i = 0; i < 10; i++)
                gesture.Observe(Frame(Quaternion.Identity, i % 2 == 0, service), time += 25);
            Require(gesture.Gestures == 0 && gesture.Gesture is null,
                "A motion already in progress must not carry from practice into shortcuts");
        }
    }
}
Console.WriteLine("Practice normalizes model pitch on both motion services and drops gestures across session changes.");
Require(HeadMotion.UseAlternateFirst(null, true) && !HeadMotion.UseAlternateFirst(null, false), "first service by model");
Require(!HeadMotion.UseAlternateFirst(14, true) && HeadMotion.UseAlternateFirst(16, false), "verified report overrides model");
// Replay the start of a recorded sensor stream, including its pre-fusion reports.
foreach (int extraWarmupMs in new[] { 0, 500 })
{
    var tracker = new HeadMotion(spatial: true);
    long start = 10_000;
    for (int ms = 0; ms <= 80 + extraWarmupMs; ms += 5)
    {
        tracker.Observe(Frame(Quaternion.Identity, true), start + ms);
        if (ms <= 80) Require(!tracker.Calibrated, "Pre-fusion reports claimed a forward direction");
    }
    Quaternion pose = default;
    foreach (var (ms, x, y, z) in new[] { (82, 32429, -317, 4369), (85, 32426, -423, 4370),
        (88, 32423, -542, 4372), (94, 32419, -676, 4374), (100, 32414, -824, 4376) })
    {
        var v = new Vector3(x, y, z) / 32768;
        pose = new Quaternion(v, MathF.Sqrt(1 - v.LengthSquared()));
        tracker.Observe(Frame(pose, true), start + ms + extraWarmupMs);
    }
    Require(tracker.Calibrated && Math.Abs(tracker.SpatialYaw) < 3 && Math.Abs(tracker.SpatialPitch) < 3 &&
        Math.Abs(tracker.SpatialRoll) < 3, "First fused pose should leave a forward-facing listener centered");
    // Apply known physical turns to that same earbud mount. Measure the actual
    // output of the renderer for a centered mono impulse after each turn.
    var renderer = new SpatialStereo();
    long time = start + extraWarmupMs + 100;
    int heading = 0;
    foreach (int target in new[] { 0, 65, -65, 0 })
    {
        do
        {
            heading += Math.Sign(target - heading);
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -heading * MathF.PI / 180);
            for (int i = 0; i < (heading == target ? 16 : 1); i++)
                tracker.Observe(Frame(rotation * pose, true), time += 10);
        } while (heading != target);
        renderer.Pose(tracker.SpatialYaw, tracker.SpatialPitch, tracker.SpatialRoll);
        renderer.Process(new byte[48_000]);
        renderer.Process(new byte[512]);
        float[] impulse = new float[2048];
        impulse[0] = impulse[1] = .25f;
        renderer.Process(MemoryMarshal.AsBytes(impulse.AsSpan()));
        double left = 0, right = 0;
        for (int i = 0; i < impulse.Length; i += 2)
        {
            left += impulse[i] * impulse[i];
            right += impulse[i + 1] * impulse[i + 1];
        }
        double db = 10 * Math.Log10(left / right);
        Require(target == 0 ? Math.Abs(db) < .5 : db * Math.Sign(target) > 3,
            $"Heading {target}: unexpected rendered L/R level difference {db:F2} dB");
    }
}
Console.WriteLine("Recorded startup and synthetic turns passed from sensor packets through to stereo output.");
var glitchTracker = new HeadMotion(spatial: true);
var normalPose = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.8f);
glitchTracker.Observe(Frame(normalPose, true), 50);
glitchTracker.Observe(Frame(normalPose, true), 70);
Require(glitchTracker.Calibrated, "glitch test needs a reliable starting pose");
glitchTracker.Observe(Frame(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.7f), true), 90);
Require(glitchTracker.SpatialYaw == 0 && glitchTracker.LastSample == 70, "a single sensor outlier turned the audio");
for (int ms = 110; ms <= 590; ms += 20)
    glitchTracker.Observe(Frame(Quaternion.Identity, true), ms);
Require(glitchTracker.SpatialYaw == 0 && glitchTracker.LastSample == 70, "early-style zero bursts must not overwrite a valid heading");
glitchTracker.Observe(Frame(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.7f), true), 610);
Require(glitchTracker.LastSample == 610 && glitchTracker.SpatialYaw > 0, "a valid turn after the outage must reach the renderer");
foreach (int cadence in new[] { 10, 20, 40, 200, 250 })
{
    var streaming = new HeadMotion(spatial: true);
    Quaternion Turn(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180);
    streaming.Observe(Frame(Turn(17), true), 1000);
    streaming.Observe(Frame(Turn(17), true), 1000);
    Require(!streaming.Calibrated, "batched frames cannot establish a fresh center");
    float turn = Math.Min(cadence * .24f, 30);
    streaming.Observe(Frame(Turn(17 + turn), true), 1000 + cadence);
    Require(streaming.Calibrated && streaming.SpatialYaw == 0, $"center while turning at {cadence} ms cadence");
    streaming.Observe(Frame(Turn(17 + 2 * turn), true), 1000 + 2 * cadence);
    Require(streaming.SpatialYaw < -.5, "motion reaches the renderer on the next report");
}
var cold = new HeadMotion(spatial: true);
var side = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.8f);
cold.Observe(Frame(side, true), 1000);
cold.Observe(Frame(Quaternion.Identity, true), 1020);
Require(!cold.Calibrated, "one discontinuous packet cannot set center");
cold.Observe(Frame(Quaternion.Identity, true), 1800);
Require(!cold.Calibrated, "a stale report cannot set center");
Require(!cold.Observe(Frame(Quaternion.Identity, true, 24), 1810), "unsupported service");
cold.Observe(Frame(Quaternion.Identity, true, 14), 1840);
Require(cold.Calibrated && cold.ReportService == 14, "valid new pair establishes fresh center");
Console.WriteLine("Head tracking calibrated on fast reports and the observed 5 Hz cadence, while rejecting bursts, stale reports and discontinuities.");

foreach (Quaternion mount in new[] { Quaternion.Identity, Quaternion.CreateFromYawPitchRoll(.63f, -.92f, .28f) })
{
    var tracker = new HeadMotion(spatial: true);
    long time = 0;
    void Feed(Quaternion pose)
    {
        bool longer = (time / 20) % 2 == 0;
        Require(tracker.Observe(Frame(pose, longer, longer ? (byte)16 : (byte)14), time += 20), "valid fused packet");
    }
    for (int i = 0; i < 14; ++i) Feed(mount);
    Require(tracker.Calibrated, "steady calibration");
    double previous = 0;
    foreach (int direction in new[] { 1, -1 })
    {
        for (int step = 1; step <= 360; step++)
        {
            int heading = direction == 1 ? step : 360 - step;
            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -heading * MathF.PI / 180);
            Feed(turn * mount);
            Require(Math.Abs(tracker.SpatialYaw - previous) < 2, $"rear seam jump at {heading}");
            Require(Math.Abs(tracker.SpatialYaw - heading) < 2, $"turn stalled at {heading}");
            Require(Math.Abs(tracker.SpatialPitch) < 1, "yaw leaked into pitch");
            Require(Math.Abs(tracker.SpatialRoll) < 1, "yaw leaked into bank");
            previous = tracker.SpatialYaw;
        }
    }
    Vector3 chinAxis = new(0, 1, 1);
    Vector3 right = mount.W * chinAxis + Vector3.Cross(new Vector3(mount.X, mount.Y, mount.Z), chinAxis);
    right.Z = 0;
    right = Vector3.Normalize(right);
    Vector3 forward = Vector3.Cross(Vector3.UnitZ, right);
    Quaternion Pose(double yaw, double pitch, double bank) =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(-yaw * Math.PI / 180)) *
        Quaternion.CreateFromAxisAngle(right, (float)(pitch * Math.PI / 180)) *
        Quaternion.CreateFromAxisAngle(forward, (float)(bank * Math.PI / 180));
    for (int direction = -1; direction <= 1; direction += 2)
    {
        for (int n = 0; n < 20; n++) Feed(Pose(15, 20, direction * 35) * mount);
        Require(Math.Abs(tracker.SpatialRoll - direction * 35) < .4, "signed bank must survive a combined turn");
        Require(Math.Abs(tracker.SpatialPitch - 20) < .4 && Math.Abs(tracker.SpatialYaw - 15) < .4, "bank axis isolation");
    }
    for (int n = 21; n <= 105; n++)
    {
        Feed(Pose(15, n, 35) * mount);
        Require(Math.Abs(tracker.SpatialPitch - n) < 3, $"pitch discontinuity at {n}");
        Require(Math.Abs(tracker.SpatialYaw - 15) < 2 && Math.Abs(tracker.SpatialRoll - 35) < 2,
            $"passing through straight up must hold heading and bank at {n}");
    }
    var origin = Pose(15, 105, 35);
    for (int i = 0; i < 8; i++) Feed(origin * mount);
    tracker.RecenterSpatial();
    for (int i = 0; i < 16; i++) Feed(origin * Pose(0, 0, -25) * mount);
    Require(Math.Abs(tracker.SpatialRoll + 25) < .4 && Math.Abs(tracker.SpatialPitch) < .4 && Math.Abs(tracker.SpatialYaw) < .4,
        "recenter must rebase a physical tilted frame before measuring the new roll");
    double previousRoll = tracker.SpatialRoll;
    for (int deg = -24; deg <= 360; ++deg)
    {
        Feed(origin * Pose(0, 0, deg) * mount);
        Require(Math.Abs(tracker.SpatialRoll - previousRoll) < 2, $"roll seam jump at {deg}");
        Require(Math.Abs(tracker.SpatialRoll - deg) < 2, $"roll stalled at {deg}");
        previousRoll = tracker.SpatialRoll;
    }
    int samples = tracker.Samples;
    byte[] malformed = Frame(mount, true);
    malformed.AsSpan(malformed.Length - 38, 6).Fill(0x7f);
    Require(!tracker.Observe(malformed, time += 20), "reject nonunit sensor vector");
    Require(!tracker.Observe(Frame(mount, false, 19), time += 20), "reject wrong service");
    Require(!tracker.Observe(Frame(mount, true)[..^1], time += 20), "reject truncated payload");
    Require(tracker.Samples == samples, "invalid packets must not refresh pose");
}

var pitchTracker = new HeadMotion(spatial: true);
long t = 0;
for (int n = 0; n < 14; n++) pitchTracker.Observe(Frame(Quaternion.Identity, true), t += 40);
var lookUp = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 30 * MathF.PI / 180);
for (int n = 0; n < 20; n++) pitchTracker.Observe(Frame(lookUp, false), t += 40);
Require(Math.Abs(pitchTracker.SpatialPitch - 30) < .2 && Math.Abs(pitchTracker.SpatialYaw) < .2, "looking up raises pitch");
Console.WriteLine("Motion decoder passed clockwise and counterclockwise 360°, pitch through zenith, signed roll and its wrap, tilted recenter, mixed envelope lengths, two mounts and invalid frames.");
