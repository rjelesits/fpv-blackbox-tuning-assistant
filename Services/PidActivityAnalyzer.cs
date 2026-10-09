using System.Globalization;
using System.IO;

namespace FPVBlackboxTuningAssistant.Services;

public static class PidActivityAnalyzer
{
    private const double ActiveThrottleMinimum = 1100;
    private const int MinimumSamples = 2000;
    private const double MinimumDurationSeconds = 2;
    private static readonly string[] AxisNames = ["Roll", "Pitch", "Yaw"];
    private static readonly string[] RequiredTerms = ["P", "I"];
    private static readonly string[] AllTerms = ["P", "I", "D", "F"];

    public static PidActivityAssessment Analyze(string csvPath, string? firmware = null, string? modeFilter = null)
    {
        using var reader = new StreamReader(csvPath);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return PidActivityAssessment.Unavailable("Die Flug-CSV enthält keine Messwertspalten.");

        var headers = headerLine.Split(',').Select(name => name.Trim()).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
            columns.TryAdd(headers[index], index);

        if (!columns.TryGetValue("time (us)", out var timeColumn)
            || !columns.TryGetValue("rcCommand[3]", out var throttleColumn))
            return PidActivityAssessment.Unavailable("Zeit- oder Throttle-Spalte fehlt.");

        var modeColumn = -1;
        if (modeFilter is not null && !columns.TryGetValue("flightModeFlags (flags)", out modeColumn))
            return PidActivityAssessment.Unavailable("Flugmodus-Spalte fehlt; eine getrennte Modusauswertung ist nicht möglich.");

        var termColumns = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var term in AllTerms)
        {
            var indexes = Enumerable.Range(0, 3)
                .Select(axis => columns.TryGetValue($"axis{term}[{axis}]", out var column) ? column : -1)
                .ToArray();
            if (RequiredTerms.Contains(term, StringComparer.Ordinal) && indexes.Any(index => index < 0))
                return PidActivityAssessment.Unavailable($"PID-Term {term} fehlt für mindestens eine Achse.");
            if (indexes.Any(index => index >= 0))
                termColumns.Add(term, indexes);
        }

        var accumulators = AxisNames
            .Select(axis => termColumns
                .Where(pair => pair.Value[Array.IndexOf(AxisNames, axis)] >= 0)
                .ToDictionary(pair => pair.Key, _ => new TermAccumulator(), StringComparer.Ordinal))
            .ToArray();
        var values = termColumns.Keys.ToDictionary(term => term, _ => new double[3], StringComparer.Ordinal);
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
            foreach (var (term, indexes) in termColumns)
            {
                for (var axis = 0; axis < 3; axis++)
                {
                    if (indexes[axis] < 0)
                        continue;

                    if (!TryParse(fields[indexes[axis]], out var value))
                    {
                        validRow = false;
                        break;
                    }

                    values[term][axis] = value;
                }

                if (!validRow)
                    break;
            }

            if (!validRow)
                continue;

            samples++;
            firstTime = Math.Min(firstTime, (long)timestamp);
            lastTime = Math.Max(lastTime, (long)timestamp);
            foreach (var (term, axisValues) in values)
            {
                for (var axis = 0; axis < 3; axis++)
                {
                    if (termColumns[term][axis] >= 0)
                        accumulators[axis][term].Add(axisValues[axis]);
                }
            }
        }

        var durationSeconds = samples > 0 && lastTime >= firstTime
            ? (lastTime - firstTime) / 1_000_000d
            : 0;
        if (samples < MinimumSamples || durationSeconds < MinimumDurationSeconds)
            return PidActivityAssessment.Unavailable(
                $"Zu wenige aktive Messwerte für eine PID-Term-Übersicht ({samples:N0} Werte über {durationSeconds:N1} s).",
                samples,
                durationSeconds);

        return new PidActivityAssessment(
            IsAvailable: true,
            Reason: string.Empty,
            Samples: samples,
            DurationSeconds: durationSeconds,
            Axes: accumulators.Select((axisAccumulator, axis) => new PidAxisActivityMetric(
                AxisNames[axis],
                axisAccumulator.Select(pair => pair.Value.Build(pair.Key)).ToArray())
            {
                MissingTerms = AllTerms.Except(axisAccumulator.Keys, StringComparer.Ordinal).ToArray()
            }).ToArray());
    }

    public static IReadOnlyList<ModeSpecificPidActivityAssessment> AnalyzeByMode(
        string csvPath,
        string firmware,
        FlightModeAssessment? modeAssessment)
    {
        if (modeAssessment?.IsAvailable != true)
            return [];

        return modeAssessment.Modes
            .Where(mode => FlightModeAnalyzer.IsTuningMode(mode.Mode))
            .Select(mode => new ModeSpecificPidActivityAssessment(
                mode.Mode,
                Analyze(csvPath, firmware, mode.Mode)))
            .ToArray();
    }

    private static bool TryParse(string value, out double parsed) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed);

    private sealed class TermAccumulator
    {
        private double _sumAbsolute;
        private double _sumSquares;
        private double _peakAbsolute;
        private long _count;

        public void Add(double value)
        {
            var absolute = Math.Abs(value);
            _sumAbsolute += absolute;
            _sumSquares += value * value;
            _peakAbsolute = Math.Max(_peakAbsolute, absolute);
            _count++;
        }

        public PidTermActivityMetric Build(string term) => new(
            term,
            _count == 0 ? 0 : _sumAbsolute / _count,
            _count == 0 ? 0 : Math.Sqrt(_sumSquares / _count),
            _peakAbsolute);
    }
}

public sealed record PidTermActivityMetric(
    string Term,
    double MeanAbsolute,
    double RootMeanSquare,
    double PeakAbsolute);

public sealed record PidAxisActivityMetric(
    string Axis,
    IReadOnlyList<PidTermActivityMetric> Terms)
{
    public IReadOnlyList<string> MissingTerms { get; init; } = [];
}

public sealed record PidActivityAssessment(
    bool IsAvailable,
    string Reason,
    long Samples,
    double DurationSeconds,
    IReadOnlyList<PidAxisActivityMetric> Axes)
{
    public static PidActivityAssessment Unavailable(
        string reason,
        long samples = 0,
        double durationSeconds = 0) => new(
            false,
            reason,
            samples,
            durationSeconds,
            Array.Empty<PidAxisActivityMetric>());
}

public sealed record ModeSpecificPidActivityAssessment(
    string Mode,
    PidActivityAssessment Assessment);
