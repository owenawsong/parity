namespace WPods.Core;

// One retry series per interruption. A valid pose or a new wear state re-arms it.
internal sealed class HeadTrackingRetry
{
    private static readonly int[] DelaysMs = [1000, 3000, 8000];
    private long _nextAttempt;
    private int _failures;

    public bool Exhausted => _failures > DelaysMs.Length;
    public bool Due(long now) => !Exhausted && now >= _nextAttempt;

    public void Reset()
    {
        _failures = 0;
        _nextAttempt = 0;
    }

    public void Failed(long now)
    {
        if (Exhausted) return;
        if (_failures < DelaysMs.Length) _nextAttempt = now + DelaysMs[_failures];
        _failures++;
    }
}
