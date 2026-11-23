using System.Runtime.InteropServices;
using WPods.Core;
using Timer = System.Threading.Timer;

namespace WPods.Audio;

public sealed partial class EarPause
{
    private const int Unknown = -1;
#if !PAIRSIDE_LIVE
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(15);
#endif

    private readonly Lock _lock = new();
    private readonly ConnectionManager _connections;
    private readonly MicPipeline _mic;
    // Under _lock.
    private AapSession? _session;
    private int _latest = Unknown;
    private bool _earDetectionOn;
    private bool _running;
    private bool _reset;
    private string? _forget;
    private bool _sleepPending;
    private Timer? _timer;
    private int _timerGeneration;
    // Worker only (at most one worker runs, guarded by _running).
    private int _applied = Unknown;
#if PAIRSIDE_LIVE
    private readonly EarMediaTransport _earMedia = new();
    private long _resumeUntil;
#endif
    private bool _pending;
    private int _resumeSeconds;

    public EarPause(ConnectionManager connections, MicPipeline mic)
    {
        _connections = connections;
        _mic = mic;
        connections.Attached += OnAttached;
        connections.Detached += OnDetached;
        connections.Settings.HostSettingsChanged += OnHostSettingsChanged;
    }

    private void OnHostSettingsChanged(ulong address, HostSettings settings)
    {
        lock (_lock)
        {
            int seconds = EarPlaybackPolicy.Window(settings.EarResumeSeconds);
            if (_session?.Address != address || seconds == _resumeSeconds) return;
            _resumeSeconds = seconds;
            _forget = "resume preference changed";
            Kick();
        }
    }

    private void OnAttached(AapSession session)
    {
        lock (_lock)
        {
            _session = session;
            _resumeSeconds = EarPlaybackPolicy.Window(_connections.Settings.Get(session.Address).EarResumeSeconds);
            _latest = Unknown;
            _earDetectionOn = session.State.EarDetection == true;
            _reset = true;
            _sleepPending = false;
            _forget = "session changed";
            Kick();
        }
        session.Changed += OnChanged;
    }

    private void OnDetached(AapSession session)
    {
        session.Changed -= OnChanged;
        lock (_lock)
        {
            if (_session == session)
            {
                _session = null;
                _latest = Unknown;
                _reset = true;
                _sleepPending = false;
                _forget = "AirPods detached";
                Kick();
            }
        }
    }

    private void OnChanged(DeviceChange change)
    {
        if ((change & (DeviceChange.Ear | DeviceChange.Settings | DeviceChange.Sleep)) == 0)
        {
            return;
        }
        lock (_lock)
        {
            if (_session is not { } session)
            {
                return;
            }
            DeviceState state = session.State;
            if ((change & DeviceChange.Sleep) != 0)
            {
                _sleepPending = state.SleepState == 1 && state.Status == SessionStatus.Ready &&
                    _connections.Settings.Get(session.Address).SleepPause;
                if (_sleepPending) Kick();
            }
            bool on = state.EarDetection == true;
            if (_earDetectionOn && !on)
            {
                _forget = "ear detection turned off";
                Kick();
            }
            _earDetectionOn = on;
            if ((change & DeviceChange.Ear) != 0)
            {
                _latest = EarPlaybackPolicy.Count(state.PrimaryEar, state.SecondaryEar,
                    Models.FromModelNumber(state.ModelNumber)?.SingleBattery == true);
                if (_latest == Unknown) _forget = "wear state unavailable";
                Kick();
            }
        }
    }

    // Under _lock.
    private void Kick()
    {
        if (!_running)
        {
            _running = true;
            ThreadPool.UnsafeQueueUserWorkItem(static p => p.Run(), this, preferLocal: false);
        }
    }

    private async void Run()
    {
        while (true)
        {
            AapSession? session;
            int count;
            bool reset;
            bool sleep;
            string? forget;
            lock (_lock)
            {
                reset = _reset;
                _reset = false;
                sleep = _sleepPending;
                _sleepPending = false;
                forget = _forget;
                _forget = null;
                session = _session;
                count = _latest;
                if (count > 0 && count > _applied)
                {
                    // A bud returned. The worker checks the deadline before it sends Play.
                    StopTimer();
                }
                if (!reset && !sleep && forget is null && (session is null || count == _applied))
                {
                    _running = false;
                    return;
                }
            }
            if (reset)
            {
                _applied = Unknown;
            }
            if (forget is not null)
            {
                Forget(forget);
            }
            if (session is not null && sleep)
            {
                await PauseForSleepAsync(session).ConfigureAwait(false);
            }
            if (session is not null && count != _applied)
            {
                await ApplyAsync(session, _applied, count).ConfigureAwait(false);
                _applied = count;
            }
        }
    }

    private bool CanPauseForSleep(AapSession session)
    {
        if (_connections.Session != session || session.State.Status != SessionStatus.Ready || session.State.SleepState != 1 ||
            !_connections.Settings.Get(session.Address).SleepPause || session.CallAudio ||
            _mic.HandsFreeInUse || _mic.Status != MicStatus.NotActive) return false;
#if PAIRSIDE_LIVE
        return true; // The player-specific output is checked immediately before pause.
#else
        return DefaultOutputWatcher.wpn_audio_is_default_output(session.Address) == 1;
#endif
    }

    private async Task PauseForSleepAsync(AapSession session)
    {
        if (!CanPauseForSleep(session)) return;
        Forget("AirPods reported sleep");
#if PAIRSIDE_LIVE
        if (await _earMedia.PauseAsync(session.Address, () => CanPauseForSleep(session), remember: false).ConfigureAwait(false))
            Log.Info("earpause", "AirPods reported sleep: paused their media player");
#else
        await Task.CompletedTask;
        wpn_media_pause_all();
        wpn_media_forget();
#endif
    }

    private async Task ApplyAsync(AapSession session, int previous, int count)
    {
        string what = $"in ear {(previous == Unknown ? "?" : previous)} -> {count}";
        if (previous == Unknown || count == Unknown)
        {
            Log.Info("earpause", $"{what}: first report, no action");
            return;
        }
        if (count < previous)
        {
            await OnRemovedAsync(session, what, count).ConfigureAwait(false);
            return;
        }
        if (!_pending)
        {
            Log.Info("earpause", $"{what}: no resume (nothing paused by WPods)");
            return;
        }
        _pending = false;
#if PAIRSIDE_LIVE
        bool resumed = await _earMedia.ResumeAsync(session.Address, () =>
        {
            lock (_lock) return _forget is null && _resumeSeconds > 0 && Environment.TickCount64 < _resumeUntil &&
                CanControl(session, count);
        }).ConfigureAwait(false);
        Log.Info("earpause", $"{what}: {(resumed ? "resumed paused AirPods player" : "resume skipped")}");
#else
        if (!CanAct(session, what))
        {
            wpn_media_forget();
            Log.Info("earpause", $"{what}: resume skipped, paused sessions forgotten");
            return;
        }
        int resumed = wpn_media_resume_paused();
        Log.Info("earpause", $"{what}: resume -> {Describe(resumed)}");
#endif
    }

    // Never plays: pauses whatever is playing, and at 0 in ear starts the resume window.
    private async Task OnRemovedAsync(AapSession session, string what, int count)
    {
#if PAIRSIDE_LIVE
        if (CanControl(session, count))
        {
            int seconds = EarPlaybackPolicy.Window(_connections.Settings.Get(session.Address).EarResumeSeconds);
            bool paused = await _earMedia.PauseAsync(session.Address, () => CanControl(session, count), remember: seconds > 0).ConfigureAwait(false);
            if (paused && seconds > 0)
            {
                _resumeUntil = Environment.TickCount64 + seconds * 1000L;
                lock (_lock) StartTimer(TimeSpan.FromSeconds(seconds));
            }
            if (seconds == 0) return;
            _pending |= paused;
            Log.Info("earpause", $"{what}: {(paused ? "paused AirPods player" : "no unambiguous AirPods player to pause")}");
        }
#else
        await Task.CompletedTask;
        if (CanAct(session, what))
        {
            int paused = wpn_media_pause_all();
            _pending |= paused > 0;
            Log.Info("earpause", $"{what}: pause all -> {(paused >= 0 ? $"{paused} session(s) paused" : $"error 0x{paused:X8}")}");
        }
        if (count != 0)
        {
            return;
        }
        if (!_pending)
        {
            Log.Info("earpause", $"{what}: no bud in ear, nothing to resume");
            return;
        }
        lock (_lock)
        {
            StartTimer(ResumeWindow);
        }
        Log.Info("earpause", $"{what}: no bud in ear; resume if one is back within {ResumeWindow.TotalSeconds:0} s");
#endif
    }

    private void Forget(string reason)
    {
        lock (_lock)
        {
            StopTimer();
        }
        if (!_pending)
        {
            return;
        }
        _pending = false;
#if PAIRSIDE_LIVE
        _earMedia.Forget();
#else
        wpn_media_forget();
#endif
        Log.Info("earpause", $"{reason}: paused sessions forgotten (no resume)");
    }

    // Under _lock.
    private void StartTimer(TimeSpan duration)
    {
        StopTimer();
        int generation = _timerGeneration;
        _timer = new Timer(_ => OnWindowExpired(generation), null, duration, Timeout.InfiniteTimeSpan);
    }

    // Under _lock. A callback already queued sees a newer generation and does nothing.
    private void StopTimer()
    {
        if (_timer is null)
        {
            return;
        }
        _timer.Dispose();
        _timer = null;
        _timerGeneration++;
    }

    private void OnWindowExpired(int generation)
    {
        lock (_lock)
        {
            if (generation != _timerGeneration)
            {
                return;
            }
            StopTimer();
            _forget = "resume window expired";
            Kick();
        }
    }

#if PAIRSIDE_LIVE
    private bool CanControl(AapSession session, int count)
    {
        lock (_lock)
        {
            return _session == session && _latest == count && _earDetectionOn && session.State.Status == SessionStatus.Ready &&
                !session.CallAudio && !_mic.HandsFreeInUse && !_mic.Capturing;
        }
    }
#else
    private static bool CanAct(AapSession session, string what)
    {
        if (session.State.EarDetection != true)
        {
            Log.Info("earpause", $"{what}: skipped, ear detection is {(session.State.EarDetection is null ? "unknown" : "off")}");
            return false;
        }
        int output = DefaultOutputWatcher.wpn_audio_is_default_output(session.Address);
        if (output != 1)
        {
            Log.Info("earpause", $"{what}: skipped, AirPods are not the default output{(output < 0 ? $" (error 0x{output:X8})" : "")}");
            return false;
        }
        return true;
    }

    private static string Describe(int resumed) => resumed switch
    {
        > 0 => $"{resumed} session(s) resumed",
        0 => "nothing resumed (forgotten after other playback, something is playing, or no session still paused)",
        _ => $"error 0x{resumed:X8}",
    };

    [LibraryImport("wpods_native.dll")]
    private static partial int wpn_media_pause_all();

    [LibraryImport("wpods_native.dll")]
    private static partial int wpn_media_resume_paused();

    [LibraryImport("wpods_native.dll")]
    private static partial void wpn_media_forget();
#endif
}
