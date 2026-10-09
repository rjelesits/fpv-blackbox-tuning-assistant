using System.Globalization;
using System.IO;

namespace FPVBlackboxTuningAssistant.Services;

public static class SetpointTrackingAnalyzer
{
    private const double ActiveThrottleMinimum = 1100;
    private const int MinimumSamples = 2000;
    private const double MinimumDurationSeconds = 2;
    private static readonly string[] AxisNames = ["Roll", "Pitch", "Yaw"];

    public static SetpointTrackingAssessment Analyze(
        string csvPath,
        string? headersCsvPath,
        string firmware,
        string? modeFilter = null)
    {
        if (!firmware.Contains("Betaflight", StringComparison.OrdinalIgnoreCase))
            return SetpointTrackingAssessment.Unavailable("Die Setpoint-/Gyro-Kennzahl ist derzeit nur für Betaflight-Logs freigegeben.");

        if (string.IsNullOrWhiteSpace(headersCsvPath) || !TryReadGyroScale(headersCsvPath, out var gyroScale))
            return SetpointTrackingAssessment.Unavailable("Die Gyro-Skalierung aus dem Log-Header fehlt oder ist ungültig.");

        using var reader = new StreamReader(csvPath);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return SetpointTrackingAssessment.Unavailable("Die Flug-CSV enthält keine Messwertspalten.");

        var headers = headerLine.Split(',').Select(name => name.Trim()).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
            columns.TryAdd(headers[index], index);

        if (!columns.TryGetValue("time (us)", out var timeColumn)
            || !columns.TryGetValue("rcCommand[3]", out var throttleColumn))
            return SetpointTrackingAssessment.Unavailable("Zeit- oder Throttle-Spalte fehlt.");

        var modeColumn = -1;
        if (modeFilter is not null && !columns.TryGetValue("flightModeFlags (flags)", out modeColumn))
            return SetpointTrackingAssessment.Unavailable("Flugmodus-Spalte fehlt; eine getrennte Modusauswertung ist nicht möglich.");

        var setpointColumns = Enumerable.Range(0, 3)
            .Select(index => columns.TryGetValue($"setpoint[{index}]", out var column) ? column : -1)
            .ToArray();
        var gyroColumns = Enumerable.Range(0, 3)
            .Select(index => columns.TryGetValue($"gyroADC[{index}]", out var column) ? column : -1)
            .ToArray();
        if (setpointColumns.Any(index => index < 0) || gyroColumns.Any(index => index < 0))
            return SetpointTrackingAssessment.Unavailable("Setpoint- oder Gyro-Achsen fehlen im Log.");

        var accumulators = Enumerable.Range(0, 3).Select(_ => new AxisAccumulator()).ToArray();
        var setpoints = new double[3];
        var gyroRates = new double[3];
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
                || !TryParse(fields[timeColumn], out var timestamp)
                || !TryParse(fields[throttleColumn], out var throttle)
                || throttle < ActiveThrottleMinimum)
                continue;

            if (modeFilter is not null
                && !string.Equals(FlightModeAnalyzer.ClassifyModeFlags(fields[modeColumn], firmware), modeFilter, StringComparison.Ordinal))
                continue;

            var validRow = true;
            for (var axis = 0; axis < 3; axis++)
            {
                if (!TryParse(fields[setpointColumns[axis]], out setpoints[axis])
                    || !TryParse(fields[gyroColumns[axis]], out var rawGyro))
                {
                    validRow = false;
                    break;
                }

                gyroRates[axis] = rawGyro * gyroScale;
            }

            if (!validRow)
                continue;

            samples++;
            firstTime = Math.Min(firstTime, (long)timestamp);
            lastTime = Math.Max(lastTime, (long)timestamp);
            for (var axis = 0; axis < 3; axis++)
                accumulators[axis].Add(Math.Abs(setpoints[axis] - gyroRates[axis]));
        }

        var durationSeconds = samples > 0 && lastTime >= firstTime
            ? (lastTime - firstTime) / 1_000_000d
            : 0;
        if (samples < MinimumSamples || durationSeconds < MinimumDurationSeconds)
            return SetpointTrackingAssessment.Unavailable(
                $"Zu wenige aktive Messwerte für einen Setpoint-/Gyro-Vergleich ({samples:N0} Werte über {durationSeconds:N1} s).",
                samples,
                durationSeconds);

        return new SetpointTrackingAssessment(
            IsAvailable: true,
            Reason: string.Empty,
            Samples: samples,
            DurationSeconds: durationSeconds,
            Axes: accumulators.Select((accumulator, index) => accumulator.Build(AxisNames[index])).ToArray());
    }

    public static IReadOnlyList<ModeSpecificSetpointTrackingAssessment> AnalyzeByMode(
        string csvPath,
        string? headersCsvPath,
        string firmware,
        FlightModeAssessment? modeAssessment)
    {
        if (modeAssessment?.IsAvailable != true)
            return [];

        return modeAssessment.Modes
            .Where(mode => FlightModeAnalyzer.IsTuningMode(mode.Mode))
            .Select(mode => new ModeSpecificSetpointTrackingAssessment(
                mode.Mode,
                Analyze(csvPath, headersCsvPath, firmware, mode.Mode)))
            .ToArray();
    }

    private static bool TryReadGyroScale(string headersCsvPath, out double gyroScale)
    {
        gyroScale = 0;
        if (!File.Exists(headersCsvPath))
            return false;

        using var reader = new StreamReader(headersCsvPath);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var separator = line.IndexOf(',');
            if (separator < 0
                || !line[..separator].Trim().Trim('"').Equals("gyro_scale", StringComparison.OrdinalIgnoreCase)
                    && !line[..separator].Trim().Trim('"').Equals("gyro.scale", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[(separator + 1)..].Trim().Trim('"');
            float parsed;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits))
                    return false;

                parsed = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            }
            else if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return false;
            }

            gyroScale = parsed;
            return double.IsFinite(gyroScale) && gyroScale > 0;
        }

        return false;
    }

    private static bool TryParse(string value, out double parsed) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed);

    private sealed class AxisAccumulator
    {
        private readonly List<double> _absoluteErrors = [];
        private double _sum;

        public void Add(double absoluteError)
        {
            _sum += absoluteError;
            _absoluteErrors.Add(absoluteError);
        }

        public SetpointTrackingAxisMetric Build(string axisName)
        {
            var sorted = _absoluteErrors.Order().ToArray();
            var percentileIndex = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);
            return new SetpointTrackingAxisMetric(
                axisName,
                MeanAbsoluteErrorDps: _sum / sorted.Length,
                Percentile95AbsoluteErrorDps: sorted[percentileIndex]);
        }
    }
}

public sealed record SetpointTrackingAxisMetric(
    string Axis,
    double MeanAbsoluteErrorDps,
    double Percentile95AbsoluteErrorDps);

public sealed record SetpointTrackingAssessment(
    bool IsAvailable,
    string Reason,
    long Samples,
    double DurationSeconds,
    IReadOnlyList<SetpointTrackingAxisMetric> Axes)
{
    public static SetpointTrackingAssessment Unavailable(
        string reason,
        long samples = 0,
        double durationSeconds = 0) => new(
            false,
            reason,
            samples,
            durationSeconds,
            Array.Empty<SetpointTrackingAxisMetric>());
}

public sealed record ModeSpecificSetpointTrackingAssessment(
    string Mode,
    SetpointTrackingAssessment Assessment);
