using System.Runtime.InteropServices;
using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private bool _micSetupRequested;
    private int _micWatchRequest;
    private MicSetupWatch? _micSetupWatch;
    private readonly System.Windows.Forms.Timer _micSetupTimer = new() { Interval = 125 };

    private sealed class MicSetupWatch(int request)
    {
        // Only the highest sample magnitude crosses threads. -1 means no PCM since the previous tick.
        internal int Request { get; } = request;
        internal int Peak = -1;
        internal int Revision;
        internal ListenInputMeter? ListenMeter;
        internal ListenInputMeter? CallsMeter;
        internal ListenInputMeter? TargetMeter;
        internal int CapturedMs, CaptureDb = -60;
        internal bool SettingDefault;
        internal string? CheckEndpoint;
        internal bool TestingCallsDefault;
        internal bool TestingHardwareMic;
        internal string CheckStopReason = "idle";
        internal bool? TargetMuted;
        internal string? RouteError;
        internal bool ReadingNames;
        internal long RefreshAfter;
        internal AudioEndpoints.Routes? Routes;
        internal MixerDevice? Target;
        internal ulong Address;
        internal CancellationTokenSource? Check;
        internal int CheckRequest;
        internal AapSession? CheckSession;
        internal int CheckPeak = -1;
        internal volatile bool FinishSample;
        internal MicSetupStatus? Previous;
        internal IReadOnlyList<MixerDevice>? DeviceList;
        internal MixerDevice[] Sources = [];
        internal AudioEndpoints.MicrophoneUse[] AppInputs = [];

        internal void StopCheck(string reason)
        {
            if (Check is not { IsCancellationRequested: false } check) return;
            CheckStopReason = reason;
            check.Cancel();
        }

        internal void Observe(ReadOnlySpan<short> samples, long position)
        {
            int peak = 0;
            foreach (short sample in samples)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
            }
            RecordPeak(ref Peak, peak);
            if (Volatile.Read(ref Check) is not null) RecordPeak(ref CheckPeak, peak);
        }

        private static void RecordPeak(ref int bucket, int value)
        {
            int previous = Volatile.Read(ref bucket);
            while (value > previous)
            {
                int observed = Interlocked.CompareExchange(ref bucket, value, previous);
                if (observed == previous)
                {
                    return;
                }
                previous = observed;
            }
        }
    }

    private sealed record MicSetupStatus(string? Input, string? CallsInput, string? Output, string? Target,
        string? CallsId, string? TargetId, bool DefaultInput, bool DefaultCalls, bool CallsMuted, int? VoiceDb,
        int? CallsDb, int? TargetDb, bool TargetMuted, int CapturedMs, int? CaptureDb, bool SettingDefault, string? RouteError, string? Fault,
        MixerDevice[] Sources, string? Monitoring, bool CanMonitor, int? ListenDb, string? ListenStatus,
        string? CallsOutput, AudioEndpoints.MicrophoneUse[] AppInputs);

    private void UpdateMicSetupWatch()
    {
        bool needed = _micSetupRequested && _mainShown && _main is { WindowState: not FormWindowState.Minimized };
        if (needed ? _micSetupWatch?.Request == _micWatchRequest : _micSetupWatch == null)
        {
            return;
        }
        if (_micSetupWatch is { } old)
        {
            _micSetupTimer.Stop();
            _mic.Written -= old.Observe;
            _micSetupWatch = null;
            old.Check?.Cancel();
            old.ListenMeter?.Dispose();
            old.CallsMeter?.Dispose();
            old.TargetMeter?.Dispose();
            _mixer.StopMonitor();
        }
        if (!needed) return;
        var watch = new MicSetupWatch(_micWatchRequest);
        _micSetupWatch = watch;
        _mic.Written += watch.Observe;
        _micSetupTimer.Start();
        OnMicSetupTick(null, EventArgs.Empty);
    }

    private void OnMicSetupTick(object? sender, EventArgs args)
    {
        if (_micSetupWatch is not { } watch || _main is not { } main)
        {
            return;
        }
        int revision = DefaultOutputWatcher.EndpointRevision;
        AapSession? session = _connections.Session;
        ulong address = session?.Address ?? 0;
        if (watch.CheckSession is { } checkedSession && (session != checkedSession || session.State.Status != SessionStatus.Ready))
        {
            watch.StopCheck("disconnected");
        }
        if (address != watch.Address || revision != watch.Revision)
        {
            if (address != watch.Address)
            {
                watch.RouteError = null;
                watch.Routes = null;
                watch.Target = null;
                watch.TargetMuted = null;
                watch.AppInputs = [];
            }
            watch.RefreshAfter = 0;
            watch.Revision = revision;
            watch.Address = address;
        }
        if (!watch.ReadingNames && Environment.TickCount64 >= watch.RefreshAfter)
        {
            ReadMicSetupRoutes(watch, address);
        }
        if (watch.Previous is null && watch.ReadingNames)
        {
            return;
        }
        int peak = Interlocked.Exchange(ref watch.Peak, -1);
        int? db = peak < 0 ? null : AudioEndpoints.PeakDb(peak / 32768d);
        AudioEndpoints.Routes? routes = watch.Routes;
        MixerDevice? target = watch.Target;
        IReadOnlyList<MixerDevice> devices = _mixer.Devices;
        if (devices != watch.DeviceList)
        {
            watch.DeviceList = devices;
            watch.Sources = devices.Where(static d => d.Flow == MixerFlow.Input && d.State == MixerState.Active &&
                d.Pair == 0 && !d.OwnMic).ToArray();
        }
        bool canMonitor = session?.State.Status == SessionStatus.Ready && !session.CallAudio && !_mic.HandsFreeInUse;
        if (!canMonitor) _mixer.StopMonitor();
        string? monitorStatus = _mixer.MonitorStatus;
        string? monitoring = _mixer.MonitorSource;
        string? meteredEndpoint = monitoring == Mixer.EarbudMic ? null : monitoring;
        if (watch.ListenMeter?.Endpoint != meteredEndpoint)
        {
            watch.ListenMeter?.Dispose();
            watch.ListenMeter = meteredEndpoint is null ? null : new ListenInputMeter(meteredEndpoint);
        }
        bool defaultCalls = session?.State.Status == SessionStatus.Ready && target is not null && target.Id == routes?.CommunicationsInput?.Id;
        string? callsId = routes?.CommunicationsInput?.Id;
        if (watch.Check is not null)
        {
            if (watch.CheckEndpoint != (watch.TestingCallsDefault ? callsId : target?.Id))
                watch.StopCheck("inputChanged");
            else if ((watch.TestingCallsDefault ? routes?.CommunicationsInput?.Muted : watch.TargetMuted) == true)
                watch.StopCheck("muted");
            // Opening the physical HFP microphone for this test activates its own call-audio link.
            else if (!watch.TestingHardwareMic && (session?.CallAudio == true || _mic.HandsFreeInUse))
                watch.StopCheck("busy");
        }
        if (watch.CallsMeter?.Endpoint != callsId)
        {
            watch.CallsMeter?.Dispose();
            watch.CallsMeter = callsId is null ? null : new ListenInputMeter(callsId);
        }
        string? separateTarget = target?.Id != callsId ? target?.Id : null;
        if (watch.TargetMeter?.Endpoint != separateTarget)
        {
            watch.TargetMeter?.Dispose();
            watch.TargetMeter = separateTarget is null ? null : new ListenInputMeter(separateTarget);
        }
        var status = new MicSetupStatus(routes?.Input?.Name, routes?.CommunicationsInput?.Name, routes?.Output?.Name,
            target?.Name, callsId, target?.Id, target is not null && target.Id == routes?.Input?.Id,
            defaultCalls, routes?.CommunicationsInput?.Muted == true, db, watch.CallsMeter?.Level,
            separateTarget is null ? watch.CallsMeter?.Level : watch.TargetMeter?.Level, watch.TargetMuted == true,
            Volatile.Read(ref watch.CapturedMs), watch.Check is null ? null : Volatile.Read(ref watch.CaptureDb),
            watch.SettingDefault, watch.RouteError, target?.OwnMic == true ? _mic.FaultCode : null,
            watch.Sources, monitoring, canMonitor, monitoring == Mixer.EarbudMic ? db : watch.ListenMeter?.Level, monitorStatus,
            routes?.CommunicationsOutput?.Name, watch.AppInputs);
        if (watch.Previous == status)
        {
            return;
        }
        var w = _writer.Begin("micSetup");
        w.WriteNumber("watchRequest", watch.Request);
        w.WriteString("input", status.Input);
        w.WriteString("callsInput", status.CallsInput);
        w.WriteString("target", status.Target);
        w.WriteString("output", status.Output);
        w.WriteString("callsOutput", status.CallsOutput);
        w.WriteStartArray("activeInputs");
        foreach (var input in status.AppInputs)
        {
            w.WriteStartObject();
            w.WriteString("app", input.App);
            w.WriteString("input", input.Input);
            w.WriteBoolean("onAirPods", string.Equals(input.Endpoint, status.TargetId, StringComparison.OrdinalIgnoreCase));
            w.WriteBoolean("onCallsDefault", string.Equals(input.Endpoint, status.CallsId, StringComparison.OrdinalIgnoreCase));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("fault", status.Fault);
        w.WriteString("callsId", status.CallsId);
        w.WriteString("targetId", status.TargetId);
        w.WriteBoolean("settingDefault", status.SettingDefault);
        w.WriteString("routeError", status.RouteError);
        w.WriteNumber("capturedMs", status.CapturedMs);
        if (status.CaptureDb is int recorded) w.WriteNumber("captureDb", recorded);
        if (status.CallsDb is int callPeak) w.WriteNumber("callsDb", callPeak);
        if (status.TargetDb is int targetPeak && target is not null) w.WriteNumber("targetDb", targetPeak);
        w.WriteBoolean("targetMuted", status.TargetMuted);
        w.WriteBoolean("defaultInput", status.DefaultInput);
        w.WriteBoolean("defaultCalls", status.DefaultCalls);
        w.WriteBoolean("callsMuted", status.CallsMuted);
        w.WriteBoolean("canMonitor", status.CanMonitor);
        w.WriteString("monitoring", status.Monitoring);
        w.WriteString("listenStatus", status.ListenStatus);
        if (status.ListenDb is int level) w.WriteNumber("listenDb", level);
        else w.WriteNull("listenDb");
        w.WriteStartArray("sources");
        foreach (MixerDevice source in status.Sources)
        {
            w.WriteStartObject();
            w.WriteString("id", source.Id);
            w.WriteString("name", source.Name);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (db is int voice) w.WriteNumber("peakDb", voice);
        else w.WriteNull("peakDb");
        main.Pane.Post(_writer.End());
        watch.Previous = status;
    }

    private void SetListenMonitor(string? source, int percent)
    {
        if (_micSetupWatch is not { } watch) return;
        if (source is null)
        {
            _mixer.StopMonitor();
        }
        else if (watch.Check is null &&
            _connections.Session is { State.Status: SessionStatus.Ready, CallAudio: false } session && !_mic.HandsFreeInUse)
        {
            if (source == Mixer.EarbudMic && !Models.HasStereoMic(session.State.ModelNumber)) return;
            if (!_mixer.StartMonitor(session.Address, source, percent)) SendToast("noResponse", session.Address);
        }
        OnMicSetupTick(null, EventArgs.Empty);
    }

    private async void ReadMicSetupRoutes(MicSetupWatch watch, ulong address)
    {
        watch.ReadingNames = true;
        bool pinChanged = false;
        int revision = watch.Revision;
        bool IsCurrent() => watch == _micSetupWatch && (_connections.Session?.Address ?? 0) == address &&
            revision == DefaultOutputWatcher.EndpointRevision;
        string? PinnedInput() => watch.Check is not null && !watch.TestingCallsDefault ? watch.CheckEndpoint : null;
        try
        {
            IReadOnlyList<MixerDevice> devices = _mixer.Devices;
            // Pin an explicit AirPods test to the same input if Windows changes its calls default.
            string? pinned = PinnedInput();
            bool stereoMic = Models.HasStereoMic(_connections.Session?.State.ModelNumber);
            var snapshot = await Task.Run(() =>
            {
                var routes = AudioEndpoints.ReadDefaults(address);
                var calls = routes.CommunicationsInput;
                var target = SelectPairTestInput(devices, address, calls?.Id, pinned, AudioEndpoints.IsVirtualPairMicrophone, stereoMic);
                bool? muted = target is null ? null : target.Id == calls?.Id ? calls.Muted : AudioEndpoints.ReadInputMute(target.Id);
                AudioEndpoints.MicrophoneUse[] appInputs;
                try { appInputs = AudioEndpoints.ActiveMicrophones(); }
                catch (COMException) { appInputs = []; }
                return (routes, target, muted, appInputs);
            });
            pinChanged = PinnedInput() != pinned;
            if (!pinChanged && IsCurrent())
            {
                (watch.Routes, watch.Target, watch.TargetMuted) = (snapshot.routes, snapshot.target, snapshot.muted);
                if (!watch.AppInputs.SequenceEqual(snapshot.appInputs)) watch.AppInputs = snapshot.appInputs;
            }
        }
        catch (COMException)
        {
            // The audio service can restart or an endpoint can disappear during the lookup.
            watch.Routes = null;
            watch.Target = null;
            watch.TargetMuted = null;
            watch.AppInputs = [];
        }
        finally
        {
            bool pairInput = watch.Routes?.HardwareCallInput == true ||
                (watch.Target is { } target && watch.Routes?.CommunicationsInput?.Id == target.Id);
            bool watchingCall = pairInput && (_mic.Capturing || _mic.HandsFreeInUse);
            watch.RefreshAfter = pinChanged || !IsCurrent() ? 0 :
                Environment.TickCount64 + (watchingCall || watch.Check is not null ? 750 : watch.Target is null || watch.AppInputs.Length > 0 ? 1200 : 3500);
            watch.ReadingNames = false;
        }
    }

    internal static MixerDevice? SelectPairTestInput(IReadOnlyList<MixerDevice> devices, ulong address, string? callsId,
        string? pinned, Func<string, ulong, bool> isVirtualPair, bool stereoMic = true)
    {
        if (address == 0) return null;
        var inputs = devices.Where(d => d.Flow == MixerFlow.Input && d.State == MixerState.Active &&
            (!d.OwnMic || stereoMic) && (d.Pair == address || (d.OwnMic && isVirtualPair(d.Id, address)))).ToArray();
        return inputs.FirstOrDefault(d => d.Id == pinned) ?? inputs.FirstOrDefault(d => d.Id == callsId) ??
            inputs.FirstOrDefault(d => d.OwnMic) ?? inputs.FirstOrDefault();
    }

    private async void UseAirPodsForCalls(string endpoint)
    {
        if (_micSetupWatch is not { SettingDefault: false, Check: null } watch ||
            _connections.Session is not { State.Status: SessionStatus.Ready } session ||
            watch.Address != session.Address || watch.Target?.Id != endpoint) return;
        watch.SettingDefault = true;
        watch.RouteError = null;
        OnMicSetupTick(null, EventArgs.Empty);
        try
        {
            bool applied = await Task.Run(() => AudioEndpoints.SetPairCallsInput(endpoint, session.Address));
            if (!applied) watch.RouteError = "failed";
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            watch.RouteError = "failed";
            Log.Warn("mic", $"Windows call input switch failed 0x{e.HResult:X8}");
        }
        finally
        {
            watch.SettingDefault = false;
            watch.RefreshAfter = 0;
            if (_micSetupWatch == watch) OnMicSetupTick(null, EventArgs.Empty);
        }
    }

    private async void RecordMicCheck(WebPane pane, string? endpoint, string? source, int request)
    {
        if (_micSetupWatch is not { } watch)
        {
            PostMicCheck(pane, request, "unavailable");
            return;
        }
        if (watch.Check is not null)
        {
            if (watch.CheckRequest != request) PostMicCheck(pane, request, "busy");
            return;
        }
        void Reply(string status, MicCheck.Recording? recording = null)
        {
            if (_micSetupWatch == watch) PostMicCheck(pane, request, status, endpoint, recording);
        }
        var session = _connections.Session;
        string? targetId = watch.Target?.Id;
        bool pairInput = endpoint is not null && endpoint == targetId;
        bool callsDefault = source == "calls";
        if (endpoint is null || (!callsDefault && !pairInput) || (callsDefault && endpoint != watch.Routes?.CommunicationsInput?.Id) ||
            (pairInput && (session?.State.Status != SessionStatus.Ready || session.Address != watch.Address)))
        {
            Reply(callsDefault && endpoint is not null ? "inputChanged" : "unavailable");
            return;
        }
        if ((callsDefault ? watch.Routes?.CommunicationsInput?.Muted : watch.TargetMuted) == true)
        {
            Reply("muted");
            watch.RefreshAfter = 0;
            return;
        }
        if (session?.CallAudio == true || _mic.HandsFreeInUse || _mixer.MonitorSource is not null || watch.SettingDefault)
        {
            Reply("busy");
            return;
        }
        using var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref watch.CheckPeak, -1);
        watch.TestingCallsDefault = callsDefault;
        watch.TestingHardwareMic = pairInput && watch.Target?.OwnMic == false;
        watch.CheckStopReason = "idle";
        watch.CheckEndpoint = endpoint;
        watch.FinishSample = false;
        watch.Check = cancel;
        watch.CheckRequest = request;
        watch.CapturedMs = 0;
        watch.CaptureDb = -60;
        watch.CheckSession = pairInput ? session : null;
        watch.RefreshAfter = 0;
        Reply("recording");
        try
        {
            MicCheck.Recording recorded = await Task.Run(() => MicCheck.Capture(endpoint, callsDefault, cancel.Token, (ms, db) =>
            {
                Volatile.Write(ref watch.CapturedMs, ms);
                Volatile.Write(ref watch.CaptureDb, db);
            }, () => watch.FinishSample));
            cancel.Token.ThrowIfCancellationRequested();
            if (_micSetupWatch != watch) return;
            if (pairInput && _connections.Session != session) { Reply("disconnected"); return; }
            bool audible = recorded.Level != "silent";
            int sourcePeak = AudioEndpoints.PeakDb(Math.Max(0, Volatile.Read(ref watch.CheckPeak)) / 32768d);
            Log.Info("mic", $"sound check: Windows {(audible ? "received signal" : "reported silence")}, peak {recorded.PeakDb} dBFS, Pairside feed {sourcePeak} dBFS");
            Reply(!audible && pairInput && !watch.TestingHardwareMic && sourcePeak > -45 ? "routeSilent" : recorded.Level, recorded);
        }
        catch (MicCheck.MutedInputException)
        {
            Reply("muted");
            watch.RefreshAfter = 0;
        }
        catch (MicCheck.InputChangedException)
        {
            Reply("inputChanged");
            watch.RefreshAfter = 0;
        }
        catch (MicCheck.ShortSampleException)
        {
            Reply("tooShort");
        }
        catch (MicCheck.NoFramesException)
        {
            Reply("noFrames");
        }
        catch (UnauthorizedAccessException)
        {
            Reply("denied");
        }
        catch (OperationCanceledException)
        {
            Reply(watch.CheckStopReason);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            Log.Warn("mic", $"sound check failed 0x{e.HResult:X8}: {e.Message}");
            if (_micSetupWatch == watch)
            {
                string result = cancel.IsCancellationRequested ? watch.CheckStopReason :
                    pairInput && session != _connections.Session ? "disconnected" : e.HResult switch
                    {
                        unchecked((int)0x88890004) or unchecked((int)0x88890026) or unchecked((int)0x80070490)
                            => "inputChanged", // Endpoint removed or invalidated.
                        unchecked((int)0x8889000A) => "busy", // AUDCLNT_E_DEVICE_IN_USE
                        _ => "failed"
                    };
                Reply(result);
            }
        }
        finally
        {
            watch.Check = null;
            watch.CheckEndpoint = null;
            watch.CheckSession = null;
            watch.CapturedMs = 0;
        }
    }

    private void PostMicCheck(WebPane pane, int request, string status, string? endpoint = null, MicCheck.Recording? recording = null)
    {
        if (_main?.Pane != pane || !MainLive) return;
        var w = _writer.Begin("micCheck");
        w.WriteNumber("request", request);
        w.WriteString("status", status);
        w.WriteString("endpoint", endpoint);
        if (recording is not null)
        {
            w.WriteNumber("durationMs", (recording.Wave.Length - 44) / 96);
            w.WriteNumber("peakDb", recording.PeakDb);
            w.WriteStartArray("envelope");
            foreach (int bar in recording.Envelope) w.WriteNumberValue(bar);
            w.WriteEndArray();
            w.WriteBase64String("wave", recording.Wave);
        }
        pane.Post(_writer.End());
    }
}
