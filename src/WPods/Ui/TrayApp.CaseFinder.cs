using WPods.Core;
using WPods.Nearby;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private readonly System.Windows.Forms.Timer _caseFinderTimer = new() { Interval = 600 };
    private ulong _caseFinderDevice;
    private readonly CaseSignalTrend _caseTrend = new();
    private (ulong Device, int? Dbm, long? Age, bool? Direct, string? Trend)? _lastCaseSignal;

    private void UpdateCaseFinderWatch()
    {
        bool active = _caseFinderDevice != 0 && _mainShown && _main is { WindowState: not FormWindowState.Minimized };
#if PAIRSIDE_LIVE
        _nearby.SetFinderActive(active);
#endif
        if (active)
        {
            if (!_caseFinderTimer.Enabled)
            {
                _lastCaseSignal = null;
                _caseFinderTimer.Start();
            }
            OnCaseFinderTick(null, EventArgs.Empty);
        }
        else
        {
            _caseFinderTimer.Stop();
            _caseTrend.Reset();
        }
    }

    private void OnCaseFinderTick(object? sender, EventArgs args)
    {
        var signal = _nearby.GetCaseSignal(_caseFinderDevice);
        int? dbm = signal is { } reading ? (int)Math.Round(reading.Dbm) : null;
        long? age = signal is { } sample ? Math.Max(0, (Environment.TickCount64 - sample.SeenTick) / 1000) : null;
        if (age >= 60)
            age = age.Value / 60 * 60;
        var trend = signal is { } current
            ? _caseTrend.Update(_caseFinderDevice, current.Direct, current.SeenTick, current.Dbm, Environment.TickCount64)
            : null;
        if (signal is null) _caseTrend.Reset();
        var next = (_caseFinderDevice, dbm, age, signal?.Direct, trend);
        if (_lastCaseSignal == next || _main is not { } main)
        {
            return;
        }
        _lastCaseSignal = next;
        var w = _writer.Begin("caseSignal");
        w.WriteString("id", Mac.Format(_caseFinderDevice));
        if (dbm is { } value && age is { } seconds)
        {
            w.WriteNumber("dbm", value);
            w.WriteNumber("age", Math.Max(0, seconds));
            w.WriteString("source", signal!.Value.Direct ? "case" : "earbuds");
            if (trend != null) w.WriteString("trend", trend);
        }
        else
        {
            w.WriteNull("dbm");
            w.WriteNull("age");
        }
        main.Pane.Post(_writer.End());
    }
}
