using System.Runtime.InteropServices;
using System.Text.Json;
using WPods.Audio;
using WPods.Core;
#if PAIRSIDE_LIVE
using Windows.Media.Control;
#endif

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private readonly System.Windows.Forms.Timer _headMotionTimer = new() { Interval = 125 };
    private HeadMotionTest? _headMotionTest;
    private const int SpotifyMotionRequest = int.MaxValue;
    private bool SpotifyOwnsMotion => _headMotionTest?.SpatialRequest == SpotifyMotionRequest;
    private void StopWindowMotion()
    {
        if (_headMotionTest?.Playback != true && !SpotifyOwnsMotion && _queuedSpatial?.Request != SpotifyMotionRequest) StopHeadMotion();
    }

    private bool _stoppingHeadMotion;
    private (ulong Address, int Request)? _queuedSpatial;
    private CancellationTokenSource? _headAction;
    private Task _motionStop = Task.CompletedTask;

    private sealed class HeadMotionTest(AapSession session, bool playback, int spatialRequest)
    {
        internal readonly AapSession Session = session;
        internal readonly long Started = Environment.TickCount64;
        internal readonly HeadMotion Motion = new(spatialRequest > 0);
        internal readonly int SpatialRequest = spatialRequest;
        internal readonly bool Playback = playback;
        internal Task<bool> Write = Task.FromResult(true);
        internal bool AlternateFirst;
        internal bool TriedBoth;
        internal long FirstFrameAt;
        internal string Status = "";
        internal int Samples = -1;
        internal string? Gesture;
        internal int Count;
        internal string? MediaResult;
        internal int Pitch;
        internal int Yaw;
        // The Spotify audio thread consumes the newest position. Publish it on
        // receipt so moving a window or repainting a page cannot delay the stage.
        internal Action<double, double, double>? AudioPose;

        internal void RecenterAudio()
        {
            lock (Motion)
            {
                if (!Motion.Calibrated) return;
                Motion.RecenterSpatial();
                AudioPose?.Invoke(0, 0, 0);
            }
        }

        internal void Observe(ReadOnlySpan<byte> packet)
        {
            lock (Motion)
            {
                long now = Environment.TickCount64;
                if (!Motion.Observe(packet, now)) return;
                if (Motion.Samples == 1) FirstFrameAt = now;
                if (Motion.Calibrated)
                    AudioPose?.Invoke(Motion.SpatialYaw, Motion.SpatialPitch, Motion.SpatialRoll);
            }
        }
    }

    private void WriteGestureBindings(Utf8JsonWriter writer)
    {
        writer.WriteString("nodAction", _prefs.NodAction);
        writer.WriteString("shakeAction", _prefs.ShakeAction);
        writer.WriteString("gestureSensitivity", _prefs.GestureSensitivity);
    }

    private void SendGestureBindings()
    {
        var writer = _writer.Begin("gestureBindings");
        WriteGestureBindings(writer);
        PostMain(_writer.End());
    }

    private void SetGestureSensitivity(string value)
    {
        SavePrefs(_prefs with { GestureSensitivity = value });
        if (_headMotionTest is { SpatialRequest: 0 } test)
            lock (test.Motion) test.Motion.SetGestureSensitivity(value);
        SendGestureBindings();
    }

    private void SetGestureBinding(string gesture, string action)
    {
        SavePrefs(gesture == "nod" ? _prefs with { NodAction = action } : _prefs with { ShakeAction = action });
        SendGestureBindings();
        _headAction?.Cancel();
        if (_headMotionTest is { Playback: true } && _prefs.NodAction == "none" && _prefs.ShakeAction == "none")
            StopHeadMotion();
    }

    private void StartHeadMotion(ulong address, bool playback, int spatialRequest = 0)
    {
        if (_exiting) return;
        if (spatialRequest > 0 && _stoppingHeadMotion)
        {
            _queuedSpatial = (address, spatialRequest);
            return;
        }
        if (playback && _prefs.NodAction == "none" && _prefs.ShakeAction == "none")
        {
            PostHeadMotion(address, "stopped", 0, null);
            return;
        }
        if (playback && SpotifyOwnsMotion)
        {
            PostHeadMotion(address, "spatialBusy", 0, null);
            return;
        }
        if (_headMotionTest is not null || _stoppingHeadMotion || (spatialRequest != SpotifyMotionRequest && (!MainLive || _main?.WindowState == FormWindowState.Minimized)) ||
            _connections.Session is not { } session || session.Address != address ||
            session.State.Status != SessionStatus.Ready || session.CallAudio || _mic.Status != MicStatus.NotActive ||
            _mic.HandsFreeInUse || (session.State.Capabilities & DeviceCapabilities.SleepDetection) == 0)
        {
            if (spatialRequest > 0)
                PostSpatialPose(address, spatialRequest, spatialRequest == SpotifyMotionRequest &&
                    (_mic.Capturing || _mic.Status != MicStatus.NotActive) ? "micInUse" : "unavailable");
            else PostHeadMotion(address, "unavailable", 0, null);
            return;
        }

        var test = new HeadMotionTest(session, playback, spatialRequest)
        {
            AlternateFirst = HeadMotion.UseAlternateFirst(session.VerifiedMotionService,
                Models.FromModelNumber(session.State.ModelNumber)?.Name == "AirPods Pro 3")
        };
        if (spatialRequest == 0) test.Motion.SetGestureSensitivity(_prefs.GestureSensitivity);
#if PAIRSIDE_LIVE
        if (spatialRequest == SpotifyMotionRequest && _spotify is { } bridge)
            test.AudioPose = bridge.Pose;
#endif
        _headMotionTest = test;
        session.PacketReceived += test.Observe;
        test.Write = session.WriteAsync(HeadMotion.Command(start: true, alternate: test.AlternateFirst)).AsTask();
        _headMotionTimer.Tick -= OnHeadMotionTick;
        _headMotionTimer.Tick += OnHeadMotionTick;
        _headMotionTimer.Interval = spatialRequest > 0 ? 32 : playback ? 125 : 50;
        _headMotionTimer.Start();
        OnHeadMotionTick(null, EventArgs.Empty);
    }

    private void OnHeadMotionTick(object? sender, EventArgs args)
    {
        if (_headMotionTest is not { } test) return;
        if ((!test.Playback && test.SpatialRequest != SpotifyMotionRequest && (!MainLive || _main?.WindowState == FormWindowState.Minimized)) ||
            _connections.Session != test.Session || test.Session.CallAudio || _mic.Status != MicStatus.NotActive || _mic.HandsFreeInUse)
        {
            StopHeadMotion(test.SpatialRequest == SpotifyMotionRequest && _connections.Session == test.Session &&
                (_mic.Capturing || _mic.Status != MicStatus.NotActive) ? "micInUse" :
                test.SpatialRequest > 0 ? "unavailable" : "stopped");
            return;
        }
        if (test.Playback && !CanUsePlaybackGesture(test.Session))
        {
            StopHeadMotion();
            return;
        }
        int samples;
        string? gesture;
        int count;
        int pitch;
        int yaw;
        double spatialYaw;
        double spatialPitch;
        double spatialRoll;
        long lastSample;
        long firstSample;
        bool calibrated;
        lock (test.Motion)
        {
            spatialYaw = test.Motion.SpatialYaw;
            spatialPitch = test.Motion.SpatialPitch;
            spatialRoll = test.Motion.SpatialRoll;
            samples = test.Motion.Samples;
            gesture = test.Motion.Gesture;
            calibrated = test.Motion.Calibrated;
            count = test.Motion.Gestures;
            pitch = test.Motion.GesturePitchUp;
            yaw = test.Motion.Yaw;
            lastSample = test.Motion.LastSample;
            firstSample = test.FirstFrameAt;
            if (test.Motion.ReportService is byte observedService)
                test.Session.VerifiedMotionService = observedService;
        }
        bool newGesture = count > test.Count;
        bool moved = pitch != test.Pitch || yaw != test.Yaw;
        (test.Count, test.Pitch, test.Yaw, test.Gesture) = (count, pitch, yaw, gesture);
        if (test.Playback && newGesture)
        {
            test.MediaResult = null;
            var binding = gesture switch { "nod" => _prefs.NodAction, "shake" => _prefs.ShakeAction, _ => "none" };
            if (binding != "none" && _headAction is null) ApplyGestureAction(test, binding);
        }
        long elapsed = Environment.TickCount64 - test.Started;
        if (samples == 0 && !test.TriedBoth && elapsed >= (test.SpatialRequest > 0 ? 650 : 1800) && test.Write.IsCompleted)
        {
            test.TriedBoth = true;
            test.Write = test.Session.WriteAsync(HeadMotion.Command(start: true, alternate: !test.AlternateFirst)).AsTask();
        }
        if ((samples == 0 && elapsed >= 4800) || (test.SpatialRequest > 0 && !calibrated && elapsed >= 9000))
        {
            StopHeadMotion("noSignal");
            return;
        }
        if (samples > 0 && Environment.TickCount64 - lastSample >= 1700)
        {
            StopHeadMotion("signalLost");
            return;
        }
        if (!test.Playback && test.SpatialRequest == 0 && elapsed >= 30_000)
        {
            StopHeadMotion("finished");
            return;
        }
        string status = samples == 0 ? "waiting" : calibrated ? "ready" : "calibrating";
        bool changedStatus = status != test.Status;
        if (!changedStatus && samples == test.Samples) return;
        (test.Status, test.Samples) = (status, samples);
        if (test.Playback && !changedStatus && !newGesture) return;
        if (test.SpatialRequest > 0)
        {
            if (changedStatus && calibrated)
                Log.Info("aap", $"Spatial tracking ready: first frame {firstSample - test.Started} ms, ready {elapsed} ms, service {test.Session.VerifiedMotionService}");
            PostSpatialPose(test.Session.Address, test.SpatialRequest, status, spatialYaw, spatialPitch, spatialRoll);
            return;
        }
        if (!test.Playback && !changedStatus && !newGesture && !moved) return;
        PostHeadMotion(test.Session.Address, status, samples, gesture, test.Playback, count, pitch, yaw, test.MediaResult);
    }

    private void StopSpatialMotion(ulong address, int request)
    {
        if (_queuedSpatial == (address, request)) _queuedSpatial = null;
        if (_headMotionTest is { SpatialRequest: > 0 } active &&
            active.Session.Address == address && active.SpatialRequest == request) StopHeadMotion();
    }

    private void StopHeadMotion(string status = "stopped")
    {
        _queuedSpatial = null;
        if (_headMotionTest is not { } test)
        {
            _headAction?.Cancel();
            return;
        }
        _headMotionTest = null;
        _headAction?.Cancel();
        _motionStop = FinishMotion(test, status);
    }

    private async Task FinishMotion(HeadMotionTest test, string status)
    {
        _stoppingHeadMotion = true;
        _headMotionTimer.Stop();
        test.Session.PacketReceived -= test.Observe;
        lock (test.Motion) test.AudioPose = null;
        try
        {
            await test.Write;
            if (_connections.Session == test.Session)
            {
                if (!test.AlternateFirst || test.TriedBoth) await test.Session.WriteAsync(HeadMotion.Command(start: false, alternate: false));
                if (test.AlternateFirst || test.TriedBoth) await test.Session.WriteAsync(HeadMotion.Command(start: false, alternate: true));
            }

        }
        catch (OperationCanceledException) { status = "stopped"; }
        catch (Exception e) { Log.Warn("aap", $"Motion stop failed: {e.GetType().Name}"); }
        finally
        {
            _stoppingHeadMotion = false;
        }
        Log.Info("aap", $"head motion test {status}: {Math.Max(0, test.Samples)} frames");
        if (test.SpatialRequest > 0) PostSpatialPose(test.Session.Address, test.SpatialRequest, status);
        else PostHeadMotion(test.Session.Address, status, Math.Max(0, test.Samples), test.Gesture, test.Playback, test.Count);
        if (_queuedSpatial is { } queued)
        {
            _queuedSpatial = null;
            StartHeadMotion(queued.Address, false, queued.Request);
        }
    }

    private async void ApplyGestureAction(HeadMotionTest test, string binding)
    {
        int eventCount = test.Count;
        using var action = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _headAction = action;
        try
        {
            string result;
            if (binding is "transparency" or "noiseCancellation")
            {
                var mode = binding == "transparency" ? ListeningMode.Transparency : ListeningMode.NoiseCancellation;
                var state = test.Session.State;
                bool available = CanUsePlaybackGesture(test.Session) &&
                    (state.Capabilities & DeviceCapabilities.ListeningMode) != 0 && NoiseBlocked(state, mode) is null;
                bool applied = available && (state.ListeningMode == mode ||
                    await test.Session.SetListeningModeAsync(mode, action.Token));
                result = applied ? binding : "modeUnavailable";
            }
            else
            {
                result = await UsePlaybackGesture(test.Session, binding, action.Token);
            }
            if (_headMotionTest == test && test.Count == eventCount && !action.IsCancellationRequested)
            {
                test.MediaResult = result;
                PostHeadMotion(test.Session.Address, test.Status, test.Samples, test.Gesture,
                    true, test.Count, test.Pitch, test.Yaw, result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Warn("aap", $"head gesture action failed: {e.Message}");
            if (_headMotionTest == test && !action.IsCancellationRequested)
            {
                test.MediaResult = "actionUnavailable";
                PostHeadMotion(test.Session.Address, test.Status, test.Samples, test.Gesture,
                    true, test.Count, test.Pitch, test.Yaw, test.MediaResult);
            }
        }
        finally { if (_headAction == action) _headAction = null; }
    }

    private void ReplayPlaybackGestures()
    {
        if (_headMotionTest is { Playback: true } test)
            PostHeadMotion(test.Session.Address, string.IsNullOrEmpty(test.Status) ? "waiting" : test.Status, test.Samples, test.Gesture, true, test.Count,
                test.Pitch, test.Yaw, test.MediaResult);
        else if (_connections.Session is { } session)
            PostHeadMotion(session.Address, "stopped", 0, null);
    }

    private async Task<string> UsePlaybackGesture(AapSession session, string binding, CancellationToken ct)
    {
#if PAIRSIDE_LIVE
        try
        {
            if (!CanUsePlaybackGesture(session) ||
                await Task.Run(() => DefaultOutputWatcher.wpn_audio_is_default_output(session.Address), ct) != 1)
            {
                return "noPlayback";
            }
            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
            var player = manager.GetCurrentSession();
            ct.ThrowIfCancellationRequested();
            if (player is null || !CanUsePlaybackGesture(session)) return "noPlayback";
            var info = player.GetPlaybackInfo();
            if (info is null) return "noPlayback";
            switch (binding)
            {
                case "next":
                    return info.Controls.IsNextEnabled && await player.TrySkipNextAsync().AsTask(ct) ? "skipped" : "nextUnavailable";
                case "previous":
                    return info.Controls.IsPreviousEnabled && await player.TrySkipPreviousAsync().AsTask(ct) ? "previous" : "previousUnavailable";
                case "pause" when info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    return "paused";
                case "pause" or "playPause" when info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                    return info.Controls.IsPauseEnabled && await player.TryPauseAsync().AsTask(ct) ? "paused" : "actionUnavailable";
                case "playPause" when info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    return info.Controls.IsPlayEnabled && await player.TryPlayAsync().AsTask(ct) ? "playing" : "actionUnavailable";
                default:
                    return "actionUnavailable";
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn("aap", $"head gesture media control failed: {e.Message}");
            return "noPlayback";
        }
#else
        await Task.CompletedTask;
        return "noPlayback";
#endif
    }

    private bool CanUsePlaybackGesture(AapSession session) => !_exiting && _connections.Session == session &&
        session.State.PrimaryEar == EarStatus.InEar && session.State.SecondaryEar == EarStatus.InEar &&
        !session.CallAudio && _mic.Status == MicStatus.NotActive && !_mic.HandsFreeInUse;

    private void PostSpatialPose(ulong address, int request, string status, double yaw = 0, double pitch = 0, double roll = 0)
    {
#if PAIRSIDE_LIVE
        if (request == SpotifyMotionRequest)
        {
            if (status != "stopped" || _queuedSpatial?.Request != request) SpotifyMotionStatus(address, status);
            if (status == "ready") SendSpotifyPose(address, yaw, pitch);
            return;
        }
#endif
        if (!MainLive || _main is not { } main) return;
        var w = _writer.Begin("spatialPose");
        w.WriteString("id", Mac.Format(address));
        w.WriteNumber("request", request);
        w.WriteString("status", status);
        w.WriteNumber("yaw", yaw);
        w.WriteNumber("pitch", pitch);
        w.WriteNumber("roll", roll);
        main.Pane.Post(_writer.End());
    }

    private void PostHeadMotion(ulong address, string status, int samples, string? gesture,
        bool playback = false, int count = 0, int pitch = 0, int yaw = 0, string? mediaResult = null)
    {
        if (!MainLive || _main is not { } main) return;
        var w = _writer.Begin("headMotion");
        w.WriteString("id", Mac.Format(address));
        w.WriteString("status", status);
        w.WriteNumber("samples", samples);
        w.WriteString("gesture", gesture);
        w.WriteBoolean("playback", playback);
        w.WriteString("mediaResult", mediaResult);
        w.WriteNumber("count", count);
        w.WriteNumber("pitch", pitch);
        w.WriteNumber("yaw", yaw);
        main.Pane.Post(_writer.End());
    }
}
