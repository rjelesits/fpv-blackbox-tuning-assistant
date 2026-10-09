using System.Globalization;
using System.IO;

namespace FPVBlackboxTuningAssistant.Services;

public static class MotorOutputAnalyzer
{
    private const double ActiveThrottleMinimum = 1100;
    private const int MinimumSamples = 2000;
    private const double MinimumDurationSeconds = 2;
    private const double StrongOutputBiasPercent = 12;
    private const double StrongErpmBiasPercent = 20;
    public const int MinimumFlightFrames = 10000;

    public static MotorOutputAssessment Analyze(string csvPath)
    {
        using var reader = new StreamReader(csvPath);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return MotorOutputAssessment.Unavailable("Die CSV-Datei enthält keine Messwertspalten.");

        var headers = headerLine.Split(',').Select(name => name.Trim()).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
            columns.TryAdd(headers[index], index);

        if (!columns.TryGetValue("rcCommand[3]", out var throttleColumn)
            || !columns.TryGetValue("time (us)", out var timeColumn))
            return MotorOutputAssessment.Unavailable("Throttle- oder Zeitspalte fehlt.");

        var motorColumns = Enumerable.Range(0, 4)
            .Select(index => columns.TryGetValue($"motor[{index}]", out var column) ? column : -1)
            .ToArray();
        if (motorColumns.Any(index => index < 0))
            return MotorOutputAssessment.Unavailable("Die vier Motor-Ausgangsspalten fehlen.");

        var setpointColumns = Enumerable.Range(0, 3)
            .Select(index => columns.TryGetValue($"setpoint[{index}]", out var column) ? column : -1)
            .ToArray();
        var canFilterLowDemand = setpointColumns.All(index => index >= 0);
        var erpmColumns = Enumerable.Range(0, 4)
            .Select(index => columns.TryGetValue($"eRPM[{index}]", out var column) ? column : -1)
            .ToArray();
        var hasErpm = erpmColumns.All(index => index >= 0);

        var motorSums = new double[4];
        var erpmSums = new double[4];
        var erpmCounts = new long[4];
        var samples = 0L;
        var firstTime = long.MaxValue;
        var lastTime = long.MinValue;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;

            var fields = line.Split(',');
            if (fields.Length < headers.Length
                || !TryParse(fields[throttleColumn], out var throttle)
                || throttle < ActiveThrottleMinimum
                || !TryParse(fields[timeColumn], out var timestamp))
                continue;

            if (canFilterLowDemand && !HasLowRotationDemand(fields, setpointColumns))
                continue;

            var validRow = true;
            var motorValues = new double[4];
            for (var motor = 0; motor < 4; motor++)
            {
                if (!TryParse(fields[motorColumns[motor]], out motorValues[motor]))
                {
                    validRow = false;
                    break;
                }
            }

            if (!validRow)
                continue;

            samples++;
            firstTime = Math.Min(firstTime, (long)timestamp);
            lastTime = Math.Max(lastTime, (long)timestamp);
            for (var motor = 0; motor < 4; motor++)
            {
                motorSums[motor] += motorValues[motor];
                if (hasErpm && TryParse(fields[erpmColumns[motor]], out var erpm) && erpm > 0)
                {
                    erpmSums[motor] += erpm;
                    erpmCounts[motor]++;
                }
            }
        }

        var durationSeconds = samples > 0 && lastTime >= firstTime
            ? (lastTime - firstTime) / 1_000_000d
            : 0;
        if (samples == 0)
            return MotorOutputAssessment.Unavailable("Keine verwertbaren aktiven Motor-Messwerte gefunden.");

        var motorMeans = motorSums.Select(sum => sum / samples).ToArray();
        var outputBiasPercent = BiasFromMedian(motorMeans);
        var erpmMeans = Enumerable.Range(0, 4)
            .Select(index => erpmCounts[index] > 0 ? erpmSums[index] / erpmCounts[index] : 0)
            .ToArray();
        var erpmBiasPercent = erpmCounts.All(count => count >= MinimumSamples)
            ? BiasFromMedian(erpmMeans)
            : Array.Empty<double>();

        var enoughData = samples >= MinimumSamples && durationSeconds >= MinimumDurationSeconds;
        var strongestOutputMotor = IndexOfLargestAbsoluteValue(outputBiasPercent);
        var strongestErpmMotor = erpmBiasPercent.Length == 4
            ? IndexOfLargestAbsoluteValue(erpmBiasPercent)
            : -1;
        var outputDeviation = enoughData && Math.Abs(outputBiasPercent[strongestOutputMotor]) >= StrongOutputBiasPercent;
        var erpmDeviation = enoughData && strongestErpmMotor >= 0
            && Math.Abs(erpmBiasPercent[strongestErpmMotor]) >= StrongErpmBiasPercent;

        return new MotorOutputAssessment(
            IsAvailable: true,
            Reason: enoughData ? string.Empty : "Zu wenige aktive Messwerte für eine belastbare Motor-Plausibilitätsprüfung.",
            Samples: samples,
            DurationSeconds: durationSeconds,
            MotorMeanOutput: motorMeans,
            MotorOutputBiasPercent: outputBiasPercent,
            ErpmMean: erpmMeans,
            ErpmBiasPercent: erpmBiasPercent,
            StrongOutputDeviation: outputDeviation,
            StrongErpmDeviation: erpmDeviation,
            StrongestOutputMotorIndex: strongestOutputMotor,
            StrongestErpmMotorIndex: strongestErpmMotor,
            UsedLowRotationDemandFilter: canFilterLowDemand);
    }

    private static bool HasLowRotationDemand(string[] fields, int[] columns)
    {
        ReadOnlySpan<double> limits = [40, 40, 30];
        for (var axis = 0; axis < columns.Length; axis++)
        {
            if (!TryParse(fields[columns[axis]], out var setpoint)
                || Math.Abs(setpoint) > limits[axis])
                return false;
        }

        return true;
    }

    private static double[] BiasFromMedian(double[] values)
    {
        var sorted = values.Order().ToArray();
        var median = (sorted[1] + sorted[2]) / 2;
        if (median <= 0)
            return [0, 0, 0, 0];

        return values.Select(value => (value - median) / median * 100).ToArray();
    }

    private static int IndexOfLargestAbsoluteValue(double[] values)
    {
        var largest = 0;
        for (var index = 1; index < values.Length; index++)
        {
            if (Math.Abs(values[index]) > Math.Abs(values[largest]))
                largest = index;
        }

        return largest;
    }

    private static bool TryParse(string value, out double parsed) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
}

public sealed record MotorOutputAssessment(
    bool IsAvailable,
    string Reason,
    long Samples,
    double DurationSeconds,
    IReadOnlyList<double> MotorMeanOutput,
    IReadOnlyList<double> MotorOutputBiasPercent,
    IReadOnlyList<double> ErpmMean,
    IReadOnlyList<double> ErpmBiasPercent,
    bool StrongOutputDeviation,
    bool StrongErpmDeviation,
    int StrongestOutputMotorIndex,
    int StrongestErpmMotorIndex,
    bool UsedLowRotationDemandFilter)
{
    public bool HasStrongDeviation => StrongOutputDeviation || StrongErpmDeviation;

    public int MotorIndexForStrongestSignal => StrongErpmDeviation
        ? StrongestErpmMotorIndex
        : StrongestOutputMotorIndex;

    public double StrongestDeviationPercent => StrongErpmDeviation
        ? Math.Abs(ErpmBiasPercent[StrongestErpmMotorIndex])
        : Math.Abs(MotorOutputBiasPercent[StrongestOutputMotorIndex]);

    public string StrongestSignalName => StrongErpmDeviation && !StrongOutputDeviation
        ? "eRPM"
        : "Motorausgang";

    public static MotorOutputAssessment Unavailable(string reason) => new(
        false,
        reason,
        0,
        0,
        Array.Empty<double>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        false,
        false,
        -1,
        -1,
        false);
}
