#if PAIRSIDE_LIVE
using System.Text.Json;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private readonly BatteryTimeline _batteryTimeline = new(Path.Combine(AppIdentity.Profile, "battery-timeline.json"));
    private ulong _batteryWatch;
    private string _batteryPart = "left";
    private bool _batterySaveFailed;

    private void CollectBatteryHistory(bool checkpoint)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool visibleChange = false;
        void Record(ulong address, string part, Battery battery, long time, string source)
        {
            if (battery.Percent is not byte percent) return;
            var id = Mac.Format(address);
            if (_batteryTimeline.CrossedTarget(id, part, percent, time, now))
            {
                _batteryTimeline.Save(now);
                if (!_exiting)
                    _tray.ShowBalloonTip(10_000, DeviceName(address), Text("battery.chargeNotice")
                        .Replace("{part}", Text("battery." + part)).Replace("{pct}", percent.ToString()), ToolTipIcon.None);
                if (address == _batteryWatch && part == _batteryPart) visibleChange = true;
            }
            bool? charging = battery.Status switch
            {
                BatteryStatus.Charging => true, BatteryStatus.Discharging => false, _ => null,
            };
            if (_batteryTimeline.Observe(id, part, percent, time, charging, source, now)
                && address == _batteryWatch && (part == _batteryPart ||
                    ((part is "left" or "right") && (_batteryPart is "left" or "right")))) visibleChange = true;
        }
        var session = _session;
        if (session is not null)
        {
            var docked = _nearby.GetCaseBudBroadcast(session.Address);
            for (int i = 0; i < BatteryTimeline.Parts.Length; ++i)
            {
                string part = BatteryTimeline.Parts[i];
                var sample = session.State.BatteryReportAt(i);
                Battery? caseBattery = part switch { "left" => docked?.Left, "right" => docked?.Right, _ => null };
                // A still-cached case observation cannot displace a newer report from the earbud.
                if (docked is { } buds && caseBattery is { Percent: not null } value &&
                    buds.SeenAt <= now && buds.SeenAt > (sample?.At ?? 0))
                    Record(session.Address, part, value, buds.SeenAt, "case");
                else if (sample is not null)
                    Record(session.Address, part, sample.Value, sample.At, "device");
            }
        }
        foreach (var device in _devices)
        {
            if (session?.Address != device.Address && _nearby.GetCaseBudBroadcast(device.Address) is { } buds)
            {
                Record(device.Address, "left", buds.Left, buds.SeenAt, "case");
                Record(device.Address, "right", buds.Right, buds.SeenAt, "case");
            }
            if (_nearby.GetCaseBroadcast(device.Address) is { } observed)
                Record(device.Address, "case", observed.Battery, observed.SeenAt, observed.Direct ? "case" : "nearby");
            else if (_caseHistory?.Get(device.Address) is { } cached &&
                _batteryTimeline.Observe(Mac.Format(device.Address), "case", cached.Percent, cached.SeenAt, null, "saved", now) &&
                device.Address == _batteryWatch && _batteryPart == "case") visibleChange = true;
        }
        if (checkpoint)
        {
            if (_exiting || _batterySaveFailed) _batterySaveFailed = !_batteryTimeline.Save(now);
            else _batteryTimeline.QueueSave(now);
        }
        if (visibleChange || checkpoint) SendBatteryHistory();
    }

    private void WatchBatteryHistory(ulong address, string? part)
    {
        _batteryWatch = 0;
        if (!MainLive || _mainCompact || !_devices.Exists(d => d.Address == address) ||
            part == null || !BatteryTimeline.Parts.Contains(part)) return;
        _batteryPart = part;
        _batteryWatch = address;
        SendBatteryHistory();
    }

    private void SetBatteryHistory(ulong address, bool enabled)
    {
        if (address == 0 || _batteryWatch != address || !MainLive || _mainCompact) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _batteryTimeline.SetEnabled(Mac.Format(address), enabled, now);
        _batterySaveFailed = !_batteryTimeline.Save(now);
        SendBatteryHistory();
    }

    private void SendBatteryHistory()
    {
        if (_batteryWatch == 0 || _exiting || !MainLive || _mainCompact || !_mainInitSent) return;
        var id = Mac.Format(_batteryWatch);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var points = _batteryTimeline.Read(id, _batteryPart, now);
        var otherBudPoints = _batteryPart is "left" or "right"
            ? _batteryTimeline.Read(id, _batteryPart == "left" ? "right" : "left", now)
            : null;
        PostMain(JsonSerializer.Serialize(new { type = "batteryHistory", id, part = _batteryPart,
            enabled = _batteryTimeline.Enabled(id), target = _batteryTimeline.AlertTarget(id, _batteryPart),
            saveFailed = _batterySaveFailed, points, otherBudPoints }, PulseJson));
    }

    private void SetChargeAlert(ulong address, string? part, int target)
    {
        if (address == 0 || _batteryWatch != address || part != _batteryPart || !MainLive || _mainCompact ||
            target is not (0 or 80 or 90 or 100)) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string id = Mac.Format(address);
        var recent = _batteryTimeline.Read(id, part, now).LastOrDefault();
        long newest = recent?.At ?? 0;
        byte? current = recent != null && now - newest <= 90_000 ? recent.Pct : null;
        if (part == "case" && _nearby.GetCaseBroadcast(address) is { Battery.Percent: { } pct } seen &&
            now - seen.SeenAt <= 90_000 && seen.SeenAt >= newest)
        {
            current = pct;
            newest = seen.SeenAt;
        }
        if (part is "left" or "right" && _nearby.GetCaseBudBroadcast(address) is { } buds &&
            buds.SeenAt <= now && now - buds.SeenAt <= 90_000 && buds.SeenAt >= newest &&
            (part == "left" ? buds.Left : buds.Right).Percent is { } dockedPercent)
        {
            current = dockedPercent;
            newest = buds.SeenAt;
        }
        if (_session?.Address == address &&
            _session.State.BatteryReportAt(Array.IndexOf(BatteryTimeline.Parts, part)) is { } sessionReport &&
            now - sessionReport.At <= 90_000 && sessionReport.At >= newest)
            current = sessionReport.Value.Percent;
        _batteryTimeline.SetAlert(id, part, target, now, current);
        _batterySaveFailed = !_batteryTimeline.Save(now);
        SendBatteryHistory();
    }
}
#endif
