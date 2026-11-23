namespace WPods.Audio;

internal sealed class HeadphoneLevelSession
{
    private const double SignalFloor = -120;
    private double energySeconds;
    private double measuredSeconds;
    private double activeSeconds;

    internal double MeasuredMs => measuredSeconds * 1000;
    internal double ActiveMs => activeSeconds * 1000;
    internal int? AverageDb => measuredSeconds > 0 ? DisplayDb(Db(energySeconds / measuredSeconds)) : null;

    internal void Add(double seconds, double leftDb, double rightDb)
    {
        measuredSeconds += seconds;
        if (leftDb >= SignalFloor || rightDb >= SignalFloor) activeSeconds += seconds;
        energySeconds += seconds * StereoPower(leftDb, rightDb);
    }

    internal static int? StereoDb(double leftDb, double rightDb) =>
        DisplayDb(Db(StereoPower(leftDb, rightDb)));

    internal static int? DisplayDb(double db) => double.IsFinite(db) && db >= SignalFloor ? (int)Math.Round(db) : null;

    private static double StereoPower(double leftDb, double rightDb) => (Power(leftDb) + Power(rightDb)) / 2;
    private static double Db(double power) => 10 * Math.Log10(power);
    private static double Power(double db) => double.IsFinite(db) ? Math.Pow(10, db / 10) : 0;
}
