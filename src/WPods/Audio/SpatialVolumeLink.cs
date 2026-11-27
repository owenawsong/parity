#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WPods.Audio;

internal sealed class SpatialVolumeLink : IDisposable
{
    private readonly AudioEndpointVolume _pair, _virtual;
    private readonly Func<bool> _ownsRoute;
    private readonly Guid _context = Guid.NewGuid();
    private readonly Lock _worker = new();
    private volatile bool _active;
    private volatile bool _failed;
    private int _pending;

    internal bool Failed => _failed;

    internal SpatialVolumeLink(AudioEndpointVolume pair, AudioEndpointVolume virtualOutput, Func<bool> ownsRoute)
    {
        _pair = pair;
        _virtual = virtualOutput;
        _ownsRoute = ownsRoute;
        _pair.NotificationGuid = _virtual.NotificationGuid = _context;
        Copy(Read(_pair), _virtual);
    }

    internal void Start()
    {
        _active = true;
        _pair.OnVolumeNotification += PairChanged;
        _virtual.OnVolumeNotification += VirtualChanged;
    }

    private void PairChanged(AudioVolumeNotificationData change) => Queue(change, 1);
    private void VirtualChanged(AudioVolumeNotificationData change) => Queue(change, 2);

    private void Queue(AudioVolumeNotificationData change, int source)
    {
        // Never call the Windows audio service from its notification callback.
        if (_active && change.EventContext != _context && Interlocked.Exchange(ref _pending, source) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static link => link.Sync(), this, preferLocal: false);
    }

    private void Sync()
    {
        lock (_worker)
        {
            int source = Interlocked.Exchange(ref _pending, 0);
            if (!_active || source == 0) return;
            try
            {
                if (!_ownsRoute()) return;
                if (source == 1) Copy(Read(_pair), _virtual);
                else
                {
                    Copy(Read(_virtual), _pair);
                    // The Bluetooth endpoint may round the requested level to a supported step.
                    if (_ownsRoute() && Volatile.Read(ref _pending) == 0) Copy(Read(_pair), _virtual);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                _failed = true;
            }
        }
    }

    private static (float Level, bool Muted) Read(AudioEndpointVolume endpoint) => (endpoint.MasterVolumeLevelScalar, endpoint.Mute);

    private static void Copy((float Level, bool Muted) value, AudioEndpointVolume endpoint)
    {
        bool wasMuted = endpoint.Mute;
        if (value.Muted && !wasMuted) endpoint.Mute = true;
        if (Math.Abs(endpoint.MasterVolumeLevelScalar - value.Level) > .001f)
            endpoint.MasterVolumeLevelScalar = value.Level;
        if (!value.Muted && wasMuted) endpoint.Mute = false;
    }

    public void Dispose()
    {
        _active = false;
        _pair.OnVolumeNotification -= PairChanged;
        _virtual.OnVolumeNotification -= VirtualChanged;
        // An in-flight sync must finish before the devices are released.
        lock (_worker) { }
    }
}
#endif
