#if PAIRSIDE_LIVE
using System.Text.Json;
using System.Xml;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private readonly AppleHeadphoneHistory _appleHistoryFile = new(Path.Combine(AppIdentity.Profile, "headphone-level-days.json"));
    private AppleHeadphoneHistory.Day[]? _appleHistoryDays;
    private CancellationTokenSource? _appleImport;
    private bool _watchAppleHistory;
    private string? _appleHistoryNotice;

    private void WatchAppleHistory(bool enable)
    {
        _watchAppleHistory = enable && MainLive && !_mainCompact;
        if (!_watchAppleHistory)
        {
            _appleImport?.Cancel();
            return;
        }
        if (_appleHistoryDays == null)
        {
            try { _appleHistoryDays = _appleHistoryFile.Load(); }
            catch (Exception e) when (HistoryFileError(e)) { _appleHistoryNotice = "readFailed"; }
        }
        SendAppleHistory();
    }

    private static bool HistoryFileError(Exception e) =>
        e is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or JsonException;

    private void SendAppleHistory()
    {
        if (_watchAppleHistory && MainLive && !_mainCompact && !_exiting)
            PostMain(JsonSerializer.Serialize(new { type = "appleHeadphoneHistory", busy = _appleImport != null,
                days = _appleHistoryDays ?? [], notice = _appleHistoryNotice }, PulseJson));
    }

    private async void ImportAppleHistory()
    {
        if (!_watchAppleHistory || !MainLive || _mainCompact || _exiting || _appleImport != null || _main == null) return;
        using var picker = new OpenFileDialog
        {
            Filter = "Apple Health (*.zip;*.xml)|*.zip;*.xml", Multiselect = false, RestoreDirectory = true,
        };
        if (picker.ShowDialog(_main) != DialogResult.OK) return;
        var source = picker.FileName;
        using var request = new CancellationTokenSource();
        _appleImport = request;
        _appleHistoryNotice = null;
        SendAppleHistory();
        try
        {
            var days = await Task.Run(() => AppleHeadphoneHistory.Import(source, request.Token), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_exiting || !_watchAppleHistory) return;
            if (days.Length == 0) { _appleHistoryNotice = "empty"; return; }
            _appleHistoryFile.Save(days);
            _appleHistoryDays = days;
            _appleHistoryNotice = "imported";
        }
        catch (OperationCanceledException) { _appleHistoryNotice = "cancelled"; }
        catch (Exception e) when (HistoryFileError(e)) { _appleHistoryNotice = "importFailed"; }
        finally
        {
            _appleImport = null;
            SendAppleHistory();
        }
    }

    private void ClearAppleHistory()
    {
        if (!_watchAppleHistory || !MainLive || _mainCompact || _appleImport != null) return;
        try
        {
            _appleHistoryFile.Clear();
            _appleHistoryDays = [];
            _appleHistoryNotice = "removed";
        }
        catch (Exception e) when (HistoryFileError(e)) { _appleHistoryNotice = "removeFailed"; }
        SendAppleHistory();
    }
}
#endif
