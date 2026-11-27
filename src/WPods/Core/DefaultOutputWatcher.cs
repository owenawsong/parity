using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using WPods.Audio;

namespace WPods.Core;

/// <summary>
/// Default-output follow: with several AirPods pairs linked, the session goes to the pair that is the default Windows
/// multimedia playback device and switches when Windows switches. A default output that is not
/// a linked pair keeps the current session; a pair the user disconnected is never attached
/// (<see cref="ConnectionManager.FollowDefaultOutputAsync"/>). Never pages. The IMMNotificationClient callback only
/// flags the change and starts one thread-pool worker, which waits <see cref="CoalesceDelay"/> (Windows raises one
/// callback per role for each change) and then decides once. Create before <see cref="ConnectionManager.Start"/>;
/// dispose on exit (same thread).
/// </summary>
public sealed partial class DefaultOutputWatcher : IDisposable
{
    private const int ClsctxInprocServer = 0x1;
    private const int RenderFlow = 0; // eRender
    private const int MultimediaRole = 1; // eMultimedia
    private const int CommunicationsRole = 2; // eCommunications

    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(250);
    private static readonly Guid MmDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid MmDeviceEnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    internal static event Action? PlaybackRouteChanged;
    private static int _endpointRevision;
    private static int _mediaRevision;
    internal static int EndpointRevision => Volatile.Read(ref _endpointRevision);
    internal static int MediaRevision => Volatile.Read(ref _mediaRevision);

    private readonly ConnectionManager _connections;
    private readonly Lock _lock = new();
    // Set by the constructor.
    private unsafe void* _enumerator;
    private unsafe void* _client;
    // Under _lock.
    private bool _pending;
    private bool _running;
    private bool _disposed;

    public unsafe DefaultOutputWatcher(ConnectionManager connections)
    {
        _connections = connections;
        void* enumerator;
        int hr = CoCreateInstance(MmDeviceEnumeratorClass, null, ClsctxInprocServer, MmDeviceEnumeratorInterface, &enumerator);
        if (hr < 0)
        {
            Log.Warn("follow", $"default output not followed: MMDeviceEnumerator error 0x{hr:X8}");
            return;
        }
        void* client = ComInterfaceMarshaller<IMMNotificationClient>.ConvertToUnmanaged(new NotificationClient(this));
        // IMMDeviceEnumerator::RegisterEndpointNotificationCallback
        hr = ((delegate* unmanaged<void*, void*, int>)Slot(enumerator, 6))(enumerator, client);
        if (hr < 0)
        {
            ComInterfaceMarshaller<IMMNotificationClient>.Free(client);
            Release(enumerator);
            Log.Warn("follow", $"default output not followed: register error 0x{hr:X8}");
            return;
        }
        _enumerator = enumerator;
        _client = client;
        Log.Info("follow", "following the default output");
    }

    public unsafe void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        if (_enumerator is null)
        {
            return;
        }
        // IMMDeviceEnumerator::UnregisterEndpointNotificationCallback
        int hr = ((delegate* unmanaged<void*, void*, int>)Slot(_enumerator, 7))(_enumerator, _client);
        ComInterfaceMarshaller<IMMNotificationClient>.Free(_client);
        Release(_enumerator);
        Log.Info("follow", $"stopped, unregister 0x{hr:X8}");
    }

    /// <summary>1 if the pair is the default console render endpoint, 0 if not, &lt;0 HRESULT. Synchronous; not on the UI thread.</summary>
    [LibraryImport("wpods_native.dll")]
    internal static partial int wpn_audio_is_default_output(ulong btAddress);

    // Follow the same Windows role that the Overview displays and changes. The
    // console role can stay on another pair and must not supersede this choice.
    internal static bool IsMediaOutput(ulong address)
    {
        try { return AudioEndpoints.IsPhysicalPair(address, AudioEndpoints.ReadMediaOutputContainer()); }
        catch (Exception e) when (e is COMException or InvalidCastException) { return false; }
    }

    internal static ulong FindMediaOutputPair(IEnumerable<ulong> addresses)
    {
        Guid? output;
        try { output = AudioEndpoints.ReadMediaOutputContainer(); }
        catch (Exception e) when (e is COMException or InvalidCastException) { return 0; }
        return addresses.FirstOrDefault(address => AudioEndpoints.IsPhysicalPair(address, output));
    }

    // On an audio service thread: flag and return at once.
    private void OnDefaultDeviceChanged(int flow, int role)
    {
        if (flow != RenderFlow || role != MultimediaRole)
        {
            return;
        }
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _pending = true;
            if (_running)
            {
                return;
            }
            _running = true;
        }
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(CoalesceDelay).ConfigureAwait(false);
            while (TakePending())
            {
                await FollowAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // A change that came after the last pass (or after a failed one) starts a new worker.
            bool again;
            lock (_lock)
            {
                again = _pending && !_disposed;
                _running = again;
            }
            if (again)
            {
                _ = Task.Run(RunAsync);
            }
        }
    }

    private bool TakePending()
    {
        lock (_lock)
        {
            if (!_pending || _disposed)
            {
                return false;
            }
            _pending = false;
            return true;
        }
    }

    private async Task FollowAsync()
    {
        ulong attached = _connections.Session?.Address ?? 0;
        string attachedText = attached == 0 ? "none" : Mac.Format(attached);
        ulong target = FindMediaOutputPair(_connections.GetDevices().Where(device => device.LinkUp).Select(device => device.Address));
        if (target != 0)
        {
            if (target == attached)
            {
                Log.Info("follow", $"default output is the attached pair {attachedText}, nothing to do");
                return;
            }
            Log.Info("follow", $"default output is {Mac.Format(target)}, session={attachedText}: following");
            await _connections.FollowDefaultOutputAsync(target).ConfigureAwait(false);
            return;
        }
        Log.Info("follow", $"default output is not a linked pair, keeping session={attachedText}");
    }

    private static unsafe void* Slot(void* instance, int slot) => (*(void***)instance)[slot];

    private static unsafe void Release(void* instance) => ((delegate* unmanaged<void*, uint>)Slot(instance, 2))(instance);

    [LibraryImport("ole32.dll")]
    private static unsafe partial int CoCreateInstance(in Guid classId, void* outer, int context, in Guid interfaceId, void** instance);

    // PROPERTYKEY, passed by value; never read.
    internal readonly struct PropertyKey
    {
        public Guid FormatId { get; init; }
        public uint PropertyId { get; init; }
    }

    [GeneratedComInterface]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    internal partial interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged(nint deviceId, uint newState);

        [PreserveSig]
        int OnDeviceAdded(nint deviceId);

        [PreserveSig]
        int OnDeviceRemoved(nint deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged(nint deviceId, PropertyKey key);
    }

    [GeneratedComClass]
    private sealed partial class NotificationClient(DefaultOutputWatcher owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(nint deviceId, uint newState)
        {
            Interlocked.Increment(ref _endpointRevision);
            Interlocked.Increment(ref _mediaRevision);
            PlaybackRouteChanged?.Invoke();
            return 0;
        }

        public int OnDeviceAdded(nint deviceId) => OnDeviceStateChanged(deviceId, 0);

        public int OnDeviceRemoved(nint deviceId) => OnDeviceStateChanged(deviceId, 0);

        public int OnDefaultDeviceChanged(int flow, int role, nint defaultDeviceId)
        {
            if (role is MultimediaRole or CommunicationsRole)
            {
                Interlocked.Increment(ref _endpointRevision);
            }
            if (role == MultimediaRole && flow == RenderFlow)
            {
                Interlocked.Increment(ref _mediaRevision);
                PlaybackRouteChanged?.Invoke();
            }
            owner.OnDefaultDeviceChanged(flow, role);
            return 0;
        }

        public int OnPropertyValueChanged(nint deviceId, PropertyKey key)
        {
            if (key.PropertyId == 14 && key.FormatId == new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"))
            {
                Interlocked.Increment(ref _endpointRevision);
                PlaybackRouteChanged?.Invoke();
            }
            return 0;
        }
    }
}
