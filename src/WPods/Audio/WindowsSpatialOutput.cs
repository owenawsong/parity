#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Devices;

namespace WPods.Audio;

internal static partial class AudioEndpoints
{
    internal sealed record SpatialOutput(string EndpointId, string Provider);

    private static async Task<(string Endpoint, string Interface)?> FindPairOutputAsync(ulong address, CancellationToken ct, bool requireStereo = false)
    {
        var pair = PairContainer(address);
        if (!pair.HasValue || pair == Guid.Empty) return null;

        const string containerKey = "System.Devices.ContainerId";
        const string instanceKey = "System.Devices.DeviceInstanceId";
        const string instancePrefix = @"SWD\MMDEVAPI\";
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector(),
            new[] { containerKey, instanceKey }).AsTask(ct).ConfigureAwait(false);
        string Id(DeviceInformation device) => ((string)device.Properties[instanceKey])[instancePrefix.Length..];
        var matches = devices.Where(d => d.IsEnabled && d.Properties.GetValueOrDefault(containerKey) is Guid id &&
            id == pair && d.Properties.GetValueOrDefault(instanceKey) is string instance &&
            instance.StartsWith(instancePrefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(Id, StringComparer.OrdinalIgnoreCase).ToArray();
        if (matches.Length == 0) return null;
        string? preferred = ReadDefaultRenderId();
        using var endpoints = new MMDeviceEnumerator();
        int? Channels(string id)
        {
            try
            {
                using var device = endpoints.GetDevice(id);
                using var client = device.AudioClient;
                return client.MixFormat.Channels;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException) { return null; }
        }
        string? output = SelectPairRenderEndpoint(matches.Select(Id).ToArray(), preferred, Channels, requireStereo);
        return output == null ? null : (output, matches.Single(d => Id(d) == output).Id);
    }

    internal static async Task<PlaybackChange> UsePairPlaybackAsync(ulong address, string originalOutput,
        Func<bool> stillSelected, CancellationToken ct)
    {
        var output = await FindPairOutputAsync(address, ct).ConfigureAwait(false);
        if (output is null) return PlaybackChange.Unavailable;
        ct.ThrowIfCancellationRequested();
        var policy = (IEndpointPolicy)(object)new PolicyConfigClient();
        try
        {
            // A call can end or the user can choose new speakers while WinRT enumerates the pair.
            // Only replace the exact media output shown when the user clicked.
            PlaybackChange change = ComparePlaybackChoice(originalOutput, output.Value.Endpoint, ReadDefaultRenderId());
            if (change != PlaybackChange.Pending) return change;
            ct.ThrowIfCancellationRequested();
            if (!stillSelected()) return PlaybackChange.Superseded;
            if (policy.SetDefaultEndpoint(output.Value.Endpoint, EMultimedia) < 0) return PlaybackChange.Unavailable;
            for (int i = 0; i < 8; i++)
            {
                change = ComparePlaybackChoice(originalOutput, output.Value.Endpoint, ReadDefaultRenderId());
                if (change != PlaybackChange.Pending) return change;
                await Task.Delay(80, ct).ConfigureAwait(false);
            }
            return PlaybackChange.Unavailable;
        }
        finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(policy); }
    }

    internal static async Task<string?> SpatialPlaybackIdAsync(ulong address, CancellationToken ct, string? previous = null)
    {
        if (previous != null)
        {
            var devices = CreateEnumerator();
            try
            {
                if (devices.GetDevice(previous, out var endpoint) >= 0)
                {
                    try { if (IsActivePairPlaybackEndpoint(endpoint, address)) return previous; }
                    finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(endpoint); }
                }
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(devices); }
        }
        // The live renderer just needs the endpoint. Provider discovery belongs to the Windows settings page.
        var output = await FindPairOutputAsync(address, ct, requireStereo: true).ConfigureAwait(false);
        return output?.Endpoint;
    }

    internal static async Task<SpatialOutput?> ReadSpatialOutputAsync(ulong address, CancellationToken ct)
    {
        var output = await FindPairOutputAsync(address, ct).ConfigureAwait(false);
        if (output is null) return null;
        // The WinRT spatial API expects an interface ID. A raw IMMDevice endpoint ID can
        // silently return an empty configuration even for an output supporting Windows Sonic.
        string provider = "unknown";
        try
        {
            var spatial = SpatialAudioDeviceConfiguration.GetForDeviceId(output.Value.Interface);
            if (spatial is not null) provider = SpatialProvider(spatial.DefaultSpatialAudioFormat, spatial.IsSpatialAudioSupported);
        }
        catch (Exception) { /* Windows device properties remain available when the provider cannot be read. */ }
        return new(output.Value.Endpoint, provider);
    }

    private static string? ReadDefaultRenderId() => ReadDefaultEndpointId(ERender, EMultimedia);

    internal static string SpatialProvider(string? subtype, bool supported)
    {
        if (!Guid.TryParse(subtype, out var format)) return supported ? "unknown" : "unsupported";
        if (format == Guid.Empty) return supported ? "off" : "unsupported";
        if (format == Guid.Parse(SpatialAudioFormatSubtype.WindowsSonic)) return "sonic";
        if (format == Guid.Parse(SpatialAudioFormatSubtype.DolbyAtmosForHeadphones)) return "dolbyHeadphones";
        if (format == Guid.Parse(SpatialAudioFormatSubtype.DolbyAtmosForHomeTheater)) return "dolbyTheater";
        if (format == Guid.Parse(SpatialAudioFormatSubtype.DTSHeadphoneX)) return "dtsHeadphones";
        return "other";
    }
}
#endif
