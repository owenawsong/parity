#if PAIRSIDE_LIVE
using System.Text.Json;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private readonly PulseHistory _pulse = new(Path.Combine(AppIdentity.Profile, "Pulse"));
    private static readonly JsonSerializerOptions PulseJson = new(JsonSerializerDefaults.Web);
    private AapSession? _pulseSensor;
    private Action<long, byte>? _pulseHandler;
    private ulong _pulseWatch;
    private int _pulseRevision;
    private long _pulseGeneration;
    private Task _pulseStop = Task.CompletedTask;
    private bool _pulseStarting;
    private bool _pulseStartedAutomatically;
    // A stopped or failed recording must not restart on the next telemetry update.
    private AapSession? _pulseWearEpisode;
    private string? _pulseNotice;
    private ulong _pulseNoticeDevice;
    private PulseFeed? _pulseFeed;
    private bool _pulseFeedUnavailable;

    private void StartPulseFeed(string token)
    {
        var feed = new PulseFeed(token);
        try
        {
            feed.Start();
            _pulseFeed = feed;
            _pulseFeedUnavailable = false;
            if (_pulse.Live()?.Points.LastOrDefault() is { } last) feed.Publish(last.Time, last.Bpm);
        }
        catch (System.Net.HttpListenerException)
        {
            feed.Dispose();
            _pulseFeedUnavailable = true;
        }
    }

    private void ConfigurePulseFeed(bool enable)
    {
        if (!enable)
        {
            _pulseFeed?.Dispose();
            _pulseFeed = null;
            _pulseFeedUnavailable = false;
            SavePrefs(_prefs with { PulseFeedToken = null });
        }
        else if (_pulseFeed is null)
        {
            string token = _prefs.PulseFeedToken ?? PulseFeed.NewToken();
            StartPulseFeed(token);
            if (_pulseFeed is not null) SavePrefs(_prefs with { PulseFeedToken = token });
        }
        SendPulseFeed();
    }

    private void RotatePulseFeed()
    {
        if (_pulseFeed is null) return;
        string token = PulseFeed.NewToken();
        _pulseFeed.Rotate(token);
        SavePrefs(_prefs with { PulseFeedToken = token });
        SendPulseFeed();
    }

    private void SendPulseFeed() => PulseMessage(new { type = "pulseFeed", overlay = _pulseFeed?.OverlayUrl,
        data = _pulseFeed?.DataUrl, unavailable = _pulseFeedUnavailable });

    private void PulseMessage(object message) => PostMain(JsonSerializer.Serialize(message, PulseJson));

    private void SendLivePulse()
    {
        if (_pulseWatch == 0 || !MainLive || !_mainInitSent || _mainCompact) return;
        bool recording = _pulse.Address == _pulseWatch;
        int seconds = PulseHistory.ValidChartWindow(_prefs.PulseWindowSeconds) ? _prefs.PulseWindowSeconds : 60;
        PulseMessage(new { type = "pulseLive", id = Mac.Format(_pulseWatch), recording,
            automatic = _settings.Get(_pulseWatch).AutomaticPulse,
            windowSeconds = seconds, sessionScale = _prefs.PulseSessionScale,
            trace = recording ? _pulse.Live(seconds) : null, notice = _pulseNoticeDevice == _pulseWatch ? _pulseNotice : null });
    }

    private bool CanWatchPulse(ulong address) => address != 0 && _devices.Exists(d => d.Address == address);
    private void WatchPulse(ulong address)
    {
        _pulseWatch = CanWatchPulse(address) ? address : 0;
        _pulseRevision++;
        SendLivePulse();
        if (_pulseWatch != 0) RefreshPulseLibrary();
    }

    private async void RefreshPulseLibrary()
    {
        ulong address = _pulseWatch;
        if (address == 0) return;
        int revision = ++_pulseRevision;
        List<PulseHistory.Entry> sessions = [];
        bool failed = false;
        long? excluded = _pulse.Address == address ? _pulse.CurrentKey : null;
        try
        {
            sessions = await Task.Run(() => _pulse.List(address, excluded));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
        if (_pulseWatch == address && revision == _pulseRevision && MainLive)
        {
            PulseMessage(new { type = "pulseLibrary", id = Mac.Format(address), sessions, failed });
        }
    }

    private async void ReadPulse(ulong address, long key, int request)
    {
        if (address != _pulseWatch || !CanWatchPulse(address) || _pulse.CurrentKey == key) return;
        PulseHistory.Trace? trace = null;
        try { trace = await Task.Run(() => _pulse.Read(address, key)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (_pulseWatch == address && MainLive)
            PulseMessage(new { type = "pulseDetail", id = Mac.Format(address), key, trace, request });
    }

    private async void ReadPulseWindow(ulong address, long key, long from, long to, int request)
    {
        if (address != _pulseWatch || _pulse.CurrentKey == key) return;
        IReadOnlyList<PulseHistory.Point>? points = null;
        try { points = await Task.Run(() => _pulse.ReadWindow(address, key, from, to)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (_pulseWatch == address && MainLive)
            PulseMessage(new { type = "pulseWindow", id = Mac.Format(address), key, points, request });
    }

    private void DeletePulse(ulong address, long key)
    {
        if (address != _pulseWatch || !CanWatchPulse(address)) return;
        try { _pulse.Delete(address, key); SetPulseNotice(address, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetPulseNotice(address, "deleteFailed"); }
        RefreshPulseLibrary();
        SendLivePulse();
    }

    private void SetPulseNotice(ulong address, string? notice)
    {
        _pulseNoticeDevice = address;
        _pulseNotice = notice;
    }

    private void SetAutomaticPulse(ulong address, bool enable)
    {
        if (_pulseWatch != address || !CanWatchPulse(address)) return;
        HostSettings saved = _settings.Get(address);
        if (saved.AutomaticPulse == enable) { SendLivePulse(); return; }
        try
        {
            _settings.Set(address, saved with { AutomaticPulse = enable });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { _settings.Set(address, saved); }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { }
            SetPulseNotice(address, "preferenceFailed");
            SendLivePulse();
            return;
        }
        SetPulseNotice(address, null);
        if (!enable && _pulseStartedAutomatically && _pulseSensor?.Address == address) EndPulse(null);
        if (_pulseWearEpisode?.Address == address) _pulseWearEpisode = null;
        CheckPulseSensor(_connections.Session);
        SendLivePulse();
    }

    private async void StartPulse(ulong address, bool automatic = false)
    {
        if (_exiting || _pulseSensor != null || (!automatic && (_pulseWatch != address || !MainLive)) ||
            _connections.Session is not { State.Status: SessionStatus.Ready, State.HeartRateSupported: true } sensor ||
            sensor.Address != address)
        {
            SetPulseNotice(address, "unavailable");
            SendLivePulse();
            return;
        }
        long generation = ++_pulseGeneration;
        try
        {
            _pulse.Start(address, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _pulseSensor = sensor;
            _pulseStartedAutomatically = automatic;
            _pulseStarting = true;
            _pulseHandler = (time, bpm) => _ui.Post(_ =>
            {
                if (_pulseGeneration == generation && !_pulseStarting) AcceptPulse(sensor, time, bpm);
            }, null);
            SetPulseNotice(address, null);
            SendLivePulse();
            await _pulseStop;
            if (_pulseGeneration != generation) return;
            sensor.HeartRateSample += _pulseHandler;
            bool started = await sensor.SetHeartRateAsync(true);
            if (_pulseGeneration != generation) return;
            _pulseStarting = false;
            if (!started) EndPulse("noSignal");
            else CheckPulseSensor(_connections.Session);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            if (_pulseGeneration != generation) return;
            if (_pulseSensor == sensor) EndPulse("unavailable");
            else { SetPulseNotice(address, "unavailable"); SendLivePulse(); }
        }
    }

    private void AcceptPulse(AapSession sensor, long time, byte bpm)
    {
        if (_pulseSensor != sensor || _exiting) return;
        try { if (!_pulse.Add(time, bpm)) return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            EndPulse("saveFailed");
            return;
        }
        _pulseFeed?.Publish(time, bpm);
        SendLivePulse();
    }

    private void CheckPulseSensor(AapSession? session)
    {
        if (_exiting) return;
        bool ready = session?.State is { Status: SessionStatus.Ready, HeartRateSupported: true };
        bool wearing = ready && (session!.State.PrimaryEar == EarStatus.InEar || session.State.SecondaryEar == EarStatus.InEar);
        if (_pulseSensor is { } sensor)
        {
            if (session != sensor || !ready) EndPulse("disconnected");
            else if (_pulseStartedAutomatically && !wearing) EndPulse(null);
            else if (!_pulseStarting && sensor.State.HeartRate is (HeartRateStatus.Failed or HeartRateStatus.NoReading))
                EndPulse("noSignal");
        }
        if (!wearing) { _pulseWearEpisode = null; return; }
        if (_pulseWearEpisode == session || !_settings.Get(session!.Address).AutomaticPulse) return;
        _pulseWearEpisode = session;
        if (_pulseSensor == null) StartPulse(session.Address, automatic: true);
    }

    private void EndPulse(string? reason)
    {
        if (_pulseSensor is not { } sensor) return;
        _pulseGeneration++;
        _pulseStarting = false;
        _pulseStartedAutomatically = false;
        _pulseSensor = null;
        _pulseFeed?.Clear();
        sensor.HeartRateSample -= _pulseHandler;
        _pulseHandler = null;
        try { _pulse.End(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { reason = "saveFailed"; }
        SetPulseNotice(sensor.Address, reason);
        SendLivePulse();
        RefreshPulseLibrary();
        if (_connections.Session == sensor && !_exiting) _pulseStop = StopPulseSensor(sensor);
    }

    private static async Task StopPulseSensor(AapSession sensor)
    {
        try { await sensor.SetHeartRateAsync(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
        {
            Log.Warn("pulse", "Sensor was unavailable while ending the session.");
        }
    }
}
#endif
