#if PAIRSIDE_LIVE
using WPods.Audio;
using WPods.Core;

namespace WPods.Ui;

internal sealed partial class TrayApp
{
    private const int MaxAppSoundProfiles = 24;

    private static string AppSoundKey(ulong address, string source) => $"{Mac.Format(address)}/{source}";

    private AppSoundProfile? AppSoundFor(ulong address, string source) =>
        _prefs.AppSoundProfiles?.GetValueOrDefault(AppSoundKey(address, source));

    private (int Angle, string Level) SpatialRenderPrefs(ulong address)
    {
        var app = address == 0 ? null : AppSoundFor(address, _spatialPlayer.Key);
        return (SpatialStereo.SpeakerAngle(app?.SpeakerAngle ?? _prefs.SpatialSpeakerAngle),
            SpatialStereo.LevelChoice(app?.PlaybackLevel ?? _prefs.SpatialLevel));
    }

    private void SetSpatialRenderPrefs(int? angle = null, string? level = null)
    {
        ulong address = _connections.Session?.Address ?? 0;
        string source = _spatialPlayer.Key;
        if (address != 0 && AppSoundFor(address, source) is { } app)
            WriteAppSound(address, source, app with { SpeakerAngle = angle ?? app.SpeakerAngle, PlaybackLevel = level ?? app.PlaybackLevel });
        else
            SavePrefs(_prefs with { SpatialSpeakerAngle = angle ?? _prefs.SpatialSpeakerAngle, SpatialLevel = level ?? _prefs.SpatialLevel });
        ApplySpatialRenderPrefs(address);
        SendSpotify();
    }

    private void ApplySpatialRenderPrefs(ulong address)
    {
        if (_spotifySession?.Address != address || _spotify == null) return;
        var (angle, level) = SpatialRenderPrefs(address);
        _spotify.SetSpeakerAngle(angle);
        _spotify.SetLevel(level);
    }

    private SpatialApp? ResolveSoundSource(ulong address, string? key)
    {
        if (SpatialApp.FromKey(key) is { } app) return app;
        if (key == null || AppSoundFor(address, key) is not { } saved) return null;
        return SpatialApp.RestoreDescriptor(key, saved.ProcessName, saved.Identity);
    }

    private IEnumerable<SpatialApp> SavedSoundSources(ulong address)
    {
        if (address == 0 || _prefs.AppSoundProfiles is not { } profiles) yield break;
        string prefix = AppSoundKey(address, "");
        foreach (var key in profiles.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            if (ResolveSoundSource(address, key[prefix.Length..]) is { } app) yield return app;
    }

    private void WriteAppSound(ulong address, string source, AppSoundProfile? profile, UiPrefs? prefs = null)
    {
        prefs ??= _prefs;
        var profiles = prefs.AppSoundProfiles is { } saved
            ? new Dictionary<string, AppSoundProfile>(saved, StringComparer.Ordinal)
            : new Dictionary<string, AppSoundProfile>(StringComparer.Ordinal);
        string key = AppSoundKey(address, source);
        if (profile == null) profiles.Remove(key);
        else profiles[key] = profile;
        SavePrefs(prefs with { AppSoundProfiles = profiles });
    }

    private async void SetRememberAppSound(ulong address, string source, bool enabled)
    {
        await _spotifyGate.WaitAsync();
        try
        {
            var app = _spatialPlayer;
            if (_exiting || app.Key != source || _connections.Session is not { State.Status: SessionStatus.Ready } session ||
                session.Address != address) return;
            bool remembered = AppSoundFor(address, source) != null;
            if (enabled == remembered) return;
            if (enabled)
            {
                if (SavedSoundSources(address).Take(MaxAppSoundProfiles).Count() >= MaxAppSoundProfiles)
                {
                    SendSpotify();
                    return;
                }
                string mode = _spatialResume?.Session.Address == address ? _spatialResume.Mode :
                    _spotifySession?.Address == address ? _spotifyMode : "stereo";
                var bands = PcEqFor(address)?.Bands;
                var (angle, level) = SpatialRenderPrefs(address);
                WriteAppSound(address, source, new(mode, _pcEqAddress == address,
                    PcEqualizer.Valid(bands) ? bands! : FlatPcEq, app.ProcessName, app.Identity, angle, level));
            }
            else
            {
                WriteAppSound(address, source, null);
                if (_spotifySession?.Address == address) ApplyPcEq(address);
                ApplySpatialRenderPrefs(address);
            }
            SendPcEq(address);
            SendSpotify();
        }
        finally { _spotifyGate.Release(); }
    }

    private void RememberAppMode(ulong address, string mode)
    {
        string source = _spatialPlayer.Key;
        if (AppSoundFor(address, source) is { } profile && profile.Mode != mode)
            WriteAppSound(address, source, profile with { Mode = mode });
    }

    private void RememberAppEqualizer(ulong address, bool enabled)
    {
        string source = _spatialPlayer.Key;
        if (AppSoundFor(address, source) is { } profile && profile.Equalizer != enabled)
            WriteAppSound(address, source, profile with { Equalizer = enabled });
    }
}
#endif
