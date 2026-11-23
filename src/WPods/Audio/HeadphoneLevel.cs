using System.Runtime.InteropServices;
using WPods.Core;
using static WPods.Audio.AudioEndpoints;

namespace WPods.Audio;

/// <summary>Measures the digital stereo mix of one physical pair's Windows playback endpoint.</summary>
internal static class HeadphoneLevel
{
    private const int Loopback = 0x00020000;
    private const int EventCallback = 0x00040000;
    private const uint Silent = 2;
    private const int WindowMs = 250;
    private const int RouteCheckMs = 5000;
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Reading Waiting = new("waiting");
    private static readonly Reading Unsupported = new("unsupported");

    internal sealed record Reading(string Status, int? LeftDb = null, int? RightDb = null,
        int? VolumePercent = null, bool? Muted = null, string? OutputName = null, int? Stream = null,
        long? ElapsedMs = null, double? SampleMs = null, double? MeasuredMs = null, double? ActiveMs = null, int? AverageDb = null, int? SignalDb = null);

    internal static void Watch(ulong address, Func<bool> connected, Action<Reading> post, CancellationToken cancel)
    {
        int? lastFault = null;
        long start = Environment.TickCount64;
        int stream = 0;
        HeadphoneLevelSession session = new();
        void Publish(Reading reading) => post(reading with
        {
            MeasuredMs = session.MeasuredMs, ActiveMs = session.ActiveMs, AverageDb = session.AverageDb,
        });
        while (!cancel.IsCancellationRequested && connected())
        {
            try
            {
                var enumerator = CreateEnumerator();
                try
                {
                    int revision = DefaultOutputWatcher.EndpointRevision;
                    string? endpoint = FindPairPlaybackEndpoint(enumerator, address);
                    if (endpoint is null) Publish(Waiting);
                    else Capture(enumerator, endpoint, address, revision, start, ++stream, session, connected, Publish, cancel);
                }
                finally { Marshal.ReleaseComObject(enumerator); }
                lastFault = null;
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidCastException)
            {
                // Invalidated, unplugged and no-device errors can accompany normal Bluetooth route changes.
                bool invalidated = error.HResult is unchecked((int)0x88890004) or unchecked((int)0x88890026) or
                    unchecked((int)0x80070490);
                if (!invalidated && lastFault != error.HResult)
                    Log.Warn("level", $"Playback level unavailable 0x{error.HResult:X8}");
                lastFault = error.HResult;
                Publish(invalidated ? Waiting : Unsupported);
            }
            if (!connected()) return;
            cancel.WaitHandle.WaitOne(1000);
        }
    }

    private static unsafe void Capture(IMMDeviceEnumerator enumerator, string endpoint, ulong address, int revision,
        long start, int stream, HeadphoneLevelSession session, Func<bool> connected, Action<Reading> post, CancellationToken cancel)
    {
        using var ready = new AutoResetEvent(false);
        Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpoint, out IMMDevice device));
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IAudioEndpointVolume? volume = null;
        bool started = false;
        try
        {
            if (!IsActivePairPlaybackEndpoint(device, address))
            {
                post(Waiting);
                return;
            }
            string? outputName = ReadFriendlyName(device);
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17, 0, out object instance));
            client = (IAudioClient)instance;
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out nint format));
            int rate;
            try
            {
                rate = ReadStereoFloatRate(format);
                if (rate == 0)
                {
                    post(Unsupported with { OutputName = outputName });
                    return;
                }
                // Shared event-driven loopback is supported on Windows 10 version 1703 and newer.
                Marshal.ThrowExceptionForHR(client.Initialize(0, Loopback | EventCallback, 0, 0, format, 0));
            }
            finally { Marshal.FreeCoTaskMem(format); }

            Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            iid = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out object captureService));
            capture = (IAudioCaptureClient)captureService;
            if (cancel.IsCancellationRequested || !connected()) return;
            Marshal.ThrowExceptionForHR(client.Start());
            started = true;
            volume = OpenEndpointVolume(device);
            post(Waiting with { OutputName = outputName });

            AWeightedRms meter = new(rate);
            WaitHandle[] signals = [cancel.WaitHandle, ready];
            long now = Environment.TickCount64;
            long nextWindow = now + WindowMs;
            long nextRouteCheck = now + RouteCheckMs;
            while (!cancel.IsCancellationRequested && connected())
            {
                int timeout = (int)Math.Max(0, nextWindow - Environment.TickCount64);
                if (WaitHandle.WaitAny(signals, timeout) == 0 || !connected()) return;
                now = Environment.TickCount64;
                int currentRevision = DefaultOutputWatcher.EndpointRevision;
                if (now >= nextRouteCheck || revision != currentRevision)
                {
                    revision = currentRevision;
                    nextRouteCheck = now + RouteCheckMs;
                    if (FindPairPlaybackEndpoint(enumerator, address) != endpoint)
                    {
                        post(Waiting);
                        return;
                    }
                }
                // Drain at readiness and at the reporting deadline. The timeout also detects
                // an invalidated endpoint that stopped signaling before it produced another packet.
                while (!cancel.IsCancellationRequested)
                {
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out nint samples, out uint frames, out uint flags, out _, out _));
                    // A zero-frame result acquired no packet and needs no ReleaseBuffer.
                    if (frames == 0) break;
                    try
                    {
                        // Consume the WASAPI buffer in place. Only aggregate energy and filter state leave this scope.
                        if ((flags & Silent) == 0 && samples != 0)
                            meter.Add(new ReadOnlySpan<float>((void*)samples, checked((int)frames * 2)));
                        else
                            meter.AddSilence(frames);
                    }
                    finally { Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames)); }
                }
                if (now >= nextWindow)
                {
                    var (frames, left, right) = meter.Take();
                    double seconds = (double)frames / rate;
                    if (frames > 0) session.Add(seconds, left, right);
                    int? leftDb = HeadphoneLevelSession.DisplayDb(left);
                    int? rightDb = HeadphoneLevelSession.DisplayDb(right);
                    int? percent = volume is not null && volume.GetMasterVolumeLevelScalar(out float level) >= 0 && float.IsFinite(level)
                        ? (int)Math.Round(Math.Clamp(level, 0, 1) * 100) : null;
                    bool? muted = volume is not null && volume.GetMute(out int isMuted) >= 0 ? isMuted != 0 : null;
                    post(new Reading(frames == 0 ? "waiting" : "live", leftDb, rightDb, percent, muted, outputName, stream,
                        frames == 0 ? null : now - start, seconds * 1000, SignalDb: frames == 0 ? null : HeadphoneLevelSession.StereoDb(left, right)));
                    nextWindow = now + WindowMs;
                }
            }
        }
        finally
        {
            if (started) client!.Stop();
            if (volume is not null) Marshal.ReleaseComObject(volume);
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
        }
    }

    private static int ReadStereoFloatRate(nint format)
    {
        if (format == 0 || Marshal.ReadInt16(format, 2) != 2 || Marshal.ReadInt16(format, 12) != 8 ||
            Marshal.ReadInt16(format, 14) != 32) return 0;
        ushort tag = unchecked((ushort)Marshal.ReadInt16(format));
        if (tag == 0xFFFE)
        {
            // WAVEFORMATEXTENSIBLE stores the front-left/right mask and the sample subtype.
            if (Marshal.ReadInt16(format, 16) < 22 || Marshal.ReadInt16(format, 18) != 32 ||
                Marshal.ReadInt32(format, 20) is not (0 or 3) ||
                Marshal.PtrToStructure<Guid>(format + 24) != FloatSubtype) return 0;
        }
        else if (tag != 3) return 0;
        int rate = Marshal.ReadInt32(format, 4);
        return rate >= 44100 ? rate : 0;
    }
}
