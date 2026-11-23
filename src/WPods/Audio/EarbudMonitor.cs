#if PAIRSIDE_LIVE
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WPods.Core;

namespace WPods.Audio;

internal sealed class EarbudMonitor : IWaveProvider, IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly EarbudMonitorBuffer _buffer = new();
    private float _gain;
    private int _failed;
    internal bool Failed => Volatile.Read(ref _failed) != 0;
    internal bool Rendering => _buffer.Rendered > 0;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    internal EarbudMonitor(ulong address)
    {
        var enumerator = AudioEndpoints.CreateEnumerator();
        string id;
        try { id = AudioEndpoints.FindPairPlaybackEndpoint(enumerator, address) ?? throw new InvalidOperationException("No playback endpoint for this pair."); }
        finally { Marshal.ReleaseComObject(enumerator); }
        using var devices = new MMDeviceEnumerator();
        _device = devices.GetDevice(id);
        try
        {
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 10);
            try { _output.Init(this); }
            catch { _output.Dispose(); throw; }
            _output.PlaybackStopped += OnStopped;
        }
        catch { _device.Dispose(); throw; }
    }

    private void OnStopped(object? sender, StoppedEventArgs args) => Volatile.Write(ref _failed, 1);
    internal void SetVolume(int percent) => Volatile.Write(ref _gain, Math.Clamp(percent, 0, 50) / 50f);
    internal void Push(ReadOnlySpan<float> frames) => _buffer.Push(frames);
    internal void Start(int percent) { SetVolume(percent); _output.Play(); }
    public int Read(byte[] buffer, int offset, int count)
    {
        _buffer.Read(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count)), Volatile.Read(ref _gain));
        return count;
    }

    public void Dispose()
    {
        _output.PlaybackStopped -= OnStopped;
        _output.Dispose();
        _device.Dispose();
        Log.Info("listen", $"Earbud mic monitor stopped: decoded={_buffer.Received}, rendered={_buffer.Rendered}, overflow={_buffer.Dropped} frames");
    }
}
#endif
