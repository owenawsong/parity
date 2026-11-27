using WPods.Core;
using System.Runtime.InteropServices;
#if PAIRSIDE_LIVE
using System.Runtime.InteropServices.Marshalling;
#endif

namespace WPods.Audio;

// Windows reports several changes while switching endpoints. Read once they settle,
// and drop results queued for a previous route or a closed window.
internal sealed partial class OutputRouteWatch : IDisposable
{
    private readonly Lock _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly SynchronizationContext _ui;
    private readonly Action<AudioEndpoints.PlaybackOutput?> _publish;
    private int _revision;
    private bool _disposed;
#if PAIRSIDE_LIVE
    private readonly Lock _volumeGate = new();
    private AudioEndpoints.IAudioEndpointVolume? _volume;
    private nint _volumeCallback;
    private string? _volumeId;
    private int _volumeSilence;
    private int _volumeRefreshQueued;
#endif
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
#if PAIRSIDE_LIVE
        lock (_volumeGate) ReadRoute();
#else
        ReadRoute();
#endif
    }

    private void ReadRoute()
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
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // The Windows audio service can restart while an endpoint is being read.
        }
#if PAIRSIDE_LIVE
        RefreshVolume(output?.Id);
        if (output != null) output = output with { Silence = (AudioEndpoints.PlaybackSilence)Volatile.Read(ref _volumeSilence) };
#endif
        _ui.Post(_ =>
        {
            lock (_gate)
            {
                if (_disposed || revision != _revision) return;
            }
            _publish(output);
        }, null);
    }

#if PAIRSIDE_LIVE
    private unsafe void RefreshVolume(string? deviceId)
    {
        if (!string.Equals(deviceId, _volumeId, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseVolume();
            if (deviceId == null) return;
            try
            {
                _volume = AudioEndpoints.OpenOutputVolume(deviceId);
                if (_volume == null) return;
                _volumeCallback = (nint)ComInterfaceMarshaller<IVolumeCallback>.ConvertToUnmanaged(new VolumeCallback(this));
                Marshal.ThrowExceptionForHR(_volume.RegisterControlChangeNotify(_volumeCallback));
                _volumeId = deviceId;
            }
            catch (Exception e) when (e is COMException or InvalidCastException) { ReleaseVolume(); }
        }
        if (_volume is not { } volume) return;
        try
        {
            if (volume.GetMute(out int muted) < 0 || volume.GetMasterVolumeLevelScalar(out float level) < 0)
            {
                ReleaseVolume();
                return;
            }
            Volatile.Write(ref _volumeSilence, (int)AudioEndpoints.OutputSilence(muted != 0, level));
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { ReleaseVolume(); }
    }

    private void OnVolumeChanged(bool muted, float level)
    {
        if (Volatile.Read(ref _volumeSilence) == (int)AudioEndpoints.OutputSilence(muted, level) ||
            Interlocked.Exchange(ref _volumeRefreshQueued, 1) != 0) return;
        // The callback only queues work. Core Audio calls and locks stay on the worker.
        ThreadPool.UnsafeQueueUserWorkItem(static watch =>
        {
            Interlocked.Exchange(ref watch._volumeRefreshQueued, 0);
            watch.Refresh();
        }, this, preferLocal: false);
    }

    private unsafe void ReleaseVolume()
    {
        if (_volumeCallback != 0)
        {
            if (_volumeId != null) _volume?.UnregisterControlChangeNotify(_volumeCallback);
            ComInterfaceMarshaller<IVolumeCallback>.Free((void*)_volumeCallback);
            _volumeCallback = 0;
        }
        if (_volume != null) Marshal.ReleaseComObject(_volume);
        _volume = null;
        _volumeId = null;
        Volatile.Write(ref _volumeSilence, 0);
    }

    [GeneratedComInterface, Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    internal partial interface IVolumeCallback
    {
        [PreserveSig] int OnNotify(nint data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeNotification
    {
        public Guid Context;
        public int Muted;
        public float MasterVolume;
    }

    [GeneratedComClass]
    private sealed partial class VolumeCallback(OutputRouteWatch watch) : IVolumeCallback
    {
        public unsafe int OnNotify(nint data)
        {
            if (data != 0)
            {
                var notification = (VolumeNotification*)data;
                watch.OnVolumeChanged(notification->Muted != 0, notification->MasterVolume);
            }
            return 0;
        }
    }
#endif

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DefaultOutputWatcher.PlaybackRouteChanged -= Refresh;
            _timer.Dispose();
        }
#if PAIRSIDE_LIVE
        ThreadPool.UnsafeQueueUserWorkItem(static watch =>
        {
            lock (watch._volumeGate) watch.ReleaseVolume();
        }, this, preferLocal: false);
#endif
    }
}
