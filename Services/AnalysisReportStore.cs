using System.IO;
using System.Text.Json;

namespace FPVBlackboxTuningAssistant.Services;

public static class AnalysisReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetReportPath(string blackboxPath) =>
        Path.ChangeExtension(blackboxPath, ".analysis.json");

    public static LogAnalysisReport Create(
        LogStorageResult storedLog,
        BlackboxDecodeResult decoded,
        string dataQualityStatus,
        string hardwareCheckStatus)
    {
        return new LogAnalysisReport
        {
            DroneName = storedLog.DroneName,
            OriginalFileName = storedLog.OriginalFileName,
            StoredBlackboxFileName = Path.GetFileName(storedLog.StoredPath),
            Sha256 = storedLog.Sha256,
            AnalyzedAtUtc = DateTime.UtcNow,
            DecoderVersion = decoded.DecoderVersion,
            DataQualityStatus = dataQualityStatus,
            HardwareCheckStatus = hardwareCheckStatus,
            SessionCount = decoded.Sessions.Count,
            FlightFrameCount = decoded.FlightFrameCount,
            IFrameCount = decoded.Sessions.Sum(session => session.IFrames),
            PFrameCount = decoded.Sessions.Sum(session => session.PFrames),
            Sessions = decoded.Sessions.Select(CreateSessionReport).ToList()
        };
    }

    public static void Save(string reportPath, LogAnalysisReport report)
    {
        var temporaryPath = reportPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, JsonOptions));
        File.Move(temporaryPath, reportPath, overwrite: true);
    }

    public static LogAnalysisReport? Load(string reportPath)
    {
        if (!File.Exists(reportPath))
            return null;

        return JsonSerializer.Deserialize<LogAnalysisReport>(File.ReadAllText(reportPath), JsonOptions);
    }

    private static LogSessionAnalysisReport CreateSessionReport(BlackboxSessionSummary session)
    {
        var assessment = session.MotorAssessment;
        var tracking = session.SetpointTrackingAssessment;
        var pidActivity = session.PidActivityAssessment;
        return new LogSessionAnalysisReport
        {
            LogNumber = session.LogNumber,
            TotalFrames = session.TotalFrames,
            IFrames = session.IFrames,
            PFrames = session.PFrames,
            EventFrameCount = session.EventFrameCount,
            Board = session.Board,
            Firmware = session.Firmware,
            LoopTimeMicroseconds = session.LoopTimeUs,
            FlightModeChangeCount = session.FlightModeChangeCount,
            FlightModeAvailable = session.FlightModeAssessment?.IsAvailable == true,
            FlightModeReason = session.FlightModeAssessment?.Reason ?? "Keine Flugmodus-Auswertung verfügbar.",
            FlightModeDurationSeconds = session.FlightModeAssessment?.DurationSeconds ?? 0,
            FlightModeTransitionCount = session.FlightModeAssessment?.TransitionCount ?? 0,
            FlightModeBoundaryEventCount = session.FlightModeAssessment?.BoundaryEventCount ?? 0,
            FlightModes = session.FlightModeAssessment?.Modes.ToList() ?? [],
            FlightModeTransitions = session.FlightModeAssessment?.Transitions.ToList() ?? [],
            MotorAssessmentAvailable = assessment?.IsAvailable == true,
            MotorAssessmentReason = assessment?.Reason ?? "Keine Motorbewertung verfügbar.",
            MotorAssessmentSamples = assessment?.Samples ?? 0,
            MotorAssessmentDurationSeconds = assessment?.DurationSeconds ?? 0,
            MotorOutputBiasPercent = assessment?.MotorOutputBiasPercent.ToArray() ?? [],
            ErpmBiasPercent = assessment?.ErpmBiasPercent.ToArray() ?? [],
            StrongOutputDeviation = assessment?.StrongOutputDeviation ?? false,
            StrongErpmDeviation = assessment?.StrongErpmDeviation ?? false,
            SetpointTrackingAvailable = tracking?.IsAvailable == true,
            SetpointTrackingReason = tracking?.Reason ?? "Keine Setpoint-/Gyro-Auswertung verfügbar.",
            SetpointTrackingSamples = tracking?.Samples ?? 0,
            SetpointTrackingDurationSeconds = tracking?.DurationSeconds ?? 0,
            SetpointTrackingAxes = tracking?.Axes.ToList() ?? [],
            SetpointTrackingByMode = session.SetpointTrackingByMode.ToList(),
            PidActivityAvailable = pidActivity?.IsAvailable == true,
            PidActivityReason = pidActivity?.Reason ?? "Keine PID-Term-Auswertung verfügbar.",
            PidActivitySamples = pidActivity?.Samples ?? 0,
            PidActivityDurationSeconds = pidActivity?.DurationSeconds ?? 0,
            PidActivityAxes = pidActivity?.Axes.ToList() ?? [],
            PidActivityByMode = session.PidActivityByMode.ToList(),
            TuningManeuverByMode = session.TuningManeuverByMode.ToList()
        };
    }
}

public sealed class LogAnalysisReport
{
    public int Version { get; set; } = 5;
    public string DroneName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredBlackboxFileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTime AnalyzedAtUtc { get; set; }
    public string DecoderVersion { get; set; } = string.Empty;
    public string DataQualityStatus { get; set; } = string.Empty;
    public string HardwareCheckStatus { get; set; } = string.Empty;
    public int SessionCount { get; set; }
    public int FlightFrameCount { get; set; }
    public int IFrameCount { get; set; }
    public int PFrameCount { get; set; }
    public List<LogSessionAnalysisReport> Sessions { get; set; } = [];
    public List<RecordedHardwareCheck> UserHardwareChecks { get; set; } = [];
    public int[] ShortLogSessionNumbers { get; set; } = [];
    public string ShortLogRecoveryContext { get; set; } = string.Empty;
    public BetaflightTuningBaselineReport? TuningBaseline { get; set; }
    public List<TuningRecommendationAuditReport> TuningRecommendationHistory { get; set; } = [];
    public List<ExportedTuningProposalReport> ExportedTuningProposals { get; set; } = [];
}

/// <summary>
/// Persisted record of a user-requested tuning evaluation, including blocked
/// outcomes, the exact inputs, evidence, and proposed allowlisted changes.
/// </summary>
public sealed class TuningRecommendationAuditReport
{
    public Guid RecommendationId { get; set; }
    public string SourceLogSha256 { get; set; } = string.Empty;
    public DateTime EvaluatedAtUtc { get; set; }
    public string Goal { get; set; } = string.Empty;
    public bool HardwareChecksComplete { get; set; }
    public bool HardwareIssueReported { get; set; }
    public bool SameFlightControllerConfirmed { get; set; }
    public bool ActiveProfilesConfirmed { get; set; }
    public bool PropwashObserved { get; set; }
    public string MotorTemperature { get; set; } = string.Empty;
    public List<string> AffectedAxes { get; set; } = [];
    public string FirmwareVersion { get; set; } = string.Empty;
    public string CliSnapshotKind { get; set; } = string.Empty;
    public string CliSnapshotFileName { get; set; } = string.Empty;
    public string CliSnapshotSha256 { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string BoardName { get; set; } = string.Empty;
    public int? ActivePidProfile { get; set; }
    public int? ActiveRateProfile { get; set; }
    public string OutcomeMessage { get; set; } = string.Empty;
    public List<BetaflightCliProposalChange> Changes { get; set; } = [];
}

/// <summary>
/// Auditable metadata for the exact CLI draft saved by the user. The report stores
/// only the draft identity and proposed allowlisted changes, not the CLI file itself.
/// </summary>
public sealed class ExportedTuningProposalReport
{
    public Guid RecommendationId { get; set; }
    public string SourceLogSha256 { get; set; } = string.Empty;
    public string SourceLogFileName { get; set; } = string.Empty;
    public string DraftFileName { get; set; } = string.Empty;
    public string DraftSha256 { get; set; } = string.Empty;
    public string CliBaselineFileName { get; set; } = string.Empty;
    public string CliBaselineSha256 { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string BoardName { get; set; } = string.Empty;
    public int TargetPidProfile { get; set; }
    public int TargetRateProfile { get; set; }
    public DateTime ExportedAtUtc { get; set; }
    public List<BetaflightCliProposalChange> Changes { get; set; } = [];
}

/// <summary>
/// Stores only the allowlisted tuning values parsed from a CLI file. The raw
/// CLI export is deliberately not copied into the analysis report.
/// </summary>
public sealed class BetaflightTuningBaselineReport
{
    public string SnapshotKind { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string BoardName { get; set; } = string.Empty;
    public int? ActivePidProfile { get; set; }
    public int? ActiveRateProfile { get; set; }
    public List<int> PidProfiles { get; set; } = [];
    public List<int> RateProfiles { get; set; } = [];
    public List<BetaflightTuningSetting> TuningSettings { get; set; } = [];
    public bool SameFlightControllerConfirmed { get; set; }
    public bool ActiveProfilesConfirmed { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

public sealed class LogSessionAnalysisReport
{
    public int LogNumber { get; set; }
    public int TotalFrames { get; set; }
    public int IFrames { get; set; }
    public int PFrames { get; set; }
    public int EventFrameCount { get; set; }
    public int LoopTimeMicroseconds { get; set; }
    public string Board { get; set; } = string.Empty;
    public int FlightModeChangeCount { get; set; }
    public bool FlightModeAvailable { get; set; }
    public string FlightModeReason { get; set; } = string.Empty;
    public double FlightModeDurationSeconds { get; set; }
    public int FlightModeTransitionCount { get; set; }
    public int FlightModeBoundaryEventCount { get; set; }
    public List<FlightModeMetric> FlightModes { get; set; } = [];
    public List<FlightModeTransition> FlightModeTransitions { get; set; } = [];
    public string Firmware { get; set; } = string.Empty;
    public bool MotorAssessmentAvailable { get; set; }
    public string MotorAssessmentReason { get; set; } = string.Empty;
    public long MotorAssessmentSamples { get; set; }
    public double MotorAssessmentDurationSeconds { get; set; }
    public double[] MotorOutputBiasPercent { get; set; } = [];
    public double[] ErpmBiasPercent { get; set; } = [];
    public bool StrongOutputDeviation { get; set; }
    public bool StrongErpmDeviation { get; set; }
    public bool SetpointTrackingAvailable { get; set; }
    public string SetpointTrackingReason { get; set; } = string.Empty;
    public long SetpointTrackingSamples { get; set; }
    public double SetpointTrackingDurationSeconds { get; set; }
    public List<SetpointTrackingAxisMetric> SetpointTrackingAxes { get; set; } = [];
    public List<ModeSpecificSetpointTrackingAssessment> SetpointTrackingByMode { get; set; } = [];
    public bool PidActivityAvailable { get; set; }
    public string PidActivityReason { get; set; } = string.Empty;
    public long PidActivitySamples { get; set; }
    public double PidActivityDurationSeconds { get; set; }
    public List<PidAxisActivityMetric> PidActivityAxes { get; set; } = [];
    public List<ModeSpecificPidActivityAssessment> PidActivityByMode { get; set; } = [];
    public List<ModeSpecificTuningManeuverAssessment> TuningManeuverByMode { get; set; } = [];
}

public sealed class RecordedHardwareCheck
{
    public string Step { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; }
}
