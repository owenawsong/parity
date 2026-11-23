using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private OutputRouteWatch? _outputRoute;
    private AudioEndpoints.PlaybackOutput? _playbackOutput;
    private string _outputAction = "idle";
#if PAIRSIDE_LIVE
    private CancellationTokenSource? _outputSwitch;
    private NowPlayingWatch? _nowPlaying;
#endif

    private void SendOutputRoute(ulong address)
    {
        var w = _writer.Begin("outputRoute");
        w.WriteString("id", Mac.Format(address));
#if PAIRSIDE_LIVE
        w.WriteString("otherOutput", _playbackOutput?.OtherOutput);
#else
        w.WriteNull("otherOutput");
#endif
        if (_playbackOutput is { } output)
        {
            w.WriteStartObject("defaultOutput");
            w.WriteString("name", output.Name);
            if (output.OnPair is bool onPair) w.WriteBoolean("onPair", onPair);
            w.WriteEndObject();
        }
        else w.WriteNull("defaultOutput");
        w.WriteString("action", _outputAction);
        PostMain(_writer.End());
    }

#if PAIRSIDE_LIVE
    private async void UsePairOutput(ulong address)
    {
        var watch = _outputRoute;
        if (watch?.Address != address || _outputAction == "switching" || _playbackOutput is not { OtherOutput: not null } original ||
            _connections.Session is not { State.Status: SessionStatus.Ready } session || session.Address != address) return;
        _outputAction = "switching";
        SendOutputRoute(address);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _outputSwitch = timeout;
        var result = AudioEndpoints.PlaybackChange.Unavailable;
        try
        {
            result = await Task.Run(() => AudioEndpoints.UsePairPlaybackAsync(address, original.Id, timeout.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Warn("route", $"Playback switch failed ({e.GetType().Name})"); }
        if (_outputSwitch == timeout) _outputSwitch = null;
        if (_outputRoute != watch) return;
        _outputAction = result == AudioEndpoints.PlaybackChange.Unavailable ? "settings" : "idle";
        if (result == AudioEndpoints.PlaybackChange.Selected) _playbackOutput = null;
        SendOutputRoute(address);
        watch.Refresh();
    }

#endif

    private void UpdateNowPlayingWatch(bool replay = false)
    {
#if PAIRSIDE_LIVE
        UpdateRoomMeter();
#endif
        if (!MainLive || !_mainInitSent || _main!.WindowState == FormWindowState.Minimized)
        {
            _outputRoute?.Dispose();
            _outputRoute = null;
#if PAIRSIDE_LIVE
            _outputSwitch?.Cancel();
            _nowPlaying?.Dispose();
            _nowPlaying = null;
#endif
            return;
        }
        ulong address = _connections.Session?.Address ?? Selected() ?? 0;
        if (_outputRoute?.Address != address)
        {
#if PAIRSIDE_LIVE
            _outputSwitch?.Cancel();
#endif
            _outputRoute?.Dispose();
            _playbackOutput = null;
            _outputAction = "idle";
            _outputRoute = new OutputRouteWatch(address, _ui, output =>
            {
                if (_playbackOutput?.OtherOutput != output?.OtherOutput && _outputAction == "settings") _outputAction = "idle";
                _playbackOutput = output;
                SendOutputRoute(address);
            });
        }
        else
        {
            _outputRoute.Refresh();
        }
#if PAIRSIDE_LIVE
        if (_mainCompact || _connections.Session is not { State.Status: SessionStatus.Ready })
        {
            _nowPlaying?.Dispose();
            _nowPlaying = null;
            return;
        }
        if (_nowPlaying is null)
        {
            PostMain("""{"type":"nowPlaying","media":null}""");
            _nowPlaying = new NowPlayingWatch(_ui, PostMain);
            _nowPlaying.Start();
        }
        else if (replay)
        {
            _nowPlaying.Replay();
        }
#endif
    }
}
