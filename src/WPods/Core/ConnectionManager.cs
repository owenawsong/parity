using System.ComponentModel;
using System.IO.Pipes;

namespace WPods.Core;

/// <summary>Why a pair is not attached although WPods is not trying to attach it.</summary>
public enum IdleReason
{
    /// <summary>Attached, link down, or WPods is attaching / retrying.</summary>
    None,
    /// <summary>The user pressed Disconnect: no auto-attach until the link drops or the user connects the pair.</summary>
    UserDisconnected,
    /// <summary>
    /// Every fast retry failed with the link up (slow retries continue), or 3 sessions in a row got no initial report.
    /// Tried again at once on the next link-up, case opening or user Connect.
    /// </summary>
    AttachGaveUp,
}

/// <summary>
/// A paired AirPods device for the device list. Capabilities is the live set while attached, else every capability
/// any attach reported plus the model baseline.
/// </summary>
public sealed record KnownDevice(
    ulong Address, string Name, string? ModelNumber, bool LinkUp, bool HasKeys, bool Attached, DeviceCapabilities Capabilities,
    IdleReason Idle, StemModes StemModes, string? FirmwareBuild = null, bool FirmwareLive = false);

/// <summary>
/// Owns the single AAP session. Never pages: attaches only pairs whose ACL link Windows reports up (last-used first)
/// and retries while it stays up. Events fire on thread-pool or CM notification threads; handlers must not block.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    // Windows may keep the ACL up after a disconnect request (e.g. buds in a closed case); a second request follows.
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(3);
    // The AACP channel may refuse for 1-3 s after the ACL comes up while A2DP/AVRCP/HFP are set up, or longer with
    // the buds in an open case; these retries cover about 15.5 s.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];
    // After the fast retries, one attempt per 30 s for as long as the link stays up.
    private static readonly TimeSpan SlowRetryDelay = TimeSpan.FromSeconds(30);
    // Sessions in a row without an initial settings report before the pair is marked AttachGaveUp.
    private const int NoReportSessions = 3;

    private readonly BluetoothLink _link = new();
    // Serializes every attach/detach decision; _session writes, _retries, _retrySerial and _disposed are guarded by it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ulong, AttachRetry> _retries = [];
    // The pairs with a pending retry, for readers without the gate (the UI): changed only with _retries.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _retrying = new();
    // Guards _idle and _linkDownWaits (read by GetDevices and the CM notification thread without the gate).
    private readonly Lock _idleLock = new();
    private readonly Dictionary<ulong, IdleReason> _idle = [];
    private readonly Dictionary<ulong, TaskCompletionSource> _linkDownWaits = [];
    private (ulong Address, CancellationTokenSource Cancellation)? _openingControl;
    private bool _closing;
    private AapSession? _session;
    // Closed session still shown during an automatic re-attach after no initial report; set only under the gate.
    private AapSession? _reattaching;
    private long _retrySerial;
    private bool _disposed;

    public ConnectionManager(SettingsStore settings)
    {
        Settings = settings;
    }

    public SettingsStore Settings { get; }
    internal ConnectionTimeline Timeline { get; } = new();

    public AapSession? Session => Volatile.Read(ref _session);

    /// <summary>UI display only: hold the previous session while retrying an incomplete initial handshake.</summary>
    public AapSession? ShownSession => Session ?? Volatile.Read(ref _reattaching);

    public ulong LastUsed => Settings.LastUsed;

    /// <summary>A session was opened; raised before it reads its first packet, so subscribers miss nothing.</summary>
    public event Action<AapSession>? Attached;

    /// <summary>The attached session has completed its handshake.</summary>
    internal event Action<AapSession>? Ready;

    /// <summary>The session was closed (link down, user disconnect, switch or failure).</summary>
    public event Action<AapSession>? Detached;

    /// <summary>ACL link of a paired AirPods went up (true) or down (false).</summary>
    public event Action<ulong, bool>? LinkChanged;
    internal event Action<ulong>? DisconnectRequested;

    /// <summary>The result of <see cref="GetDevices"/> changed.</summary>
    public event Action? DevicesChanged;

    /// <summary>Applying the Hands-Free policy on attach failed.</summary>
    public event Action<ulong, Exception>? HandsFreeFailed;

    /// <summary>True when the attach-time Hands-Free policy starts a real change in Windows, false when it ends.</summary>
    public event Action<ulong, bool>? HandsFreeChanging;

    public void Start()
    {
        Log.Info("conn", $"start, lastUsed={Mac.Format(Settings.LastUsed)}");
        _link.LinkChanged += OnLinkChanged;
        _link.DevicesChanged += OnLinkDevicesChanged;
        _link.Start();
        foreach (var device in _link.Snapshot())
            if (device.Up) Timeline.Record(device.Address, ConnectionEvent.LinkUp);
        CheckPairings();
        // The initial enumeration raises no events; subscribers created before Start need the first list.
        DevicesChanged?.Invoke();
        _ = Task.Run(() => OnLinkAsync(0));
    }

    public List<KnownDevice> GetDevices()
    {
        AapSession? session = ShownSession;
        var list = new List<KnownDevice>();
        foreach (LinkDevice device in _link.Snapshot())
        {
            bool attached = session?.Address == device.Address;
            DeviceModel? remembered = Settings.GetModel(device.Address);
            string? modelNumber = (attached ? session!.State.ModelNumber : null) ?? remembered?.Number
                ?? Models.ModelNumberOf(device.ProductId);
            string name = attached ? session!.State.Name ?? device.Name : device.Name;
            DeviceCapabilities capabilities = attached
                ? session!.State.Capabilities
                : Settings.GetCapabilities(device.Address) | (Models.FromModelNumber(modelNumber)?.Capabilities ?? DeviceCapabilities.None);
            string? liveFirmware = attached && session == Session ? session!.State.FirmwareBuild : null;
            string? firmware = liveFirmware ?? (remembered?.Number == modelNumber ? remembered?.FirmwareBuild : null);
            list.Add(new KnownDevice(
                device.Address, name, modelNumber, device.Up, Settings.HasKeys(device.Address), attached, capabilities,
                attached ? IdleReason.None : GetIdle(device.Address), Settings.GetStemModes(device.Address),
                firmware, liveFirmware is not null));
        }
        return list;
    }

    /// <summary>
    /// Switches the session to a pair whose link is already up. Never pages. On failure a fresh retry series runs,
    /// so the pair may still attach shortly after (<see cref="Attached"/>).
    /// </summary>
    public Task<bool> ConnectAsync(ulong address) => ConnectAsync(address, null);

    internal async Task<bool> ConnectAsync(ulong address, Func<bool>? requestIsCurrent, CancellationToken ct = default)
    {
        Log.Info("conn", $"ConnectAsync {Mac.Format(address)}: waiting for gate");
        long waitStart = Environment.TickCount64;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Log.Info("conn", $"ConnectAsync {Mac.Format(address)}: gate after {Environment.TickCount64 - waitStart} ms, session={Describe(_session)}, linkUp={_link.IsUp(address)}");
            if (_disposed || requestIsCurrent?.Invoke() == false)
            {
                return false;
            }
            ct.ThrowIfCancellationRequested();
            SetIdle(address, IdleReason.None, "user connect");
            if (_session?.Address == address)
            {
                Log.Info("conn", "ConnectAsync -> true (already attached)");
                return true;
            }
            if (!_link.IsUp(address))
            {
                Log.Info("conn", "ConnectAsync -> false (link not up)");
                return false;
            }
            bool attached = await SwitchLocked(address, "user", requestIsCurrent, ct).ConfigureAwait(false);
            Log.Info("conn", attached ? "ConnectAsync -> true" : "ConnectAsync -> false (retries continue)");
            return attached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Switches the session to the new default output pair unless the user disconnected it. Never pages.</summary>
    public async Task FollowDefaultOutputAsync(ulong address)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                await FollowLocked(address, "default output changed").ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mac-like full disconnect: detaches at once, then asks Windows to drop the audio link, again if it is still up
    /// 3 s later. False when there is no session or both requests failed (the pair is then attached again).
    /// </summary>
    public async Task<bool> DisconnectAsync(ulong? expectedAddress = null)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            return await DisconnectCoreAsync(done, expectedAddress).ConfigureAwait(false);
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>Completes when a pending Disconnect of the pair is over; Windows may still drop the link for it.</summary>
    public Task DisconnectFinished(ulong address) =>
        Volatile.Read(ref _pendingDisconnect) is { } p && p.Address == address ? p.Done : Task.CompletedTask;

    private sealed record PendingDisconnect(ulong Address, Task Done);

    private PendingDisconnect? _pendingDisconnect;

    private async Task<bool> DisconnectCoreAsync(TaskCompletionSource done, ulong? expectedAddress)
    {
        ulong address;
        Task linkDown;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A concurrent switch may have finished while the UI stopped the mic or this request waited on the gate.
            if (expectedAddress is { } expected && _session?.Address != expected) return true;
            if (_session is not { } session)
            {
                Log.Info("conn", "DisconnectAsync -> false (no session)");
                return false;
            }
            address = session.Address;
            Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}");
            Volatile.Write(ref _pendingDisconnect, new PendingDisconnect(address, done.Task));
            DisconnectRequested?.Invoke(address);
            RemoveRetryLocked(address);
            linkDown = WaitForLinkDown(address);
            SetIdle(address, IdleReason.UserDisconnected, "user disconnect");
            await DetachLocked("user disconnect").ConfigureAwait(false);
            await AttachOtherUpLockedAsync(address).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        bool accepted = false;
        try
        {
            // Checked again before attempt 2: a lid open or Connect within the wait clears the flag, and a second
            // request would drop the link the user just reconnected.
            for (int attempt = 1; attempt <= 2 && _link.IsUp(address) && GetIdle(address) == IdleReason.UserDisconnected; attempt++)
            {
                int hr = await Task.Run(() => Native.wpn_bt_disconnect_audio(address)).ConfigureAwait(false);
                Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}: disconnect audio attempt {attempt}/2 hr=0x{hr:X8}");
                if (hr < 0)
                {
                    continue;
                }
                accepted = true;
                try
                {
                    await linkDown.WaitAsync(DisconnectTimeout).ConfigureAwait(false);
                    break;
                }
                catch (TimeoutException)
                {
                    Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}: link still up {DisconnectTimeout.TotalSeconds} s after attempt {attempt}");
                }
            }
        }
        finally
        {
            lock (_idleLock)
            {
                _linkDownWaits.Remove(address);
            }
        }
        bool linkUp = _link.IsUp(address);
        Log.Info("conn", $"DisconnectAsync {Mac.Format(address)}: final link {(linkUp ? "up" : "down")}, accepted by Windows={accepted}, idle={GetIdle(address)}");
        if (accepted || !linkUp)
        {
            return true;
        }
        // Both requests rejected: the pair is still connected; show its real state.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (GetIdle(address) == IdleReason.UserDisconnected)
            {
                SetIdle(address, IdleReason.None, "disconnect failed");
                if (_session is null && _link.IsUp(address))
                {
                    await AttachLockedAsync(address, "disconnect failed", newSeries: true).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        Log.Warn("conn", $"DisconnectAsync {Mac.Format(address)} -> false (both requests failed)");
        return false;
    }

    /// <summary>A Hands-Free change applies at once to the attached device; throws if Windows rejects it (still saved).</summary>
    public async Task SetHostSettingsAsync(ulong address, HostSettings settings)
    {
        bool handsFreeChanged = Settings.Get(address).DisableHandsFree != settings.DisableHandsFree;
        Log.Info("conn", $"host settings {Mac.Format(address)}: disableHandsFree={settings.DisableHandsFree} playbackKeepAlive={settings.LowLatency} micKeepAlive={settings.LowLatencyMic} micMode={settings.MicMode} duckPercent={settings.DuckPercent}");
        Settings.Set(address, settings);
        if (handsFreeChanged && Session is { } session && session.Address == address)
        {
            if (settings.DisableHandsFree)
            {
                await ApplyHandsFreeAsync(session, disable: true).ConfigureAwait(false);
            }
            else
            {
                await ApplyHandsFreeAsync(session, disable: false, retries: 3, retryDelayMs: 1000).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Log.Info("conn", "dispose");
        lock (_idleLock)
        {
            _closing = true;
            _openingControl?.Cancellation.Cancel();
        }
        _link.Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _retries.Clear();
            _retrying.Clear();
            if (_session is not null)
            {
                await DetachLocked("exit").ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // A pair was added or removed (CM notification thread): relay, then check for re-pairs off that thread.
    private void OnLinkDevicesChanged()
    {
        DevicesChanged?.Invoke();
        _ = Task.Run(() =>
        {
            if (CheckPairings())
            {
                DevicesChanged?.Invoke();
            }
        });
    }

    // True when a re-paired pair's remembered data was cleared.
    private bool CheckPairings()
    {
        bool cleared = false;
        foreach (LinkDevice device in _link.Snapshot())
        {
            cleared |= CheckPairing(device.Address);
            SeedMicDefaults(device.Address, Settings.GetModel(device.Address)?.Number ?? Models.ModelNumberOf(device.ProductId));
        }
        return cleared;
    }

    private void SeedMicDefaults(ulong address, string? modelNumber)
    {
        try
        {
            Settings.SeedMicDefaults(address, modelNumber);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("conn", $"mic defaults for {Mac.Format(address)} not saved: {e.Message}");
        }
    }

    // A new install date of the pair's Windows device entry means it was removed and paired again: its remembered
    // capabilities and pod settings are cleared (true). An unreadable date (e.g. radio off) changes nothing.
    private bool CheckPairing(ulong address)
    {
        if (Native.GetDeviceInstallDate(address) is not { } date)
        {
            Log.Info("conn", $"pairing date of {Mac.Format(address)}: not readable, unchanged");
            return false;
        }
        try
        {
            return Settings.SetPairingDate(address, date, stored => Native.HasDeviceEntryDated(address, stored));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Checked again on the next enumeration or attach.
            Log.Warn("conn", $"pairing date not saved: {e.Message}");
            return false;
        }
    }

    // Called on the CM notification thread: relay, then decide off that thread.
    private void OnLinkChanged(ulong address, bool up)
    {
        Log.Info("conn", $"link {Mac.Format(address)} {(up ? "up" : "down")}");
        Timeline.Record(address, up ? ConnectionEvent.LinkUp : ConnectionEvent.LinkDown);
        if (!up)
        {
            lock (_idleLock)
            {
                if (_openingControl is { } opening && opening.Address == address && !_link.IsUp(address))
                {
                    opening.Cancellation.Cancel();
                }
                if (_linkDownWaits.Remove(address, out TaskCompletionSource? wait))
                {
                    wait.TrySetResult();
                }
            }
        }
        LinkChanged?.Invoke(address, up);
        DevicesChanged?.Invoke();
        _ = Task.Run(() => OnLinkAsync(address));
    }

    // Re-reads the current link state rather than trusting event order. address 0 = startup.
    private async Task OnLinkAsync(ulong address)
    {
        long waitStart = Environment.TickCount64;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }
            bool up = address != 0 && _link.IsUp(address);
            Log.Info("conn", $"link decision for {(address == 0 ? "startup" : Mac.Format(address))}: gate after {Environment.TickCount64 - waitStart} ms, linkUp now={(address == 0 ? "n/a" : up.ToString())}, session={Describe(_session)}");
            if (address != 0 && !up)
            {
                RemoveRetryLocked(address);
                SetIdle(address, IdleReason.None, "link down");
                if (_session?.Address == address)
                {
                    await DetachLocked("link down").ConfigureAwait(false);
                }
                await AttachOtherUpLockedAsync(address).ConfigureAwait(false);
                return;
            }
            if (address != 0)
            {
                // Every link-up event starts a new attach series for that pair.
                RemoveRetryLocked(address);
                if (GetIdle(address) == IdleReason.AttachGaveUp)
                {
                    SetIdle(address, IdleReason.None, "link up again");
                }
            }
            if (_session is { } current)
            {
                if (address != 0 && current.Address != address)
                {
                    if (DefaultOutputWatcher.IsMediaOutput(address))
                    {
                        await FollowLocked(address, "link up as the default output").ConfigureAwait(false);
                        return;
                    }
                    Log.Info("follow", $"link up {Mac.Format(address)}: not the media output, keeping session={Describe(current)}");
                }
                Log.Info("conn", "link decision: a session is attached, nothing to do");
                return;
            }
            ulong playing = DefaultOutputWatcher.FindMediaOutputPair(
                _link.Snapshot().Where(device => CanAutoAttach(device.Address)).Select(device => device.Address));
            // On reopen Windows may already be playing to a different pair
            // than the one that last held the control session.
            if (playing != 0)
            {
                if (_retrying.ContainsKey(playing) || await AttachLockedAsync(playing, "reattach to current Windows output", newSeries: false).ConfigureAwait(false)
                    || CanAutoAttach(playing))
                {
                    return;
                }
            }
            ulong last = Settings.LastUsed;
            if (last != playing && CanAutoAttach(last) &&
                await AttachLockedAsync(last, address == 0 ? "startup last-used" : "link-up last-used", newSeries: false).ConfigureAwait(false))
            {
                return;
            }
            ulong candidate = address != 0 ? address : _link.FirstUp();
            if (candidate != last && candidate != playing && CanAutoAttach(candidate))
            {
                await AttachLockedAsync(candidate, address == 0 ? "startup first-up" : "link-up", newSeries: false).ConfigureAwait(false);
            }
            else
            {
                Log.Info("conn", $"link decision: nothing more to attach (candidate={Mac.Format(candidate)} idle={GetIdle(candidate)}, lastUsed={Mac.Format(last)} idle={GetIdle(last)}, retry pending={_retries.ContainsKey(candidate) || _retries.ContainsKey(last)})");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Never pages.
    private async Task FollowLocked(ulong address, string why)
    {
        if (_session?.Address == address)
        {
            Log.Info("follow", $"{Mac.Format(address)} ({why}): already attached");
            return;
        }
        if (!_link.IsUp(address))
        {
            Log.Info("follow", $"{Mac.Format(address)} ({why}): link not up, keeping session={Describe(_session)}");
            return;
        }
        if (GetIdle(address) == IdleReason.UserDisconnected)
        {
            Log.Info("follow", $"{Mac.Format(address)} ({why}): user disconnected, keeping session={Describe(_session)}");
            return;
        }
        // A previous route notification can wait behind an attach. Recheck
        // under the connection gate before making any change to the session.
        if (!DefaultOutputWatcher.IsMediaOutput(address)) return;
        Log.Info("follow", $"{Mac.Format(address)} ({why}): switching from session={Describe(_session)}");
        await SwitchLocked(address, "follow default output", () => DefaultOutputWatcher.IsMediaOutput(address)).ConfigureAwait(false);
    }

    private Task<bool> SwitchLocked(ulong address, string reason, Func<bool>? requestIsCurrent, CancellationToken ct = default)
    {
        RemoveRetryLocked(address);
        return TrySwitchLockedAsync(address, new SwitchRequest(reason, requestIsCurrent, ct));
    }

    private bool CanSwitch(ulong address, SwitchRequest request) =>
        !_disposed && !request.Cancellation.IsCancellationRequested && CanRetry(address) && request.IsCurrent?.Invoke() != false;

    private async Task<bool> TrySwitchLockedAsync(ulong address, SwitchRequest request)
    {
        if (!CanSwitch(address, request)) return false;
        var previous = _session;
        // Different Bluetooth links can open a control channel independently.
        // Keep the working session until the target has accepted its channel.
        var stream = await OpenControlLockedAsync(address, request.Reason, request.Cancellation).ConfigureAwait(false);
        try
        {
            if (!CanSwitch(address, request)) return false;
            if (stream is null)
            {
                await ScheduleRetryLockedAsync(address, request).ConfigureAwait(false);
                return false;
            }
            if (previous is not null)
            {
                await DetachLocked(request.Reason == "user" ? "switch" : request.Reason).ConfigureAwait(false);
                if (!CanSwitch(address, request))
                {
                    stream.Dispose();
                    stream = null;
                    if (CanAutoAttach(previous.Address))
                    {
                        RemoveRetryLocked(previous.Address);
                        await ScheduleRetryLockedAsync(previous.Address).ConfigureAwait(false);
                    }
                    return false;
                }
            }
            RemoveRetryLocked(address);
            StartControlLocked(address, stream);
            stream = null;
            return true;
        }
        finally { stream?.Dispose(); }
    }

    private bool CanAutoAttach(ulong address) =>
        !Volatile.Read(ref _closing) && address != 0 && _link.IsUp(address) && GetIdle(address) == IdleReason.None;

    private bool CanRetry(ulong address) =>
        !Volatile.Read(ref _closing) && _link.IsUp(address) && GetIdle(address) != IdleReason.UserDisconnected;

    /// <summary>
    /// Case lid opened or a bud left the case: as on iPhone and Mac, a held-back pair whose link is up is attached.
    /// Called on the BLE thread; returns at once.
    /// </summary>
    public void OnCaseOpened(ulong address)
    {
        if (GetIdle(address) != IdleReason.None && _link.IsUp(address))
        {
            _ = Task.Run(() => ReleaseOnCaseOpenedAsync(address));
        }
    }

    private async Task ReleaseOnCaseOpenedAsync(ulong address)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !DisconnectFinished(address).IsCompleted || GetIdle(address) == IdleReason.None || !_link.IsUp(address))
            {
                return;
            }
            SetIdle(address, IdleReason.None, "lid opened or bud out of case");
        }
        finally
        {
            _gate.Release();
        }
        await OnLinkAsync(address).ConfigureAwait(false);
    }

    // One attempt now; on failure the next retry is scheduled. newSeries restarts the retry count.
    private async Task<bool> AttachLockedAsync(ulong address, string reason, bool newSeries)
    {
        if (newSeries)
        {
            RemoveRetryLocked(address);
        }
        if (await TryAttachLockedAsync(address, reason).ConfigureAwait(false))
        {
            return true;
        }
        await ScheduleRetryLockedAsync(address).ConfigureAwait(false);
        return false;
    }

    /// <summary>A retry is pending, as opposed to waiting for a first attach.</summary>
    public bool IsRetrying(ulong address) => _retrying.ContainsKey(address);

    private void SetRetryLocked(ulong address, AttachRetry retry)
    {
        _retries[address] = retry;
    }

    private void RemoveRetryLocked(ulong address)
    {
        _retries.Remove(address);
        _retrying.TryRemove(address, out _);
    }

    // Replaces any pending retry; after the fast series the pair is marked AttachGaveUp and retried slowly.
    private async Task ScheduleRetryLockedAsync(ulong address, SwitchRequest? request = null)
    {
        if (_disposed || !CanRetry(address) || (request is not null && !CanSwitch(address, request)))
        {
            RemoveRetryLocked(address);
            Log.Info("conn", $"attach {Mac.Format(address)}: no retry (linkUp={_link.IsUp(address)}, idle={GetIdle(address)})");
            return;
        }
        if (!_retries.TryGetValue(address, out AttachRetry? retry))
        {
            retry = new AttachRetry();
            SetRetryLocked(address, retry);
        }
        TimeSpan delay;
        if (retry.Attempts >= RetryDelays.Length)
        {
            if (GetIdle(address) != IdleReason.AttachGaveUp)
            {
                Log.Warn("conn", $"attach {Mac.Format(address)}: all {RetryDelays.Length} fast retries failed with the link up; retrying every {SlowRetryDelay.TotalSeconds} s");
                SetIdle(address, IdleReason.AttachGaveUp, "retries exhausted");
                await AttachOtherUpLockedAsync(address).ConfigureAwait(false);
            }
            delay = SlowRetryDelay;
        }
        else
        {
            delay = RetryDelays[retry.Attempts++];
        }
        retry.Serial = ++_retrySerial;
        _retrying[address] = 0;
        DevicesChanged?.Invoke();
        Log.Info("conn", $"attach {Mac.Format(address)}: retry {retry.Attempts}/{RetryDelays.Length} in {delay.TotalMilliseconds} ms");
        _ = RetryAsync(address, retry.Serial, delay, request);
    }

    private async Task RetryAsync(ulong address, long serial, TimeSpan delay, SwitchRequest? request)
    {
        try { await Task.Delay(delay, request?.Cancellation ?? default).ConfigureAwait(false); }
        catch (OperationCanceledException) when (request?.Cancellation.IsCancellationRequested == true) { }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_retries.TryGetValue(address, out AttachRetry? retry) || retry.Serial != serial)
            {
                Log.Info("conn", $"attach {Mac.Format(address)}: retry superseded");
                return;
            }
            if (request is not null)
            {
                if (_session?.Address == address || !CanSwitch(address, request))
                {
                    RemoveRetryLocked(address);
                    DevicesChanged?.Invoke();
                    return;
                }
                try
                {
                    if (await TrySwitchLockedAsync(address, request).ConfigureAwait(false)) return;
                }
                catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
                if (retry.Serial != serial) return; // The attempt scheduled a new delay.
                RemoveRetryLocked(address);
                DevicesChanged?.Invoke();
                return;
            }
            string reason = $"retry {retry.Attempts}/{RetryDelays.Length}";
            if (_session is not null)
            {
                Log.Info("conn", $"attach {Mac.Format(address)}: {reason} skipped, session={Describe(_session)}");
                _retrying.TryRemove(address, out _);
                if (_session.Address != address) RemoveRetryLocked(address);
                DevicesChanged?.Invoke();
                return;
            }
            if (_disposed || !CanRetry(address))
            {
                Log.Info("conn", $"attach {Mac.Format(address)}: {reason} stopped (linkUp={_link.IsUp(address)}, idle={GetIdle(address)})");
                RemoveRetryLocked(address);
                return;
            }
            if (!await TryAttachLockedAsync(address, reason).ConfigureAwait(false))
            {
                await ScheduleRetryLockedAsync(address).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryAttachLockedAsync(ulong address, string reason)
    {
        var stream = await OpenControlLockedAsync(address, reason).ConfigureAwait(false);
        if (stream is null) return false;
        if (!CanRetry(address))
        {
            stream.Dispose();
            return false;
        }
        StartControlLocked(address, stream);
        return true;
    }

    private async Task<NamedPipeClientStream?> OpenControlLockedAsync(ulong address, string reason, CancellationToken ct = default)
    {
        if (_disposed || !_link.IsUp(address)) return null;
        if (!_link.IsListed(address))
        {
            Timeline.Record(address, ConnectionEvent.ControlsUnavailable);
            Log.Warn("conn", $"attach {Mac.Format(address)} ({reason}): no AAP service device");
            return null;
        }
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_idleLock)
        {
            if (_closing || !_link.IsUp(address)) return null;
            _openingControl = (address, opening);
        }
        NamedPipeClientStream? stream;
        int error;
        try
        {
            (stream, error) = await ServicePipes.OpenAapAsync(address, opening.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The link dropped or Parity is exiting. A cancelled open is not a
            // failure to report and a later link-up gets its own attempt.
            return null;
        }
        finally
        {
            lock (_idleLock) _openingControl = null;
        }
        if (opening.IsCancellationRequested)
        {
            stream?.Dispose();
            ct.ThrowIfCancellationRequested();
            return null;
        }
        if (stream is null)
        {
            if (!ct.IsCancellationRequested) Timeline.Record(address, ConnectionEvent.ControlsUnavailable);
            Log.Warn("conn", $"attach {Mac.Format(address)} ({reason}): open failed, Win32 error {error}, linkUp={_link.IsUp(address)}");
            if (error == Native.ErrorDeviceNotConnected)
            {
                // The service sees no ACL: correct a stale "up" so the next real link-up raises an event again.
                _link.Recheck(address);
            }
            return null;
        }
        Log.Info("conn", $"attach {Mac.Format(address)} ({reason}): opened");
        return stream;
    }

    private void StartControlLocked(ulong address, NamedPipeClientStream stream)
    {
        Timeline.Record(address, ConnectionEvent.ControlsOpening);
        _retrying.TryRemove(address, out _);
        // Before the session reads the remembered capabilities and pod-saved settings.
        CheckPairing(address);
        var session = new AapSession(address, stream, Settings, _link.ProductId(address));
        SeedMicDefaults(address, session.State.ModelNumber);
        if (_reattaching is { } previous && previous.Address == address)
        {
            session.CarryOver(previous);
        }
        session.Changed += change =>
        {
            if ((change & DeviceChange.Connection) != 0 && session.State.Status == SessionStatus.Ready)
            {
                Timeline.Record(address, ConnectionEvent.ControlsReady);
                Ready?.Invoke(session);
            }
            if ((change & DeviceChange.Identity) != 0)
            {
                SaveModel(session);
            }
            if ((change & (DeviceChange.Identity | DeviceChange.Keys | DeviceChange.Capabilities)) != 0)
            {
                DevicesChanged?.Invoke();
            }
        };
        Volatile.Write(ref _session, session);
        try
        {
            Settings.LastUsed = address;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept in memory and written with the next settings change; the attach goes on.
            Log.Warn("conn", $"lastUsed not saved: {e.Message}");
        }
        SetIdle(address, IdleReason.None, "attached");
        Attached?.Invoke(session);
        session.Start();
        _ = WatchAsync(session);
        _ = ApplyHandsFreeOnAttachAsync(session);
        DevicesChanged?.Invoke();
    }

    private async Task DetachLocked(string reason)
    {
        AapSession session = _session!;
        Log.Info("conn", $"detach {Mac.Format(session.Address)} ({reason}), status={session.State.Status}");
        Volatile.Write(ref _session, null);
        Timeline.Record(session.Address, reason == "no initial report" ? ConnectionEvent.HandshakeTimedOut : ConnectionEvent.ControlsClosed);
        // A completed initial report ends a run of sessions without one (ReattachAfterNoReportLockedAsync).
        if (session.State.InitialReportDone && _retries.TryGetValue(session.Address, out AttachRetry? retry))
        {
            retry.NoReports = 0;
        }
        await session.DisposeAsync().ConfigureAwait(false);
        Detached?.Invoke(session);
        DevicesChanged?.Invoke();
    }

    // Session ended on its own: with the link still up the same pair is retried (the count restarts only after Ready,
    // so open-then-fail loops stay bounded); otherwise another pair is tried.
    private async Task WatchAsync(AapSession session)
    {
        SessionEnd end = await session.Completion.ConfigureAwait(false);
        ulong address = session.Address;
        Log.Info("conn", $"session {Mac.Format(address)} ended: {end}, status={session.State.Status}");
        if (end == SessionEnd.Closed)
        {
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session != session)
            {
                Log.Info("conn", "session end: already detached");
                return;
            }
            bool ready = session.State.Status == SessionStatus.Ready;
            bool reattach = end == SessionEnd.NoInitialReport && CanAutoAttach(address);
            if (reattach)
            {
                // Shown (ShownSession) until the re-attach ends, so the UI never sees the pair detached in between.
                Volatile.Write(ref _reattaching, session);
            }
            await DetachLocked(end switch
            {
                SessionEnd.LinkLost => "read error 1167",
                SessionEnd.NoInitialReport => "no initial report",
                _ => "read error",
            }).ConfigureAwait(false);
            if (reattach)
            {
                await ReattachAfterNoReportLockedAsync(address).ConfigureAwait(false);
                return;
            }
            if (CanAutoAttach(address))
            {
                Log.Info("conn", $"session end with the link still up (end={end}, wasReady={ready}): retrying attach");
                if (ready)
                {
                    RemoveRetryLocked(address);
                }
                await ScheduleRetryLockedAsync(address).ConfigureAwait(false);
                return;
            }
            Log.Info("conn", $"session end: no retry (linkUp={_link.IsUp(address)}, idle={GetIdle(address)}), trying other pairs");
            await AttachOtherUpLockedAsync(address).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reopens only the AAP session (the audio link stays up), at most NoReportSessions in a row, then marks the pair
    // AttachGaveUp without slow retries. Counted in AttachRetry, so every new retry series restarts the count.
    private async Task ReattachAfterNoReportLockedAsync(ulong address)
    {
        if (!_retries.TryGetValue(address, out AttachRetry? retry))
        {
            retry = new AttachRetry();
            SetRetryLocked(address, retry);
        }
        if (++retry.NoReports >= NoReportSessions)
        {
            Log.Warn("conn", $"attach {Mac.Format(address)}: no initial report in {NoReportSessions} sessions in a row; giving up");
            Volatile.Write(ref _reattaching, null);
            RemoveRetryLocked(address);
            SetIdle(address, IdleReason.AttachGaveUp, "no initial report");
            await AttachOtherUpLockedAsync(address).ConfigureAwait(false);
            return;
        }
        bool attached = await AttachLockedAsync(address, $"no initial report, session {retry.NoReports + 1}/{NoReportSessions}", newSeries: false).ConfigureAwait(false);
        Volatile.Write(ref _reattaching, null);
        if (!attached)
        {
            // Opening failed: the pair now shows as detached while the fast retries run.
            DevicesChanged?.Invoke();
        }
    }

    // Attaches another pair whose link is already up (last-used first), skipping held-back pairs. Never pages.
    private async Task AttachOtherUpLockedAsync(ulong excluded)
    {
        if (_session is not null || _disposed)
        {
            return;
        }
        ulong last = Settings.LastUsed;
        if (last != excluded && CanAutoAttach(last) && await AttachLockedAsync(last, "fallback last-used", newSeries: true).ConfigureAwait(false))
        {
            return;
        }
        foreach (LinkDevice device in _link.Snapshot())
        {
            if (device.Address != excluded && device.Address != last && CanAutoAttach(device.Address)
                && await AttachLockedAsync(device.Address, "fallback", newSeries: true).ConfigureAwait(false))
            {
                return;
            }
        }
        Log.Info("conn", $"fallback: no other pair to attach (excluded {Mac.Format(excluded)})");
    }

    // Persists the 0x1D model so the device list shows it while the pair is not attached.
    private void SaveModel(AapSession session)
    {
        if (session.State.ModelNumber is not { } number || session.State.ModelName is not { } name)
        {
            return;
        }
        try
        {
            var remembered = Settings.GetModel(session.Address);
            string? firmware = session.State.FirmwareBuild ?? (remembered?.Number == number ? remembered.FirmwareBuild : null);
            Settings.SetModel(session.Address, new DeviceModel(number, name, firmware));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cosmetic data only; retried on the next attach.
            Log.Warn("conn", $"model not saved: {e.Message}");
        }
    }

    private async Task ApplyHandsFreeOnAttachAsync(AapSession session)
    {
        ulong address = session.Address;
        // Set on the SetEnabled thread before Windows changes the service; read after the await.
        bool changing = false;
        try
        {
            await ApplyHandsFreeAsync(session, Settings.Get(address).DisableHandsFree, () =>
            {
                if (!changing)
                {
                    changing = true;
                    HandsFreeChanging?.Invoke(address, true);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Warn("conn", $"hands-free on attach failed: {e.Message}");
            if (Session == session) HandsFreeFailed?.Invoke(address, e);
        }
        finally
        {
            if (changing)
            {
                HandsFreeChanging?.Invoke(address, false);
            }
        }
    }

    // Retries the transient errors Windows reports right after a connect, or for a second or two after a disable.
    private async Task ApplyHandsFreeAsync(AapSession session, bool disable, Action? changing = null, int retries = 1, int retryDelayMs = 750)
    {
        ulong address = session.Address;
        bool StillWanted() => Session == session && Settings.Get(address).DisableHandsFree == disable;
        Log.Info("conn", $"hands-free {Mac.Format(address)}: {(disable ? "disable" : "enable")}");
        for (int attempt = 1; StillWanted(); attempt++)
        {
            try
            {
                if (await Task.Run(() => HandsFree.SetEnabled(address, !disable, StillWanted, changing)).ConfigureAwait(false))
                    Log.Info("conn", "hands-free applied");
                return;
            }
            catch (Win32Exception e) when (attempt <= retries
                && e.NativeErrorCode is Native.ErrorServiceDoesNotExist or Native.ErrorNotFound or Native.ErrorInvalidParameter)
            {
                Log.Info("conn", $"hands-free: error {e.NativeErrorCode}, retry {attempt}/{retries} in {retryDelayMs} ms");
                await Task.Delay(retryDelayMs).ConfigureAwait(false);
            }
        }
    }

    private IdleReason GetIdle(ulong address)
    {
        lock (_idleLock)
        {
            return _idle.GetValueOrDefault(address);
        }
    }

    private void SetIdle(ulong address, IdleReason reason, string why)
    {
        IdleReason previous;
        lock (_idleLock)
        {
            previous = _idle.GetValueOrDefault(address);
            if (previous == reason)
            {
                return;
            }
            if (reason == IdleReason.None)
            {
                _idle.Remove(address);
            }
            else
            {
                _idle[address] = reason;
            }
        }
        Log.Info("conn", $"{Mac.Format(address)}: idle {previous} -> {reason} ({why})");
        if (reason == IdleReason.AttachGaveUp) Timeline.Record(address, ConnectionEvent.RetriesExhausted);
        DevicesChanged?.Invoke();
    }

    // Completed by OnLinkChanged when the pair's link goes down; registered before the disconnect request.
    private Task WaitForLinkDown(ulong address)
    {
        lock (_idleLock)
        {
            if (!_linkDownWaits.TryGetValue(address, out TaskCompletionSource? wait))
            {
                wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _linkDownWaits[address] = wait;
            }
            return wait.Task;
        }
    }

    private static string Describe(AapSession? session) => session is null ? "none" : Mac.Format(session.Address);

    private sealed record SwitchRequest(string Reason, Func<bool>? IsCurrent, CancellationToken Cancellation);

    // Attach retry series of one pair; guarded by _gate.
    private sealed class AttachRetry
    {
        public int Attempts { get; set; }
        // Sessions in a row that ended without an initial report (ReattachAfterNoReportLockedAsync).
        public int NoReports { get; set; }
        // Identifies the pending delay; a newer schedule or a reset makes older delays no-ops.
        public long Serial { get; set; }
    }
}
