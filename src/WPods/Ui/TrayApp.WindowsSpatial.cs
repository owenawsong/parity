#if PAIRSIDE_LIVE
using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private async void RequestWindowsSpatial(ulong address, int request, bool openSettings)
    {
        if (!MainLive || !_mainInitSent || _connections.Session is not { State.Status: SessionStatus.Ready } session ||
            session.Address != address) return;
        var pane = _main!.Pane;
        AudioEndpoints.SpatialOutput? output = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            output = await Task.Run(() => AudioEndpoints.ReadSpatialOutputAsync(address, timeout.Token), timeout.Token);
        }
        catch (Exception e) { Log.Warn("spatial", $"Windows output unavailable ({e.GetType().Name})"); }
        if (!MainLive || _main?.Pane != pane || _connections.Session != session || session.State.Status != SessionStatus.Ready) return;
        if (openSettings && output is not null)
        {
            // Only Windows or the app owning a spatial format can select that format.
            // Resolve the endpoint at click time rather than trusting an ID from the UI.
            Launch("ms-settings:sound-properties?endpointId=" + Uri.EscapeDataString(output.EndpointId));
        }
        var w = _writer.Begin("windowsSpatial");
        w.WriteString("id", Mac.Format(address));
        w.WriteNumber("request", request);
        w.WriteString("provider", output?.Provider);
        PostMain(_writer.End());
    }
}
#endif
