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
    private readonly HeadTrackingRetry _trackingRetry = new();
    private (EarStatus? Primary, EarStatus? Secondary) _trackingWear;
    private ulong _spotifyRoomViewer;
    private bool _spotifyRoutingViewer;
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
            bool refreshSourceList = _spotifyRoutingViewer && Environment.TickCount64 >= _nextSourceScan;
            bool refreshPaths = bridge != null && (_spotifyRoutingViewer || player.IsWindowsMix);
            if (refreshSourceList || refreshPaths)
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
                if (refreshPaths && sink != null && pair != null)
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

    private void WatchSpotifyRoom(ulong address, bool showRoutes)
    {
        if (_spotifyRoomViewer == address && _spotifyRoutingViewer == showRoutes) return;
        _outputRevision++;
        if (_spotifyRoomViewer != address)
        {
            _lastShownPose = null;
            _lastLevels = -1;
        }
        _spotifyRoomViewer = address;
        _spotifyRoutingViewer = showRoutes;
        _nextSourceScan = 0;
        if (address == 0 || (!showRoutes && !_spatialPlayer.IsWindowsMix)) _playbackPaths = [];
        SendSpotify();
        if (_stereoOutputTimer.Enabled) ReadStereoOutput();
    }

    private void UpdateRoomMeter()
    {
        if (_exiting) return;
        UpdateSpatialMotionPollRate();
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

    private void UpdateSpatialMotionPollRate()
    {
        if (_headMotionTest is not { SpatialRequest: SpotifyMotionRequest, Status: "ready" } motion) return;
        bool drawing = _spotifyRoomViewer == motion.Session.Address && MainLive &&
            !_mainCompact && _main?.WindowState != FormWindowState.Minimized;
        // Audio still receives each sensor packet directly. The slower poll
        // just checks for signal loss when the diagram is out of view.
        int interval = drawing ? 32 : 125;
        if (_headMotionTimer.Interval != interval) _headMotionTimer.Interval = interval;
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
                if (!SpotifyRouting.AnyPending && !WindowsSpatialMix.Pending)
                {
                    _spotifyTimer.Stop();
                    if (_spotifyStatus == "restore")
                    {
                        _spotifyStatus = "idle";
                        SendSpotify();
                    }
                }
                return;
            }
            bool resumeProcessing = _spotifyMode != "stereo" || _pcEqAddress == _spotifySession?.Address;
            if (_connections.Session != _spotifySession || _spotifySession?.State.Status != SessionStatus.Ready)
            {
                bool wasRequested = _spotifySession != null && Volatile.Read(ref _spatialDisconnectAddress) == _spotifySession.Address;
                var active = _connections.Session;
                if (resumeProcessing && !wasRequested && _spotifySession is { } previous &&
                    (active == null || active.Address == previous.Address))
                {
                    _spatialResume = new(previous, _spatialPlayer, _spotifyMode, bridge.OriginalRoute,
                        Reason: "reconnectingAudio", RejoinUntil: Environment.TickCount64 + 25_000);
                    await CloseSpotify("reconnectingAudio");
                }
                else
                {
                    if (_pcEqAddress == _spotifySession?.Address) ClearPcEqMode();
                    await CloseSpotify(wasRequested ? "idle" : "ended");
                }
                return;
            }
            if (_spotifySession.CallAudio || _mic.HandsFreeInUse)
            {
                if (resumeProcessing)
                    _spatialResume = new(_spotifySession, _spatialPlayer, _spotifyMode, bridge.OriginalRoute);
                await CloseSpotify(resumeProcessing ? "callPaused" : "idle");
                return;
            }
            if (bridge.Failed)
            {
                if (!resumeProcessing || Environment.TickCount64 - _bridgeStartedAt < 6000)
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
            bool eqSignal = _pcEqAddress == _spotifySession.Address && status == "playing";
            bool eqChanged = _lastPcEqSignal != eqSignal;
            _lastPcEqSignal = eqSignal;
            if (_spotifyMode == "tracked" && !UpdateHeadTracking(_spotifySession))
            {
                if (eqChanged) SendSpotify();
                return;
            }
            if (_spotifyStatus != status || eqChanged) { _spotifyStatus = status; SendSpotify(); }
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

    private async void SetSpatialPlayer(SpatialApp player, ulong requestedAddress, bool recall = false)
    {
        if (!recall && player == _spatialPlayer && _spotifyGate.CurrentCount != 0) return;
        int revision = Interlocked.Increment(ref _spatialChoiceRevision);
        var previousMode = _spatialStartingMode ?? _spatialResume?.Mode ?? _spotifyMode;
        _spatialStartup?.Cancel();
        await _spotifyGate.WaitAsync();
        try
        {
            if (_exiting || revision != _spatialChoiceRevision ||
                (requestedAddress != 0 && _connections.Session is { State.Status: SessionStatus.Ready } active &&
                    active.Address != requestedAddress)) return;
            ulong address = _connections.Session is { State.Status: SessionStatus.Ready } session &&
                (requestedAddress == 0 || requestedAddress == session.Address) ? session.Address : 0;
            var saved = address == 0 ? null : AppSoundFor(address, player.Key);
            bool usePairDefaults = address != 0 && saved == null && SavedSoundSources(address).Any();
            bool wasSuspended = _spatialResume != null;
            _spatialResume = null;
            if (player != _spatialPlayer && (_spotify != null || wasSuspended)) await CloseSpotify("idle");
            if (_exiting || revision != _spatialChoiceRevision) return;
            if (player != _spatialPlayer)
            {
                try { SpatialApp.RememberSelection(player); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { Log.Warn("spatial", $"Could not save app selection: {e.Message}"); }
                _spatialPlayer = player;
                SavePrefs(_prefs with { SpatialPlayer = player.Key });
            }
            _spotifyStatus = SpotifyRouting.AnyPending || WindowsSpatialMix.Pending ? "restore" : "idle";
            _outputRevision++;
            _nextSourceScan = 0;

            var mode = saved?.Mode is "fixed" or "tracked" ? saved.Mode : saved != null || usePairDefaults ? "stereo" : previousMode;
            if (address != 0)
            {
                if (saved != null || usePairDefaults)
                {
                    _pcEqAddress = saved?.Equalizer == true ? address : 0;
                    _pcEqCompareFlat = false;
                }
                if (mode == "tracked" && (_connections.Session?.State.Capabilities & DeviceCapabilities.SleepDetection) != DeviceCapabilities.SleepDetection)
                    mode = "fixed";
                _spotifyNoticeAddress = address;
                if (_spotifySession?.Address == address) ApplyPcEq(address);
                SendPcEq(address);
                if (_spotify != null || mode != "stereo" || _pcEqAddress == address)
                {
                    await ChangeSpatialMode(address, mode);
                    return;
                }
            }
            if (requestedAddress != 0 && requestedAddress != address) SendPcEq(requestedAddress);
            ReadStereoOutput();
            SendSpotify();
        }
        finally { _spotifyGate.Release(); }
    }

    private async void SetSpotifyMode(ulong address, string mode, bool remember = false)
    {
        int revision = Interlocked.Increment(ref _spatialChoiceRevision);
        if (address == _spotifyNoticeAddress && mode != _spatialStartingMode) _spatialStartup?.Cancel();
        await _spotifyGate.WaitAsync();
        try
        {
            if (_exiting || revision != _spatialChoiceRevision) return;
            if (remember && _connections.Session is { State.Status: SessionStatus.Ready } session &&
                session.Address == address && (mode != "tracked" ||
                    (session.State.Capabilities & DeviceCapabilities.SleepDetection) != 0))
                RememberAppMode(address, mode);
            if (_spatialResume is { } resume && resume.Session.Address == address && (mode != "stereo" || _pcEqAddress == address))
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
            if (mode == "stereo" && _pcEqAddress != address) { await CloseSpotify("idle"); return; }
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
                    _spotify = await Task.Run(() => new SpotifyBridge(_spatialPlayer, output, address,
                        () => Interruption() is null, expected,
                        () => !_exiting && _connections.Session == session && session.State.Status == SessionStatus.Ready &&
                            Volatile.Read(ref _spatialDisconnectAddress) != address && !session.CallAudio && !_mic.HandsFreeInUse,
                        spatial: mode != "stereo"));
                    _bridgeStartedAt = Environment.TickCount64;
                    _spotifySession = session;
                    ApplySpatialRenderPrefs(address);
                    ApplyPcEq(address);
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
            _spotify.SetSpatial(mode != "stereo");
            bool alreadyTracking = _spotifyMode == "tracked" && _headMotionTest?.SpatialRequest == SpotifyMotionRequest;
            if (mode == "tracked" && alreadyTracking) { SendSpotify(); return; }
            _spotifyMode = mode;
            _spotifyStatus = "waiting";
            _spotifyTimer.Start();
            if (mode == "tracked")
            {
                _spotifyHasPose = false;
                _lastShownPose = null;
                _trackingRetry.Reset();
                UpdateHeadTracking(session);
            }
            else
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
        _lastPcEqSignal = false;
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
                _trackingRetry.Reset();
                _spotifyHasPose = true;
                _spotifyStatus = _spotify.HasSignal ? "playing" : "waiting";
                SendSpotify();
            }
        }
        else if (status is not ("waiting" or "calibrating" or "stopped"))
        {
            if (_spotifySession.CallAudio || _mic.HandsFreeInUse)
            {
                SpotifyTick(null, EventArgs.Empty);
                return;
            }
            if (status == "micInUse" || _mic.Capturing || _mic.Status != MicStatus.NotActive)
            {
                _trackingRetry.Reset();
                SetHeadTrackingStatus("trackingMic");
                return;
            }
            _trackingRetry.Failed(Environment.TickCount64);
            SetHeadTrackingStatus(_trackingRetry.Exhausted ? "noMotion" : "recoveringMotion");
        }
    }

    // Leave the playback route in place throughout calibration and retries.
    private bool UpdateHeadTracking(AapSession session)
    {
        var wear = (session.State.PrimaryEar, session.State.SecondaryEar);
        if (wear != _trackingWear)
        {
            _trackingWear = wear;
            _trackingRetry.Reset();
        }
        if (Models.FromModelNumber(session.State.ModelNumber)?.SingleBattery != true &&
            IsOut(wear.PrimaryEar) && IsOut(wear.SecondaryEar))
        {
            SetHeadTrackingStatus("trackingWear");
            StopSpatialMotion(session.Address, SpotifyMotionRequest);
            return false;
        }
        if (_spotifyHasPose && SpotifyOwnsMotion) return true;
        if (_headMotionTest is not null || _stoppingHeadMotion)
        {
            if (!SpotifyOwnsMotion) SetHeadTrackingStatus("recoveringMotion");
            return false;
        }
        if (_mic.Capturing || _mic.Status != MicStatus.NotActive)
        {
            _trackingRetry.Reset();
            SetHeadTrackingStatus("trackingMic");
            return false;
        }
        if (!_trackingRetry.Due(Environment.TickCount64))
        {
            SetHeadTrackingStatus(_trackingRetry.Exhausted ? "noMotion" : "recoveringMotion");
            return false;
        }
        SetHeadTrackingStatus("calibrating");
        StartHeadMotion(session.Address, false, SpotifyMotionRequest);
        return false;

        static bool IsOut(EarStatus? ear) => ear is EarStatus.OutOfEar or EarStatus.InCase or EarStatus.Disconnected;
    }

    private void SetHeadTrackingStatus(string status)
    {
        if (_spotifyStatus == status && !_spotifyHasPose) return;
        _spotifyHasPose = false;
        _lastShownPose = null;
        _spotify?.Pose(0);
        _spotifyStatus = status;
        SendSpotify();
    }

    private void SendSpotify()
    {
        UpdateRoomMeter();
        if (!MainLive || !_mainInitSent) return;
        var w = _writer.Begin("spotifySpatial");
        ulong profileAddress = _connections.Session?.Address ?? _spotifyRoomViewer;
        var savedSources = SavedSoundSources(profileAddress).ToArray();
        w.WriteString("source", _spatialPlayer.Key);
        var (angle, level) = SpatialRenderPrefs(profileAddress);
        w.WriteNumber("speakerAngle", angle);
        w.WriteString("level", level);
        w.WriteStartArray("apps");
        foreach (var app in _spatialApps.Append(_spatialPlayer).Concat(savedSources).DistinctBy(app => app.Key))
        {
            if (app.IsWindowsMix) continue;
            w.WriteStartObject();
            w.WriteString("key", app.Key);
            w.WriteString("name", app.DisplayName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (profileAddress != 0)
        {
            w.WriteString("profileId", Mac.Format(profileAddress));
            w.WriteStartArray("rememberedSources");
            foreach (var app in savedSources) w.WriteStringValue(app.Key);
            w.WriteEndArray();
            if (AppSoundFor(profileAddress, _spatialPlayer.Key) is { } saved)
            {
                var savedMode = saved.Mode switch
                {
                    "tracked" => (_connections.Session?.State.Capabilities & DeviceCapabilities.SleepDetection) ==
                        DeviceCapabilities.SleepDetection ? "tracked" : "fixed",
                    "fixed" => "fixed",
                    _ => "stereo",
                };
                w.WriteString("savedMode", savedMode);
                w.WriteBoolean("savedEqualizer", saved.Equalizer);
            }
        }
        w.WriteString("mode", _spotifyMode);
        w.WriteString("status", _spotifyStatus);
        ulong address = _spotifySession?.Address ?? _spotifyNoticeAddress;
        w.WriteBoolean("routed", _spotify != null);
        w.WriteBoolean("pcEqEnabled", _pcEqAddress != 0 && _pcEqAddress == address);
        w.WriteBoolean("pcEqPlaying", _lastPcEqSignal && _spotify != null && _pcEqAddress == address);
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
