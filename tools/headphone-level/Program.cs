using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WPods.Audio;

// Read-only research meter. Select a render endpoint explicitly so speaker playback
// can never silently replace a disconnected pair. No microphone or file recording.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args is ["--self-test"]) return CheckMeter();
        if (args is ["--validate-calibration", var calibrationFile])
        {
            try
            {
                PrintCalibration(AcousticCalibration.Load(calibrationFile));
                return 0;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        string? calibrationPath = null;
        if (args is [_, _, _, _, "--calibration", var path])
        {
            calibrationPath = path;
            args = args[..4];
        }
        using var devices = new MMDeviceEnumerator();
        if (args is ["--list"])
        {
            foreach (var item in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (item) Console.WriteLine($"{item.FriendlyName}\n  {item.ID}");
            return 0;
        }
        if (args is not ["--endpoint", var id, "--seconds", var duration] ||
            !int.TryParse(duration, out int seconds) || seconds is < 1 or > 120)
        {
            Console.Error.WriteLine("Use --list, --self-test, --validate-calibration <file>, or --endpoint <render endpoint ID> --seconds <1..120> [--calibration <file>].");
            return 2;
        }
        try
        {
            using var device = devices.GetDevice(id);
            if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
                throw new InvalidOperationException("The selected playback endpoint is unavailable.");
            using var capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32 ||
                format.Channels != 2 || format.SampleRate < 44100)
                throw new InvalidOperationException($"Expected stereo floating-point playback at 44.1 kHz or higher. Actual format: {format}.");
            var calibration = calibrationPath is not null ? AcousticCalibration.Load(calibrationPath) : null;
            calibration?.CheckRoute(device.ID, format.SampleRate);
            var volume = device.AudioEndpointVolume;
            if (calibration is not null)
            {
                PrintCalibration(calibration);
                if (volume.Channels.Count != 2) throw new InvalidOperationException("Calibration requires stereo endpoint gain controls.");
                Console.WriteLine("Use the bench setup printed above. Wear, firmware and audio processing cannot be verified by this tool.");
            }

            var meter = new AWeightedRms(format.SampleRate);
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += (_, e) => meter.Add(MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded)));
            capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
            Console.WriteLine($"Playback: {device.FriendlyName} | {format.SampleRate} Hz.");
            int volumeRevision = 0;
            void VolumeChanged(AudioVolumeNotificationData _) => Interlocked.Increment(ref volumeRevision);
            volume.OnVolumeNotification += VolumeChanged;
            try
            {
                var baseline = ReadVolume(volume);
                int revision = Volatile.Read(ref volumeRevision);
                capture.StartRecording();
                for (int i = 0; i < seconds; i++)
                {
                    if (await Task.WhenAny(Task.Delay(1000), stopped.Task) == stopped.Task)
                        throw new InvalidOperationException("Endpoint capture stopped early.", await stopped.Task);
                    var (frames, left, right) = meter.Take();
                    var current = ReadVolume(volume);
                    int currentRevision = Volatile.Read(ref volumeRevision);
                    bool stable = current == baseline && currentRevision == revision;
                    string reading = frames == 0 ? "No playback samples." :
                        calibration is null ? $"{frames} frames | L {Display(left)} | R {Display(right)} | {VolumeLabel(current.Gain)} | mute {current.Muted}{(stable ? "" : " | gain changed")}" :
                        Estimate(calibration, current, stable, frames, left, right);
                    Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff}Z | {reading}");
                    (baseline, revision) = (current, currentRevision);
                }
                capture.StopRecording();
                if (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)) is { } error) throw error;
            }
            finally { volume.OnVolumeNotification -= VolumeChanged; }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Display(double db) => double.IsNegativeInfinity(db) ? "silence" : $"{db:F1} dBFS(A)";

    private static (AcousticCalibration.Gains Gain, bool Muted) ReadVolume(AudioEndpointVolume volume)
    {
        var channels = volume.Channels;
        double master = volume.MasterVolumeLevel;
        return (new AcousticCalibration.Gains(master, channels.Count == 2 ? channels[0].VolumeLevel : double.NaN,
            channels.Count == 2 ? channels[1].VolumeLevel : double.NaN).Rounded(), volume.Mute);
    }

    private static string VolumeLabel(AcousticCalibration.Gains gain) =>
        $"gain dB master {gain.MasterDb:F2}, L {gain.LeftDb:F2}, R {gain.RightDb:F2}";

    private static string Estimate(AcousticCalibration calibration,
        (AcousticCalibration.Gains Gain, bool Muted) volume, bool stable, long frames, double left, double right)
    {
        if (volume.Muted || !stable || frames < calibration.SampleRate * .9 || frames > calibration.SampleRate * 1.1)
            return "Acoustic estimate unavailable for this window (muted, gain changed or incomplete playback).";
        var estimate = calibration.Estimate(volume.Gain, left, right);
        return $"Estimated L {Dba(estimate.Left)} | R {Dba(estimate.Right)} | {VolumeLabel(volume.Gain)}";
    }

    private static string Dba(double? value) => value is double db ? $"{db:F1} dBA" : "outside validated range";

    private static void PrintCalibration(AcousticCalibration calibration)
    {
        Console.WriteLine($"{calibration.ReferencePlane} | {calibration.Setup}");
        Console.WriteLine($"Validated {calibration.GainStates} gain state(s), {calibration.HoldoutCount} held-out trials. Worst held-out error: {calibration.WorstHoldoutDb:F2} dB.");
    }

    private static int CheckMeter()
    {
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            foreach (var (hz, adjustment) in new[] { (100, -19.1), (1000, 0.0), (4000, 1.0) })
            {
                var meter = new AWeightedRms(rate);
                var samples = new float[2 * rate];
                for (int i = 0; i < rate; i++)
                {
                    samples[2 * i] = (float)(0.1 * Math.Sin(2 * Math.PI * i * hz / rate));
                    samples[2 * i + 1] = samples[2 * i] / 2;
                }
                meter.Add(samples);
                meter.Take();
                for (int i = 0; i < samples.Length; i += 256)
                    meter.Add(samples.AsSpan(i, Math.Min(256, samples.Length - i)));
                var (frames, left, right) = meter.Take();
                if (frames != rate || Math.Abs(left - (-23.0103 + adjustment)) > 0.25 ||
                    Math.Abs(left - right - 6.0206) > 0.001 || meter.Take().Frames != 0) return 1;
                meter.AddSilence((uint)rate);
                meter.Take();
                meter.AddSilence((uint)rate);
                var quiet = meter.Take();
                if (quiet.Frames != rate || quiet.Left > -120 || quiet.Right > -120) return 1;
            }
        }
        var session = new HeadphoneLevelSession();
        if (session.MeasuredMs != 0 || session.ActiveMs != 0 || session.AverageDb is not null) return 1;
        session.Add(.25, double.NegativeInfinity, double.NegativeInfinity);
        if (session.MeasuredMs != 250 || session.ActiveMs != 0 || session.AverageDb is not null) return 1;
        session.Add(.25, -20, double.NegativeInfinity);
        session.Add(.5, -40, -40);
        // Stereo power for a single active channel is halved. The silence in the first window
        // contributes to the denominator, and the quieter window is twice as long.
        if (session.MeasuredMs != 1000 || session.ActiveMs != 750 || session.AverageDb != -29) return 1;
        Console.WriteLine("Passed digital level, frequency weighting, stereo separation, silence, window reset and session weighting checks.");
        CheckCalibration();
        return 0;
    }

    private static void CheckCalibration()
    {
        var firstGain = new AcousticCalibration.Gains(-10, -10, -13);
        var secondGain = new AcousticCalibration.Gains(-20, -21, -25);
        static AcousticCalibration.Trial[] Sweep(string prefix, AcousticCalibration.Gains gain, int leftOffset, int rightOffset)
        {
            AcousticCalibration.Trial Trial(string id, string signal, bool holdout, int db, int residual) =>
                new(prefix + id, signal, holdout, gain, db, db - 4, db + leftOffset + residual, db - 4 + rightOffset - residual);
            return
            [
                Trial("fit1", "tone", false, -40, 0),
                Trial("fit2", "tone", false, -10, 0),
                Trial("test1", "program simulation", true, -35, 1),
                Trial("test2", "speech", true, -15, -1),
            ];
        }

        var trials = new[] { Sweep("A", firstGain, 93, 89), Sweep("B", secondGain, 81, 76) }.SelectMany(x => x).ToArray();
        var samples = new AcousticCalibration.Measurements("fixture", 48000, "ear-simulator", "synthetic test", trials);
        var model = AcousticCalibration.Fit(samples);
        model.CheckRoute("FIXTURE", 48000);
        if (model.WorstHoldoutDb != 1 || model.GainStates != 2 || model.HoldoutCount != 4 ||
            model.Estimate(firstGain, -25, -29) != (68d, 60d) ||
            model.Estimate(secondGain, -25, -29) != (56d, 47d) ||
            model.Estimate(firstGain with { LeftDb = -11 }, -25, -29) != (null, null) ||
            model.Estimate(firstGain, -39, double.NegativeInfinity) != (null, null))
            throw new InvalidOperationException("Wrong calibration estimate or unsupported range accepted.");
        if (Estimate(model, (firstGain, true), true, 48000, -25, -29).StartsWith("Estimated", StringComparison.Ordinal) ||
            Estimate(model, (firstGain, false), false, 48000, -25, -29).StartsWith("Estimated", StringComparison.Ordinal) ||
            Estimate(model, (firstGain, false), true, 22000, -25, -29).StartsWith("Estimated", StringComparison.Ordinal))
            throw new InvalidOperationException("A muted, interrupted or incomplete reading must be discarded.");

        static void Reject(Action attempt)
        {
            try { attempt(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Expected calibration to reject this trial set or endpoint.");
        }

        Reject(() => model.CheckRoute("another endpoint", 48000));
        Reject(() => model.CheckRoute("fixture", 44100));
        var before = trials[2];
        trials[2] = before with { LeftDba = before.LeftDba + 4 };
        Reject(() => AcousticCalibration.Fit(samples));
        trials[2] = before with { Signal = "TONE" };
        Reject(() => AcousticCalibration.Fit(samples));
        trials[2] = before with { Id = trials[0].Id };
        Reject(() => AcousticCalibration.Fit(samples));
        Console.WriteLine("Passed acoustic bench replay, gain/range matching, mute and route rejection, and held-out validation.");
    }
}
