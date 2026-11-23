#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private int _connectionCheckRevision;

    private async void ReadConnectionCheck(int request)
    {
        int revision = ++_connectionCheckRevision;
        var pane = _main?.Pane;
        AudioEndpoints.Routes? routes = null;
        string? spatialEndpoint = null;
        try
        {
            (routes, spatialEndpoint) = await Task.Run(() =>
                (AudioEndpoints.ReadDefaults(), SpotifyBridge.VirtualOutputId()));
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("diagnostics", $"Windows audio endpoint check failed: 0x{e.HResult:X8}");
        }
        if (_exiting || revision != _connectionCheckRevision || !MainLive || _main?.Pane != pane) return;
        var session = _connections.Session;
        var service = _service.Snapshot();
        string status = _restartPending ? "restart" : service.State switch
        {
            ServiceState.Running => "running",
            ServiceState.Native => "native",
            ServiceState.NotRunning when service.Detail == "not-installed" => "appOnly",
            ServiceState.NotRunning => "offline",
            ServiceState.Unsupported when service.Detail == "conflicting-service" => "conflict",
            ServiceState.Unsupported when service.Detail == "usbip-missing" => "missingBridge",
            ServiceState.Unsupported when service.Detail == "not-usb" => "unsupported",
            ServiceState.Unsupported => "noAdapter",
            _ => "checking",
        };
        var w = _writer.Begin("connectionCheck");
        w.WriteNumber("request", request);
        w.WriteString("service", status);
        w.WriteString("serviceVersion", service.Version);
        w.WriteBoolean("connected", session?.State.Status == SessionStatus.Ready);
        w.WriteString("discovery", Selected() is not { } address ? "pairFirst" :
            Find(address)?.HasKeys != true ? "connectFirst" : _nearby.DiscoveryStatus);
        w.WriteBoolean("knownEndpoints", routes != null);
        w.WriteString("playback", routes?.Output?.Name);
        w.WriteString("callInput", routes?.CommunicationsInput?.Name);
        w.WriteString("callOutput", routes?.CommunicationsOutput?.Name);
        if (routes?.CommunicationsInput?.Muted is bool muted) w.WriteBoolean("callMuted", muted);
        if (routes != null)
        {
            w.WriteBoolean("spatialDevice", spatialEndpoint != null);
            w.WriteBoolean("virtualDefaultOutsideParity", WindowsSpatialMix.Same(spatialEndpoint, routes.Output?.Id) &&
                _spotify?.App.IsWindowsMix != true && !WindowsSpatialMix.Pending);
        }
        PostMain(_writer.End());
    }
}
#endif
