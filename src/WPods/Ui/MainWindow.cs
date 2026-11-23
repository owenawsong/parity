using WPods.Core;

namespace WPods.Ui;

/// <summary>
/// The main window: no title bar or frame (the page draws its own), Windows 11 round corners and shadow by DWM,
/// Resize strips surround the web pane. WPods keeps its fixed width while Pairside can resize on both axes.
/// </summary>
internal sealed class MainWindow : Form
{
    public const int WidthDip = 400;
    public const int MinWidthDip = 360;
    public const int MaxWidthDip = 1400;
    public const int MinHeightDip = 480;
    public const int MaxHeightDip = 960;
    public const int ResizeStripDip = 4;

    private bool _dark;
    private bool _destroying;
    private bool _compact;

    public MainWindow(Rectangle bounds, Color background, bool dark, string title, Icon icon, WebPane pane, bool compact)
    {
        Pane = pane;
        _compact = compact;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        BackColor = background;
        Text = title;
        Icon = icon;
        _dark = dark;
    }

    public WebPane Pane { get; }

    public void SetCompact(bool compact) => _compact = compact;

    /// <summary>Close button, Alt+F4, the taskbar's Close or an outside WM_CLOSE: the app hides the window instead.</summary>
    public event Action? CloseRequested;

    /// <summary>The user finished resizing; dimensions in DIPs.</summary>
    public event Action<double, double>? Resized;

    private float DpiScale => NativeUi.GetDpiForWindow(Handle) / 96f;

    public void SetTheme(Color background, bool dark)
    {
        BackColor = background;
        Pane.SetBackground(background);
        _dark = dark;
        if (IsHandleCreated)
        {
            NativeUi.SetWindowInt(Handle, NativeUi.DwmwaUseImmersiveDarkMode, dark ? 1 : 0);
        }
    }

    /// <summary>Destroys the window for real (exit, language switch, hidden too long).</summary>
    public void Destroy()
    {
        _destroying = true;
        Pane.Dispose();
        Dispose();
    }

    /// <summary>Resizes to fit the page's content (DIPs, without the resize strips); see <see cref="SetHeightDip"/>.</summary>
    public void FitContentHeight(double contentDip) => SetHeightDip(contentDip + 2 * ResizeStripDip);

    /// <summary>
    /// Sets the window height in DIPs within the height limits and the work area, keeping the window's centre. Not saved
    /// as the user's height.
    /// </summary>
    public void SetHeightDip(double heightDip)
    {
        if (WindowState != FormWindowState.Normal)
        {
            return;
        }
        Rectangle area = Screen.FromHandle(Handle).WorkingArea;
        double dip = Math.Clamp(heightDip, MinHeightDip, MaxHeightDip);
        int height = Math.Min((int)Math.Ceiling(dip * DpiScale), area.Height);
        int top = Math.Clamp(Top - (height - Height) / 2, area.Top, area.Bottom - height);
        NativeUi.SetBounds(Handle, new Rectangle(Left, top, Width, height));
    }

    public void BeginDrag()
    {
        NativeUi.ReleaseCapture();
        NativeUi.SendMessage(Handle, NativeUi.WmNcLButtonDown, NativeUi.HtCaption, 0);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaWindowCornerPreference, NativeUi.DwmwcpRound);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaUseImmersiveDarkMode, _dark ? 1 : 0);
        Pane.SetBounds(PaneBounds());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Only Destroy, Windows shutdown or Application.Exit really close it; TrayApp keeps using the window otherwise.
        if (!_destroying && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.ApplicationExitCall))
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated)
        {
            Pane.SetBounds(PaneBounds());
        }
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Pane.NotifyMoved();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Pane.Focus();
    }

    protected override unsafe void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeUi.WmNcCalcSize when m.WParam != 0:
                // The whole window is client area.
                m.Result = 0;
                return;
            case NativeUi.WmNcHitTest:
            {
                if (_compact)
                {
                    m.Result = NativeUi.HtClient;
                    return;
                }
                int y = (short)((long)m.LParam >> 16) - Top;
                int strip = Strip();
                if (AppIdentity.IsPairside)
                {
                    int x = (short)((long)m.LParam & 0xffff) - Left;
                    if (x < strip)
                    {
                        m.Result = y < strip ? NativeUi.HtTopLeft : y >= Height - strip ? NativeUi.HtBottomLeft : NativeUi.HtLeft;
                        return;
                    }
                    if (x >= Width - strip)
                    {
                        m.Result = y < strip ? NativeUi.HtTopRight : y >= Height - strip ? NativeUi.HtBottomRight : NativeUi.HtRight;
                        return;
                    }
                }
                m.Result = y < strip ? NativeUi.HtTop : y >= Height - strip ? NativeUi.HtBottom : NativeUi.HtClient;
                return;
            }
            case NativeUi.WmGetMinMaxInfo:
            {
                float scale = NativeUi.GetDpiForWindow(m.HWnd) / 96f;
                var info = (NativeUi.MinMaxInfo*)m.LParam;
                int minWidth = _compact ? 1 : AppIdentity.IsPairside ? MinWidthDip : WidthDip;
                int maxWidth = AppIdentity.IsPairside ? MaxWidthDip : WidthDip;
                int minHeight = _compact ? 1 : MinHeightDip;
                info->MinTrackSize = new Point((int)MathF.Round(minWidth * scale), (int)MathF.Round(minHeight * scale));
                info->MaxTrackSize = new Point((int)MathF.Round(maxWidth * scale), (int)MathF.Round(MaxHeightDip * scale));
                m.Result = 0;
                return;
            }
            case NativeUi.WmDpiChanged:
            {
                var r = *(NativeUi.Rect*)m.LParam;
                NativeUi.SetBounds(Handle, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                m.Result = 0;
                return;
            }
            case NativeUi.WmExitSizeMove:
                base.WndProc(ref m);
                Resized?.Invoke(Width / DpiScale, Height / DpiScale);
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }

    private int Strip() => (int)MathF.Round(ResizeStripDip * DpiScale);

    private Rectangle PaneBounds()
    {
        int strip = Strip();
        int horizontal = AppIdentity.IsPairside ? strip : 0;
        return new Rectangle(horizontal, strip, Math.Max(0, Width - 2 * horizontal), Math.Max(0, Height - 2 * strip));
    }
}
