namespace Pairside;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Pairside.Preview.Activate");
        using var instance = new Mutex(true, @"Local\Pairside.Desktop", out bool first);
        if (!first)
        {
            Desktop.AllowActivation();
            activation.Set();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var desktop = new Desktop(activation);
        Application.Run(desktop);
    }
}
