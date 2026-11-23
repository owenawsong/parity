#if PAIRSIDE_LIVE
using Windows.Media.Control;
using WPods.Core;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Player = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace WPods.Audio;

// The ear worker serializes requests. Broker callbacks only invalidate a pending resume.
internal sealed class EarMediaTransport
{
    private ResumeWatch? _watch;

    internal async Task<bool> PauseAsync(ulong address, Func<bool> stillWanted, bool remember = true)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var manager = await Manager.RequestAsync().AsTask(timeout.Token).ConfigureAwait(false);
            var streams = AudioEndpoints.PlayerStreams(address);
            var sessions = manager.GetSessions();
            var candidates = sessions.Where(s => s.GetPlaybackInfo()?.PlaybackStatus == Status.Playing &&
                AudioEndpoints.PlaysOnPair(streams, s.SourceAppUserModelId, paused: false)).ToArray();
            // Multiple SMTC sessions can identify themselves as the same browser. WASAPI cannot
            // say which tab owns a stream. Likewise two different players can both be audible.
            if (candidates.Length != 1) return false;
            var target = candidates[0];
            if (sessions.Count(s => s.SourceAppUserModelId.Equals(target.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)) != 1)
                return false;
            if (!stillWanted() || target.GetPlaybackInfo()?.PlaybackStatus != Status.Playing) return false;
            ResumeWatch? watch = remember ? new(manager, target, sessions) : null;
            try
            {
                if (!stillWanted() || !await target.TryPauseAsync().AsTask(timeout.Token).ConfigureAwait(false)) return false;
                Forget();
                _watch = watch;
                watch = null;
                return true;
            }
            finally { watch?.Dispose(); }
        }
        catch (Exception e)
        {
            Log.Warn("earpause", $"media pause unavailable ({e.GetType().Name})");
            return false;
        }
    }

    internal async Task<bool> ResumeAsync(ulong address, Func<bool> stillWanted)
    {
        using var watch = _watch;
        _watch = null;
        if (watch is null || watch.Invalid) return false;
        try
        {
            var target = watch.Target;
            var sessions = watch.Manager.GetSessions();
            if (!sessions.Any(s => Same(s, target)) || target.GetPlaybackInfo()?.PlaybackStatus != Status.Paused) return false;
            var streams = AudioEndpoints.PlayerStreams(address);
            if (!AudioEndpoints.PlaysOnPair(streams, target.SourceAppUserModelId, paused: true) ||
                sessions.Any(s => s.GetPlaybackInfo()?.PlaybackStatus == Status.Playing &&
                    AudioEndpoints.PlaysOnPair(streams, s.SourceAppUserModelId, paused: false))) return false;
            if (!stillWanted() || watch.Invalid) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            return await target.TryPlayAsync().AsTask(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn("earpause", $"media resume unavailable ({e.GetType().Name})");
            return false;
        }
    }

    internal void Forget()
    {
        _watch?.Dispose();
        _watch = null;
    }

    private static bool Same(Player a, Player b) => ReferenceEquals(a, b) ||
        (a is WinRT.IWinRTObject left && b is WinRT.IWinRTObject right && left.NativeObject.ThisPtr == right.NativeObject.ThisPtr);

    private sealed class ResumeWatch : IDisposable
    {
        internal readonly Manager Manager;
        internal readonly Player Target;
        internal volatile bool Invalid;
        private readonly List<Action> _detach = [];

        internal ResumeWatch(Manager manager, Player target, IReadOnlyList<Player> sessions)
        {
            Manager = manager;
            Target = target;
            try
            {
                manager.SessionsChanged += SessionsChanged;
                _detach.Add(() => manager.SessionsChanged -= SessionsChanged);
                target.MediaPropertiesChanged += TrackChanged;
                _detach.Add(() => target.MediaPropertiesChanged -= TrackChanged);
                foreach (var session in sessions)
                {
                    int playing = session.GetPlaybackInfo()?.PlaybackStatus == Status.Playing ? 1 : 0;
                    void PlaybackChanged(Player sender, PlaybackInfoChangedEventArgs args)
                    {
                        try
                        {
                            int now = sender.GetPlaybackInfo()?.PlaybackStatus == Status.Playing ? 1 : 0;
                            if (Interlocked.Exchange(ref playing, now) == 0 && now == 1) Invalid = true;
                        }
                        catch (Exception) { Invalid = true; }
                    }
                    session.PlaybackInfoChanged += PlaybackChanged;
                    _detach.Add(() => session.PlaybackInfoChanged -= PlaybackChanged);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }
        private void SessionsChanged(Manager sender, SessionsChangedEventArgs args) => Invalid = true;
        private void TrackChanged(Player sender, MediaPropertiesChangedEventArgs args) => Invalid = true;
        public void Dispose()
        {
            Invalid = true;
            foreach (var detach in _detach)
            {
                try { detach(); }
                catch (Exception) { /* The player may already be closed. */ }
            }
            _detach.Clear();
        }
    }
}
#endif
