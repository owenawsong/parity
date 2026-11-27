using System.Text.Json;
using System.Text.Json.Serialization;

// Bench-only transfer measurement. Each observed volume state gets its own
// offset, so any attenuation already present in loopback is accounted for once.
internal sealed class AcousticCalibration
{
    private const double MaxResidualDb = 3;
    private const double MinTestSpanDb = 6;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal sealed record Measurements(string EndpointId, int SampleRate, string ReferencePlane,
        string Setup, Trial[] Trials);
    internal sealed record Trial(string Id, string Signal, bool Holdout, Gains Gain,
        double LeftDbFs, double RightDbFs, double LeftDba, double RightDba);

    [method: JsonConstructor]
    internal readonly record struct Gains(double MasterDb, double LeftDb, double RightDb)
    {
        internal Gains Rounded() => new(Math.Round(MasterDb, 2), Math.Round(LeftDb, 2), Math.Round(RightDb, 2));
        internal bool IsFinite => double.IsFinite(MasterDb) && double.IsFinite(LeftDb) && double.IsFinite(RightDb);
    }

    private readonly record struct EarModel(double Offset, double Minimum, double Maximum)
    {
        internal double? Estimate(double digital) => digital >= Minimum && digital <= Maximum
            ? digital + Offset
            : null;
    }

    private readonly Dictionary<Gains, (EarModel Left, EarModel Right)> _models = [];
    internal string EndpointId { get; }
    internal int SampleRate { get; }
    internal string ReferencePlane { get; }
    internal string Setup { get; }
    internal int GainStates => _models.Count;
    internal int HoldoutCount { get; private set; }
    internal double WorstHoldoutDb { get; private set; }

    private AcousticCalibration(Measurements input)
    {
        EndpointId = input.EndpointId;
        SampleRate = input.SampleRate;
        ReferencePlane = input.ReferencePlane;
        Setup = input.Setup;
    }

    internal static AcousticCalibration Load(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 1024 * 1024) throw new InvalidDataException("Calibration must be smaller than 1 MiB.");
        var data = JsonSerializer.Deserialize<Measurements>(file, Json)
            ?? throw new InvalidDataException("Expected a calibration measurement set.");
        return Fit(data);
    }

    internal static AcousticCalibration Fit(Measurements data)
    {
        if (string.IsNullOrWhiteSpace(data.EndpointId) || data.SampleRate is < 44100 or > 192000 ||
            string.IsNullOrWhiteSpace(data.Setup) ||
            data.ReferencePlane is not ("ear-simulator" or "diffuse-field-corrected") ||
            data.Trials is not { Length: > 0 })
            throw new InvalidDataException("Specify the endpoint, sample rate, measurement setup, reference plane and trials.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trial in data.Trials)
        {
            if (trial is null || string.IsNullOrWhiteSpace(trial.Id) || !ids.Add(trial.Id.Trim()) ||
                string.IsNullOrWhiteSpace(trial.Signal) || !trial.Gain.IsFinite ||
                !ValidDigital(trial.LeftDbFs) || !ValidDigital(trial.RightDbFs) ||
                !double.IsFinite(trial.LeftDba) || !double.IsFinite(trial.RightDba))
                throw new InvalidDataException("Each trial needs unique ID, signal, finite gains, stereo digital RMS and measured dBA.");
        }

        var calibration = new AcousticCalibration(data);
        foreach (var group in data.Trials.GroupBy(t => t.Gain.Rounded()))
        {
            Trial[] fit = group.Where(t => !t.Holdout).ToArray();
            Trial[] check = group.Where(t => t.Holdout).ToArray();
            var trainingSignals = fit.Select(t => t.Signal.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (fit.Length < 2 || check.Length < 2 || check.Any(t => trainingSignals.Contains(t.Signal.Trim())))
                throw new InvalidDataException("Each gain state needs at least two fit trials and two independent validation trials using different signals.");

            var left = FitEar(fit, check, t => t.LeftDbFs, t => t.LeftDba, out double leftResidual);
            var right = FitEar(fit, check, t => t.RightDbFs, t => t.RightDba, out double rightResidual);
            calibration._models.Add(group.Key, (left, right));
            calibration.HoldoutCount += check.Length;
            calibration.WorstHoldoutDb = Math.Max(calibration.WorstHoldoutDb, Math.Max(leftResidual, rightResidual));
        }
        return calibration;
    }

    private static EarModel FitEar(Trial[] fit, Trial[] check, Func<Trial, double> digital,
        Func<Trial, double> acoustic, out double worstHoldout)
    {
        double offset = fit.Average(t => acoustic(t) - digital(t));
        double fitLow = fit.Min(digital), fitHigh = fit.Max(digital);
        double low = check.Min(digital), high = check.Max(digital);
        if (low < fitLow || high > fitHigh || high - low < MinTestSpanDb)
            throw new InvalidDataException("Held-out trials must span at least 6 dB inside the fitted range for each ear.");
        worstHoldout = check.Max(t => Math.Abs(digital(t) + offset - acoustic(t)));
        if (!double.IsFinite(offset) || worstHoldout > MaxResidualDb ||
            fit.Max(t => Math.Abs(digital(t) + offset - acoustic(t))) > MaxResidualDb)
            throw new InvalidDataException($"The constant-offset model failed the research residual limit. Worst held-out error: {worstHoldout:F2} dB.");
        return new EarModel(offset, low, high);
    }

    private static bool ValidDigital(double value) => double.IsFinite(value) && value <= 0;

    internal void CheckRoute(string endpoint, int sampleRate)
    {
        if (!MatchesRoute(endpoint, sampleRate))
            throw new InvalidDataException("Calibration belongs to a different endpoint or sample rate.");
    }

    internal bool MatchesRoute(string endpoint, int sampleRate) =>
        SampleRate == sampleRate && string.Equals(EndpointId, endpoint, StringComparison.OrdinalIgnoreCase);

    internal (double? Left, double? Right) Estimate(Gains gain, double left, double right)
    {
        return _models.TryGetValue(gain.Rounded(), out var state)
            ? (state.Left.Estimate(left), state.Right.Estimate(right))
            : (null, null);
    }
}
