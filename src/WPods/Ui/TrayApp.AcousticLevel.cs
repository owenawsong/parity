#if PARITY_OWNER_PREVIEW
using System.Text.Json;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private AcousticCalibration? _acousticCalibration;
    private bool _acousticImport;

    private async void ImportAcousticCalibration(WebPane pane)
    {
        if (_acousticImport || _exiting || !MainLive || _mainCompact || _main is null) return;
        _acousticImport = true;
        try
        {
            using var picker = new OpenFileDialog
            {
                Filter = "Calibration measurements (*.json)|*.json", Multiselect = false, RestoreDirectory = true,
                Title = "Open calibration measurements",
            };
            if (picker.ShowDialog(_main) != DialogResult.OK)
            {
                PostAcousticCalibration(pane, "cancelled");
                return;
            }
            var path = picker.FileName;
            var calibration = await Task.Run(() => AcousticCalibration.Load(path));
            if (_exiting || _main?.Pane != pane) return;
            if (_headphoneLevel is { Acoustic: true }) StopHeadphoneLevel();
            _acousticCalibration = calibration;
            PostAcousticCalibration(pane, "loaded");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            PostAcousticCalibration(pane, "rejected");
        }
        finally { _acousticImport = false; }
    }

    private void PostAcousticCalibration(WebPane pane, string status)
    {
        if (_exiting || _main?.Pane != pane) return;
        var writer = _writer.Begin("acousticCalibration");
        writer.WriteString("status", status);
        if (_acousticCalibration is { } calibration)
        {
            writer.WriteString("referencePlane", calibration.ReferencePlane);
            writer.WriteNumber("worstHoldoutDb", calibration.WorstHoldoutDb);
        }
        pane.Post(_writer.End());
    }
}
#endif
