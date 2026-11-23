#if PAIRSIDE_LIVE
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace WPods.Audio;

internal sealed class SpotifyBridge : IDisposable
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SpatialStereo.Rate, 2);
    private readonly MMDeviceEnumerator _devices = new();
    private readonly MMDevice _sink, _airpods;
    private readonly Loopback _capture;
    private readonly WasapiOut _output;
    private readonly SpatialFeed _feed = new();
    private long _lastSignal;
    private long _levelTime;
    private int _levels;
    internal volatile bool Metering;
    internal int SourceLevels
    {
        get
        {
            int media = Environment.TickCount64 - Interlocked.Read(ref _levelTime) <= 180 ? Volatile.Read(ref _levels) : 0;
            int test = _feed.Check.Levels;
            return Math.Max(media & 255, test & 255) | Math.Max(media >> 8, test >> 8) << 8;
        }
    }
    private volatile bool _failed;
    private readonly int InitialPid;
    internal readonly string SourceIdentity = "";
    private bool _mixOwnsDefault;
    internal sealed record RouteBeforeSpatial(string Pair, WindowsSpatialMix.Saved? Mix, SpotifyRouting.Saved? App, string? DefaultMedia = null)
    {
        internal bool CanResume(SpatialApp source) => source.IsWindowsMix
            ? Mix != null && WindowsSpatialMix.MatchesMedia(Mix, AudioEndpoints.DefaultPlaybackRole)
            : App != null && (App.Multimedia != null || DefaultStillMatches()) && SpotifyRouting.ReadyToResume(source, App);

        internal bool DefaultStillMatches() =>
            string.Equals(DefaultMedia, AudioEndpoints.DefaultPlaybackRole(1), StringComparison.OrdinalIgnoreCase);
    }
    internal RouteBeforeSpatial OriginalRoute { get; }
    internal static long? ResumeWindow(long now, long? quietSince, bool callAudio, bool captureActive) =>
        callAudio || captureActive ? null : quietSince ?? now;
    internal static bool CanRetryResume(long now, long rejoinUntil, long? quietSince) =>
        (rejoinUntil == 0 || now < rejoinUntil) && quietSince is long quiet && now - quiet < 10_000;

    internal int CurrentPid => SpatialApp.SameIdentity(SourceIdentity, SpatialApp.IdentityOf(InitialPid)) ? InitialPid : App.FindPid(SourceIdentity);
    // The UI and playback diagnostics read these across COM apartments.
    internal string SinkId { get; }
    internal string PairId { get; }
    internal bool Failed => _failed;
    internal bool HasSignal => _lastSignal != 0 && Environment.TickCount64 - Interlocked.Read(ref _lastSignal) < 1800;
    internal void Pose(double yaw, double pitch = 0, double roll = 0) => _feed.Dsp.Pose(yaw, pitch, roll);
    internal void SetSpeakerAngle(int degrees) => _feed.Dsp.SetSpeakerAngle(degrees);
    internal void SetLevel(string? level) => _feed.Dsp.SetLevel(level);
    internal void TestSpeakers() => _feed.Check.Start();

    private sealed class Loopback(MMDevice source) : WasapiCapture(source, true, 24)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
    }
    private sealed class SpatialFeed : IWaveProvider
    {
        internal readonly SpatialPlaybackBuffer Buffer = new(SpatialStereo.Rate / 10);
        internal readonly SpatialStereo Dsp = new();
        internal readonly SpeakerCheck Check = new();
        public WaveFormat WaveFormat => Format;
        public int Read(byte[] bytes, int offset, int count)
        {
            var audio = bytes.AsSpan(offset, count);
            var samples = MemoryMarshal.Cast<byte, float>(audio);
            Buffer.Read(samples);
            Check.Mix(samples);
            Dsp.Process(audio);
            return count;
        }
    }

    // A finite L / center / R reference mixed upstream of the same HRTF as the
    // user's music. Preparing the tap here keeps synthesis off the output thread.
    internal sealed class SpeakerCheck
    {
        private const int Interval = SpatialStereo.Rate / 2;
        private readonly float[] _tap = CreateTap();
        private int _frame = -1;
        private int _levels;

        internal void Start() => Interlocked.CompareExchange(ref _frame, 0, -1);
        internal int Levels => Volatile.Read(ref _levels);

        private static float[] CreateTap()
        {
            var tap = new float[SpatialStereo.Rate / 6];
            for (int i = 0; i < tap.Length; i++)
            {
                float time = i / (float)SpatialStereo.Rate;
                float envelope = MathF.Min(1, i / 200f) * MathF.Exp(-25 * time) * MathF.Min(1, (tap.Length - 1 - i) / 400f);
                float phase = 2 * MathF.PI * 660 * time;
                tap[i] = .08f * envelope * (MathF.Sin(phase) + .45f * MathF.Sin(phase * 2.5f) + .22f * MathF.Sin(phase * 5));
            }
            return tap;
        }

        internal void Mix(Span<float> samples)
        {
            int frame = Volatile.Read(ref _frame);
            if (frame < 0) return;
            float left = 0, right = 0;
            for (int i = 0; i + 1 < samples.Length; i += 2, frame++)
            {
                if (frame >= 2 * Interval + _tap.Length) { frame = -1; break; }
                int offset = frame % Interval;
                if (offset >= _tap.Length) continue;
                int position = frame / Interval;
                float value = position == 1 ? .7071f * _tap[offset] : _tap[offset];
                if (position != 2)
                {
                    samples[i] += value;
                    left = Math.Max(left, Math.Abs(value));
                }
                if (position != 0)
                {
                    samples[i + 1] += value;
                    right = Math.Max(right, Math.Abs(value));
                }
            }
            static int Level(float peak) => peak <= .001f ? 0 : (int)Math.Clamp((20 * Math.Log10(peak) + 60) * 100 / 60, 0, 100);
            Volatile.Write(ref _levels, frame < 0 ? 0 : Level(left) | (Level(right) << 8));
            Volatile.Write(ref _frame, frame);
        }
    }

    internal SpatialApp App { get; }

    // A per-app Windows output can differ from the default multimedia endpoint.
    // Report only an active player render session with one unambiguous destination.
    internal static AudioEndpoints.PlaybackOutput? ReadStereoOutput(SpatialApp app, ulong address)
    {
        var ids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(app.ProcessName))
            using (process)
            {
                if (app.Identity == null || SpatialApp.SameIdentity(app.Identity, SpatialApp.IdentityOf(process.Id)))
                    ids.Add((uint)process.Id);
            }
        if (ids.Count == 0) return null;
        using var devices = new MMDeviceEnumerator();
        AudioEndpoints.PlaybackOutput? output = null;
        int matches = 0;
        foreach (var endpoint in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                var sessions = endpoint.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive || !ids.Contains(session.GetProcessID)) continue;
                    matches++;
                    string id = endpoint.ID;
                    output = new(id, endpoint.FriendlyName, AudioEndpoints.PlaybackBelongsToPair(id, address));
                    break;
                }
            }
        }
        return matches == 1 ? output : null;
    }

    internal SpotifyBridge(SpatialApp app, string airpodsId, ulong address, Func<bool>? stillAvailable = null,
        RouteBeforeSpatial? expected = null)
    {
        App = app;
        try
        {
            _airpods = _devices.GetDevice(airpodsId);
            PairId = _airpods.ID;
            if (!App.IsWindowsMix)
            {
                InitialPid = App.FindPid();
                if (InitialPid == 0) throw new InvalidOperationException("noPlayer");
                SourceIdentity = SpatialApp.IdentityOf(InitialPid) ?? throw new InvalidOperationException("noPlayer");
            }
            _sink = FindVirtualOutput();
            SinkId = _sink.ID;
            if (PairId == SinkId || !SinkAvailable(out _)) throw new InvalidOperationException("virtualBusy");
            _capture = new Loopback(_sink) { WaveFormat = Format };
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, _) => _failed = true;
            _output = new WasapiOut(_airpods, AudioClientShareMode.Shared, true, 40);
            _output.PlaybackStopped += (_, _) => _failed = true;
            _output.Init(_feed);
            if (stillAvailable?.Invoke() == false) throw new InvalidOperationException("unavailable");
            _capture.StartRecording();
            _output.Play();
            if (stillAvailable?.Invoke() == false) throw new InvalidOperationException("unavailable");
            if (App.IsWindowsMix)
            {
                OriginalRoute = new(PairId, WindowsSpatialMix.Start(SinkId, PairId, expected?.Mix), null);
                _mixOwnsDefault = true;
            }
            else
            {
                string? defaultMedia = AudioEndpoints.DefaultPlaybackRole(1);
                if (expected?.App is { Multimedia: null } && !expected.DefaultStillMatches())
                    throw new InvalidOperationException("outputChanged");
                OriginalRoute = new(PairId, null, SpotifyRouting.Apply(App, InitialPid, SinkId, expected?.App), defaultMedia);
            }
            AudioEndpoints.SpotifyRoute = new(address, SinkId, App.ProcessName, App.IsWindowsMix);
        }
        catch { Dispose(); throw; }
    }

    private MMDevice FindVirtualOutput()
        => FindVirtualOutput(_devices) ?? throw new InvalidOperationException("noVirtual");

    internal static string? VirtualOutputId()
    {
        using var devices = new MMDeviceEnumerator();
        using var endpoint = FindVirtualOutput(devices);
        return endpoint?.ID;
    }

    private static MMDevice? FindVirtualOutput(MMDeviceEnumerator devices)
    {
        foreach (var d in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (d.FriendlyName.Contains("Virtual Speakers for AudioRelay", StringComparison.OrdinalIgnoreCase)) return d;
            d.Dispose();
        }
        return null;
    }

    internal bool SinkAvailable(out bool spotifyActive)
    {
        spotifyActive = false;
        // An AudioRelay server may already be using this device to stream elsewhere.
        var relays = Process.GetProcessesByName("AudioRelay");
        foreach (var relay in relays) relay.Dispose();
        if (relays.Length != 0) return false;
        foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            if (!_devices.HasDefaultAudioEndpoint(DataFlow.Render, role)) continue;
            using var def = _devices.GetDefaultAudioEndpoint(DataFlow.Render, role);
            if (def.ID == SinkId && (!_mixOwnsDefault || role == Role.Communications)) return false;
            if (_mixOwnsDefault && role == Role.Multimedia && def.ID != SinkId) return false;
        }
        if (_mixOwnsDefault)
        {
            spotifyActive = HasSignal;
            return WindowsSpatialMix.Same(AudioEndpoints.DefaultPlaybackRole(1), SinkId);
        }
        return CheckSessions(out spotifyActive);
    }

    internal bool CheckSessions(out bool spotifyActive)
    {
        spotifyActive = false;
        using var fresh = _devices.GetDevice(SinkId);
        var sessions = fresh.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var s = sessions[i];
            if (s.State != AudioSessionState.AudioSessionStateActive) continue;
            if (s.GetProcessID == Environment.ProcessId) continue; // Our loopback capture client
            if (App.IsWindowsMix || s.IsSystemSoundsSession || s.GetProcessID == 0) return false;
            try
            {
                using var p = Process.GetProcessById((int)s.GetProcessID);
                if (!p.ProcessName.Equals(App.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                    (SourceIdentity != null && !SpatialApp.SameIdentity(SourceIdentity, SpatialApp.IdentityOf(p.Id)))) return false;
                spotifyActive = true;
            }
            catch (ArgumentException) { }
        }
        return true;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        var values = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
        for (int i = 0; i < values.Length; i++)
        {
            if (Math.Abs(values[i]) > .0001f) { Interlocked.Exchange(ref _lastSignal, Environment.TickCount64); break; }
        }
        _feed.Buffer.Push(values);
        if (!Metering) return;
        // Read the original left/right channels, before HRTF rendering. No PCM
        // buffers are copied or retained. The overview and hidden app do no metering.
        double left = 0, right = 0;
        int frames = values.Length / 2;
        if (frames == 0) return;
        for (int i = 0; i < frames * 2; i += 2)
        {
            left += values[i] * values[i];
            right += values[i + 1] * values[i + 1];
        }
        int Level(double energy) => (int)Math.Clamp((10 * Math.Log10(Math.Max(energy / frames, 1e-6)) + 60) * 100 / 60, 0, 100);
        Volatile.Write(ref _levels, Level(left) | (Level(right) << 8));
        Interlocked.Exchange(ref _levelTime, Environment.TickCount64);
    }
    public void Dispose()
    {
        AudioEndpoints.SpotifyRoute = null;
        // Restore the app path independently of capture / render teardown.
        try
        {
            if (_mixOwnsDefault) WindowsSpatialMix.Restore();
            else if (!App.IsWindowsMix && SpotifyRouting.Pending(App)) SpotifyRouting.RestoreAvailable(App);
        }
        finally
        {
            if (_capture != null) _capture.DataAvailable -= OnData;
            try { _capture?.Dispose(); }
            finally
            {
                try { _output?.Dispose(); }
                finally { _sink?.Dispose(); _airpods?.Dispose(); _devices.Dispose(); }
            }
        }
    }
}
#endif
