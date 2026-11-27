#if PAIRSIDE_LIVE
using System.Text.Json;
using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private static readonly int[] FlatPcEq = new int[PcEqualizer.BandCount];
    private const int MaxPcEqPresets = 12;
    private ulong _pcEqAddress;
    private bool _pcEqCompareFlat;
    private bool _lastPcEqSignal;

    private PcEqProfile? PcEqFor(ulong address) => _prefs.PcEqualizers?.GetValueOrDefault(Mac.Format(address));

    private static int[]? ReadPcEqCurve(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != PcEqualizer.BandCount) return null;
        var curve = new int[PcEqualizer.BandCount];
        int i = 0;
        foreach (var band in value.EnumerateArray())
            if (band.ValueKind != JsonValueKind.Number || !band.TryGetInt32(out curve[i++])) return null;
        return PcEqualizer.Valid(curve) ? curve : null;
    }

    private void SetPcEqEnabled(ulong address, bool enabled)
    {
        if (enabled && (_connections.Session is not { State.Status: SessionStatus.Ready } session ||
            session.Address != address)) return;
        if (!enabled && _pcEqAddress != address) return;

        if (enabled)
        {
            _pcEqAddress = address;
            _pcEqCompareFlat = false;
        }
        else ClearPcEqMode();
        RememberAppEqualizer(address, enabled);
        if (_spotifySession?.Address == address && _spotify != null)
        {
            ApplyPcEq(address);
            // The shared output keeps playing dry through the same stream.
            // Restoring the Windows default here would reopen every player.
            SendSpotify();
        }
        else if (_spatialStartingMode is null && _spatialResume?.Session.Address != address)
        {
            if (enabled) SetSpotifyMode(address, "stereo");
            else SendSpotify();
        }
        else if (!enabled && (_spatialStartingMode == "stereo" || _spatialResume is { Mode: "stereo" }))
        {
            _spatialStartup?.Cancel();
            SetSpotifyMode(address, "stereo");
        }
        else SendSpotify();
        SendPcEq(address);
    }

    private void ReturnToDirectPlayback(ulong address)
    {
        if (_spotify is not null && _spotifySession?.Address == address &&
            _spotifyMode == "stereo" && _pcEqAddress != address)
            SetSpotifyMode(address, "stereo");
    }

    private void ClearPcEqMode()
    {
        ulong address = _pcEqAddress;
        _pcEqAddress = 0;
        _pcEqCompareFlat = false;
        if (address != 0) SendPcEq(address);
    }

    private void ApplyPcEq(ulong address)
    {
        bool enabled = _pcEqAddress == address;
        if (enabled)
        {
            var bands = AppSoundFor(address, _spatialPlayer.Key)?.Bands ?? PcEqFor(address)?.Bands;
            _spotify?.SetEqCurve(PcEqualizer.Valid(bands) ? bands! : FlatPcEq);
        }
        _spotify?.SetEqMode(enabled, _pcEqCompareFlat);
    }

    private void ComparePcEq(ulong address, bool flat)
    {
        if (_pcEqAddress != address || _spotifySession?.Address != address || _spotify is null) return;
        _pcEqCompareFlat = flat;
        _spotify.SetEqMode(enabled: true, flat);
        SendPcEq(address);
    }

    private void EditPcEq(ulong address, int[] bands, string command, string? name, int request, string? scope)
    {
        string? activeScope = AppSoundFor(address, _spatialPlayer.Key) == null ? null : _spatialPlayer.Key;
        var appProfile = scope == null ? null : AppSoundFor(address, scope);
        if (scope != null && appProfile == null)
        {
            if (request > 0) SendPcEq(address, request);
            return;
        }
        if (scope == activeScope && _spotifySession?.Address == address && _pcEqAddress == address)
            _spotify?.SetEqCurve(bands);
        if (command == "previewPcEq" || Find(address) is null) return;

        var previous = PcEqFor(address);
        var presets = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        if (previous?.Presets is { } saved)
        {
            foreach (var item in saved)
                if (ValidPresetName(item.Key) && PcEqualizer.Valid(item.Value) && presets.Count < MaxPcEqPresets)
                    presets.TryAdd(item.Key, item.Value);
        }
        if (command == "savePcEqPreset")
        {
            name = name?.Trim();
            if (!ValidPresetName(name) || (!presets.ContainsKey(name!) && presets.Count >= MaxPcEqPresets))
            {
                SendPcEq(address, request, presetRejected: true);
                return;
            }
            presets[name!] = bands;
        }
        else if ((appProfile?.Bands ?? previous?.Bands) is { } curve && curve.AsSpan().SequenceEqual(bands))
        {
            SendPcEq(address, request);
            return;
        }

        var profiles = _prefs.PcEqualizers is { } all
            ? new Dictionary<string, PcEqProfile>(all, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PcEqProfile>(StringComparer.OrdinalIgnoreCase);
        var prefs = _prefs;
        if (appProfile == null || command == "savePcEqPreset")
        {
            profiles[Mac.Format(address)] = new(appProfile == null ? bands : previous?.Bands ?? FlatPcEq, presets);
            prefs = prefs with { PcEqualizers = profiles };
        }
        if (appProfile != null) WriteAppSound(address, scope!, appProfile with { Bands = bands }, prefs);
        else SavePrefs(prefs);
        SendPcEq(address, request);
    }

    private static bool ValidPresetName(string? name) => name is { Length: > 0 and <= 32 } && !name.Any(char.IsControl);

    private void SendPcEq(ulong address, int request = 0, bool presetRejected = false)
    {
        if (_exiting || !MainLive || !_mainInitSent) return;
        var profile = PcEqFor(address);
        var app = AppSoundFor(address, _spatialPlayer.Key);
        var curve = app?.Bands ?? profile?.Bands;
        var w = _writer.Begin("pcEq");
        w.WriteString("id", Mac.Format(address));
        if (app != null) w.WriteString("scope", _spatialPlayer.Key);
        if (request > 0) w.WriteNumber("request", request);
        w.WriteBoolean("enabled", _pcEqAddress == address);
        w.WriteBoolean("flat", _pcEqAddress == address && _pcEqCompareFlat);
        if (presetRejected) w.WriteBoolean("presetRejected", true);
        w.WriteStartArray("bands");
        foreach (int db in PcEqualizer.Valid(curve) ? curve! : FlatPcEq) w.WriteNumberValue(db);
        w.WriteEndArray();
        w.WriteStartArray("presets");
        if (profile?.Presets is { } presets)
        {
            foreach (var (name, bands) in presets.Where(p => ValidPresetName(p.Key) && PcEqualizer.Valid(p.Value)).Take(MaxPcEqPresets))
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteStartArray("bands");
                foreach (int db in bands) w.WriteNumberValue(db);
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        PostMain(_writer.End());
    }
}
#endif
