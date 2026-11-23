using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WPods.Audio;

// Read-only research meter. Select a render endpoint explicitly so speaker playback
// can never silently replace a disconnected pair. No microphone or file recording.
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"]) return CheckMeter();
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
            Console.Error.WriteLine("Use --list, --self-test, or --endpoint <render endpoint ID> --seconds <1..120>.");
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
            var meter = new AWeightedRms(format.SampleRate);
            var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += (_, e) => meter.Add(MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded)));
            capture.RecordingStopped += (_, e) => stopped.TrySetResult(e.Exception);
            Console.WriteLine($"Playback: {device.FriendlyName}. Digital A-weighted RMS; acoustic calibration unavailable.");
            capture.StartRecording();
            for (int i = 0; i < seconds; i++)
            {
                if (await Task.WhenAny(Task.Delay(1000), stopped.Task) == stopped.Task)
                    throw new InvalidOperationException("Endpoint capture stopped early.", await stopped.Task);
                var (frames, left, right) = meter.Take();
                Console.WriteLine(frames == 0 ? "No playback samples." :
                    $"{frames} frames | L {Display(left)} | R {Display(right)} | endpoint gain {device.AudioEndpointVolume.MasterVolumeLevel:F1} dB; mute {device.AudioEndpointVolume.Mute}");
            }
            capture.StopRecording();
            if (await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)) is { } error) throw error;
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Display(double db) => double.IsNegativeInfinity(db) ? "silence" : $"{db:F1} dBFS(A)";

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
        return 0;
    }
}
