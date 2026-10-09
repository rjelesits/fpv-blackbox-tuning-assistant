using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace FPVBlackboxTuningAssistant.Services;

public sealed class BlackboxDecoder
{
    private const string DecoderRelativePath = "third_party/bbl_parser/v1.1.1/bbl_parser.exe";
    private const string DecoderVersion = "1.1.1";
    private const int StatusDllNotFound = unchecked((int)0xC0000135);
    private const string VcRuntimeDownloadUrl = "https://aka.ms/vc14/vc_redist.x64.exe";
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(2);

    private static readonly Regex LogLine = new(
        @"^Log\s+(?<log>\d+)\s+of\s+(?<total>\d+),\s+frames:\s*(?<frames>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IFrameLine = new(
        @"^I frames\s+(?<count>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PFrameLine = new(
        @"^P frames\s+(?<count>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EFrameLine = new(
        @"^E frames\s+(?<count>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LoopTimeLine = new(
        @"^Looptime\s+(?<value>\d+)\s+avg\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CsvExportLine = new(
        @"^Exported flight data to:\s*(?<path>.+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HeadersExportLine = new(
        @"^Exported headers to:\s*(?<path>.+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<BlackboxDecodeResult> DecodeAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var decoderPath = Path.Combine(AppContext.BaseDirectory, DecoderRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(decoderPath))
            throw new BlackboxDecodeException($"Der mitgelieferte BBL-Decoder wurde nicht gefunden: {decoderPath}");

        EnsureVisualCppRuntime();

        var workspace = Path.Combine(Path.GetTempPath(), $"fpv-blackbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = decoderPath,
                WorkingDirectory = workspace,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("--force-export");
            startInfo.ArgumentList.Add("--event");
            startInfo.ArgumentList.Add("--output-dir");
            startInfo.ArgumentList.Add(workspace);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new BlackboxDecodeException("Der BBL-Decoder konnte nicht gestartet werden.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DecodeTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new BlackboxDecodeException("Die BBL-Dekodierung hat das Zeitlimit von zwei Minuten überschritten.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                if (process.ExitCode == StatusDllNotFound)
                    throw MissingVisualCppRuntime();

                var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new BlackboxDecodeException(FormatDecoderError(details));
            }

            var sessions = await Task.Run(() => ParseSessions(stdout)
                .Select(session =>
                {
                    var hasCsv = !string.IsNullOrWhiteSpace(session.CsvPath) && File.Exists(session.CsvPath);
                    var eventPath = hasCsv ? Path.ChangeExtension(session.CsvPath!, ".event") : null;
                    var flightModeAssessment = hasCsv
                        ? FlightModeAnalyzer.Analyze(session.CsvPath!, eventPath, session.Firmware)
                        : FlightModeAssessment.Unavailable("Keine Flug-CSV für die Flugmodus-Auswertung vorhanden.");
                    return session with
                    {
                        FlightModeChangeCount = hasCsv
                            ? CountFlightModeChanges(eventPath!)
                            : 0,
                        FlightModeAssessment = flightModeAssessment,
                        MotorAssessment = session.FlightFrameCount >= MotorOutputAnalyzer.MinimumFlightFrames && hasCsv
                            ? MotorOutputAnalyzer.Analyze(session.CsvPath!)
                            : MotorOutputAssessment.Unavailable("Zu kurze Log-Session oder keine Flug-CSV für die Motorbewertung."),
                        SetpointTrackingAssessment = hasCsv
                            ? SetpointTrackingAnalyzer.Analyze(session.CsvPath!, session.HeadersCsvPath, session.Firmware)
                            : SetpointTrackingAssessment.Unavailable("Keine Flug-CSV für die Setpoint-/Gyro-Auswertung vorhanden."),
                        PidActivityAssessment = hasCsv
                            ? PidActivityAnalyzer.Analyze(session.CsvPath!)
                            : PidActivityAssessment.Unavailable("Keine Flug-CSV für die PID-Term-Auswertung vorhanden."),
                        SetpointTrackingByMode = hasCsv
                            ? SetpointTrackingAnalyzer.AnalyzeByMode(session.CsvPath!, session.HeadersCsvPath, session.Firmware, flightModeAssessment)
                            : [],
                        PidActivityByMode = hasCsv
                            ? PidActivityAnalyzer.AnalyzeByMode(session.CsvPath!, session.Firmware, flightModeAssessment)
                            : [],
                        TuningManeuverByMode = hasCsv
                            ? TuningManeuverAnalyzer.AnalyzeByMode(session.CsvPath!, session.Firmware, flightModeAssessment)
                            : []
                    };
                })
                .ToArray(), cancellationToken);
            return new BlackboxDecodeResult(DecoderVersion, sessions, stdout, stderr);
        }
        catch (Win32Exception ex)
        {
            throw new BlackboxDecodeException(
                $"Windows konnte den BBL-Decoder nicht starten. Installiere das Microsoft Visual C++ Redistributable (x64) und starte die App erneut: {VcRuntimeDownloadUrl}",
                ex,
                isMissingVisualCppRuntime: true);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void EnsureVisualCppRuntime()
    {
        if (!NativeLibrary.TryLoad("VCRUNTIME140.dll", out var runtimeHandle))
            throw MissingVisualCppRuntime();

        NativeLibrary.Free(runtimeHandle);
    }

    private static BlackboxDecodeException MissingVisualCppRuntime() => new(
        $"Für den BBL-Decoder fehlt die Microsoft Visual C++ Laufzeit (x64; VCRUNTIME140.dll). Installiere das offizielle Redistributable und starte die App erneut: {VcRuntimeDownloadUrl}",
        isMissingVisualCppRuntime: true);

    private static IReadOnlyList<BlackboxSessionSummary> ParseSessions(string stdout)
    {
        var sessions = new List<BlackboxSessionSummary>();
        SessionBuilder? current = null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            var logMatch = LogLine.Match(line);
            if (logMatch.Success)
            {
                if (current is not null)
                    sessions.Add(current.Build());

                current = new SessionBuilder
                {
                    LogNumber = ParseInt(logMatch, "log"),
                    TotalLogs = ParseInt(logMatch, "total"),
                    TotalFrames = ParseInt(logMatch, "frames")
                };
                continue;
            }

            if (current is null)
                continue;

            if (line.StartsWith("Firmware:", StringComparison.Ordinal))
                current.Firmware = line["Firmware:".Length..].Trim();
            else if (line.StartsWith("Board:", StringComparison.Ordinal))
                current.Board = line["Board:".Length..].Trim();
            else if (IFrameLine.Match(line) is { Success: true } iMatch)
                current.IFrames = ParseInt(iMatch, "count");
            else if (PFrameLine.Match(line) is { Success: true } pMatch)
                current.PFrames = ParseInt(pMatch, "count");
            else if (EFrameLine.Match(line) is { Success: true } eMatch)
                current.EFrames = ParseInt(eMatch, "count");
            else if (LoopTimeLine.Match(line) is { Success: true } loopMatch)
                current.LoopTimeUs = ParseInt(loopMatch, "value");
            else if (CsvExportLine.Match(line) is { Success: true } csvMatch)
                current.CsvPath = csvMatch.Groups["path"].Value.Trim();
            else if (HeadersExportLine.Match(line) is { Success: true } headersMatch)
                current.HeadersCsvPath = headersMatch.Groups["path"].Value.Trim();
        }

        if (current is not null)
            sessions.Add(current.Build());

        return sessions;
    }

    private static int ParseInt(Match match, string group) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int CountFlightModeChanges(string eventPath)
    {
        if (!File.Exists(eventPath))
            return 0;

        var count = 0;
        try
        {
            foreach (var line in File.ReadLines(eventPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String
                        && string.Equals(name.GetString(), "Flight mode change", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed event records and keep the remaining analysis available.
                }
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        return count;
    }

    private static string FormatDecoderError(string details)
    {
        var firstUsefulLine = details.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("Usage:", StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(firstUsefulLine)
            ? "Der BBL-Decoder konnte die Datei nicht dekodieren."
            : $"Der BBL-Decoder hat die Datei abgelehnt: {firstUsefulLine}";
    }

    private sealed class SessionBuilder
    {
        public int LogNumber { get; init; }
        public int TotalLogs { get; init; }
        public int TotalFrames { get; init; }
        public int IFrames { get; set; }
        public int PFrames { get; set; }
        public int EFrames { get; set; }
        public int LoopTimeUs { get; set; }
        public string? CsvPath { get; set; }
        public string? HeadersCsvPath { get; set; }
        public string Firmware { get; set; } = string.Empty;
        public string Board { get; set; } = string.Empty;

        public BlackboxSessionSummary Build() => new(LogNumber, TotalLogs, TotalFrames, IFrames, PFrames, LoopTimeUs, Firmware, Board, CsvPath, null)
        {
            HeadersCsvPath = HeadersCsvPath,
            EventFrameCount = EFrames
        };
    }
}

public sealed record BlackboxSessionSummary(
    int LogNumber,
    int TotalLogs,
    int TotalFrames,
    int IFrames,
    int PFrames,
    int LoopTimeUs,
    string Firmware,
    string Board,
    string? CsvPath,
    MotorOutputAssessment? MotorAssessment)
{
    public int FlightFrameCount => IFrames + PFrames;
    public string? HeadersCsvPath { get; init; }
    public int EventFrameCount { get; init; }
    public int FlightModeChangeCount { get; init; }
    public FlightModeAssessment? FlightModeAssessment { get; init; }
    public SetpointTrackingAssessment? SetpointTrackingAssessment { get; init; }
    public PidActivityAssessment? PidActivityAssessment { get; init; }
    public IReadOnlyList<ModeSpecificSetpointTrackingAssessment> SetpointTrackingByMode { get; init; } = [];
    public IReadOnlyList<ModeSpecificPidActivityAssessment> PidActivityByMode { get; init; } = [];
    public IReadOnlyList<ModeSpecificTuningManeuverAssessment> TuningManeuverByMode { get; init; } = [];
}

public sealed record BlackboxDecodeResult(
    string DecoderVersion,
    IReadOnlyList<BlackboxSessionSummary> Sessions,
    string StandardOutput,
    string StandardError)
{
    public int FlightFrameCount => Sessions.Sum(session => session.FlightFrameCount);
    public bool HasFlightFrames => FlightFrameCount > 0;
}

public sealed class BlackboxDecodeException : Exception
{
    public BlackboxDecodeException(string message, Exception? innerException = null, bool isMissingVisualCppRuntime = false)
        : base(message, innerException)
    {
        IsMissingVisualCppRuntime = isMissingVisualCppRuntime;
    }

    public bool IsMissingVisualCppRuntime { get; }
}
