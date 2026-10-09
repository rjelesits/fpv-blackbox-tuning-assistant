using System.Globalization;
using System.IO;

namespace FPVBlackboxTuningAssistant.Services;

/// <summary>
/// Describes how much deliberate roll/pitch stick input is present in each mode.
/// It is a data-coverage check, not a PID-quality score.
/// </summary>
public static class TuningManeuverAnalyzer
{
    public const double DetailedTuningMinimumSampleRateHz = 1800;
    public const int MinimumHighStickExcursionsPerAxis = 3;
    public const int MinimumNearFullStickExcursionsPerAxis = 3;
    private const double ActiveThrottleMinimum = 1100;
    private const double HighStickCommandThreshold = 250;
    private const double NearFullStickCommandThreshold = 450;
    private const long MinimumExcursionDurationMicroseconds = 25_000;
    private const long MaximumContiguousGapMicroseconds = 10_000;
    private const int MinimumSamples = 2000;
    private const double MinimumDurationSeconds = 2;
    private static readonly string[] AxisNames = ["Roll", "Pitch"];

    public static IReadOnlyList<ModeSpecificTuningManeuverAssessment> AnalyzeByMode(
        string csvPath,
        string firmware,
        FlightModeAssessment? modeAssessment)
    {
        if (modeAssessment?.IsAvailable != true)
            return [];

        return modeAssessment.Modes
            .Where(mode => FlightModeAnalyzer.IsTuningMode(mode.Mode))
            .Select(mode => new ModeSpecificTuningManeuverAssessment(
                mode.Mode,
                Analyze(csvPath, firmware, mode.Mode)))
            .ToArray();
    }

    public static TuningManeuverAssessment Analyze(string csvPath, string firmware, string modeFilter)
    {
        using var reader = new StreamReader(csvPath);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return TuningManeuverAssessment.Unavailable("Die Flug-CSV enthält keine Messwertspalten.");

        var headers = headerLine.Split(',').Select(name => name.Trim()).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
            columns.TryAdd(headers[index], index);

        if (!columns.TryGetValue("time (us)", out var timeColumn)
            || !columns.TryGetValue("rcCommand[3]", out var throttleColumn)
            || !columns.TryGetValue("flightModeFlags (flags)", out var modeColumn))
            return TuningManeuverAssessment.Unavailable("Zeit-, Throttle- oder Flugmodus-Spalte fehlt.");

        var stickColumns = Enumerable.Range(0, AxisNames.Length)
            .Select(axis => columns.TryGetValue($"rcCommand[{axis}]", out var column) ? column : -1)
            .ToArray();
        var setpointColumns = Enumerable.Range(0, AxisNames.Length)
            .Select(axis => columns.TryGetValue($"setpoint[{axis}]", out var column) ? column : -1)
            .ToArray();
        if (stickColumns.Any(column => column < 0) || setpointColumns.Any(column => column < 0))
            return TuningManeuverAssessment.Unavailable("Roll-/Pitch-Stick- oder Setpoint-Spalten fehlen.");

        var accumulators = Enumerable.Range(0, AxisNames.Length).Select(_ => new AxisAccumulator()).ToArray();
        var stickValues = new double[AxisNames.Length];
        var setpointValues = new double[AxisNames.Length];
        long samples = 0;
        long firstTime = long.MaxValue;
        long lastTime = long.MinValue;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;

            var fields = line.Split(',');
            if (fields.Length < headers.Length
                || !TryParse(fields[timeColumn], out var timestamp)
                || !TryParse(fields[throttleColumn], out var throttle))
                continue;

            var mode = FlightModeAnalyzer.ClassifyModeFlags(fields[modeColumn], firmware);
            if (!string.Equals(mode, modeFilter, StringComparison.Ordinal)
                || throttle < ActiveThrottleMinimum)
            {
                foreach (var accumulator in accumulators)
                    accumulator.EndExcursions();
                continue;
            }

            var validRow = true;
            for (var axis = 0; axis < AxisNames.Length; axis++)
            {
                if (!TryParse(fields[stickColumns[axis]], out stickValues[axis])
                    || !TryParse(fields[setpointColumns[axis]], out setpointValues[axis]))
                {
                    validRow = false;
                    break;
                }
            }

            if (!validRow)
            {
                foreach (var accumulator in accumulators)
                    accumulator.EndExcursions();
                continue;
            }

            samples++;
            firstTime = Math.Min(firstTime, (long)timestamp);
            lastTime = Math.Max(lastTime, (long)timestamp);
            for (var axis = 0; axis < AxisNames.Length; axis++)
                accumulators[axis].Add((long)timestamp, stickValues[axis], setpointValues[axis]);
        }

        foreach (var accumulator in accumulators)
            accumulator.EndExcursions();

        var durationSeconds = samples > 0 && lastTime >= firstTime
            ? (lastTime - firstTime) / 1_000_000d
            : 0;
        if (samples < MinimumSamples || durationSeconds < MinimumDurationSeconds)
            return TuningManeuverAssessment.Unavailable(
                $"Zu wenige aktive Messwerte für die Manöverabdeckung ({samples:N0} Werte über {durationSeconds:N1} s).",
                samples,
                durationSeconds,
                accumulators.Select((accumulator, index) => accumulator.Build(AxisNames[index])).ToArray());

        return new TuningManeuverAssessment(
            IsAvailable: true,
            Reason: string.Empty,
            Samples: samples,
            DurationSeconds: durationSeconds,
            SampleRateHz: samples / durationSeconds,
            Axes: accumulators.Select((accumulator, index) => accumulator.Build(AxisNames[index])).ToArray());
    }

    private static bool TryParse(string value, out double parsed) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed);

    private sealed class AxisAccumulator
    {
        private long? _excursionStart;
        private long _lastExcursionSample;
        private long? _nearFullExcursionStart;
        private long _lastNearFullExcursionSample;
        private int _excursionCount;
        private int _nearFullExcursionCount;
        private long _totalExcursionMicroseconds;
        private double _peakStickCommand;
        private double _peakSetpointDps;
        private double _peakSetpointAtHighStickDps;
        private double _peakSetpointAtNearFullStickDps;

        public void Add(long timestamp, double stickCommand, double setpoint)
        {
            _peakSetpointDps = Math.Max(_peakSetpointDps, Math.Abs(setpoint));
            var absoluteStickCommand = Math.Abs(stickCommand);
            if (absoluteStickCommand >= NearFullStickCommandThreshold)
            {
                if (_nearFullExcursionStart is not null
                    && timestamp - _lastNearFullExcursionSample > MaximumContiguousGapMicroseconds)
                    EndNearFullExcursion();

                _nearFullExcursionStart ??= timestamp;
                _lastNearFullExcursionSample = timestamp;
                _peakSetpointAtNearFullStickDps = Math.Max(_peakSetpointAtNearFullStickDps, Math.Abs(setpoint));
            }
            else
            {
                EndNearFullExcursion();
            }

            if (absoluteStickCommand < HighStickCommandThreshold)
            {
                EndExcursion();
                return;
            }

            _peakStickCommand = Math.Max(_peakStickCommand, absoluteStickCommand);
            _peakSetpointAtHighStickDps = Math.Max(_peakSetpointAtHighStickDps, Math.Abs(setpoint));
            if (_excursionStart is not null && timestamp - _lastExcursionSample > MaximumContiguousGapMicroseconds)
                EndExcursion();

            _excursionStart ??= timestamp;
            _lastExcursionSample = timestamp;
        }

        public void EndExcursion()
        {
            if (_excursionStart is null)
                return;

            var duration = Math.Max(0, _lastExcursionSample - _excursionStart.Value);
            if (duration >= MinimumExcursionDurationMicroseconds)
            {
                _excursionCount++;
                _totalExcursionMicroseconds += duration;
            }

            _excursionStart = null;
        }

        private void EndNearFullExcursion()
        {
            if (_nearFullExcursionStart is null)
                return;

            var duration = Math.Max(0, _lastNearFullExcursionSample - _nearFullExcursionStart.Value);
            if (duration >= MinimumExcursionDurationMicroseconds)
                _nearFullExcursionCount++;

            _nearFullExcursionStart = null;
        }

        public void EndExcursions()
        {
            EndExcursion();
            EndNearFullExcursion();
        }

        public TuningManeuverAxisMetric Build(string axis) => new(
            axis,
            _excursionCount,
            _totalExcursionMicroseconds / 1_000_000d,
            _peakStickCommand,
            _peakSetpointDps)
        {
            PeakSetpointAtHighStickDps = _peakSetpointAtHighStickDps,
            NearFullStickExcursionCount = _nearFullExcursionCount,
            PeakSetpointAtNearFullStickDps = _peakSetpointAtNearFullStickDps
        };
    }
}

public sealed record TuningManeuverAxisMetric(
    string Axis,
    int HighStickExcursionCount,
    double TotalHighStickDurationSeconds,
    double PeakStickCommand,
    double PeakSetpointDps)
{
    public double PeakSetpointAtHighStickDps { get; init; }
    public int NearFullStickExcursionCount { get; init; }
    public double PeakSetpointAtNearFullStickDps { get; init; }
}

public sealed record TuningManeuverAssessment(
    bool IsAvailable,
    string Reason,
    long Samples,
    double DurationSeconds,
    double SampleRateHz,
    IReadOnlyList<TuningManeuverAxisMetric> Axes)
{
    public static TuningManeuverAssessment Unavailable(
        string reason,
        long samples = 0,
        double durationSeconds = 0,
        IReadOnlyList<TuningManeuverAxisMetric>? axes = null) => new(
            false,
            reason,
            samples,
            durationSeconds,
            durationSeconds > 0 ? samples / durationSeconds : 0,
            axes ?? Array.Empty<TuningManeuverAxisMetric>());
}

public sealed record ModeSpecificTuningManeuverAssessment(
    string Mode,
    TuningManeuverAssessment Assessment);
