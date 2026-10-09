using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FPVBlackboxTuningAssistant.Services;

public static class FlightModeAnalyzer
{
    private const string ModeFlagsColumn = "flightModeFlags (flags)";
    private const double ActiveThrottleMinimum = 1100;
    private const long FlightBoundaryIgnoreWindowMicroseconds = 100_000;
    private const long MinimumModeDisplayDurationMicroseconds = 100_000;

    public static FlightModeAssessment Analyze(string csvPath, string? eventPath = null, string? firmware = null)
    {
        using var reader = new StreamReader(csvPath);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
            return FlightModeAssessment.Unavailable("Die Flug-CSV enthält keine Messwertspalten.");

        var headers = headerLine.Split(',').Select(name => name.Trim()).ToArray();
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
            columns.TryAdd(headers[index], index);

        if (!columns.TryGetValue("time (us)", out var timeColumn)
            || !columns.TryGetValue(ModeFlagsColumn, out var flagsColumn)
            || !columns.TryGetValue("rcCommand[3]", out var throttleColumn))
            return FlightModeAssessment.Unavailable("Zeit- oder Flugmodus-Spalte fehlt im Log.");

        var flightEvents = ReadFlightEvents(eventPath);
        var modes = new Dictionary<string, ModeAccumulator>(StringComparer.Ordinal);
        var samples = 0L;
        var transitions = new List<FlightModeTransition>();
        long? previousTimestamp = null;
        string? previousMode = null;
        long? firstTimestamp = null;
        var activeDurationMicroseconds = 0L;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;

            var fields = line.Split(',');
            if (fields.Length < headers.Length
                || !long.TryParse(fields[timeColumn].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp)
                || !double.TryParse(fields[throttleColumn].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var throttle)
                || throttle < ActiveThrottleMinimum)
            {
                // Keep the last mode to detect a switch made while the throttle is low,
                // but do not count idle time toward the in-flight duration.
                previousTimestamp = null;
                continue;
            }

            var mode = ClassifyModeFlags(fields[flagsColumn], firmware);
            if (previousMode is not null && previousTimestamp is not null)
            {
                var elapsed = Math.Max(0, timestamp - previousTimestamp.Value);
                modes[previousMode].AddDuration(elapsed);
                activeDurationMicroseconds += elapsed;
            }

            if (previousMode is not null
                && !string.Equals(previousMode, mode, StringComparison.Ordinal)
                && !IsAtFlightBoundary(timestamp, firstTimestamp, flightEvents.DisarmTimeMicroseconds))
            {
                transitions.Add(new FlightModeTransition(
                    previousMode,
                    mode,
                    firstTimestamp is null ? 0 : Math.Max(0, (timestamp - firstTimestamp.Value) / 1_000_000d)));
            }

            modes.TryAdd(mode, new ModeAccumulator());
            modes[mode].AddSample();
            samples++;
            firstTimestamp ??= timestamp;
            previousTimestamp = timestamp;
            previousMode = mode;
        }

        if (samples == 0)
            return FlightModeAssessment.Unavailable("Im Log wurden keine auswertbaren Flugmodus-Zeitpunkte gefunden.");

        var durationSeconds = activeDurationMicroseconds / 1_000_000d;
        var boundaryEventCount = flightEvents.ModeChangeTimesMicroseconds.Count(timestamp =>
            IsAtFlightBoundary(timestamp, firstTimestamp, flightEvents.DisarmTimeMicroseconds));
        var metrics = modes
            .Where(pair => pair.Value.DurationMicroseconds >= MinimumModeDisplayDurationMicroseconds)
            .Select(pair => new FlightModeMetric(pair.Key, pair.Value.Samples, pair.Value.DurationMicroseconds / 1_000_000d))
            .OrderByDescending(metric => metric.DurationSeconds)
            .ThenBy(metric => metric.Mode, StringComparer.Ordinal)
            .ToArray();

        return new FlightModeAssessment(true, string.Empty, samples, durationSeconds, transitions.Count, boundaryEventCount, metrics, transitions);
    }

    public static string ClassifyModeFlags(string flagsText, string? firmware)
    {
        var flags = flagsText.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // bbl_parser 1.1.1 decodes the Betaflight 2026.6.2 rcModeActivationMask
        // with the older flightModeFlags_e names. In that firmware, bit 0 is ARM,
        // bit 1 is ANGLE, and bit 2 is HORIZON. The parser therefore emits
        // ANGLE_MODE for ARM, HORIZON_MODE for Angle, and MAG for Horizon.
        if (firmware?.StartsWith("Betaflight 2026.6.2", StringComparison.OrdinalIgnoreCase) == true)
            return ClassifyBetaflight2026_6_2(flags);

        if (firmware?.StartsWith("Betaflight 2026.", StringComparison.OrdinalIgnoreCase) == true)
            return "Flugmodus nicht sicher zuordenbar (Betaflight 2026, Decoder-Flagzuordnung ungeprüft)";

        var hasAngle = flags.Contains("ANGLE_MODE", StringComparer.Ordinal);
        var hasHorizon = flags.Contains("HORIZON_MODE", StringComparer.Ordinal);

        if (flags.Contains("GPS_RESCUE_MODE", StringComparer.Ordinal))
            return "GPS-Rettung";
        if (flags.Contains("FAILSAFE_MODE", StringComparer.Ordinal))
            return "Failsafe";
        if (flags.Contains("PASSTHRU", StringComparer.Ordinal))
            return "Direkte Motorsteuerung (Passthrough)";
        if (flags.Contains("GPS_HOLD", StringComparer.Ordinal))
            return "GPS hält die Position";
        if (flags.Contains("GPS_HOME", StringComparer.Ordinal))
            return "GPS-Rückkehr zum Startpunkt";
        if (hasAngle && hasHorizon)
            return "Angle und Horizon zugleich aktiv";
        if (hasAngle)
            return "Stabilisiert sich selbst (Angle)";
        if (hasHorizon)
            return "Horizon (Selbstaufrichtung nahe Stick-Mitte)";

        // Acro/Rate is Betaflight's default roll/pitch behavior whenever neither
        // self-leveling mode is active. MAG, BARO and HEADFREE are separate mode
        // flags; none of them turns that default into an Angle/Horizon mode.
        var modifiers = new List<string>();
        if (flags.Contains("MAG", StringComparer.Ordinal))
            modifiers.Add("Magnetometer aktiv");
        if (flags.Contains("BARO", StringComparer.Ordinal))
            modifiers.Add("Höhenhaltung aktiv");
        if (flags.Contains("HEADFREE", StringComparer.Ordinal))
            modifiers.Add("Headfree aktiv");

        return modifiers.Count == 0
            ? "Manuell/Acro (Betaflight-Standard ohne Angle/Horizon)"
            : $"Manuell/Acro ({string.Join(", ", modifiers)})";
    }

    public static bool IsTuningMode(string mode) =>
        mode.StartsWith("Manuell/Acro", StringComparison.Ordinal)
        || string.Equals(mode, "Stabilisiert sich selbst (Angle)", StringComparison.Ordinal)
        || string.Equals(mode, "Horizon (Selbstaufrichtung nahe Stick-Mitte)", StringComparison.Ordinal);

    private static string ClassifyBetaflight2026_6_2(string[] flags)
    {
        var hasAngle = flags.Contains("HORIZON_MODE", StringComparer.Ordinal);
        var hasHorizon = flags.Contains("MAG", StringComparer.Ordinal);
        if (hasAngle && hasHorizon)
            return "Angle und Horizon zugleich aktiv";
        if (hasAngle)
            return "Stabilisiert sich selbst (Angle)";
        if (hasHorizon)
            return "Horizon (Selbstaufrichtung nahe Stick-Mitte)";

        // ANGLE_MODE is the ARM bit in this firmware's mask. Other legacy names
        // cannot safely be interpreted with this decoder, so do not use them to
        // make additional mode claims.
        return "Manuell/Acro (Betaflight-Standard ohne Angle/Horizon)";
    }

    private static bool IsAtFlightBoundary(long timestamp, long? firstFlightTimestamp, long? disarmTimestamp) =>
        firstFlightTimestamp is not null
        && timestamp - firstFlightTimestamp.Value <= FlightBoundaryIgnoreWindowMicroseconds
        || disarmTimestamp is not null
        && disarmTimestamp.Value >= timestamp
        && disarmTimestamp.Value - timestamp <= FlightBoundaryIgnoreWindowMicroseconds;

    private static (long[] ModeChangeTimesMicroseconds, long? DisarmTimeMicroseconds) ReadFlightEvents(string? eventPath)
    {
        if (string.IsNullOrWhiteSpace(eventPath) || !File.Exists(eventPath))
            return ([], null);

        var modeChangeTimes = new List<long>();
        long? disarmTime = null;
        try
        {
            foreach (var line in File.ReadLines(eventPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var name = root.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                    if (!root.TryGetProperty("time", out var timeValue) || !timeValue.TryGetInt64(out var timestamp))
                        continue;

                    if (string.Equals(name, "Flight mode change", StringComparison.OrdinalIgnoreCase))
                        modeChangeTimes.Add(timestamp);
                    else if (string.Equals(name, "Disarm", StringComparison.OrdinalIgnoreCase))
                        disarmTime = timestamp;
                }
                catch (JsonException)
                {
                    // Ignore malformed event records and continue with valid entries.
                }
            }
        }
        catch (IOException)
        {
            return ([], null);
        }
        catch (UnauthorizedAccessException)
        {
            return ([], null);
        }

        return (modeChangeTimes.ToArray(), disarmTime);
    }

    private sealed class ModeAccumulator
    {
        public long Samples { get; private set; }
        public long DurationMicroseconds { get; private set; }

        public void AddSample() => Samples++;
        public void AddDuration(long microseconds) => DurationMicroseconds += microseconds;
    }
}

public sealed record FlightModeMetric(string Mode, long Samples, double DurationSeconds);

public sealed record FlightModeTransition(string FromMode, string ToMode, double TimeFromFlightStartSeconds);

public sealed record FlightModeAssessment(
    bool IsAvailable,
    string Reason,
    long Samples,
    double DurationSeconds,
    int TransitionCount,
    int BoundaryEventCount,
    IReadOnlyList<FlightModeMetric> Modes,
    IReadOnlyList<FlightModeTransition> Transitions)
{
    public static FlightModeAssessment Unavailable(string reason) => new(
        false,
        reason,
        0,
        0,
        0,
        0,
        Array.Empty<FlightModeMetric>(),
        Array.Empty<FlightModeTransition>());
}
