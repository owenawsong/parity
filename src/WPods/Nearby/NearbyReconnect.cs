namespace WPods.Nearby;

// Owned by one paired device, under NearbyService's lock. Silence is not a closed-lid report.
internal sealed class NearbyReconnect
{
    public bool Held { get; private set; }
    private bool _observedClosed;

    public void Hold(bool knownClosed)
    {
        Held = true;
        _observedClosed = knownClosed;
    }

    public void ObserveLid(bool open)
    {
        if (!Held) return;
        if (!open) _observedClosed = true;
        else if (_observedClosed) Held = false;
    }

    // Headphones without a lid use a new proximity arrival as their next opportunity.
    public void EndHeadphoneVisit() => Held = false;
}
