using System.Diagnostics;

namespace WPods.Ui;

/// <summary>
/// The nearby popup and the hotkey notification: a topmost tool window that never activates on show or click (only
/// Win+Shift+A activates the popup), no frame, DWM round corners and shadow. The host sizes and places it.
/// </summary>
internal sealed class PopupWindow : Form
{
    public const int WidthDip = 320;
    public const int MarginDip = 12;

    private bool _dark;
    private Rectangle _presentedBounds;
    private int _travel;
    private double _reveal;
    private double _motionFrom;
    private double _motionTo;
    private long _motionStarted;
    private System.Windows.Forms.Timer? _motion;

    public PopupWindow(Color background, bool dark, WebPane pane)
    {
        Pane = pane;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = background;
        _dark = dark;
    }

    public WebPane Pane { get; }

    /// <summary>The window moved to a monitor with another DPI; the host places it again.</summary>
    public event Action? ScaleChanged;

    /// <summary>Alt+F4 or an outside WM_CLOSE: the app hides the window instead.</summary>
    public event Action? CloseRequested;

    public event Action? Dismissed;

    public void Place(Rectangle bounds, int travel)
    {
        _presentedBounds = bounds;
        _travel = travel;
        if (Visible)
        {
            ApplyMotionFrame();
        }
    }

    public void Present()
    {
        bool animate = !Pane.ReduceMotion && NativeUi.ClientAnimationsEnabled();
        if (!Visible)
        {
            _reveal = animate ? 0 : 1;
            ApplyMotionFrame();
            Show();
        }
        AnimateTo(1, animate);
    }

    public void Dismiss() => AnimateTo(0, !Pane.ReduceMotion && NativeUi.ClientAnimationsEnabled());

    private void AnimateTo(double target, bool animate)
    {
        if (animate && _motion?.Enabled == true && _motionTo == target)
        {
            return;
        }
        _motion?.Stop();
        _motionTo = target;
        if (!animate || _reveal == target || !Visible)
        {
            FinishMotion();
            return;
        }
        if (_motion is null)
        {
            _motion = new System.Windows.Forms.Timer { Interval = 15 };
            _motion.Tick += (_, _) => RunMotionFrame();
        }
        _motionFrom = _reveal;
        _motionStarted = Stopwatch.GetTimestamp();
        _motion.Start();
    }

    private void RunMotionFrame()
    {
        bool entering = _motionTo > _motionFrom;
        double duration = entering ? 265 : 185;
        double t = Stopwatch.GetElapsedTime(_motionStarted).TotalMilliseconds /
            (duration * Math.Max(.45, Math.Abs(_motionTo - _motionFrom)));
        if (t >= 1)
        {
            FinishMotion();
            return;
        }
        double eased = entering ? 1 - Math.Pow(1 - t, 3) : t * t;
        _reveal = _motionFrom + (_motionTo - _motionFrom) * eased;
        ApplyMotionFrame();
    }

    private void ApplyMotionFrame()
    {
        Rectangle bounds = _presentedBounds;
        bounds.Offset(0, (int)Math.Round((1 - _reveal) * _travel));
        if (bounds != Bounds)
        {
            NativeUi.SetBounds(Handle, bounds);
        }
        Opacity = _reveal;
    }

    private void FinishMotion()
    {
        _motion?.Stop();
        _reveal = _motionTo;
        if (_motionTo == 0)
        {
            Hide();
            Opacity = 1;
            Dismissed?.Invoke();
            return;
        }
        ApplyMotionFrame();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // A thick frame (removed again by WM_NCCALCSIZE) is what gives the window its DWM shadow.
            cp.Style |= NativeUi.WsThickFrame;
            cp.ExStyle |= NativeUi.WsExNoActivate | NativeUi.WsExToolWindow | NativeUi.WsExTopmost;
            return cp;
        }
    }

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

    public void Destroy()
    {
        _motion?.Dispose();
        Pane.Dispose();
        Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaWindowCornerPreference, NativeUi.DwmwcpRound);
        NativeUi.SetWindowInt(Handle, NativeUi.DwmwaUseImmersiveDarkMode, _dark ? 1 : 0);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke();
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Pane.SetBounds(new Rectangle(0, 0, Width, Height));
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Pane.NotifyMoved();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeUi.WmNcCalcSize when m.WParam != 0:
                m.Result = 0;
                return;
            case NativeUi.WmNcHitTest:
                m.Result = NativeUi.HtClient;
                return;
            case NativeUi.WmMouseActivate:
                m.Result = NativeUi.MaNoActivate;
                return;
            case NativeUi.WmDpiChanged:
                m.Result = 0;
                ScaleChanged?.Invoke();
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }
}
