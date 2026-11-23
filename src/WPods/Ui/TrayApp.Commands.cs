using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using WPods.Audio;
using WPods.Core;
using WPods.Nearby;
using Shortcut = WPods.Core.Shortcut;

namespace WPods.Ui;

// Commands from the main page and the nearby popup.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan LinkWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandsFreeDropTimeout = TimeSpan.FromSeconds(5);

    // User-initiated connects in progress, their cancel sources and cancel requests.
    private readonly HashSet<ulong> _connecting = [];
    private readonly Dictionary<ulong, CancellationTokenSource> _connectCancels = [];
    private readonly HashSet<ulong> _cancelled = [];
    private readonly Dictionary<ulong, int> _handsFreeApplying = [];
    // Link-up and attach waits of user connects, completed from core threads.
    private readonly Lock _linkLock = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _linkWaits = [];
    private readonly Dictionary<ulong, TaskCompletionSource> _attachWaits = [];
    // Last micStage message per slot, replayed when the hidden main page shows again.
    private readonly string?[] _micStages = new string?[2];

    private const string ReleasesUrl = "https://github.com/FPNDev/wpods/releases/";

    private void OnMainMessage(WebPane pane, PageMessage m)
    {
        if (_main is not { } main || main.Pane != pane)
        {
            return;
        }
        // Volume drags and compact layout transitions may send per-frame updates.
        if (m.Type is not ("previewMixerVolume" or "previewOnboardEq" or "fitCompactHeight"))
        {
            Log.Info("ui", "main command " + Describe(m));
        }
        ulong id = Mac.TryParse(m.Id, out ulong address) ? address : 0;
        switch (m.Type)
        {
#if PAIRSIDE_LIVE
            case "readConnectionCheck" when m.Request > 0:
                ReadConnectionCheck(m.Request);
                break;
            case "setAutomaticPulse" when id != 0 && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SetAutomaticPulse(id, m.Value.GetBoolean());
                break;
            case "watchPulse":
                WatchPulse(id);
                break;
            case "readPulseFeed":
                SendPulseFeed();
                break;
            case "setPulseFeed" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                ConfigurePulseFeed(m.Value.GetBoolean());
                break;
            case "rotatePulseFeed":
                RotatePulseFeed();
                break;
            case "watchBatteryHistory":
                WatchBatteryHistory(id, m.Name);
                break;
            case "setBatteryHistory" when id != 0 && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SetBatteryHistory(id, m.Value.GetBoolean());
                break;
            case "setChargeAlert" when m.Value.ValueKind == JsonValueKind.Number && m.Value.TryGetInt32(out int chargeTarget):
                SetChargeAlert(id, m.Name, chargeTarget);
                break;
            case "startPulse" when id != 0:
                StartPulse(id);
                break;
            case "watchAppleHeadphoneHistory" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                WatchAppleHistory(m.Value.GetBoolean());
                break;
            case "importAppleHeadphoneHistory":
                ImportAppleHistory();
                break;
            case "cancelAppleHeadphoneImport":
                _appleImport?.Cancel();
                break;
            case "clearAppleHeadphoneHistory":
                ClearAppleHistory();
                break;
            case "endPulse" when id != 0 && _pulse.Address == id:
                EndPulse(null);
                break;
            case "readPulse" when id != 0 && m.PositionMs is > 0 && m.Request > 0:
                ReadPulse(id, m.PositionMs.Value, m.Request);
                break;
            case "readPulseWindow" when id != 0 && m.PositionMs is > 0 && m.Request > 0 &&
                m.WindowStartMs is long from && from > 0 && m.WindowEndMs is long to && to > from && to - from <= PulseHistory.MaxChartSeconds * 1000L:
                ReadPulseWindow(id, m.PositionMs.Value, from, to, m.Request);
                break;
            case "setPulseChart" when id != 0 && id == _pulseWatch:
                if (m.Name == "window" && m.Value.ValueKind == JsonValueKind.Number &&
                    m.Value.TryGetInt32(out int seconds) && PulseHistory.ValidChartWindow(seconds))
                    SavePrefs(_prefs with { PulseWindowSeconds = seconds });
                else if (m.Name == "scale" && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    SavePrefs(_prefs with { PulseSessionScale = m.Value.GetBoolean() });
                SendLivePulse();
                break;
            case "deletePulse" when id != 0 && m.PositionMs is > 0:
                DeletePulse(id, m.PositionMs.Value);
                break;
            case "usePairOutput" when id != 0:
                UsePairOutput(id);
                break;
            case "readWindowsSpatial" or "openWindowsSpatial" when id != 0 && m.Request > 0:
                RequestWindowsSpatial(id, m.Request, m.Type == "openWindowsSpatial");
                break;
            case "watchSpotifyRoom":
                WatchSpotifyRoom(id);
                break;
            case "testSpatialSpeakers" when id != 0 && _spotifySession?.Address == id && _spotifyRoomViewer == id:
                _spotify?.TestSpeakers();
                break;
            case "setSpatialPlayer" when SpatialApp.FromKey(m.Name) is { } player:
                SetSpatialPlayer(player);
                break;
            case "setSpeakerAngle" when m.Value.ValueKind == JsonValueKind.Number && m.Value.TryGetInt32(out int angle) && angle is 20 or 30 or 55:
                SavePrefs(_prefs with { SpatialSpeakerAngle = angle });
                _spotify?.SetSpeakerAngle(angle);
                SendSpotify();
                break;
            case "setSpatialLevel" when m.Name is "soft" or "matched" or "boost":
                SavePrefs(_prefs with { SpatialLevel = m.Name });
                _spotify?.SetLevel(m.Name);
                SendSpotify();
                break;
            case "spotifySpatialMode" when m.Name is "stereo" or "fixed" or "tracked":
                SetSpotifyMode(id, m.Name);
                break;
            case "spotifySpatialCenter" when id != 0 && _spotifySession?.Address == id && SpotifyOwnsMotion && _spotifyHasPose:
                _headMotionTest?.RecenterAudio();
                break;
            case "seekMedia" when m.Id is not null && m.TrackId is not null && m.PositionMs is >= 0:
                _nowPlaying?.Seek(m.Id, m.TrackId, m.PositionMs.Value);
                break;
            case "mediaCommand" when m.Id is not null && m.Name is "play" or "pause" or "previous" or "next":
                _nowPlaying?.Control(m.Id, m.Name);
                break;
#endif
            case "startSpatialMotion" when id != 0 && m.Request is > 0 and < SpotifyMotionRequest:
                StartHeadMotion(id, false, m.Request);
                break;
            case "stopSpatialMotion" when id != 0 && m.Request is > 0 and < SpotifyMotionRequest:
                StopSpatialMotion(id, m.Request);
                break;
            case "centerSpatialMotion" when id != 0 && m.Request is > 0 and < SpotifyMotionRequest &&
                _headMotionTest is { } motion && motion.SpatialRequest == m.Request && motion.Session.Address == id:
                motion.RecenterAudio();
                break;
            case "setGestureBinding" when m.Name is "nod" or "shake" && m.Value.ValueKind == JsonValueKind.String
                && m.Value.GetString() is { } action && UiPrefs.IsGestureAction(action):
                SetGestureBinding(m.Name, action);
                break;
            case "setGestureSensitivity" when m.Value.ValueKind == JsonValueKind.String
                && m.Value.GetString() is { } sensitivity && UiPrefs.IsGestureSensitivity(sensitivity):
                SetGestureSensitivity(sensitivity);
                break;
            case "startHeadMotion" when id != 0:
                StartHeadMotion(id, m.Name == "playback");
                break;
            case "stopHeadMotion" when _headMotionTest is { SpatialRequest: 0 }:
                StopHeadMotion();
                break;
            case "setOnboardEq" or "previewOnboardEq" when id != 0 && TryReadEqRequest(m.Value) is { } eq:
                QueueOnboardEq(new OnboardEqRequest(pane, id, m.Request, eq, m.Type == "setOnboardEq"));
                break;
            case "watchCaseFinder":
                _caseFinderDevice = id;
                UpdateCaseFinderWatch();
                break;
            case "watchMicSetup" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _micSetupRequested = m.Value.GetBoolean();
                _micWatchRequest = m.Request;
                UpdateMicSetupWatch();
                break;
            case "watchHeadphoneLevel" when id != 0 && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                WatchHeadphoneLevel(pane, id, m.Request, m.Value.GetBoolean());
                break;
            case "recordMicCheck":
                RecordMicCheck(pane, m.Id, m.Name, m.Request);
                break;
            case "useAirPodsForCalls" when m.Id is not null:
                UseAirPodsForCalls(m.Id);
                break;
            case "openMicrophonePrivacy":
                Launch("ms-settings:privacy-microphone");
                break;
            case "finishMicCheck":
                if (_micSetupWatch is { Check: not null } recordingWatch && recordingWatch.CheckRequest == m.Request)
                    recordingWatch.FinishSample = true;
                break;
            case "cancelMicCheck":
                if (_micSetupWatch is { } cancellingWatch && cancellingWatch.CheckRequest == m.Request)
                    cancellingWatch.StopCheck("idle");
                break;
            case "startListenMonitor" when m.Id is not null && m.Value.ValueKind == JsonValueKind.Number &&
                m.Value.TryGetInt32(out int monitorLevel):
                SetListenMonitor(m.Id, monitorLevel);
                break;
            case "setListenVolume" when m.Id is not null && m.Value.ValueKind == JsonValueKind.Number &&
                m.Value.TryGetInt32(out int listenLevel) && _micSetupWatch is not null && _connections.Session is { } listeningSession:
                _mixer.SetMonitorVolume(listeningSession.Address, m.Id, listenLevel);
                break;
            case "stopListenMonitor":
                SetListenMonitor(null, 0);
                break;
            case "openTransparencyGuide":
                Launch("https://support.apple.com/guide/airpods/dev966f5f818/web");
                break;
            case "openSoundSettings":
                Launch("ms-settings:sound");
                break;
            case "ready":
                if (_mainShown)
                {
                    SendMainInit();
                }
                break;
            case "connect" when id != 0:
                Connect(id);
                break;
            case "cancel" when id != 0:
                Cancel(id);
                break;
            case "disconnect" when id != 0:
                Disconnect(id, reconnect: false);
                break;
            case "reconnect" when id != 0:
                Disconnect(id, reconnect: true);
                break;
            case "setListeningMode" when MessageWriter.TryParse(m.Mode, out ListeningMode mode):
                if (m.Request > 0 && id != 0)
                {
                    SetPageListeningMode(pane, id, m.Request, mode);
                }
                else
                {
                    RunCommand((s, ct) => s.SetListeningModeAsync(mode, ct), Dirty.ListeningMode);
                }
                break;
            case "setToggle" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                Toggle(m.Name, m.Value.GetBoolean());
                break;
            case "setHeartRate" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                RunCommand((s, _) => new ValueTask<bool>(s.SetHeartRateAsync(on)), Dirty.HeartRate);
                break;
            }
            case "setHearingAid" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                RunCommand((s, ct) => new ValueTask<bool>(s.SetHearingAidAsync(on, ct)), Dirty.Settings);
                break;
            }
            case "setMediaAssist" when m.Name is "calls" or "media" && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool on = m.Value.GetBoolean();
                bool calls = m.Name == "calls";
                RunCommand((s, ct) => new ValueTask<bool>(calls ? s.SetMediaAssistCallsAsync(on, ct) : s.SetMediaAssistMediaAsync(on, ct)), Dirty.Settings);
                break;
            }
            case "setMicSide" when MessageWriter.TryParse(m.Side, out MicSide side):
                RunCommand((s, ct) => s.SetMicSideAsync(side, ct), Dirty.Settings);
                break;
            case "setOptimizedCharging" when id != 0 && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                RunCommand((s, ct) => s.Address == id ? s.SetOptimizedChargingAsync(m.Value.GetBoolean(), ct) : ValueTask.FromResult(false), Dirty.Settings);
                break;
            case "setCallMutePresses" when id != 0 && m.Value.ValueKind == JsonValueKind.Number &&
                m.Value.TryGetByte(out byte presses) && presses is 1 or 2:
                RunCommand((s, ct) => s.Address == id ? s.SetCallMutePressesAsync(presses, ct) : ValueTask.FromResult(false), Dirty.Settings);
                break;
            case "setPressSpeed" when m.Value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(m.Value.GetString(), out PressSpeed speed):
                RunCommand((s, ct) => s.SetPressSpeedAsync(speed, ct), Dirty.Settings);
                break;
            case "setHoldDuration" when m.Value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(m.Value.GetString(), out HoldDuration hold):
                RunCommand((s, ct) => s.SetHoldDurationAsync(hold, ct), Dirty.Settings);
                break;
            case "setToneVolume" when m.Value.ValueKind == JsonValueKind.Number:
            {
                // The AirPods reject tone volumes below 15%.
                byte volume = (byte)Math.Clamp(Math.Round(m.Value.GetDouble()), 15, 100);
                RunCommand((s, ct) => s.SetToneVolumeAsync(volume, ct), Dirty.Settings);
                break;
            }
            case "setAdaptiveStrength" when id != 0 && m.Request > 0
                && m.Value.ValueKind == JsonValueKind.Number && m.Value.TryGetByte(out byte strength) && strength <= 100:
                QueueAdaptiveStrength(new(pane, id, strength, m.Request));
                break;
            case "setHostSetting" when id != 0:
                SetHostSetting(id, m.Name, m.Value);
                break;
            case "setStemModes" when id != 0 && m.Modes is not null:
                SetStemModes(id, m.Modes);
                break;
            case "setMicModes" when MicConfig.TryParseModes(m.Modes, out HashSet<MicMode> modes):
                SaveMicConfig(_settings.Mic with { Modes = modes });
                break;
            case "setMicModel" when MessageWriter.TryParse(m.Mode, out MicMode micMode) && m.Name is not null && MicCatalog.FindModel(micMode, m.Name) is not null:
            {
                MicConfig config = _settings.Mic;
                SaveMicConfig(config with { Models = new Dictionary<MicMode, string>(config.Models) { [micMode] = m.Name } });
                break;
            }
            case "setEnhancement" when m.Value.ValueKind == JsonValueKind.Null
                || (m.Value.ValueKind == JsonValueKind.String && MicCatalog.FindEnhancement(m.Value.GetString()!) is not null):
                SaveMicConfig(_settings.Mic with { Enhancement = m.Value.GetString() });
                break;
            case "setMixerSource" when m.Id is not null && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _mixer.SetPicked(m.Id, m.Value.GetBoolean());
                SendMixer();
                break;
            case "setMixerVolume" or "previewMixerVolume" when m.Id is not null && m.Value.ValueKind == JsonValueKind.Number
                && m.Value.TryGetInt32(out int percent) && percent is >= 0 and <= 100:
                _mixer.SetVolume(m.Id, percent, save: m.Type == "setMixerVolume");
                break;
            case "setMixerEnabled" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _mixer.SetEnabled(m.Value.GetBoolean());
                SendMixer();
                break;
            case "setHotkey" when Hotkeys.IsAction(m.Name) && TryParseShortcut(m.Value, out Shortcut? shortcut):
                SetHotkey(m.Name!, shortcut);
                break;
            case "recordHotkey" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                bool recording = m.Value.GetBoolean();
                _hotkeys.Pause(recording);
                if (!recording)
                {
                    // Registering again tells which hotkeys another app took meanwhile.
                    SendHotkeys();
                }
                break;
            }
            case "setHotkeySounds" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SaveHotkeys(_settings.Hotkeys with { Sounds = m.Value.GetBoolean() });
                SendHotkeys();
                break;
            case "setMixerFilter" when m.Name is "disconnected" or "disabled" && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
            {
                // The page sends "show"; the prefs store "hide" so a missing value shows everything.
                bool hide = !m.Value.GetBoolean();
                SavePrefs(m.Name == "disconnected" ? _prefs with { MixerHideDisconnected = hide } : _prefs with { MixerHideDisabled = hide });
                SendMixer();
                break;
            }
            case "setNearbyAutoConnect" when AppIdentity.IsPairside && id != 0 &&
                m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False && _devices.Exists(d => d.Address == id):
            {
                var enabled = _prefs.AutoConnectDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (m.Value.GetBoolean()) enabled.Add(Mac.Format(id));
                else enabled.Remove(Mac.Format(id));
                SavePrefs(_prefs with { AutoConnectDevices = enabled.ToArray() });
                SendLayoutPrefs();
                break;
            }
            case "setSidebarOrder" when AppIdentity.IsPairside && m.Ids is { Length: 5 } pages &&
                pages.Distinct().Count() == pages.Length && pages.All(p => p is "audioMotion" or "spatialAudio" or "pulse" or "controls" or "micSetup"):
                SavePrefs(_prefs with { SidebarOrder = pages });
                SendLayoutPrefs();
                break;
            case "setPopupSeparateBatteries" when AppIdentity.IsPairside && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SavePrefs(_prefs with { PopupSeparateBatteries = m.Value.GetBoolean() });
                SendLayoutPrefs();
                SendPrefsToPopup();
                break;
            case "restoreWindowSize" when AppIdentity.IsPairside:
                RestoreWindowSize();
                break;
            case "setReduceMotion" when AppIdentity.IsPairside && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                bool reduceMotion = m.Value.GetBoolean();
                SavePrefs(_prefs with { ReduceMotion = reduceMotion });
                _main?.Pane.SetReducedMotion(reduceMotion);
                _popup?.Pane.SetReducedMotion(reduceMotion);
                _osd?.Pane.SetReducedMotion(reduceMotion);
                SendLayoutPrefs();
                break;
            case "setTheme" when m.Theme is "light" or "dark" or "auto":
                SavePrefs(_prefs with { Theme = m.Theme });
                ApplyTheme();
                SendPrefsToPopup();
                break;
            case "setAccent" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                SavePrefs(_prefs with { UseAccent = m.Value.GetBoolean() });
                SendPrefsToPopup();
                break;
            case "setStartup" when m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                if (!SetStartup(m.Value.GetBoolean()))
                {
                    SendStartupPref();
                }
                break;
            case "setLanguage" when m.Code is not null:
                SetLanguage(m.Code);
                break;
            case "openBluetoothSettings":
                Launch("ms-settings:bluetooth");
                break;
            case "retryBluetooth":
                _service.Retry();
                break;
            case "setAutoUpdate" when !AppIdentity.IsPairside && m.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                _service.SetAutoUpdate(m.Value.GetBoolean());
                break;
            case "checkUpdate" when !AppIdentity.IsPairside:
                _service.CheckUpdate();
                break;
            case "updateNow" when !AppIdentity.IsPairside:
                _service.UpdateNow();
                break;
            case "dismissUpdate" when _service.Update.Available is { Length: > 0 } available:
                // Hidden for this version only; a newer version shows the note again.
                SavePrefs(_prefs with { DismissedUpdate = available });
                SendUpdate();
                break;
            case "openReleaseNotes" when _service.Update.NotesUrl.StartsWith(ReleasesUrl, StringComparison.OrdinalIgnoreCase):
                Launch(_service.Update.NotesUrl);
                break;
            case "openAddDevice":
                // Windows 11 "Add a device" dialog.
                Launch("ms-settings-connectabledevices:devicediscovery");
                break;
            case "openLegal" when m.Name is "license" or "notices":
                OpenLegal(m.Name == "license" ? "LICENSE.txt" : "THIRD-PARTY-NOTICES.txt");
                break;
            case "getStarted":
                SavePrefs(_prefs with { FirstRunDone = true });
                // The welcome page fitted the window to itself; the app goes back to the user's height.
                main.SetHeightDip(_prefs.WindowHeight);
                break;
            case "restartWindows":
                RestartWindows();
                break;
            case "minimize":
                main.WindowState = FormWindowState.Minimized;
                break;
            case "close":
                HideMain();
                break;
            case "openFullWindow" when _mainCompact:
                ShowMain();
                break;
            case "openCompactWindow" when AppIdentity.IsPairside && _mainShown && !_mainCompact:
                ShowMain(compact: true);
                break;
            case "fitCompactHeight" when _mainShown && _mainCompact && m.Height > 0:
            {
                _compactHeightDip = Math.Clamp(m.Height + 2 * MainWindow.ResizeStripDip, 88, 640);
                Rectangle bounds = CompactBounds();
                if (bounds != main.Bounds)
                {
                    main.Bounds = bounds;
                }
                break;
            }
            case "beginDrag":
                main.BeginDrag();
                break;
            case "fitHeight" when !_prefs.FirstRunDone && m.Height > 0:
                main.FitContentHeight(m.Height);
                break;
        }
    }

    private void OnPopupMessage(WebPane pane, PageMessage m)
    {
        if (_popup?.Pane != pane)
        {
            return;
        }
        Log.Info("ui", "popup command " + Describe(m));
        switch (m.Type)
        {
            case "ready":
                SendPopupInit();
                break;
            case "popupConnect" when _popupShown && Mac.TryParse(m.Id, out ulong address):
                _ = _nearby.ConnectAsync(address);
                break;
            case "popupCancel" when _popupShown:
                _nearby.CancelConnect();
                break;
            case "popupDismiss" when _popupShown && m.Ids is not null:
                foreach (string id in m.Ids)
                {
                    if (Mac.TryParse(id, out ulong address))
                    {
                        _nearby.Dismiss(address);
                    }
                }
                break;
            case "popupSize":
                OnPopupSize(m.Height);
                break;
        }
    }

    // Command type plus its set fields, for the log (none are secret).
    private static string Describe(PageMessage m)
    {
        var text = new StringBuilder(m.Type);
        AppendField(text, "id", m.Id);
        AppendField(text, "mode", m.Mode);
        AppendField(text, "name", m.Name);
        AppendField(text, "side", m.Side);
        AppendField(text, "theme", m.Theme);
        AppendField(text, "code", m.Code);
        if (m.Value.ValueKind != JsonValueKind.Undefined)
        {
            AppendField(text, "value", m.Value.GetRawText());
        }
        if (m.Ids is not null)
        {
            AppendField(text, "ids", string.Join(',', m.Ids));
        }
        if (m.Height != 0)
        {
            AppendField(text, "height", m.Height.ToString(CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private static void AppendField(StringBuilder text, string name, string? value)
    {
        if (value is not null)
        {
            text.Append(' ').Append(name).Append('=').Append(value);
        }
    }

    // ---- Connect / disconnect (only user clicks may page the AirPods) ----

    private async void Connect(ulong address)
    {
        if (await ConnectCoreAsync(address) is { } failure)
        {
            SendToast(failure, address);
        }
    }

    private async Task<string?> ConnectCoreAsync(ulong address)
    {
        if (!_connecting.Add(address))
        {
            Log.Info("ui", $"connect {Mac.Format(address)}: already connecting");
            return null;
        }
        _cancelled.Remove(address);
        SendDevices();
        LogConn();
        string? failure = null;
        try
        {
            // Right after a Disconnect, Windows may still be dropping the link; joining that link would end
            // disconnected, so wait for the disconnect to finish first.
            Task pending = _connections.DisconnectFinished(address);
            if (!pending.IsCompleted)
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: waiting for the disconnect still under way");
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch (TimeoutException)
                {
                    Log.Info("ui", $"connect {Mac.Format(address)}: the disconnect did not end within 8 s; connecting anyway");
                }
            }
            // Cancel can arrive while the preceding disconnect is still finishing.
            if (_cancelled.Remove(address)) return null;
            if (_offerByAddress.TryGetValue(address, out NearbyOffer? offer) && offer.State is OfferState.Offer or OfferState.Failed)
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: through the nearby offer ({offer.State})");
                await _nearby.ConnectAsync(address);
                failure = _connections.Session?.Address == address ? null : "connectFailed";
            }
            else if (Find(address) is not { LinkUp: true } && Native.GetInterfaces(Native.RadioInterface).Count == 0)
            {
                failure = "bluetoothOff";
            }
            else if (!await ConnectAudioAsync(address))
            {
                failure = "connectFailed";
            }
        }
        finally
        {
            _connecting.Remove(address);
            SendDevices();
        }
        bool cancelled = _cancelled.Remove(address);
        Log.Info("ui", $"connect {Mac.Format(address)}: done, failure={failure ?? "none"}, cancelled={cancelled}, session={(_connections.Session is { } s ? Mac.Format(s.Address) : "none")}");
        LogConn();
        return cancelled ? null : failure;
    }

    /// <summary>
    /// Brings the link up if needed, then attaches, within <see cref="LinkWait"/>. An attach by the connection
    /// manager itself also counts; false on timeout, attach failure or Cancel.
    /// </summary>
    private async Task<bool> ConnectAudioAsync(ulong address)
    {
        using var cancel = new CancellationTokenSource(LinkWait);
        var link = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectCancels[address] = cancel;
        lock (_linkLock)
        {
            _linkWaits[address] = link;
            _attachWaits[address] = attached;
        }
        try
        {
            if (Find(address) is not { LinkUp: true })
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: link down, asking Windows to connect audio");
                int hr = await Task.Run(() => NearbyNative.wpn_bt_connect_audio(address), cancel.Token);
                Log.Info("ui", $"connect {Mac.Format(address)}: connect audio hr=0x{hr:X8}, waiting for link");
                await link.Task.WaitAsync(cancel.Token);
                Log.Info("ui", $"connect {Mac.Format(address)}: link up");
            }
            else
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: link already up");
            }
            // Off the UI thread: the attach waits up to 10 s for the service's answer.
            if (_connections.Session?.Address == address || await Task.Run(() => _connections.ConnectAsync(address, () => !cancel.IsCancellationRequested)))
            {
                Log.Info("ui", $"connect {Mac.Format(address)}: attached");
                return true;
            }
            // The connection manager may still be attaching this pair on its own after the link-up event.
            Log.Info("ui", $"connect {Mac.Format(address)}: ConnectAsync false, waiting for an attach until the 10 s deadline");
            await attached.Task.WaitAsync(cancel.Token);
            Log.Info("ui", $"connect {Mac.Format(address)}: attached by the connection manager");
            return true;
        }
        catch (OperationCanceledException)
        {
            bool attachedNow = _connections.Session?.Address == address;
            Log.Info("ui", $"connect {Mac.Format(address)}: timed out or cancelled, attached={attachedNow}, linkUp={Find(address)?.LinkUp}");
            return attachedNow;
        }
        finally
        {
            _connectCancels.Remove(address);
            lock (_linkLock)
            {
                _linkWaits.Remove(address);
                _attachWaits.Remove(address);
            }
        }
    }

    private void Cancel(ulong address)
    {
        if (!_connecting.Contains(address))
        {
            return;
        }
        Log.Info("ui", $"connect {Mac.Format(address)}: cancel requested");
        _cancelled.Add(address);
        if (_connectCancels.TryGetValue(address, out CancellationTokenSource? cancel))
        {
            cancel.Cancel();
        }
        else
        {
            _nearby.CancelConnect(address);
        }
    }

    // The core detaches at once, so "notConnected" shows immediately. A failed disconnect means the pair is attached
    // again: toast and resend the real devices and state.
    private async void Disconnect(ulong address, bool reconnect)
    {
        if (!await DisconnectCoreAsync(address))
        {
            SendToast("disconnectFailed", address);
        }
        if (reconnect)
        {
            Connect(address);
        }
    }

    private async Task<bool> DisconnectCoreAsync(ulong address)
    {
        if (_connections.Session?.Address != address) return true;
        await _mic.StopStreamAsync();
        if (_connections.Session?.Address != address) return true;
        bool ok = await _connections.DisconnectAsync(address);
        Log.Info("ui", $"disconnect {Mac.Format(address)}: {ok}");
        if (!ok) SendRealState();
        return ok;
    }

    private KnownDevice? Find(ulong address) => _connections.GetDevices().Find(d => d.Address == address);

    // ---- Device commands: success shows through the core's Changed event; failure sends the current value back ----

    // The core stores the cycle and writes it on every attach. Always answers with the stored value; a failed write
    // also toasts noResponse.
    private async void SetStemModes(ulong address, string[] names)
    {
        if (_connections.Session is { } session && session.Address == address && MessageWriter.TryParseStemModes(names, out StemModes modes))
        {
            bool ok = false;
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                ok = await session.SetStemModesAsync(modes, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // No reply in time.
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                Log.Warn("ui", $"setStemModes {Mac.Format(address)} [{string.Join(',', names)}] rejected: {e.Message}");
                ok = true;
            }
            if (!ok)
            {
                SendToast("noResponse", address);
            }
        }
        _devices = _connections.GetDevices();
        SendDevices(force: true);
    }

    private void Toggle(string? name, bool on)
    {
        Func<AapSession, CancellationToken, ValueTask<bool>>? command = name switch
        {
            "earDetection" => (s, ct) => s.SetEarDetectionAsync(on, ct),
            "conversationAwareness" => (s, ct) => s.SetConversationAwarenessAsync(on, ct),
            "personalizedVolume" => (s, ct) => s.SetPersonalizedVolumeAsync(on, ct),
            "oneBudAnc" => (s, ct) => s.SetOneBudAncAsync(on, ct),
            "allowOff" => (s, ct) => s.SetAllowOffAsync(on, ct),
            _ => null,
        };
        if (command is not null)
        {
            RunCommand(command, Dirty.Settings);
        }
    }

    private async void RunCommand(Func<AapSession, CancellationToken, ValueTask<bool>> command, Dirty field) =>
        await RunCommandAsync(command, field, toast: true);

    // False when not applied; the page then gets the current value back, and the noResponse toast when `toast`.
    private async Task<bool> RunCommandAsync(Func<AapSession, CancellationToken, ValueTask<bool>> command, Dirty field, bool toast)
    {
        AapSession? session = _connections.Session;
        bool ok = false;
        if (session is not null)
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                ok = await command(session, timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
            {
                // No reply in time, or unsupported by the device (not enrolled, no heart-rate sensor).
                Log.Warn("ui", $"device command failed: {e.GetType().Name} {e.Message}");
            }
        }
        if (ok)
        {
            return true;
        }
        Log.Warn("ui", $"device command not applied (session={(session is null ? "none" : Mac.Format(session.Address))})");
        if (_connections.Session is { } current && MainLive)
        {
            SendStateDeltas(field, current.State, force: true);
        }
        if (toast && session is not null && _connections.Session == session)
        {
            SendToast("noResponse", session.Address);
        }
        return false;
    }

    private sealed record PageModeRequest(WebPane Pane, AapSession? Session, ulong Address, int Request, ListeningMode Mode);
    private PageModeRequest? _queuedPageMode;
    private TaskCompletionSource? _pageModesFlushed;

    // Dispatch the first selection immediately. During a burst keep one queued selection and give the
    // previous write a bounded time to report, so the buds receive fewer intermediate modes.
    private async void SetPageListeningMode(WebPane pane, ulong address, int request, ListeningMode mode)
    {
        _queuedPageMode = new(pane, _connections.Session, address, request, mode);
        if (_pageModesFlushed is not null) return;
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pageModesFlushed = flushed;
        try
        {
            while (_queuedPageMode is { } waiting)
            {
                if (waiting.Session is { } previous && _connections.Session == previous && previous.Address == waiting.Address)
                    await previous.WaitForListeningModeDispatchAsync();
                // A newer selection can replace the queued one while waiting for the previous report.
                PageModeRequest next = _queuedPageMode!;
                _queuedPageMode = null;
                bool written = false;
                if (next.Pane == _main?.Pane && next.Session is { } session && session.Address == next.Address
                    && _connections.Session == session && session.State.Status == SessionStatus.Ready)
                {
                    using var timeout = new CancellationTokenSource(CommandTimeout);
                    try { written = await session.SetListeningModeAsync(next.Mode, timeout.Token); }
                    catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
                    {
                        Log.Warn("ui", $"listening mode write failed: {e.Message}");
                    }
                }
                if (_queuedPageMode is not null || _main?.Pane != next.Pane || !next.Pane.Ready) continue;
                if (!written && next.Session is not null && _connections.Session == next.Session && next.Session.Address == next.Address
                    && next.Session.State.ReportedListeningMode != next.Mode)
                    SendToast("noResponse", next.Address);
                ReplyPageListeningMode(next.Pane, next.Address, next.Request);
            }
        }
        finally
        {
            _pageModesFlushed = null;
            flushed.TrySetResult();
        }
    }

    private void ReplyPageListeningMode(WebPane pane, ulong address, int request)
    {
        var w = _writer.Begin("listeningMode");
        w.WriteString("id", Mac.Format(address));
        w.WriteNumber("request", request);
        if (_connections.ShownSession is { } current && current.Address == address)
        {
            MessageWriter.WriteListeningMode(w, current.State);
        }
        else
        {
            w.WriteNull("listeningMode");
        }
        pane.Post(_writer.End());
    }

    private sealed record AdaptiveRequest(WebPane Pane, ulong Address, byte Strength, int Request);
    private AdaptiveRequest? _pendingAdaptive;
    private bool _applyingAdaptive;

    // One write/readback at a time. Only the newest value waiting for the connection is worth sending.
    private async void QueueAdaptiveStrength(AdaptiveRequest request)
    {
        _pendingAdaptive = request;
        if (_applyingAdaptive)
        {
            return;
        }
        _applyingAdaptive = true;
        try
        {
            while (_pendingAdaptive is { } applying)
            {
                _pendingAdaptive = null;
                string status = await ApplyAdaptiveStrengthAsync(applying);
                if ((status is "confirmed" or "unconfirmed") && _pendingAdaptive is { } pending
                    && pending.Address == applying.Address && pending.Strength == applying.Strength)
                {
                    applying = pending;
                    _pendingAdaptive = null;
                }
                if (_pendingAdaptive?.Pane != applying.Pane)
                {
                    SendAdaptiveStrengthResult(applying, status);
                }
            }
        }
        finally
        {
            _applyingAdaptive = false;
        }
    }

    private async Task<string> ApplyAdaptiveStrengthAsync(AdaptiveRequest request)
    {
        string status = "failed";
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            // The UI can expose the balance control immediately after a mode tap. Wait for that
            // write to finish before checking the physical session's mode.
            if (_pageModesFlushed is { } switching)
                await switching.Task.WaitAsync(wait.Token);
            if (_main?.Pane == request.Pane && _connections.Session is { } session && session.Address == request.Address)
            {
                var (sent, confirmed) = await session.SetAdaptiveStrengthAsync(request.Strength, wait.Token);
                // Many firmwares only report the new balance on a later settings snapshot. Keep the
                // control session alive; reattaching here interrupted gestures and unrelated writes.
                status = confirmed ? "confirmed" : sent ? "unconfirmed" : "failed";
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn("aap", "adaptive filtering readback timed out");
        }
        return status;
    }

    private void SendAdaptiveStrengthResult(AdaptiveRequest request, string status)
    {
        if (_main?.Pane == request.Pane && request.Pane.Ready)
        {
            if (_connections.ShownSession is { } current && current.Address == request.Address && MainLive)
            {
                SendStateDeltas(Dirty.Settings, current.State, force: true);
            }
            var w = _writer.Begin("adaptiveStrengthResult");
            w.WriteString("id", Mac.Format(request.Address));
            w.WriteNumber("request", request.Request);
            w.WriteString("status", status);
            request.Pane.Post(_writer.End());
        }
    }

    // ---- Host settings and app prefs ----

    private async void SetHostSetting(ulong address, string? name, JsonElement value)
    {
        if (name == "earResumeSeconds" && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int seconds) && seconds is 0 or 15 or 60)
        {
            try { _settings.Set(address, _settings.Get(address) with { EarResumeSeconds = seconds }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn("earpause", $"Resume preference could not be saved: {e.Message}");
                SendToast("resumeSaveFailed", address);
            }
            SendHostSettings(address);
            return;
        }
        if (name == "sleepPause" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            await SetSleepPauseAsync(address, value.GetBoolean());
            return;
        }
        Func<HostSettings, HostSettings>? change = name switch
        {
            "disableHandsFree" when value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean() is var off => s => s with { DisableHandsFree = off },
            "lowLatency" when value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean() is var on => s => s with { LowLatency = on },
            "lowLatencyMic" when value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                _connections.Session is { } active && active.Address == address &&
                Models.HasStereoMic(active.State.ModelNumber ?? _settings.GetModel(address)?.Number) =>
                    s => s with { LowLatencyMic = value.GetBoolean() },
            // The pipeline loads the new effective mode's model (HostSettingsChanged).
            "micMode" when value.ValueKind == JsonValueKind.String && MessageWriter.TryParse(value.GetString(), out MicMode mode) => s => s with { MicMode = mode },
            "duckPercent" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int duck) && duck is >= 10 and <= 90 => s => s with { DuckPercent = duck },
            _ => null,
        };
        if (change is not null)
        {
            await ApplyHostSettingsAsync(address, change, toast: true);
        }
        SendHostSettings(address);
    }

    private async Task SetSleepPauseAsync(ulong address, bool enabled)
    {
        if (_connections.Session is { } session && session.Address == address &&
            (session.State.Capabilities & DeviceCapabilities.SleepDetection) != 0)
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            try
            {
                if (!enabled || await session.EnableSleepDetectionAsync(timeout.Token))
                {
                    if (!await ApplyHostSettingsAsync(address, s => s with { SleepPause = enabled }, toast: false))
                        SendToast("noResponse", address);
                }
                else
                {
                    SendToast("noResponse", address);
                }
            }
            catch (OperationCanceledException)
            {
                SendToast("noResponse", address);
            }
        }
        SendHostSettings(address);
    }

    private static OnboardEq? TryReadEqRequest(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !ReadBand(value, "low", out byte low) || !ReadBand(value, "mid", out byte mid) ||
            !ReadBand(value, "high", out byte high))
        {
            return null;
        }
        return new OnboardEq(enabled.GetBoolean(), low, mid, high);

        static bool ReadBand(JsonElement src, string key, out byte band)
        {
            band = 0;
            return src.TryGetProperty(key, out JsonElement item) && item.ValueKind == JsonValueKind.Number &&
                item.TryGetByte(out band) && band <= 100;
        }
    }

    private sealed record OnboardEqRequest(WebPane Pane, ulong Address, int Request, OnboardEq Eq, bool Commit);
    private OnboardEqRequest? _pendingOnboardEq;
    private bool _applyingOnboardEq;

    private async void QueueOnboardEq(OnboardEqRequest request)
    {
        _pendingOnboardEq = request;
        if (_applyingOnboardEq) return;
        _applyingOnboardEq = true;
        try
        {
            while (_pendingOnboardEq is { } next)
            {
                _pendingOnboardEq = null;
                await ApplyOnboardEqAsync(next);
            }
        }
        finally
        {
            _applyingOnboardEq = false;
        }
    }

    private async Task ApplyOnboardEqAsync(OnboardEqRequest request)
    {
        var (pane, address, sequence, eq, commit) = request;
        string status = "failed";
        if (_main?.Pane == pane && _connections.Session is { } session && session.Address == address)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(commit ? 6 : 1));
            try
            {
                if (!commit)
                {
                    await session.PreviewCustomEqAsync(eq, timeout.Token);
                    return;
                }
                var (sent, confirmed) = await session.SetCustomEqAsync(eq, timeout.Token);
                status = confirmed ? "confirmed" : sent ? "unconfirmed" : "failed";
                if (sent && !confirmed && _pendingOnboardEq is null && _headMotionTest is null && await _connections.ReadBackCustomEqAsync(session,
                        () => _mic.Status == MicStatus.NotActive && !_mic.HandsFreeInUse, timeout.Token) is { } reported)
                {
                    status = reported == eq ? "confirmed" : "different";
                }
            }
            catch (OperationCanceledException)
            {
                if (commit) Log.Info("aap", "onboard EQ readback timed out");
            }
        }
        if (!commit || _pendingOnboardEq is not null) return;
        Log.Info("aap", $"onboard EQ result: {status}");
        if (_main?.Pane != pane || !pane.Ready) return;
        if (MainLive && _connections.ShownSession is { } current && current.Address == address)
            SendStateDeltas(Dirty.Settings, current.State, force: true);
        var w = _writer.Begin("onboardEqResult");
        w.WriteString("id", Mac.Format(address));
        w.WriteNumber("request", sequence);
        w.WriteString("status", status);
        pane.Post(_writer.End());
    }

    /// <summary>
    /// Only the attached pair is editable, and Hands-Free one change at a time; false otherwise, or when Windows refused
    /// the change (the saved value goes back). <paramref name="change"/> applies to the settings current at the time of
    /// writing, so a change made meanwhile is kept. The caller sends the host settings to the page.
    /// </summary>
    private async Task<bool> ApplyHostSettingsAsync(ulong address, Func<HostSettings, HostSettings> change, bool toast)
    {
        HostSettings current = _settings.Get(address);
        HostSettings next = change(current);
        bool handsFree = next.DisableHandsFree != current.DisableHandsFree;
        if (_connections.Session?.Address != address || (handsFree && _handsFreeApplying.ContainsKey(address)))
        {
            return false;
        }
        if (handsFree)
        {
            SetHandsFreeApplying(address, true);
        }
        int lowLatencyFailures = Volatile.Read(ref _lowLatencyFailures);
        try
        {
            // Disabling Hands-Free while it is in use leaves the A2DP stream paused for good: drop it first.
            if (next.DisableHandsFree && !current.DisableHandsFree && _mic.HandsFreeInUse)
            {
                await DropHandsFreeAsync(address);
                current = _settings.Get(address);
                next = change(current);
            }
            await _connections.SetHostSettingsAsync(address, next);
            // The keep-alive raises Failed synchronously inside the call when Windows refuses the stream.
            if (next.LowLatency && !current.LowLatency && Volatile.Read(ref _lowLatencyFailures) != lowLatencyFailures)
            {
                await _connections.SetHostSettingsAsync(address, current);
                return false;
            }
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warn("ui", $"host setting change failed: {e.Message}");
            if (toast)
            {
                SendToast("handsFreeFailed", address);
            }
            await RevertHandsFreeAsync(address, current);
            return false;
        }
        finally
        {
            if (handsFree)
            {
                SetHandsFreeApplying(address, false);
            }
        }
    }

    // Counts Hands-Free changes in flight; the page shows a loader while any runs.
    private void SetHandsFreeApplying(ulong address, bool applying)
    {
        int count = _handsFreeApplying.GetValueOrDefault(address) + (applying ? 1 : -1);
        if (count > 0)
        {
            _handsFreeApplying[address] = count;
        }
        else
        {
            _handsFreeApplying.Remove(address);
        }
        if (count == (applying ? 1 : 0))
        {
            SendHostSettings(address);
        }
    }

    // Disconnects only Hands-Free (A2DP stays) and waits until it is no longer in use.
    private async Task DropHandsFreeAsync(ulong address)
    {
        int hr = await Task.Run(() => Native.wpn_bt_disconnect_handsfree(address));
        Log.Info("ui", $"hands-free {Mac.Format(address)}: in use, disconnect hands-free hr=0x{hr:X8}");
        if (hr < 0)
        {
            return;
        }
        try
        {
            await _mic.HandsFreeEndedAsync().WaitAsync(HandsFreeDropTimeout);
            Log.Info("ui", "hands-free: no longer in use");
        }
        catch (TimeoutException)
        {
            Log.Warn("ui", $"hands-free: still in use after {HandsFreeDropTimeout.TotalSeconds} s");
        }
    }

    // Under MicPipeline's lock; applied on the UI thread in the same order.
    private void OnMicStage(ulong address, MicSlot slot, string? id, StageLoad load, string? code) =>
        _ui.Post(_ => ApplyMicStage(address, slot, id, load, code), null);

    // A failed load keeps the previous stage running and toasts its code, except when dropped by a detach (address 0).
    private void ApplyMicStage(ulong address, MicSlot slot, string? id, StageLoad load, string? code)
    {
        Utf8JsonWriter w = _writer.Begin("micStage");
        w.WriteString("slot", MessageWriter.Camel(slot));
        w.WriteString("id", id);
        w.WriteString("state", MessageWriter.Camel(load));
        string json = _writer.End();
        _micStages[(int)slot] = json;
        PostMain(json);
        if (load == StageLoad.Failed && address != 0)
        {
            SendToast("micModelFailed", address, code);
        }
    }

    // The pipeline follows through MicConfigChanged.
    private void SaveMicConfig(MicConfig config)
    {
        try
        {
            _settings.SetMic(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next successful save writes it.
            Log.Warn("ui", $"mic config not saved: {e.Message}");
        }
        Utf8JsonWriter w = _writer.Begin("micConfig");
        MessageWriter.WriteMicConfig(w, "config", _settings.Mic);
        PostMain(_writer.End());
    }

    private static void OpenLegal(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, file);
        if (!File.Exists(path))
        {
            Log.Warn("ui", $"{path} missing");
            return;
        }
        Launch(path);
    }

    private async Task RevertHandsFreeAsync(ulong address, HostSettings previous)
    {
        try
        {
            await _connections.SetHostSettingsAsync(address, previous);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // Windows keeps its current state; the saved setting is already reverted.
            Log.Warn("ui", $"hands-free revert failed: {e.Message}");
        }
    }

    /// <summary>False when Windows refused the change.</summary>
    private static bool SetStartup(bool enabled)
    {
        try
        {
            StartupRegistration.SetEnabled(enabled);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or SecurityException)
        {
            Log.Warn("ui", $"startup registration not changed: {e.Message}");
            return false;
        }
    }

    private void WriteUpdate(Utf8JsonWriter w)
    {
        UpdateInfo u = AppIdentity.IsPairside ? UpdateInfo.Unknown with { Auto = false } : _service.Update;
        Version app = typeof(TrayApp).Assembly.GetName().Version ?? new Version(0, 0, 0);
        w.WriteStartObject("update");
        w.WriteString("version", u.Installed.Length > 0 ? u.Installed : app.ToString(3));
        w.WriteString("available", u.Available.Length > 0 ? u.Available : null);
        w.WriteString("status", MessageWriter.Camel(u.Status));
        w.WriteBoolean("checked", u.Checked);
        w.WriteBoolean("auto", u.Auto);
        w.WriteBoolean("managedExternally", AppIdentity.IsPairside);
        w.WriteString("dismissed", _prefs.DismissedUpdate);
        w.WriteEndObject();
    }

    private void SendAdapter()
    {
        if (_exiting)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("adapter");
        w.WriteBoolean("unsupported", _adapterUnsupported);
        PostMain(_writer.End());
    }

    private void SendRestart()
    {
        if (_exiting)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("restart");
        w.WriteBoolean("pending", _restartPending);
        PostMain(_writer.End());
    }

    private void SendUpdate()
    {
        if (_exiting)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("update");
        WriteUpdate(w);
        PostMain(_writer.End());
    }

    private void SendStartupPref()
    {
        Utf8JsonWriter w = _writer.Begin("prefs");
        w.WriteBoolean("startup", StartupRegistration.IsEnabled());
        PostMain(_writer.End());
    }

    // Tray "Start with Windows": the window's switch gets the new value.
    private void ToggleStartupFromTray()
    {
        SetStartup(!StartupRegistration.IsEnabled());
        SendStartupPref();
    }

    // "" = Same as Windows. Both pages relabel in place; the tray and notifications read labels on use.
    private void SetLanguage(string code)
    {
        string choice = code.Length == 0 ? "system" : code;
        if (choice != "system" && !_locales.Files.ContainsKey(choice))
        {
            return;
        }
        SavePrefs(_prefs with { Language = choice });
        _locale = _locales.Resolve(choice);
        Log.Info("ui", $"language {choice} -> {_locale.Code}");
        UpdateTray();
        Utf8JsonWriter w = _writer.Begin("labels");
        MessageWriter.WriteLocale(w, _locale);
        string json = _writer.End();
        PostMain(json);
        _popup?.Pane.Post(json);
        _osd?.Pane.Post(json);
    }

    private static void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            // No handler registered for the target.
            Log.Warn("ui", $"launch {target} failed: {e.Message}");
        }
    }

    // Confirmed on the restart page; a standard user may restart Windows without elevation.
    private static void RestartWindows()
    {
        Log.Info("ui", "restarting Windows: shutdown.exe /r /t 0");
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { CreateNoWindow = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            Log.Warn("ui", $"restart failed: {e.Message}");
        }
    }
}
