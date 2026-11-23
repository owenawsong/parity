using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Pairside;

internal sealed partial class Desktop : ApplicationContext
{
    private const string Origin = "https://pairside.local";
    private const string Page = Origin + "/Pairside.html?desktop";
    private static readonly Color Dark = Color.FromArgb(28, 28, 31);
    private static readonly Color Light = Color.FromArgb(242, 242, 244);
    private static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pairside", "Profile");
    private static readonly string SettingsFile = Path.Combine(Data, "window.json");

    private readonly Form _main;
    private readonly Form _trayPanel;
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly RegisteredWaitHandle _activation;
    private CoreWebView2Controller? _web;
    private Form? _shown;
    private Point _trayAnchor;
    private int _trayHeight = 380;
    private bool _ready;
    private bool _closing;
    private bool _dark = true;

    public Desktop(EventWaitHandle activation)
    {
        using Stream image = typeof(Desktop).Assembly.GetManifestResourceStream("Pairside.ico")!;
        _icon = new Icon(image);
        _main = new Form
        {
            Text = "Parity · Preview", Icon = _icon, BackColor = Dark,
            FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.Manual,
            AutoScaleMode = AutoScaleMode.None,
        };
        _trayPanel = new Form
        {
            Text = "Parity quick controls", BackColor = Dark, TopMost = true,
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, AutoScaleMode = AutoScaleMode.None,
        };
        _main.ClientSizeChanged += (_, _) => ResizeWeb(_main);
        _trayPanel.ClientSizeChanged += (_, _) => ResizeWeb(_trayPanel);
        _main.Move += (_, _) => NotifyMove(_main);
        _trayPanel.Move += (_, _) => NotifyMove(_trayPanel);
        _main.Activated += (_, _) => FocusWeb(_main);
        _trayPanel.Activated += (_, _) => FocusWeb(_trayPanel);
        _trayPanel.Deactivate += (_, _) => { if (_shown == _trayPanel) HideCurrent(); };
        _main.FormClosing += OnClose;
        _trayPanel.FormClosing += OnClose;
        PositionMain();
        SetChrome();

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open Parity Preview", null, (_, _) => Show(_main));
        _menu.Items.Add("Quick controls", null, (_, _) => ToggleTray());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit Parity Preview", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Icon = _icon, Text = "Parity · Preview", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleTray(); };
        _activation = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
        {
            try { _main.BeginInvoke(() => Show(_main)); }
            catch (InvalidOperationException) { }
        }, null, Timeout.Infinite, false);

        _main.Shown += async (_, _) => await InitializeAsync();
        Show(_main);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Data, "WebView2"));
            if (_closing) return;
            CoreWebView2Controller controller = await environment.CreateCoreWebView2ControllerAsync(_main.Handle);
            if (_closing)
            {
                controller.Close();
                return;
            }
            _web = controller;
            controller.DefaultBackgroundColor = Dark;
            controller.AllowExternalDrop = false;
            var core = controller.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping("pairside.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Deny);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Page;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnMessage;
            if (_shown is { } window) Attach(window);
            else controller.IsVisible = false;
            core.Navigate(Page);
        }
        catch (Exception e) when (!_closing)
        {
            MessageBox.Show(_main, $"Parity could not start WebView2.\n\n{e.Message}", "Parity", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitThread();
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != Page) return;
        using var document = JsonDocument.Parse(e.WebMessageAsJson);
        JsonElement message = document.RootElement;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("action", out JsonElement action)) return;
        switch (action.GetString())
        {
            case "ready":
                _ready = true;
                SendMode();
                break;
            case "hide":
                HideCurrent();
                break;
            case "expand":
                Show(_main);
                break;
            case "trayHeight" when message.TryGetProperty("height", out JsonElement height) && height.TryGetInt32(out int value):
                _trayHeight = Math.Clamp(value + 53, 120, 493);
                if (_shown == _trayPanel) PositionTray();
                break;
            case "theme" when message.TryGetProperty("theme", out JsonElement theme):
                _dark = theme.GetString() != "light";
                SetChrome();
                break;
        }
    }

    private void Show(Form window)
    {
        if (_closing) return;
        if (_shown != window)
        {
            HideCurrent();
            if (window == _trayPanel) PositionTray();
            _shown = window;
            Attach(window);
            window.Show();
            SendMode();
        }
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    private void HideCurrent()
    {
        Form? window = _shown;
        _shown = null;
        if (window == _main) SaveBounds();
        if (_web is { } controller) controller.IsVisible = false;
        window?.Hide();
    }

    private void ToggleTray()
    {
        if (_shown == _trayPanel) HideCurrent();
        else
        {
            _trayAnchor = Cursor.Position;
            Show(_trayPanel);
        }
    }

    private void Attach(Form window)
    {
        if (_web is not { } controller) return;
        controller.ParentWindow = window.Handle;
        controller.Bounds = window.ClientRectangle;
        controller.IsVisible = true;
    }

    private void SendMode()
    {
        if (!_ready || _web is not { } controller) return;
        controller.CoreWebView2.PostWebMessageAsJson(_shown == _trayPanel ? "{\"mode\":\"tray\"}" : "{\"mode\":\"full\"}");
    }

    private void ResizeWeb(Form window)
    {
        if (window == _shown && _web is { } controller) controller.Bounds = window.ClientRectangle;
    }

    private void NotifyMove(Form window)
    {
        if (window == _shown) _web?.NotifyParentWindowPositionChanged();
    }

    private void FocusWeb(Form window)
    {
        if (window == _shown) _web?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    private void PositionMain()
    {
        Rectangle screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        _main.Location = screen.Location;
        float scale = ScaleFor(screen.Location);
        _main.MinimumSize = new Size((int)(360 * scale), (int)(420 * scale));
        Rectangle saved = LoadBounds();
        if (!saved.IsEmpty && Screen.AllScreens.Any(s => s.WorkingArea.Contains(saved.Location)))
            screen = Screen.FromPoint(saved.Location).WorkingArea;
        int width = Math.Min(saved.Width > 0 ? saved.Width : (int)(704 * scale), screen.Width);
        int height = Math.Min(saved.Height > 0 ? saved.Height : (int)(660 * scale), screen.Height);
        int x = saved.IsEmpty ? screen.Left + (screen.Width - width) / 2 : saved.X;
        int y = saved.IsEmpty ? screen.Top + (screen.Height - height) / 2 : saved.Y;
        _main.Bounds = new Rectangle(
            Math.Clamp(x, screen.Left, screen.Right - width), Math.Clamp(y, screen.Top, screen.Bottom - height), width, height);
    }

    private void PositionTray()
    {
        Rectangle area = Screen.FromPoint(_trayAnchor).WorkingArea;
        float scale = ScaleFor(_trayAnchor);
        int width = Math.Min(area.Width, (int)(310 * scale));
        int height = Math.Min(area.Height, (int)Math.Ceiling(_trayHeight * scale));
        int inset = (int)(10 * scale);
        int left = Math.Clamp(_trayAnchor.X - width / 2, area.Left, area.Right - width);
        int top = Math.Max(area.Top, area.Bottom - height - inset);
        _trayPanel.Bounds = new Rectangle(left, top, width, height);
    }

    private static Rectangle LoadBounds()
    {
        try
        {
            int[]? saved = JsonSerializer.Deserialize<int[]>(File.ReadAllText(SettingsFile));
            if (saved is { Length: 4 } && saved[2] >= 360 && saved[3] >= 420)
                return new Rectangle(saved[0], saved[1], saved[2], saved[3]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return Rectangle.Empty;
    }

    private void SaveBounds()
    {
        Rectangle bounds = _main.WindowState == FormWindowState.Normal ? _main.Bounds : _main.RestoreBounds;
        try
        {
            Directory.CreateDirectory(Data);
            string temporary = SettingsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new[] { bounds.X, bounds.Y, bounds.Width, bounds.Height }));
            File.Move(temporary, SettingsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void SetChrome()
    {
        Color background = _dark ? Dark : Light;
        _main.BackColor = _trayPanel.BackColor = background;
        if (_web is { } controller) controller.DefaultBackgroundColor = background;
        int dark = _dark ? 1 : 0;
        DwmSetWindowAttribute(_main.Handle, 20, ref dark, sizeof(int));
        int rounded = 2;
        DwmSetWindowAttribute(_trayPanel.Handle, 33, ref rounded, sizeof(int));
    }

    private void OnClose(object? sender, FormClosingEventArgs e)
    {
        if (_closing || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall) return;
        e.Cancel = true;
        HideCurrent();
    }

    protected override void ExitThreadCore()
    {
        _closing = true;
        _activation.Unregister(null);
        SaveBounds();
        _tray.Dispose();
        _menu.Dispose();
        _web?.Close();
        _trayPanel.Dispose();
        _main.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private static float ScaleFor(Point point)
    {
        nint monitor = MonitorFromPoint((uint)point.X | ((long)point.Y << 32), 2);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1f;
    }

    internal static void AllowActivation() => AllowSetForegroundWindow(-1);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(long point, uint flags);

    [LibraryImport("Shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int length);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
