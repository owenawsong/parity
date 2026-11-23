namespace WPods.Ui;

internal enum ConnectionIntent { Connect, Disconnect, Cancel, Wait, AlreadyConnected, AlreadyDisconnected }

internal static class ConnectionShortcut
{
    // An existing connection or attempt owns the shortcut. When idle use the last pair if still paired.
    public static ulong Target(ulong attached, ulong last, IEnumerable<ulong> pending, IEnumerable<ulong> paired)
    {
        if (attached != 0) return attached;
        ulong RecentOrFirst(IEnumerable<ulong> candidates)
        {
            ulong first = 0;
            foreach (ulong address in candidates)
            {
                if (address == 0) continue;
                if (address == last) return address;
                if (first == 0) first = address;
            }
            return first;
        }
        ulong inFlight = RecentOrFirst(pending);
        return inFlight != 0 ? inFlight : RecentOrFirst(paired);
    }

    public static ConnectionIntent Resolve(IReadOnlyList<string> group, bool attached, bool pending)
    {
        bool connect = group.Count == 2 ? !attached : group[0] == "connection.on";
        if (attached) return connect ? ConnectionIntent.AlreadyConnected : ConnectionIntent.Disconnect;
        if (pending) return connect ? ConnectionIntent.Wait : ConnectionIntent.Cancel;
        return connect ? ConnectionIntent.Connect : ConnectionIntent.AlreadyDisconnected;
    }
}
