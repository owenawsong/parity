#if PAIRSIDE_LIVE
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Playback = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace WPods.Audio;

// UI-thread owned. Broker events only schedule work. A change during an async read
// invalidates its result before it can replace the current song or transport target.
internal sealed class NowPlayingWatch(SynchronizationContext ui, Action<string> publish) : IDisposable
{
    [Flags]
    private enum Change { None = 0, Playback = 1, Metadata = 2, Session = 4, Timeline = 8, ForceTimeline = 16 }
    private sealed record Media(string Id, string TrackId, string Title, string Artist, string App, string? SpatialSource, string? Artwork,
        bool Playing, bool CanPlayPause, bool CanNext, bool CanPrevious);

    private sealed record Track(string Title, string Artist, string Album, string Subtitle, int Number)
    {
        public static Track From(GlobalSystemMediaTransportControlsSessionMediaProperties? p) =>
            new(p?.Title?.Trim() ?? "", p?.Artist?.Trim() ?? "", p?.AlbumTitle ?? "", p?.Subtitle ?? "", p?.TrackNumber ?? 0);
    }

    private sealed record Timeline(long DurationMs, long PositionMs, long MinSeekMs, long MaxSeekMs, bool CanSeek, double Rate, long ObservedAtMs);
    private sealed record SeekRequest(string Id, string TrackId, long PositionMs);
    private Track? _track;
    private string _trackId = "";
    private string? _publishedTrack;
    private Timeline? _timeline;
    private SeekRequest? _pendingSeek;
    private bool _seeking;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _lifetime = new();
    private Manager? _manager;
    private Session? _session;
    private string _id = "";
    private string _app = "";
    private string _title = "";
    private string _artist = "";
    private string? _art;
    private Media? _last;
    private bool? _unavailable;
    private Change _pending;
    private int _queued;
    private bool _reading;
    private bool _retrying;
    private bool _controlling;
    private volatile bool _closed;

    public void Start() => Refresh(Change.Session | Change.Metadata);

    public void Replay()
    {
        if (_unavailable is bool unavailable)
        {
            Send(_last, unavailable, force: true);
            if (_last is not null) SendTimeline(_timeline, true);
        }
    }

    private void Send(Media? media, bool unavailable = false, bool force = false)
    {
        if (_closed || (!force && _last == media && _unavailable == unavailable))
            return;
        if (media is null) _publishedTrack = null;
        _last = media;
        _unavailable = unavailable;
        publish(JsonSerializer.Serialize(new { type = "nowPlaying", media, unavailable }, Json));
    }

    private void Schedule(Change change)
    {
        if (_closed || Interlocked.Or(ref _queued, (int)change) != 0) return;
        ui.Post(_ => Refresh((Change)Interlocked.Exchange(ref _queued, 0)), null);
    }
    private void OnCurrent(Manager sender, CurrentSessionChangedEventArgs args) => Schedule(Change.Session);
    private void OnSessions(Manager sender, SessionsChangedEventArgs args) => Schedule(Change.Session);
    private void OnPlayback(Session sender, PlaybackInfoChangedEventArgs args) => Schedule(Change.Playback);
    private void OnMetadata(Session sender, MediaPropertiesChangedEventArgs args) => Schedule(Change.Metadata);

    private void OnTimeline(Session sender, TimelinePropertiesChangedEventArgs args) => Schedule(Change.Timeline);

    private void Bind(Session? session)
    {
        if (_session is { } previous)
        {
            Unsubscribe(() => previous.MediaPropertiesChanged -= OnMetadata);
            Unsubscribe(() => previous.PlaybackInfoChanged -= OnPlayback);
            Unsubscribe(() => previous.TimelinePropertiesChanged -= OnTimeline);
        }
        _session = session;
        _track = null;
        _trackId = "";
        _publishedTrack = null;
        _timeline = null;
        _pendingSeek = null;
        _art = null;
        _title = _artist = _app = "";
        _id = "";
        if (session is null) return;
        _id = Guid.NewGuid().ToString("N");
        _app = DisplayName(session.SourceAppUserModelId);
        session.PlaybackInfoChanged += OnPlayback;
        session.MediaPropertiesChanged += OnMetadata;
        session.TimelinePropertiesChanged += OnTimeline;
    }

    private static string DisplayName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "";
        try
        {
            string? display = AppInfo.GetFromAppUserModelId(appId)?.DisplayInfo.DisplayName;
            if (!string.IsNullOrWhiteSpace(display)) return display;
        }
        catch (Exception) { /* Classic desktop players may have no AppInfo entry. */ }
        string name = Path.GetFileName(appId);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length <= 48 && !name.Contains('!') && !name.Contains('{') && !name.Contains('.') ? name : "";
    }

    private async void Refresh(Change change)
    {
        if (_closed) return;
        _pending |= change;
        if (_reading) return;
        _reading = true;
        try
        {
            while (_pending != Change.None && !_closed)
            {
                change = _pending;
                _pending = Change.None;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var ct = timeout.Token;
                if (_manager is null)
                {
                    var manager = await Manager.RequestAsync().AsTask(ct);
                    if (_closed) return;
                    _manager = manager ?? throw new InvalidOperationException("No media manager");
                    _manager.CurrentSessionChanged += OnCurrent;
                    _manager.SessionsChanged += OnSessions;
                    change |= Change.Session;
                }
                if ((change & Change.Session) != 0)
                {
                    var current = _manager.GetCurrentSession();
                    if (!SameSession(_session, current))
                    {
                        Bind(current);
                        change |= Change.Metadata;
                    }
                }
                if (_session is not { } session) { Send(null); continue; }
                if ((change & Change.Metadata) != 0)
                {
                    var props = await session.TryGetMediaPropertiesAsync().AsTask(ct);
                    if (_closed) return;
                    if ((_pending & (Change.Session | Change.Metadata)) != 0)
                    {
                        _pending |= Change.Metadata;
                        continue;
                    }
                    var track = Track.From(props);
                    if (track != _track)
                    {
                        _track = track;
                        _trackId = Guid.NewGuid().ToString("N");
                        _art = null;
                        _pendingSeek = null;
                    }
                    if (props?.Thumbnail is null) _art = null;
                    _title = track.Title;
                    _artist = track.Artist;
                    PublishSession(session);
                    if (props?.Thumbnail is { } thumbnail)
                        _art = await CoverAsync(thumbnail, ct);
                }
                if (_closed) return;
                if ((_pending & (Change.Session | Change.Metadata)) == 0) PublishSession(session, (change & Change.ForceTimeline) != 0);
            }
        }
        catch (Exception) when (_closed) { }
        catch (Exception)
        {
            // The broker can restart, or policy may deny access. Retry while the
            // dashboard is visible; never preserve playback commands for stale data.
            Detach();
            Send(null, unavailable: true);
            Retry();
        }
        finally { _reading = false; }
    }

    private void PublishSession(Session session, bool forceTimeline = false)
    {
        var info = session.GetPlaybackInfo();
        if (info?.PlaybackStatus == Playback.Changing && _last?.Id == _id) return;
        if (info?.PlaybackStatus is not (Playback.Playing or Playback.Paused) ||
            string.IsNullOrWhiteSpace(_title) && string.IsNullOrWhiteSpace(_artist))
        {
            Send(null);
            return;
        }
        bool playing = info.PlaybackStatus == Playback.Playing;
        var controls = info.Controls;
        Send(new Media(_id, _trackId, _title, _artist, _app, SpatialApp.FromMediaSession(session.SourceAppUserModelId), _art, playing,
            CanChangePlayback(controls, playing), controls.IsNextEnabled, controls.IsPreviousEnabled));
        SendTimeline(ReadTimeline(session, info), forceTimeline);
    }

    private void SendTimeline(Timeline? timeline, bool force)
    {
        if (_closed || _last?.TrackId != _trackId) return;
        if (!force && _publishedTrack == _trackId && _timeline == timeline) return;
        _publishedTrack = _trackId;
        _timeline = timeline;
        publish(JsonSerializer.Serialize(new { type = "mediaTimeline", id = _id, trackId = _trackId, timeline }, Json));
    }

    private static Timeline? ReadTimeline(Session session,
        GlobalSystemMediaTransportControlsSessionPlaybackInfo info)
    {
        try
        {
            var p = session.GetTimelineProperties();
            if (p is null || p.EndTime <= p.StartTime || p.EndTime == TimeSpan.MaxValue) return null;
            var length = (long)(p.EndTime.TotalMilliseconds - p.StartTime.TotalMilliseconds);
            if (length <= 0) return null;
            long Offset(TimeSpan time) => Math.Clamp((long)(time.TotalMilliseconds - p.StartTime.TotalMilliseconds), 0, length);
            var min = Offset(p.MinSeekTime);
            var max = Offset(p.MaxSeekTime);
            var rate = info.PlaybackStatus == Playback.Playing ? info.PlaybackRate ?? 1 : 0;
            if (!double.IsFinite(rate)) rate = 0;
            var stamp = p.LastUpdatedTime.ToUnixTimeMilliseconds();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (stamp <= 0 || stamp > now) stamp = now;
            return new(length, Offset(p.Position), min, max, info.Controls.IsPlaybackPositionEnabled && max > min, rate, stamp);
        }
        catch (Exception) { return null; } // A live or protected stream can omit its timeline.
    }

    // Coalesce scrubs while an app handles its previous seek. Re-read identity
    // and transport bounds directly before dispatching against that session.
    public async void Seek(string id, string trackId, long positionMs)
    {
        if (_closed || _last?.Id != id || _trackId != trackId) return;
        _pendingSeek = new(id, trackId, positionMs);
        if (_seeking) return;
        _seeking = true;
        try
        {
            while (_pendingSeek is { } request && !_closed)
            {
                _pendingSeek = null;
                if (_session is not { } current || request.Id != _id || request.TrackId != _trackId) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var props = await current.TryGetMediaPropertiesAsync().AsTask(timeout.Token);
                    if (_closed || _pendingSeek is not null || request.TrackId != _trackId ||
                        !SameSession(current, _manager?.GetCurrentSession()) || Track.From(props) != _track) continue;
                    var info = current.GetPlaybackInfo();
                    if (info?.PlaybackStatus is not (Playback.Playing or Playback.Paused) ||
                        !info.Controls.IsPlaybackPositionEnabled) continue;
                    var p = current.GetTimelineProperties();
                    if (p is null || p.EndTime <= p.StartTime || p.EndTime == TimeSpan.MaxValue) continue;
                    var min = Math.Max(p.MinSeekTime.Ticks, p.StartTime.Ticks);
                    var max = Math.Min(p.MaxSeekTime.Ticks, p.EndTime.Ticks);
                    if (max <= min) continue;
                    var wanted = checked(p.StartTime.Ticks + request.PositionMs * TimeSpan.TicksPerMillisecond);
                    await current.TryChangePlaybackPositionAsync(Math.Clamp(wanted, min, max)).AsTask(timeout.Token);
                }
                catch (Exception) { /* The session can close, reject the seek or time out. */ }
            }
        }
        finally
        {
            _seeking = false;
            Schedule(Change.Session | Change.ForceTimeline);
        }
    }

    private static bool CanChangePlayback(GlobalSystemMediaTransportControlsSessionPlaybackControls controls, bool playing) =>
        controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled);

    // Compare the native session objects, including two sessions owned by one browser.
    private static bool SameSession(Session? a, Session? b) =>
        ReferenceEquals(a, b) || (a is WinRT.IWinRTObject left && b is WinRT.IWinRTObject right &&
        left.NativeObject.ThisPtr == right.NativeObject.ThisPtr);

    public async void Control(string id, string name)
    {
        if (_closed || _controlling || _last?.Id != id || _id != id || _session is not { } session) return;
        _controlling = true;
        try
        {
            if (!SameSession(_manager?.GetCurrentSession(), session)) return;
            var info = session.GetPlaybackInfo();
            var controls = info.Controls;
            var operation = name switch
            {
                "play" when info.PlaybackStatus == Playback.Paused && controls.IsPlayEnabled => session.TryPlayAsync(),
                "pause" when info.PlaybackStatus == Playback.Playing && controls.IsPauseEnabled => session.TryPauseAsync(),
                "play" when info.PlaybackStatus == Playback.Paused && controls.IsPlayPauseToggleEnabled => session.TryTogglePlayPauseAsync(),
                "pause" when info.PlaybackStatus == Playback.Playing && controls.IsPlayPauseToggleEnabled => session.TryTogglePlayPauseAsync(),
                "next" when controls.IsNextEnabled => session.TrySkipNextAsync(),
                "previous" when controls.IsPreviousEnabled => session.TrySkipPreviousAsync(),
                _ => null,
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            if (operation is not null) await operation.AsTask(timeout.Token);
        }
        catch (Exception) { /* A media app can close or decline control at any time. */ }
        finally { _controlling = false; Schedule(Change.Session | Change.Playback); }
    }

    private static async Task<string?> CoverAsync(IRandomAccessStreamReference reference, CancellationToken ct)
    {
        // Decode locally and publish a small image for the WebView. The media app's
        // file, stream and original resolution never leave the host.
        try
        {
            using var input = await reference.OpenReadAsync().AsTask(ct);
            if (input.Size is 0 or > 8 * 1024 * 1024) return null;
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 ||
                (ulong)decoder.PixelWidth * decoder.PixelHeight > 40_000_000UL) return null;
            double scale = Math.Min(1d, 180d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            uint width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
            uint height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
            var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height };
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(ct);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync().AsTask(ct);
            using var stream = output.GetInputStreamAt(0);
            using var reader = new DataReader(stream);
            uint length = checked((uint)output.Size);
            if (await reader.LoadAsync(length).AsTask(ct) != length) return null;
            byte[] png = new byte[length];
            reader.ReadBytes(png);
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }
        catch (Exception) { return null; }
    }

    private async void Retry()
    {
        if (_retrying || _closed) return;
        _retrying = true;
        try { await Task.Delay(TimeSpan.FromSeconds(15), _lifetime.Token); }
        catch (OperationCanceledException) { return; }
        finally { _retrying = false; }
        if (!_closed && _unavailable == true) Start();
    }

    private void Detach()
    {
        Bind(null);
        if (_manager is null) return;
        Unsubscribe(() => _manager.CurrentSessionChanged -= OnCurrent);
        Unsubscribe(() => _manager.SessionsChanged -= OnSessions);
        _manager = null;
    }

    private static void Unsubscribe(Action remove)
    {
        try { remove(); }
        catch (Exception) { /* The broker or player can already be gone. */ }
    }

    public void Dispose()
    {
        _closed = true;
        _lifetime.Cancel();
        Detach();
        _lifetime.Dispose();
    }
}
#endif
