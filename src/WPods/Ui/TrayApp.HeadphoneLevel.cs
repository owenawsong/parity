using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private readonly System.Windows.Forms.Timer _headphoneLevelTimer = new() { Interval = 125 };
    private Task _headphoneLevelCompletion = Task.CompletedTask;
    private HeadphoneLevelWatch? _headphoneLevel;

    private sealed class HeadphoneLevelWatch(WebPane pane, AapSession session, int request)
    {
        internal readonly WebPane Pane = pane;
        internal readonly AapSession Session = session;
        internal readonly int Request = request;
        internal readonly CancellationTokenSource Cancel = new();
        internal HeadphoneLevel.Reading Latest = new("waiting");
        internal HeadphoneLevel.Reading? Posted;
    }

    private void WatchHeadphoneLevel(WebPane pane, ulong address, int request, bool enabled)
    {
        if (!enabled)
        {
            if (_headphoneLevel is { } current && current.Session.Address == address && current.Request == request)
                StopHeadphoneLevel(notify: false);
            return;
        }
        if (_exiting || !MainLive || _main?.WindowState == FormWindowState.Minimized ||
            _connections.Session is not { State.Status: SessionStatus.Ready } session || session.Address != address)
        {
            PostHeadphoneLevel(pane, address, request, new("idle"));
            return;
        }
        if (_headphoneLevel is { } existing && existing.Session == session && existing.Request == request) return;
        StopHeadphoneLevel();

        var watch = new HeadphoneLevelWatch(pane, session, request);
        _headphoneLevel = watch;
        Task previous = _headphoneLevelCompletion;
        _headphoneLevelCompletion = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            HeadphoneLevel.Watch(address, () => _connections.Session == session && session.State.Status == SessionStatus.Ready,
                reading => Volatile.Write(ref watch.Latest, reading), watch.Cancel.Token);
        });
        _headphoneLevelTimer.Start();
        OnHeadphoneLevelTick(null, EventArgs.Empty);
    }

    private void StopHeadphoneLevel(bool notify = true)
    {
        if (_headphoneLevel is not { } watch) return;
        _headphoneLevel = null;
        _headphoneLevelTimer.Stop();
        var cancel = watch.Cancel;
        cancel.Cancel();
        // Keep the token and its wait handle alive until the native stream has closed.
        _headphoneLevelCompletion = ReleaseHeadphoneLevel(_headphoneLevelCompletion, cancel);
        if (notify) PostHeadphoneLevel(watch.Pane, watch.Session.Address, watch.Request, new("idle"));
    }

    private static async Task ReleaseHeadphoneLevel(Task completion, CancellationTokenSource cancel)
    {
        using (cancel)
        {
            try { await completion.ConfigureAwait(false); }
            catch (Exception error) { Log.Error("level", "Playback level worker failed", error); }
        }
    }

    private void OnHeadphoneLevelTick(object? sender, EventArgs args)
    {
        if (_headphoneLevel is not { } watch) return;
        if (!MainLive || _main?.Pane != watch.Pane || _main.WindowState == FormWindowState.Minimized ||
            _connections.Session != watch.Session || watch.Session.State.Status != SessionStatus.Ready ||
            _headphoneLevelCompletion.IsCompletedSuccessfully)
        {
            StopHeadphoneLevel();
            return;
        }
        HeadphoneLevel.Reading status = _headphoneLevelCompletion.IsFaulted ? new("unsupported") : Volatile.Read(ref watch.Latest);
        if (status.Status == "live" ? ReferenceEquals(watch.Posted, status) : watch.Posted == status) return;
        PostHeadphoneLevel(watch.Pane, watch.Session.Address, watch.Request, status);
        watch.Posted = status;
    }

    private void PostHeadphoneLevel(WebPane pane, ulong address, int request, HeadphoneLevel.Reading status)
    {
        if (_main?.Pane != pane) return;
        var writer = _writer.Begin("headphoneLevel");
        writer.WriteString("id", Mac.Format(address));
        writer.WriteNumber("request", request);
        writer.WriteString("status", status.Status);
        if (status.LeftDb is int left) writer.WriteNumber("leftDb", left);
        else writer.WriteNull("leftDb");
        if (status.RightDb is int right) writer.WriteNumber("rightDb", right);
        else writer.WriteNull("rightDb");
        if (status.VolumePercent is int percent) writer.WriteNumber("volumePercent", percent);
        else writer.WriteNull("volumePercent");
        if (status.Muted is bool muted) writer.WriteBoolean("muted", muted);
        else writer.WriteNull("muted");
        writer.WriteString("outputName", status.OutputName);
        if (status.Stream is int stream) writer.WriteNumber("stream", stream);
        if (status.ElapsedMs is long elapsed) writer.WriteNumber("elapsedMs", elapsed);
        if (status.SampleMs is double sample) writer.WriteNumber("sampleMs", sample);
        if (status.MeasuredMs is double measured) writer.WriteNumber("measuredMs", measured);
        if (status.ActiveMs is double active) writer.WriteNumber("activeMs", active);
        if (status.AverageDb is int average) writer.WriteNumber("averageDb", average);
        if (status.SignalDb is int signal) writer.WriteNumber("signalDb", signal);
        pane.Post(_writer.End());
    }
}
