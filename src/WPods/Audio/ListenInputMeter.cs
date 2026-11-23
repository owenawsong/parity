using System.Runtime.InteropServices;

namespace WPods.Audio;

// Sample off the UI thread only while Live Listen is routing this source.
internal sealed class ListenInputMeter : IDisposable
{
    private const int Unavailable = int.MinValue;
    private readonly CancellationTokenSource _stop = new();
    private int _level = Unavailable;
    public string Endpoint { get; }
    public int? Level
    {
        get
        {
            int level = Volatile.Read(ref _level);
            return level == Unavailable ? null : level;
        }
    }

    internal ListenInputMeter(string endpoint)
    {
        Endpoint = endpoint;
        var token = _stop.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var meter = AudioEndpoints.OpenPeakMeter(Endpoint);
            if (meter is null) return;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Volatile.Write(ref _level, AudioEndpoints.ReadPeak(meter) ?? Unavailable);
                    await Task.Delay(125, token).ConfigureAwait(false);
                }
            }
            finally { Marshal.ReleaseComObject(meter); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (COMException) { } // The audio service or source may disappear.
        finally { Volatile.Write(ref _level, Unavailable); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
