using WPods.Core;

namespace WPods.Audio;

// Windows reports several changes while switching endpoints. Read once they settle,
// and drop results queued for a previous route or a closed window.
internal sealed class OutputRouteWatch : IDisposable
{
    private readonly Lock _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly SynchronizationContext _ui;
    private readonly Action<AudioEndpoints.PlaybackOutput?> _publish;
    private int _revision;
    private bool _disposed;
    internal ulong Address { get; }

    internal OutputRouteWatch(ulong address, SynchronizationContext ui, Action<AudioEndpoints.PlaybackOutput?> publish)
    {
        Address = address;
        _ui = ui;
        _publish = publish;
        _timer = new System.Threading.Timer(Read);
        DefaultOutputWatcher.PlaybackRouteChanged += Refresh;
        Refresh();
    }

    internal void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _revision++;
            _timer.Change(325, Timeout.Infinite);
        }
    }

    private void Read(object? unused)
    {
        int revision;
        lock (_gate)
        {
            if (_disposed) return;
            revision = _revision;
        }
        AudioEndpoints.PlaybackOutput? output = null;
        try
        {
            output = AudioEndpoints.ReadPlaybackOutput(Address);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The Windows audio service can restart while an endpoint is being read.
        }
        _ui.Post(_ =>
        {
            lock (_gate)
            {
                if (_disposed || revision != _revision) return;
            }
            _publish(output);
        }, null);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DefaultOutputWatcher.PlaybackRouteChanged -= Refresh;
            _timer.Dispose();
        }
    }
}
