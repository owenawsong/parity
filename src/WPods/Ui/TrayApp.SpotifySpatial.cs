#if PAIRSIDE_LIVE
using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private readonly SemaphoreSlim _spotifyGate = new(1);
    private CancellationTokenSource? _spatialStartup;
    private string? _spatialStartingMode;
    private readonly System.Windows.Forms.Timer _spotifyTimer = new() { Interval = 1000 };
    private SpotifyBridge? _spotify;
    private AapSession? _spotifySession;
    private SpatialApp _spatialPlayer = SpatialApp.WindowsMix;
    private string _spotifyMode = "stereo", _spotifyStatus = "idle";
    private ulong _spotifyNoticeAddress;
    private bool _spotifyHasPose;
    private ulong _spotifyRoomViewer;
    private readonly System.Windows.Forms.Timer _roomMeter = new() { Interval = 80 };
    private readonly System.Windows.Forms.Timer _stereoOutputTimer = new() { Interval = 2200 };
    private int _outputRevision;
    private bool _readingStereoOutput;
    private SpatialApp[] _spatialApps = SpatialApp.Supported;
    private long _nextSourceScan;
    private SpatialApp.PlaybackPath[] _playbackPaths = [];
    private PendingSpatialResume? _spatialResume;
    private sealed record PendingSpatialResume(AapSession Session, SpatialApp Source, string Mode,
        SpotifyBridge.RouteBeforeSpatial Original, long? QuietSince = null, string Reason = "callPaused", long RejoinUntil = 0);
    private int _spatialChoiceRevision;
    private ulong _spatialDisconnectAddress;
    private long _bridgeStartedAt;

    private async void ReadStereoOutput()
    {
        if (_readingStereoOutput || !_stereoOutputTimer.Enabled || _exiting) return;
        _readingStereoOutput = true;
        var revision = _outputRevision;
        AudioEndpoints.PlaybackOutput? output = null;
        ulong address = _spotifyRoomViewer;
        var player = _spatialPlayer;
        var bridge = _spotify;
        try
        {
            bool refreshSourceList = Environment.TickCount64 >= _nextSourceScan;
            if (refreshSourceList || bridge != null)
            {
                // Read the cached endpoint IDs before leaving the UI thread. The bridge can
                // be disposed while Windows enumerates the active render sessions.
                string? sink = bridge?.SinkId, pair = bridge?.PairId;
                var sessions = await Task.Run(AudioEndpoints.ReadSpatialSessions);
                if (_exiting || revision != _outputRevision || !_stereoOutputTimer.Enabled || bridge != _spotify) return;
                bool changed = false;
                if (refreshSourceList)
                {
                    _nextSourceScan = Environment.TickCount64 + 6500;
                    var apps = SpatialApp.AppsFromSessions(sessions);
                    changed = !_spatialApps.SequenceEqual(apps);
                    _spatialApps = apps;
                }
                if (sink != null && pair != null)
                {
                    var paths = SpatialApp.PlaybackPaths(sessions, sink, pair);
                    changed |= !_playbackPaths.SequenceEqual(paths);
                    _playbackPaths = paths;
                }
                if (changed) SendSpotify();
            }
            if (_spotify != null) return;
            output = await Task.Run(() => player.IsWindowsMix
                ? AudioEndpoints.ReadPlaybackOutput(address) : SpotifyBridge.ReadStereoOutput(player, address));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // An endpoint can disappear during enumeration. The next poll will try again.
        }
        finally { _readingStereoOutput = false; }
        if (_exiting || revision != _outputRevision || !_stereoOutputTimer.Enabled || !MainLive) return;
        if (_lastOutputRevision == revision && _lastStereoOutput == output) return;
        _lastOutputRevision = revision;
        _lastStereoOutput = output;
        var w = _writer.Begin("spotifyOutput");
        w.WriteString("id", Mac.Format(_spotifyRoomViewer));
        w.WriteString("source", player.Key);
        w.WriteString("name", output?.Name);
        if (output?.OnPair is bool onPair) w.WriteBoolean("onPair", onPair);
        PostMain(_writer.End());
    }

    private int _lastOutputRevision = -1;
    private AudioEndpoints.PlaybackOutput? _lastStereoOutput;
    private int _lastLevels = -1;
    private (double Yaw, double Pitch)? _lastShownPose;

    private void WatchSpotifyRoom(ulong address)
    {
        _outputRevision++;
        _spotifyRoomViewer = address;
        _nextSourceScan = 0;
        _lastShownPose = null;
        _lastLevels = -1;
        _playbackPaths = [];
        UpdateRoomMeter();
        if (_stereoOutputTimer.Enabled) ReadStereoOutput();
    }

    private void UpdateRoomMeter()
    {
        if (_exiting) return;
        bool enabled = _spotifyRoomViewer != 0 && _spotifySession?.Address == _spotifyRoomViewer && _spotify != null &&
            MainLive && !_mainCompact && _main?.WindowState != FormWindowState.Minimized;
        if (_spotify is { } bridge) bridge.Metering = enabled;
        _roomMeter.Enabled = enabled;
        bool readStereo = _spotifyRoomViewer != 0 && _connections.Session?.Address == _spotifyRoomViewer &&
            MainLive && !_mainCompact && _main?.WindowState != FormWindowState.Minimized;
        if (_stereoOutputTimer.Enabled != readStereo)
        {
            _outputRevision++;
            _stereoOutputTimer.Enabled = readStereo;
            if (readStereo) ReadStereoOutput();
        }
    }

    private void RoomMeterTick(object? sender, EventArgs args)
    {
        if (_spotify is not { } bridge || _spotifySession?.Address != _spotifyRoomViewer ||
            !MainLive || _mainCompact || _main?.WindowState == FormWindowState.Minimized)
        {
            UpdateRoomMeter();
            return;
        }
        int levels = bridge.SourceLevels;
        if (levels == _lastLevels) return;
        _lastLevels = levels;
        var w = _writer.Begin("spotifyLevels");
        w.WriteString("id", Mac.Format(_spotifyRoomViewer));
        w.WriteNumber("left", levels & 255);
        w.WriteNumber("right", levels >> 8);
        PostMain(_writer.End());
    }

    // The UI only observes the existing renderer. Audio consumes each hardware sample
    // directly; opening or closing the room never starts a second motion session.
    private void SendSpotifyPose(ulong address, double yaw, double pitch)
    {
        if (_spotifyRoomViewer != address || _spotifySession?.Address != address || _spotifyMode != "tracked" ||
            !MainLive || _mainCompact || _main?.WindowState == FormWindowState.Minimized) return;
        var pose = (Yaw: Math.Round(yaw * 2) / 2, Pitch: Math.Round(pitch * 2) / 2);
        if (_lastShownPose == pose) return;
        _lastShownPose = pose;
        var w = _writer.Begin("spotifyPose");
        w.WriteString("id", Mac.Format(address));
        w.WriteNumber("yaw", pose.Yaw);
        w.WriteNumber("pitch", pose.Pitch);
        PostMain(_writer.End());
    }

    private void InitSpotify()
    {
        _spatialPlayer = SpatialApp.FromKey(_settings.Ui.SpatialPlayer) ?? SpatialApp.WindowsMix;
        _stereoOutputTimer.Tick += (_, _) => ReadStereoOutput();
        _roomMeter.Tick += RoomMeterTick;
        _spotifyTimer.Tick += SpotifyTick;
        _connections.DisconnectRequested += SpatialDisconnectRequested;
        if (SpotifyRouting.AnyPending || WindowsSpatialMix.Pending) _spotifyTimer.Start();
    }

    // ConnectionManager raises this before it starts detaching the user-selected
    // pair. Record it synchronously so an auto-resume cannot race a Disconnect.
    private void SpatialDisconnectRequested(ulong address)
    {
        Volatile.Write(ref _spatialDisconnectAddress, address);
        _ui.Post(_ => SpotifyTick(null, EventArgs.Empty), null);
    }

    private void OnSpatialCallAudioChanged(bool active)
    {
        if (!active) return;
        _ui.Post(_ =>
        {
            if (!_exiting && _spotifySession?.CallAudio == true) SpotifyTick(null, EventArgs.Empty);
        }, null);
    }

    private async void SpotifyTick(object? sender, EventArgs args)
    {
        if (_exiting || !_spotifyGate.Wait(0)) return;
        try
        {
            if (_spotify?.App.IsWindowsMix != true && WindowsSpatialMix.Pending) await Task.Run(WindowsSpatialMix.Restore);
            foreach (var player in SpotifyRouting.PendingApps())
            {
                if (player != _spotify?.App && SpotifyRouting.Pending(player))
                {
                    try { await Task.Run(() => SpotifyRouting.RestoreAvailable(player)); }
                    catch (Exception e) { Log.Warn("spatial", $"Output recovery for {player.Key}: {e.Message}"); }
                }
            }
            if (_spotify is not { } bridge)
            {
                if (_spatialResume is { } resume)
                {
                    await TryResumeSpatial(resume);
                    return;
                }
                if (!SpotifyRouting.AnyPending && !WindowsSpatialMix.Pending) _spotifyTimer.Stop();
                return;
            }
            if (_connections.Session != _spotifySession || _spotifySession?.State.Status != SessionStatus.Ready)
            {
                bool wasRequested = _spotifySession != null && Volatile.Read(ref _spatialDisconnectAddress) == _spotifySession.Address;
                var active = _connections.Session;
                if (!wasRequested && _spotifySession is { } previous && (active == null || active.Address == previous.Address))
                {
                    _spatialResume = new(previous, _spatialPlayer, _spotifyMode, bridge.OriginalRoute,
                        Reason: "reconnectingAudio", RejoinUntil: Environment.TickCount64 + 25_000);
                    await CloseSpotify("reconnectingAudio");
                }
                else await CloseSpotify(wasRequested ? "idle" : "ended");
                return;
            }
            if (_spotifySession.CallAudio || _mic.HandsFreeInUse)
            {
                _spatialResume = new(_spotifySession, _spatialPlayer, _spotifyMode, bridge.OriginalRoute);
                await CloseSpotify("callPaused");
                return;
            }
            if (bridge.Failed)
            {
                if (Environment.TickCount64 - _bridgeStartedAt < 6000)
                {
                    await CloseSpotify("ended");
                    return;
                }
                // A WASAPI stream can be invalidated while the AirPods control
                // channel stays attached. Wait for the original route to return.
                _spatialResume = new(_spotifySession, _spatialPlayer, _spotifyMode, bridge.OriginalRoute,
                    Reason: "reconnectingAudio", RejoinUntil: Environment.TickCount64 + 25_000);
                await CloseSpotify("reconnectingAudio");
                return;
            }
            string status = await Task.Run(() =>
            {
                if (bridge.App.IsWindowsMix)
                {
                    if (!WindowsSpatialMix.Same(AudioEndpoints.DefaultPlaybackRole(1), bridge.SinkId)) return "ended";
                }
                else
                {
                    int pid = bridge.CurrentPid;
                    if (pid == 0 || !SpotifyRouting.Owns(pid, bridge.SinkId)) return "ended";
                }
                if (!bridge.SinkAvailable(out bool active)) return "virtualBusy";
                return active && bridge.HasSignal ? "playing" : "waiting";
            });
            if (status is "ended" or "virtualBusy") { await CloseSpotify(status); return; }
            if (_spotifyMode == "tracked" && !_spotifyHasPose)
            {
                if (_spotifyStatus == "trackingMic")
                {
                    if (_mic.Capturing || _mic.Status != MicStatus.NotActive) return;
                    _spotifyStatus = "calibrating";
                    StartHeadMotion(_spotifySession.Address, false, SpotifyMotionRequest);
                    SendSpotify();
                    return;
                }
                status = "calibrating";
            }
            if (_spotifyStatus == "noMotion" && _spotifyMode == "fixed") return;
            if (_spotifyStatus != status) { _spotifyStatus = status; SendSpotify(); }
        }
        catch (Exception e)
        {
            Log.Warn("spotify", e.Message);
            if (_spotify != null || _spatialResume != null)
            {
                _spatialResume = null;
                await CloseSpotify("failed");
            }
        }
        finally { _spotifyGate.Release(); }
    }

    private async Task TryResumeSpatial(PendingSpatialResume resume)
    {
        long now = Environment.TickCount64;
        var session = _connections.Session;
        bool requested = Volatile.Read(ref _spatialDisconnectAddress) == resume.Session.Address;
        bool rejoining = resume.RejoinUntil > now && (session == null || session.Address == resume.Session.Address);
        if (_exiting || requested || (resume.RejoinUntil != 0 && !rejoining) || (!rejoining && session != resume.Session))
        {
            _spatialResume = null;
            await CloseSpotify(requested ? "idle" : "ended");
            return;
        }
        if (session?.State.Status != SessionStatus.Ready)
        {
            if (rejoining) return;
            _spatialResume = null;
            await CloseSpotify("ended");
            return;
        }
        if (session != resume.Session)
        {
            resume = resume with { Session = session, QuietSince = null };
            _spatialResume = resume;
        }
        long? quietSince = SpotifyBridge.ResumeWindow(now, resume.QuietSince,
            resume.Session.CallAudio || _mic.HandsFreeInUse, _mic.Capturing);
        if (quietSince != resume.QuietSince)
        {
            _spatialResume = resume with { QuietSince = quietSince };
            return;
        }
        // Windows can report SCO closed before the A2DP endpoint and defaults settle.
        if (!quietSince.HasValue || now - quietSince.Value < 850) return;
        int choiceRevision = _spatialChoiceRevision;
        if (await Task.Run(() => resume.Original.CanResume(resume.Source)))
        {
            if (choiceRevision != _spatialChoiceRevision || Volatile.Read(ref _spatialDisconnectAddress) == resume.Session.Address ||
                _connections.Session != resume.Session || resume.Session.State.Status != SessionStatus.Ready ||
                resume.Session.CallAudio || _mic.HandsFreeInUse || _mic.Capturing) return;
            _spatialResume = null;
            await ChangeSpatialMode(resume.Session.Address, resume.Mode, resume);
        }
        else if (now - quietSince.Value >= 10_000)
        {
            _spatialResume = null;
            await CloseSpotify("outputChanged");
        }
    }

    private async void SetSpatialPlayer(SpatialApp player)
    {
        Interlocked.Increment(ref _spatialChoiceRevision);
        await _spotifyGate.WaitAsync();
        try
        {
            if (_exiting || player == _spatialPlayer) return;
            var previousMode = _spotifyMode;
            var address = _spotifySession?.Address;
            bool wasSuspended = _spatialResume != null;
            _spatialResume = null;
            if (_spotify != null || wasSuspended) await CloseSpotify("idle");
            try { SpatialApp.RememberSelection(player); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { Log.Warn("spatial", $"Could not save app selection: {e.Message}"); }
            _spatialPlayer = player;
            SavePrefs(_prefs with { SpatialPlayer = player.Key });
            _spotifyStatus = "idle";
            _outputRevision++;
            if (address.HasValue && previousMode != "stereo") await ChangeSpatialMode(address.Value, previousMode);
            else { ReadStereoOutput(); SendSpotify(); }
        }
        finally { _spotifyGate.Release(); }
    }

    private async void SetSpotifyMode(ulong address, string mode)
    {
        int revision = Interlocked.Increment(ref _spatialChoiceRevision);
        if (address == _spotifyNoticeAddress && mode != _spatialStartingMode) _spatialStartup?.Cancel();
        await _spotifyGate.WaitAsync();
        try
        {
            if (revision != _spatialChoiceRevision) return;
            if (_spatialResume is { } resume && resume.Session.Address == address && mode != "stereo")
            {
                _spatialResume = resume with { Mode = mode };
                SendSpotify();
                return;
            }
            _spatialResume = null;
            await ChangeSpatialMode(address, mode);
        }
        finally { _spotifyGate.Release(); }
    }

    private async Task ChangeSpatialMode(ulong address, string mode, PendingSpatialResume? resume = null)
    {
        var expected = resume?.Original;
        int choiceRevision = _spatialChoiceRevision;
        async Task AbortStart(string reason)
        {
            // A second call can start while WASAPI is reopening the route. Keep
            // the original selection and wait through the new interruption.
            if (resume != null && !_exiting && choiceRevision == _spatialChoiceRevision &&
                Volatile.Read(ref _spatialDisconnectAddress) != address)
            {
                var current = _connections.Session;
                long now = Environment.TickCount64;
                bool call = reason == "unavailable" && current == resume.Session &&
                    (resume.Session.CallAudio || _mic.HandsFreeInUse || _mic.Capturing);
                bool lostSession = current == null || current != resume.Session || current.State.Status != SessionStatus.Ready;
                bool endpointPending = (reason == "ended" || reason == "noOutput" || (reason == "unavailable" && lostSession)) &&
                    (current == null || current.Address == address) &&
                    SpotifyBridge.CanRetryResume(now, resume.RejoinUntil, resume.QuietSince);
                if (call || endpointPending)
                {
                    _spatialResume = call ? resume with { QuietSince = null } : resume;
                    reason = resume.Reason;
                }
            }
            await CloseSpotify(reason);
        }
        try
        {
            if (_exiting) return;
            _spotifyNoticeAddress = address;
            if (mode == "stereo") { await CloseSpotify("idle"); return; }
            if (expected == null && Volatile.Read(ref _spatialDisconnectAddress) == address)
                Volatile.Write(ref _spatialDisconnectAddress, 0);
            if (_connections.Session is not { State.Status: SessionStatus.Ready } session || session.Address != address ||
                session.CallAudio || _mic.HandsFreeInUse) { await AbortStart("unavailable"); return; }
            if (_spotifySession != null && _spotifySession != session) await CloseSpotify("idle");
            string? Interruption() => _exiting || _connections.Session != session || session.State.Status != SessionStatus.Ready ? "ended" :
                choiceRevision != Volatile.Read(ref _spatialChoiceRevision) ? "idle" :
                Volatile.Read(ref _spatialDisconnectAddress) == address ? "idle" :
                session.CallAudio || _mic.HandsFreeInUse || (expected != null && _mic.Capturing) ? "unavailable" : null;
            if (_spotify == null)
            {
                if (WindowsSpatialMix.Pending) await Task.Run(WindowsSpatialMix.Restore);
                foreach (var app in SpotifyRouting.PendingApps())
                {
                    if (app == _spatialPlayer || !SpotifyRouting.Pending(app)) continue;
                    try { await Task.Run(() => SpotifyRouting.RestoreAvailable(app)); }
                    catch (Exception e)
                    {
                        Log.Warn("spatial", $"Prior route still in use: {e.Message}");
                        await CloseSpotify("restore");
                        return;
                    }
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                _spatialStartup = timeout;
                _spatialStartingMode = mode;
                if (resume == null)
                {
                    _spotifyStatus = "starting";
                    SendSpotify();
                }
                else
                {
                    _spatialResume = resume;
                }
                try
                {
                    var output = await Task.Run(() => AudioEndpoints.SpatialPlaybackIdAsync(address, timeout.Token, expected?.Pair));
                    if (Interruption() is { } interrupted) { await AbortStart(interrupted); return; }
                    if (output == null) { await AbortStart("noOutput"); return; }
                    _spotify = await Task.Run(() => new SpotifyBridge(_spatialPlayer, output, address, () => Interruption() is null, expected));
                    _bridgeStartedAt = Environment.TickCount64;
                    _spotify.SetSpeakerAngle(_prefs.SpatialSpeakerAngle);
                    _spotify.SetLevel(_prefs.SpatialLevel);
                    _spotifySession = session;
                    session.CallAudioChanged += OnSpatialCallAudioChanged;
                }
                finally
                {
                    _spatialStartup = null;
                    _spatialStartingMode = null;
                }
            }
            if (Interruption() is { } reason) { await AbortStart(reason); return; }
            _spatialResume = null;
            bool alreadyTracking = _spotifyMode == "tracked" && _headMotionTest?.SpatialRequest == SpotifyMotionRequest;
            if (mode == "tracked" && alreadyTracking) { SendSpotify(); return; }
            _spotifyMode = mode;
            _spotifyStatus = "waiting";
            _spotifyTimer.Start();
            if (mode == "tracked")
            {
                _spotifyHasPose = false;
                _lastShownPose = null;
                _spotifyStatus = "calibrating";
                StartHeadMotion(address, false, SpotifyMotionRequest);
            }
            else if (mode == "fixed")
            {
                StopSpatialMotion(address, SpotifyMotionRequest);
                _spotify.Pose(0);
            }
            SendSpotify();
        }
        catch (Exception e)
        {
            if (choiceRevision != _spatialChoiceRevision)
            {
                await AbortStart("idle");
                return;
            }
            Log.Warn("spotify", $"start failed: {e.Message}");
            string reason = InterruptionReason(e);
            await AbortStart(reason);
        }

        static string InterruptionReason(Exception error) => error switch
        {
            OperationCanceledException or { HResult: unchecked((int)0x88890004) or unchecked((int)0x80070490) } => "noOutput",
            { Message: "usePairDefault" or "noPlayer" or "virtualBusy" or "noVirtual" or "unavailable" or "restore" or "outputChanged" } => error.Message,
            _ => "failed",
        };
    }

    private async Task CloseSpotify(string reason)
    {
        _lastLevels = -1;
        _playbackPaths = [];
        var bridge = _spotify;
        var session = _spotifySession;
        if (session != null) session.CallAudioChanged -= OnSpatialCallAudioChanged;
        _spotify = null;
        _spotifySession = null;
        _spotifyHasPose = false;
        _spotifyMode = "stereo";
        _lastShownPose = null;
        if (session != null) StopSpatialMotion(session.Address, SpotifyMotionRequest);
        if (_spatialResume is { } resume && reason != resume.Reason) _spatialResume = null;
        try { if (bridge != null) await Task.Run(bridge.Dispose); }
        catch (Exception e) { Log.Warn("spotify", $"route recovery pending: {e.Message}"); reason = "restore"; }
        if ((_spatialResume != null || SpotifyRouting.AnyPending || WindowsSpatialMix.Pending) && !_exiting) _spotifyTimer.Start();
        else _spotifyTimer.Stop();
        _spotifyStatus = reason;
        SendSpotify();
    }

    private void SpotifyMotionStatus(ulong id, string status)
    {
        if (_spotify == null || _spotifyMode != "tracked" || _spotifySession?.Address != id) return;
        if (status == "ready")
        {
            if (!_spotifyHasPose)
            {
                _spotifyHasPose = true;
                _spotifyStatus = _spotify.HasSignal ? "playing" : "waiting";
                SendSpotify();
            }
        }
        else if (status is not ("waiting" or "calibrating"))
        {
            if (_spotifySession.CallAudio || _mic.HandsFreeInUse)
            {
                SpotifyTick(null, EventArgs.Empty);
                return;
            }
            if (status == "micInUse" || _mic.Capturing || _mic.Status != MicStatus.NotActive)
            {
                _spotifyHasPose = false;
                _lastShownPose = null;
                _spotifyStatus = "trackingMic";
                _spotify.Pose(0);
                SendSpotify();
                return;
            }
            _spotifyMode = "fixed";
            _spotifyStatus = "noMotion";
            _spotify.Pose(0);
            SendSpotify();
        }
    }

    private void SendSpotify()
    {
        UpdateRoomMeter();
        if (!MainLive || !_mainInitSent) return;
        var w = _writer.Begin("spotifySpatial");
        w.WriteString("source", _spatialPlayer.Key);
        w.WriteNumber("speakerAngle", SpatialStereo.SpeakerAngle(_prefs.SpatialSpeakerAngle));
        w.WriteString("level", SpatialStereo.LevelChoice(_prefs.SpatialLevel));
        w.WriteStartArray("apps");
        foreach (var app in _spatialApps.Append(_spatialPlayer).DistinctBy(app => app.Key))
        {
            if (app.IsWindowsMix) continue;
            w.WriteStartObject();
            w.WriteString("key", app.Key);
            w.WriteString("name", app.DisplayName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("mode", _spotifyMode);
        w.WriteString("status", _spotifyStatus);
        if (_spotifyStatus == "starting" && _spatialStartingMode != null)
            w.WriteString("startingMode", _spatialStartingMode);
        if (_spatialResume is { } resume)
        {
            w.WriteString("resumeMode", resume.Mode);
            w.WriteString("resumeReason", resume.Reason);
        }
        w.WriteStartArray("paths");
        foreach (var path in _playbackPaths)
        {
            w.WriteStartObject();
            w.WriteString("key", path.Key);
            w.WriteString("app", path.App);
            w.WriteString("output", path.Output);
            w.WriteString("path", path.Path);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        ulong address = _spotifySession?.Address ?? _spotifyNoticeAddress;
        w.WriteString("id", address == 0 ? null : Mac.Format(address));
        PostMain(_writer.End());
    }

    private async Task ShutdownSpotify()
    {
        _connections.DisconnectRequested -= SpatialDisconnectRequested;
        _spotifyTimer.Stop();
        await _spotifyGate.WaitAsync();
        try { _spatialResume = null; await CloseSpotify("idle"); }
        finally { _spotifyGate.Release(); _spotifyTimer.Dispose(); _roomMeter.Dispose(); _stereoOutputTimer.Dispose(); }
    }
}
#endif
