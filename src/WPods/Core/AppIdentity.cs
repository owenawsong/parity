namespace WPods.Core;

internal static class AppIdentity
{
#if PAIRSIDE_LIVE
    public const string Name = "Parity";
    // Used by existing installs for settings, launch-on-login and single-instance activation.
    public const string InstallationId = "Pairside";
    public static bool IsPairside => true;
#else
    public const string Name = "WPods";
    public const string InstallationId = "WPods";
    public static bool IsPairside => false;
#endif

    public static string Profile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        IsPairside ? Path.Combine(InstallationId, "Live") : Name);
}
