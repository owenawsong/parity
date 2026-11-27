#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private int _connectionCheckRevision;

    private static (bool Known, T? Value) CheckAudio<T>(Func<T?> read, string subject) where T : class
    {
        try
        {
            return (true, read());
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("diagnostics", $"{subject} check failed: 0x{e.HResult:X8}");
            return (false, null);
        }
    }

    private async void ReadConnectionCheck(int request, ulong address)
    {
        int revision = ++_connectionCheckRevision;
        var pane = _main?.Pane;
        var (routes, spatial) = await Task.Run(() => (
            CheckAudio(() => AudioEndpoints.ReadDefaults(), "Windows audio endpoints").Value,
            CheckAudio(SpotifyBridge.VirtualOutputId, "Spatial playback device")));
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
        w.WriteBoolean("connected", address != 0 && session?.Address == address && session.State.Status == SessionStatus.Ready);
        w.WriteString("discovery", address == 0 ? "pairFirst" :
            Find(address)?.HasKeys != true ? "connectFirst" : _nearby.DiscoveryStatus);
        w.WriteBoolean("knownEndpoints", routes != null);
        w.WriteString("playback", routes?.Output?.Name);
        w.WriteString("callInput", routes?.CommunicationsInput?.Name);
        w.WriteString("callOutput", routes?.CommunicationsOutput?.Name);
        if (routes?.CommunicationsInput?.Muted is bool muted) w.WriteBoolean("callMuted", muted);
        if (routes != null)
        {
            // A Windows endpoint can exist even when its friendly name cannot be read.
            w.WriteStartObject("endpoints");
            w.WriteBoolean("playback", routes.Output != null);
            w.WriteBoolean("callInput", routes.CommunicationsInput != null);
            w.WriteBoolean("callOutput", routes.CommunicationsOutput != null);
            w.WriteEndObject();
        }
        if (spatial.Known) w.WriteBoolean("spatialDevice", spatial.Value != null);
        if (spatial.Known && routes != null)
            w.WriteBoolean("virtualDefaultOutsideParity", WindowsSpatialMix.Same(spatial.Value, routes.Output?.Id) &&
                _spotify?.App.IsWindowsMix != true && !WindowsSpatialMix.Pending);
        w.WriteStartArray("events");
        foreach (var entry in _connections.Timeline.Read(address))
        {
            w.WriteStartObject();
            string kind = entry.Kind.ToString();
            w.WriteString("kind", char.ToLowerInvariant(kind[0]) + kind[1..]);
            w.WriteNumber("time", entry.Time);
            w.WriteNumber("ageSeconds", entry.AgeSeconds);
            w.WriteNumber("count", entry.Count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        PostMain(_writer.End());
    }
}
#endif
