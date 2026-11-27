using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WPods.Core;
using WPods.Nearby;

namespace WPods.Ui;

// Main window and popup lifecycle, init messages, theme.
internal sealed partial class TrayApp
{
    private static readonly TimeSpan SuspendAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DestroyAfter = TimeSpan.FromMinutes(10);

    // Match tokens.css bg-window / surface-flyout, so the window never flashes another colour before the page paints.
    private static readonly Color MainLight = Color.FromArgb(0xF2, 0xF2, 0xF4);
    private static readonly Color MainDark = Color.FromArgb(0x09, 0x0A, 0x0B);
    private static readonly Color PopupLight = Color.FromArgb(0xFB, 0xFB, 0xFC);
    private static readonly Color PopupDark = Color.FromArgb(0x19, 0x1A, 0x1C);

    private readonly Icon _appIcon;
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private readonly System.Windows.Forms.Timer _popupTimer = new();
    private Task<CoreWebView2Environment>? _environment;
    private MainWindow? _main;
    private bool _mainShown;
    private bool _suspendPending;
    // The main page got its init: a re-show sends only what can change while hidden.
    private bool _mainInitSent;
    private Point? _mainPosition;
    private PopupWindow? _popup;
    private bool _popupShown;
    private double _popupHeight;
    // No offers: the popup is hidden and kept for the next case open (see ParkPopup).
    private bool _popupParked;
    private bool _popupSuspendPending;
    // The popup opens on its own, so its WebView2 failure balloon shows once per run.
    private bool _popupFailureShown;

    private bool Dark => _prefs.Theme == "dark" || (_prefs.Theme == "auto" && !_appsLight);

    private Color MainBackground => Dark ? MainDark : MainLight;

    private Color PopupBackground => Dark ? PopupDark : PopupLight;

    private Task<CoreWebView2Environment> EnvironmentAsync() => _environment ??= WebPane.CreateEnvironmentAsync();

    // ---- Main window ----

    private bool _mainCompact;
    private Point _compactAnchor;
    private double _compactHeightDip = 345;

    private void ShowMain(bool compact = false)
    {
        if (_exiting)
        {
            return;
        }
        _hideTimer.Stop();
        compact = compact && AppIdentity.IsPairside && _prefs.FirstRunDone &&
            !_adapterUnsupported && !_restartPending && _devices.Count != 0;
        if (compact)
        {
            _compactAnchor = Cursor.Position;
        }
        if (_main is not { } window)
        {
            Log.Info("ui", "main window: create");
            _mainCompact = compact;
            CreateMain();
            return;
        }
        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }
        if (compact != _mainCompact)
        {
            if (!_mainCompact)
            {
                _mainPosition = window.Location;
            }
            _mainCompact = compact;
            window.SetCompact(compact);
            window.Bounds = compact ? CompactBounds() : MainBounds();
            if (_mainShown && _mainInitSent)
            {
                SendMainPresentation(window.Pane);
            }
        }
        else if (compact && !_mainShown)
        {
            window.Bounds = CompactBounds();
        }
        if (!_mainShown)
        {
            Log.Info("ui", $"main window: show (page ready={window.Pane.Ready})");
            if (window.WindowState == FormWindowState.Normal)
            {
                window.Bounds = ClampToWorkArea(window.Bounds);
            }
            window.Pane.SetVisible(true);
            _mainShown = true;
            window.Show();
            UpdateMicSetupWatch();
            UpdateCaseFinderWatch();
            if (window.Pane.Ready)
            {
                if (_mainInitSent)
                {
                    SendMainRefresh(window.Pane);
                }
                else
                {
                    SendMainInit();
                }
            }
        }
        window.Activate();
        UpdateNowPlayingWatch();
    }

    private async void CreateMain()
    {
        var pane = new WebPane(OnMainMessage, OnPaneFailed, _prefs.ReduceMotion);
        var window = new MainWindow(_mainCompact ? CompactBounds() : MainBounds(), MainBackground, Dark,
            Text("app.name"), _appIcon, pane, _mainCompact);
        window.CloseRequested += HideMain;
        window.Resized += OnMainResized;
        window.Resize += (_, _) =>
        {
            UpdateMicSetupWatch();
            if (window.WindowState == FormWindowState.Minimized)
            {
                StopHeadphoneLevel();
                DiscardOnboardEqPreviews();
            }
            UpdateCaseFinderWatch();
            UpdateNowPlayingWatch();
        };
        _main = window;
        _mainShown = true;
        window.Show();
        window.Activate();
        try
        {
            CoreWebView2Environment environment = await EnvironmentAsync();
            if (!window.IsDisposed)
            {
                await pane.InitializeAsync(environment, window.Handle, "index.html", MainBackground);
            }
        }
        catch (Exception e)
        {
            // No usable WebView2: drop the empty window so the next show tries again.
            Log.Error("ui", "main window: WebView2 init failed", e);
            _environment = null;
            if (_main == window)
            {
                DestroyMain();
                ShowWebViewFailed();
            }
        }
    }

    private void HideMain()
    {
        if (_main is not { } window || !_mainShown)
        {
            return;
        }
        if (!_mainCompact && window.WindowState == FormWindowState.Normal)
        {
            _mainPosition = window.Location;
        }
        Log.Info("ui", "main window: hide");
        // A hotkey being recorded on the page ends with it.
        _hotkeys.Pause(false);
        PostVisibility(window.Pane, false);
        StopHeadphoneLevel();
        _mainShown = false;
        DiscardOnboardEqPreviews();
        UpdateNowPlayingWatch();
        StopWindowMotion();
        UpdateMicSetupWatch();
        UpdateCaseFinderWatch();
        window.Hide();
        window.Pane.SetVisible(false);
        _suspendPending = true;
        _hideTimer.Interval = (int)SuspendAfter.TotalMilliseconds;
        _hideTimer.Start();
    }

    // Suspends the hidden page after SuspendAfter and destroys the window after DestroyAfter.
    private async void OnHideTimer(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        if (_main is not { } window || _mainShown)
        {
            return;
        }
        if (_suspendPending)
        {
            _suspendPending = false;
            _hideTimer.Interval = (int)(DestroyAfter - SuspendAfter).TotalMilliseconds;
            _hideTimer.Start();
            Log.Info("ui", "main window: suspend page");
            await window.Pane.SuspendAsync();
            return;
        }
        DestroyMain();
    }

    private void DestroyMain()
    {
        if (_main is not { } window)
        {
            return;
        }
        if (!_mainCompact && window.WindowState == FormWindowState.Normal)
        {
            _mainPosition = window.Location;
        }
        Log.Info("ui", "main window: destroy");
        _hotkeys.Pause(false);
        StopHeadphoneLevel();
        _main = null;
        _mainShown = false;
        DiscardOnboardEqPreviews();
        UpdateNowPlayingWatch();
        StopWindowMotion();
        _mainInitSent = false;
        _micSetupRequested = false;
        _caseFinderDevice = 0;
#if PAIRSIDE_LIVE
        _pulseWatch = 0;
        _batteryWatch = 0;
        _pulseRevision++;
#endif
        UpdateMicSetupWatch();
        UpdateCaseFinderWatch();
        _hideTimer.Stop();
        window.Destroy();
    }

    private void OnMainResized(double widthDip, double heightDip)
    {
        if (_mainCompact)
        {
            return;
        }
        double height = Math.Round(Math.Clamp(heightDip, MainWindow.MinHeightDip, MainWindow.MaxHeightDip));
        double width = AppIdentity.IsPairside
            ? Math.Round(Math.Clamp(widthDip, MainWindow.MinWidthDip, MainWindow.MaxWidthDip)) : _prefs.WindowWidth;
        if (height != _prefs.WindowHeight || width != _prefs.WindowWidth)
        {
            SavePrefs(_prefs with { WindowHeight = height, WindowWidth = width });
        }
    }

    // A WebView2 process died and the pane is no longer Ready: rebuild that window's page.
    private void OnPaneFailed(WebPane pane, CoreWebView2ProcessFailedKind kind)
    {
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The cached environment's browser process is gone: the rebuild creates a new one.
            _environment = null;
        }
        _ui.Post(static s =>
        {
            var (app, failed) = ((TrayApp, WebPane))s!;
            Log.Warn("ui", $"WebView2 process failed ({(app._main?.Pane == failed ? "main" : app._popup?.Pane == failed ? "popup" : app._osd?.Pane == failed ? "osd" : "stale pane")})");
            if (app._main?.Pane == failed)
            {
                bool shown = app._mainShown;
                app.DestroyMain();
                if (shown)
                {
                    app.CreateMain();
                }
            }
            else if (app._popup?.Pane == failed)
            {
                app.DestroyPopup();
                app.UpdatePopup();
            }
            else if (app._osd?.Pane == failed)
            {
                // The next hotkey creates it again.
                app.DestroyOsd();
            }
        }, (this, pane));
    }

    private Rectangle CompactBounds()
    {
        (Rectangle area, uint dpi) = NativeUi.MonitorAt(_compactAnchor);
        float scale = dpi / 96f;
        int margin = (int)MathF.Round(12 * scale);
        int width = Math.Min(area.Width, (int)MathF.Round(352 * scale));
        int height = Math.Min(area.Height, (int)Math.Round(_compactHeightDip * scale));
        int left = Math.Max(area.Left, area.Right - width - margin);
        int top = Math.Max(area.Top, area.Bottom - height - margin);
        return new Rectangle(left, top, width, height);
    }

    /// <summary>Last position, else centred on the primary work area; scaled to that monitor's DPI.</summary>
    private Rectangle MainBounds()
    {
        (Rectangle area, uint dpi) = _mainPosition is { } position ? NativeUi.MonitorAt(position) : NativeUi.PrimaryMonitor();
        float scale = dpi / 96f;
        double widthDip = AppIdentity.IsPairside ? Math.Clamp(_prefs.WindowWidth, MainWindow.MinWidthDip, MainWindow.MaxWidthDip) : MainWindow.WidthDip;
        int width = Math.Min(area.Width, (int)Math.Round(widthDip * scale));
        int height = Math.Min(area.Height, (int)Math.Round(Math.Clamp(_prefs.WindowHeight, MainWindow.MinHeightDip, MainWindow.MaxHeightDip) * scale));
        Point location = _mainPosition ?? new Point(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2);
        return ClampToWorkArea(new Rectangle(location, new Size(width, height)));
    }

    private static Rectangle ClampToWorkArea(Rectangle bounds)
    {
        Rectangle area = NativeUi.MonitorAt(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)).WorkArea;
        int x = Math.Max(area.Left, Math.Min(bounds.X, area.Right - bounds.Width));
        int y = Math.Max(area.Top, Math.Min(bounds.Y, area.Bottom - bounds.Height));
        return new Rectangle(x, y, bounds.Width, bounds.Height);
    }

    private void SendMainInit()
    {
        SendMainPresentation(_main!.Pane);
        Array.Clear(_lastSent);
        Utf8JsonWriter w = _writer.Begin("init");
        MessageWriter.WriteLocale(w, _locale);
        w.WriteStartArray("languages");
        foreach ((string code, LocaleFile file) in _locales.Files)
        {
            w.WriteStartObject();
            w.WriteString("code", code);
            w.WriteString("originalName", file.OriginalName);
            w.WriteString("engName", file.EngName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("language", _prefs.Language == "system" ? "" : _prefs.Language);
        w.WriteString("windowsLanguageName", NativeUi.NativeDisplayName(NativeUi.UserUiLanguage()));
        WritePrefs(w);
        w.WriteBoolean("startup", StartupRegistration.IsEnabled());
        WriteGestureBindings(w);
        w.WriteBoolean("firstRun", !_prefs.FirstRunDone);
        w.WriteBoolean("adapterUnsupported", _adapterUnsupported);
        w.WriteBoolean("desktopOnly", _desktopOnly);
        w.WriteBoolean("restartPending", _restartPending);
        WriteUpdate(w);
        WriteDevices(w);
        if (Selected() is { } selected)
        {
            w.WriteString("selected", Mac.Format(selected));
        }
        else
        {
            w.WriteNull("selected");
        }
        MessageWriter.WriteState(w, _session);
        w.WriteString("mic", MessageWriter.Camel(_micStatus));
        MessageWriter.WriteMicCatalog(w);
        MessageWriter.WriteMicConfig(w, "micConfig", _settings.Mic);
        MessageWriter.WriteMixer(w, _mixer.Devices, _settings, _prefs);
        MessageWriter.WriteHotkeys(w, _hotkeys, _settings.Hotkeys.Sounds);
        w.WriteBoolean("visible", _mainShown);
        PostMain(_writer.End());
        _mainInitSent = true;
        UpdateNowPlayingWatch(replay: true);
        SendActivity();
        ReplayPlaybackGestures();
#if PAIRSIDE_LIVE
        SendSpotify();
#endif
        if (_settingsResetPending)
        {
            _settingsResetPending = false;
            QueueToast("settingsReset", 0, "S1");
        }
    }

    // Re-show of an initialized page: resends what may have changed while hidden, since hidden sends are dropped.
    private void SendMainRefresh(WebPane pane)
    {
        SendMainPresentation(pane);
        Array.Clear(_lastSent);
        PostVisibility(pane, true);
        SendActivity();
#if PAIRSIDE_LIVE
        SendBatteryHistory();
        SendSpotify();
#endif
        SendMainDeltas(Dirty.Devices | Dirty.Session | Dirty.Mic | Dirty.Mixer, _session);
        ReplayPlaybackGestures();
        SendStartupPref();
        SendUpdate();
        SendAdapter();
        SendRestart();
        SendHotkeys();
        Utf8JsonWriter w = _writer.Begin("accent");
        MessageWriter.WriteAccent(w, _accent);
        PostMain(_writer.End());
        w = _writer.Begin("micConfig");
        MessageWriter.WriteMicConfig(w, "config", _settings.Mic);
        PostMain(_writer.End());
        foreach (string? stage in _micStages)
        {
            if (stage is not null)
            {
                PostMain(stage);
            }
        }
    }

    private void SendMainPresentation(WebPane pane)
    {
        if (!AppIdentity.IsPairside)
        {
            return;
        }
        Utf8JsonWriter w = _writer.Begin("presentation");
        w.WriteString("mode", _mainCompact ? "tray" : "full");
        pane.Post(_writer.End());
    }

    private ulong? Selected()
    {
        if (_session is { } session)
        {
            return session.Address;
        }
        ulong last = _connections.LastUsed;
        if (_devices.Exists(d => d.Address == last))
        {
            return last;
        }
        return _devices.Count > 0 ? _devices[0].Address : null;
    }

    private void WritePrefs(Utf8JsonWriter w)
    {
        w.WriteString("theme", _prefs.Theme);
        w.WriteBoolean("useAccent", _prefs.UseAccent);
        WriteLayoutPrefs(w);
        MessageWriter.WriteAccent(w, _accent);
    }

    private void WriteLayoutPrefs(Utf8JsonWriter w)
    {
        w.WriteBoolean("reduceMotion", _prefs.ReduceMotion);
        w.WriteBoolean("popupSeparateBatteries", _prefs.PopupSeparateBatteries);
        w.WriteStartArray("sidebarOrder");
        foreach (string page in _prefs.SidebarOrder) w.WriteStringValue(page);
        w.WriteEndArray();
        w.WriteStartArray("autoConnectDevices");
        foreach (string id in _prefs.AutoConnectDevices) w.WriteStringValue(id);
        w.WriteEndArray();
        WriteMaxFinishes(w);
    }

    private void WriteMaxFinishes(Utf8JsonWriter w)
    {
        w.WriteStartObject("maxFinishes");
        foreach (var (id, finish) in _prefs.MaxFinishes) w.WriteString(id, finish);
        w.WriteEndObject();
    }

    private void SendLayoutPrefs()
    {
        var w = _writer.Begin("prefs");
        WriteLayoutPrefs(w);
        PostMain(_writer.End());
    }

    private void RestoreWindowSize()
    {
        if (_mainCompact || _main is not { } main) return;
        if (main.WindowState != FormWindowState.Normal) main.WindowState = FormWindowState.Normal;
        _mainPosition = main.Bounds.Location;
        SavePrefs(_prefs with { WindowWidth = UiPrefs.Default.WindowWidth, WindowHeight = UiPrefs.Default.WindowHeight });
        main.Bounds = MainBounds();
    }

    private void PostVisibility(WebPane pane, bool visible)
    {
        Utf8JsonWriter w = _writer.Begin("visibility");
        w.WriteBoolean("visible", visible);
        pane.Post(_writer.End());
    }

    private void SavePrefs(UiPrefs prefs)
    {
        _prefs = prefs;
        try
        {
            _settings.SetUi(prefs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Kept for this session; the next successful save writes it.
            Log.Warn("ui", $"prefs not saved: {e.Message}");
        }
    }

    // ---- Nearby popup ----

    private void UpdatePopup()
    {
        if (_offers.Count == 0)
        {
            ParkPopup();
            return;
        }
        if (_popup is not { } window)
        {
            CreatePopup();
            return;
        }
        if (!_popupParked)
        {
            window.Pane.Post(OffersMessage());
            return;
        }
        Log.Info("ui", $"popup: reuse ({_offers.Count} offers)");
        _popupParked = false;
        _popupTimer.Stop();
        window.Pane.SetVisible(true);
        // Full init: a suspended page may have missed labels, prefs or accent sent while parked.
        if (window.Pane.Ready)
        {
            SendPopupInit();
        }
        // Card height unchanged since its last report: the page reports no new size, so show at that height.
        if (_popupHeight > 0)
        {
            OnPopupSize(_popupHeight);
        }
    }

    // Hides the popup but keeps its page, so the next case open skips the WebView2 cold start.
    // The page keeps its last card through the window's exit.
    private void ParkPopup()
    {
        if (_popup is not { } window || _popupParked)
        {
            return;
        }
        Log.Info("ui", "popup: park");
        _popupParked = true;
        _popupSuspendPending = true;
        if (_popupShown)
        {
            HidePopup();
        }
        else if (!window.Visible)
        {
            window.Pane.Post(OffersMessage());
        }
        _popupTimer.Interval = (int)SuspendAfter.TotalMilliseconds;
        _popupTimer.Start();
    }

    private async void OnPopupTimer(object? sender, EventArgs e)
    {
        _popupTimer.Stop();
        if (_popup is not { } window || !_popupParked)
        {
            return;
        }
        if (_popupSuspendPending)
        {
            _popupSuspendPending = false;
            _popupTimer.Interval = (int)(DestroyAfter - SuspendAfter).TotalMilliseconds;
            _popupTimer.Start();
            Log.Info("ui", "popup: suspend page");
            window.Pane.SetVisible(false);
            await window.Pane.SuspendAsync();
            return;
        }
        DestroyPopup();
    }

    private async void CreatePopup()
    {
        Log.Info("ui", $"popup: create ({_offers.Count} offers)");
        var pane = new WebPane(OnPopupMessage, OnPaneFailed, _prefs.ReduceMotion);
        var window = new PopupWindow(PopupBackground, Dark, pane);
        window.ScaleChanged += PlacePopup;
        window.CloseRequested += HidePopup;
        window.Dismissed += () =>
        {
            if (_popup == window && _popupParked && _offers.Count == 0)
            {
                pane.Post(OffersMessage());
            }
        };
        _popup = window;
        _popupShown = false;
        _popupHeight = 0;
        // Laid out hidden at a typical height; shown once the page reports its card height.
        (Rectangle area, uint dpi) = NativeUi.PrimaryMonitor();
        float scale = dpi / 96f;
        int width = (int)MathF.Round(PopupWindow.WidthDip * scale);
        int height = (int)MathF.Round(360 * scale);
        window.Bounds = new Rectangle(area.Right - width, area.Bottom - height, width, height);
        pane.SetBounds(new Rectangle(0, 0, width, height));
        nint handle = window.Handle;
        try
        {
            CoreWebView2Environment environment = await EnvironmentAsync();
            if (!window.IsDisposed)
            {
                await pane.InitializeAsync(environment, handle, "popup.html", PopupBackground);
            }
        }
        catch (Exception e)
        {
            Log.Error("ui", "popup: WebView2 init failed", e);
            _environment = null;
            if (_popup == window)
            {
                // Dropped so the next offers retry with a new popup.
                DestroyPopup();
                if (!_popupFailureShown)
                {
                    _popupFailureShown = true;
                    ShowWebViewFailed();
                }
            }
        }
    }

    private void OnPopupSize(double heightDip)
    {
        if (_popup is not { } window)
        {
            return;
        }
        if (_offers.Count == 0)
        {
            _popupHeight = heightDip;
            HidePopup();
            return;
        }
        // A parked page can report its old, empty layout while a new offer is loading.
        if (heightDip <= 0)
        {
            return;
        }
        _popupHeight = heightDip;
        PlacePopup();
        if (!_popupShown)
        {
            Log.Info("ui", "popup: show");
            _popupShown = true;
            window.Present();
            PostVisibility(window.Pane, true);
            _host.SetHotKey(true);
        }
    }

    // Bottom-right corner of the primary work area, which excludes the taskbar.
    private void PlacePopup()
    {
        if (_popup is not { } window || _popupHeight <= 0)
        {
            return;
        }
        (Rectangle area, uint dpi) = NativeUi.PrimaryMonitor();
        float scale = dpi / 96f;
        int width = (int)MathF.Round(PopupWindow.WidthDip * scale);
        int height = (int)Math.Ceiling(_popupHeight * scale);
        int margin = (int)MathF.Round(PopupWindow.MarginDip * scale);
        int travel = (int)MathF.Round(14 * scale);
        window.Place(new Rectangle(area.Right - margin - width, area.Bottom - margin - height, width, height), travel);
    }

    private void HidePopup()
    {
        if (_popup is not { } window || !_popupShown)
        {
            return;
        }
        Log.Info("ui", "popup: hide");
        _popupShown = false;
        _host.SetHotKey(false);
        PostVisibility(window.Pane, false);
        window.Dismiss();
    }

    private void DestroyPopup()
    {
        if (_popup is not { } window)
        {
            return;
        }
        HidePopup();
        Log.Info("ui", "popup: destroy");
        _popup = null;
        _popupParked = false;
        _popupTimer.Stop();
        window.Destroy();
    }

    private void SendPopupInit()
    {
        Utf8JsonWriter w = _writer.Begin("init");
        MessageWriter.WriteLocale(w, _locale);
        WritePrefs(w);
        w.WriteBoolean("visible", _popupShown);
        WriteOffers(w);
        _popup!.Pane.Post(_writer.End());
    }

    private string OffersMessage()
    {
        Utf8JsonWriter w = _writer.Begin("offers");
        WriteOffers(w);
        return _writer.End();
    }

    private void WriteOffers(Utf8JsonWriter w)
    {
        w.WriteStartArray("offers");
        foreach (NearbyOffer offer in _offers)
        {
            MessageWriter.WriteOffer(w, offer);
        }
        w.WriteEndArray();
    }

    // Win+Shift+A: the hotkey grants this process the foreground, so the popup may take it.
    private void FocusPopup()
    {
        if (_popup is not { } window || !_popupShown)
        {
            return;
        }
        NativeUi.SetForegroundWindow(window.Handle);
        window.Pane.Focus();
        _writer.Begin("focus");
        window.Pane.Post(_writer.End());
    }

    // ---- Theme and display ----

    private void ApplyTheme()
    {
        Log.Info("ui", $"theme {_prefs.Theme}, dark={Dark}");
        _main?.SetTheme(MainBackground, Dark);
        _popup?.SetTheme(PopupBackground, Dark);
        _osd?.SetTheme(PopupBackground, Dark);
    }

    /// <summary>Only the popup and the notification need this; the main page applied the change itself.</summary>
    private void SendPrefsToPopup()
    {
        Utf8JsonWriter w = _writer.Begin("prefs");
        w.WriteString("theme", _prefs.Theme);
        w.WriteBoolean("useAccent", _prefs.UseAccent);
        w.WriteBoolean("popupSeparateBatteries", _prefs.PopupSeparateBatteries);
        WriteMaxFinishes(w);
        string json = _writer.End();
        _popup?.Pane.Post(json);
        _osd?.Pane.Post(json);
    }

    // Windows app/taskbar mode or accent colour changed.
    private void OnColorsChanged()
    {
        _lightTaskbar = WindowsTheme.TaskbarUsesLight();
        bool appsLight = WindowsTheme.AppsUseLight();
        if (appsLight != _appsLight)
        {
            _appsLight = appsLight;
            ApplyTheme();
            TrayMenu.FlushThemes();
        }
        AccentRamp? accent = WindowsTheme.ReadAccent();
        if (accent != _accent)
        {
            _accent = accent;
            Utf8JsonWriter w = _writer.Begin("accent");
            MessageWriter.WriteAccent(w, accent);
            string json = _writer.End();
            PostMain(json);
            _popup?.Pane.Post(json);
            _osd?.Pane.Post(json);
        }
        UpdateTray();
    }

    private void OnDisplayChanged()
    {
        _trayDpi = NativeUi.PrimaryMonitor().Dpi;
        UpdateTray();
        PlacePopup();
        PlaceOsd();
    }
}
