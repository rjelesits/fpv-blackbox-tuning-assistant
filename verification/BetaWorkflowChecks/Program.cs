using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FPVBlackboxTuningAssistant.Services;

if (args.Length is < 1 or > 2)
    throw new ArgumentException("Aufruf: BetaWorkflowChecks <Pfad-zum-dump-all.txt> [optionaler-Pfad-zur-BBL]");

var cliPath = Path.GetFullPath(args[0]);
var cli = BetaflightCliConfigurationParser.ParseFile(cliPath);
var expectedCliHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cliPath)));
Check(cli.HasFullTuningBaseline, "vollständige dump-all-Basis erkannt");
Check(cli.FirmwareVersion == "2026.6.2", "Firmwareversion aus dem echten CLI-Dump erkannt");
Check(cli.SourceSha256 == expectedCliHash, "Hash identifiziert exakt die importierten CLI-Dateibytes");
Check(cli.ActivePidProfile == 0 && cli.ActiveRateProfile == 0, "wiederhergestellte aktive Profile erkannt");
Check(BetaflightCliConfigurationParser.BoardNamesMatch("BEFH BETAFPVG473_V3", cli.BoardName), "Boardname des realen Logs passt zur CLI-Basis");
Check(cli.FindSetting("rates_type", BetaflightTuningScope.RateProfile, cli.ActiveRateProfile)?.Value == "ACTUAL", "aktives Rates-Profil ist ACTUAL");

var fullStick = AnalyzeSyntheticAcroCsv(475);
var moderateStick = AnalyzeSyntheticAcroCsv(425);
Check(fullStick.IsAvailable && fullStick.SampleRateHz >= TuningManeuverAnalyzer.DetailedTuningMinimumSampleRateHz,
    "synthetischer Acro-Log erfüllt Datenmenge und 2-kHz-Schwelle");
Check(fullStick.Axes.All(axis => axis.NearFullStickExcursionCount == 3), "drei getrennte Ausschläge ab 90 % werden pro Achse gezählt");
Check(moderateStick.Axes.All(axis => axis.HighStickExcursionCount == 3 && axis.NearFullStickExcursionCount == 0),
    "Ausschläge über 50 %, aber unter 90 %, reichen nicht als Maximalraten-Beleg");

var session = CreateSession(fullStick);
var result = TuningRecommendationEngine.EvaluateActualRatesTrial(
    [session], cli, sameFlightControllerConfirmed: true, activeProfilesConfirmed: true,
    hardwareChecksComplete: true, hardwareIssueReported: false, ["Roll"]);
Check(result.Changes.Count == 1, "passende Messdaten und bestätigte CLI-Basis erzeugen einen Rate-Vorschlag");
var change = result.Changes.Single();
Check(change.Name == "roll_srate" && change.PreviousValue == "67" && change.ProposedValue == "63",
    "ACTUAL-Schritt senkt nur roll_srate von 67 auf 63");
Check(change.Evidence.Contains("670 → 630 °/s", StringComparison.Ordinal), "Evidenz zeigt berechnete Maximalrate vorher/nachher");

var blocked = TuningRecommendationEngine.EvaluateActualRatesTrial(
    [CreateSession(moderateStick)], cli, true, true, true, false, ["Roll"]);
Check(blocked.Changes.Count == 0 && blocked.Message.Contains("ab 90 %", StringComparison.Ordinal),
    "mittlere Stickausschläge blockieren den Maximalraten-Vorschlag");
var hardwareBlocked = TuningRecommendationEngine.EvaluateActualRatesTrial(
    [session], cli, true, true, true, true, ["Roll"]);
Check(hardwareBlocked.Changes.Count == 0, "gemeldete Hardwareauffälligkeit sperrt den Vorschlag");
var identityBlocked = TuningRecommendationEngine.EvaluateActualRatesTrial(
    [session], cli, false, true, true, false, ["Roll"]);
Check(identityBlocked.Changes.Count == 0, "unbestätigter Flight Controller sperrt den Vorschlag");

var cappedSettings = cli.TuningSettings
    .Select(setting => setting.Scope == BetaflightTuningScope.RateProfile
        && setting.ProfileIndex == cli.ActiveRateProfile
        && setting.Name == "roll_rate_limit"
            ? setting with { Value = "600" }
            : setting)
    .ToArray();
var cappedCli = cli with { TuningSettings = cappedSettings };
var capped = TuningRecommendationEngine.EvaluateActualRatesTrial(
    [session], cappedCli, true, true, true, false, ["Roll"]);
Check(capped.Changes.Count == 0 && capped.Message.Contains("nicht verringern", StringComparison.Ordinal),
    "bereits wirksames Rate-Limit verhindert wirkungslose Änderung");

var propwashSession = CreatePropwashSession(fullStick);
var propwash = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli, sameFlightControllerConfirmed: true, activeProfilesConfirmed: true,
    hardwareChecksComplete: true, hardwareIssueReported: false, propwashObserved: true,
    ["Roll"], MotorTemperatureReport.Cool);
Check(propwash.Changes.Count == 1, "passender Acro-Datensatz und bestätigtes Propwash-Symptom erzeugen einen D-Term-Versuch");
var dChange = propwash.Changes.Single();
Check(dChange.Name == "d_roll" && dChange.PreviousValue == "30" && dChange.ProposedValue == "31"
    && dChange.Scope == BetaflightTuningScope.PidProfile && dChange.ProfileIndex == 0,
    "D-Term-Versuch ändert nur d_roll im bestätigten PID-Profil und bleibt höchstens +5 % groß");
Check(dChange.Evidence.Contains("D-RMS", StringComparison.Ordinal)
    && dChange.Reason.Contains("belegen aber nicht", StringComparison.Ordinal)
    && dChange.Risk.Length > 0 && dChange.ValidationPlan.Length > 0,
    "D-Term-Vorschlag führt Logkontext, Unsicherheit, Risiko und Nachflugprüfung auf");

var propwashUnconfirmed = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli, true, true, true, false, false, ["Roll"], MotorTemperatureReport.Cool);
Check(propwashUnconfirmed.Changes.Count == 0, "unbestätigtes Propwash-Symptom sperrt den D-Term-Versuch");
var propwashWarm = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli, true, true, true, false, true, ["Roll"], MotorTemperatureReport.Warm);
Check(propwashWarm.Changes.Count == 0, "warme Motoren sperren den D-Term-Versuch");
var propwashHot = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli, true, true, true, false, true, ["Roll"], MotorTemperatureReport.Hot);
Check(propwashHot.Changes.Count == 0, "heiße Motoren sperren den D-Term-Versuch");
var propwashHardwareBlocked = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli, true, true, true, true, true, ["Roll"], MotorTemperatureReport.Cool);
Check(propwashHardwareBlocked.Changes.Count == 0, "gemeldete Hardwareauffälligkeit sperrt den D-Term-Versuch");
var propwashIdentityBlocked = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli, false, true, true, false, true, ["Roll"], MotorTemperatureReport.Cool);
Check(propwashIdentityBlocked.Changes.Count == 0, "unbestätigter Controller sperrt den D-Term-Versuch");
var propwashLowRate = TuningRecommendationEngine.EvaluatePropwashTrial(
    [CreatePropwashSession(fullStick with { SampleRateHz = 1_000 })], cli, true, true, true, false, true,
    ["Roll"], MotorTemperatureReport.Cool);
Check(propwashLowRate.Changes.Count == 0 && propwashLowRate.Message.Contains("2 kHz", StringComparison.Ordinal),
    "zu niedrige Acro-Lograte sperrt den D-Term-Versuch");
var propwashWeakManeuvers = TuningRecommendationEngine.EvaluatePropwashTrial(
    [CreatePropwashSession(AnalyzeSyntheticAcroCsv(200))], cli, true, true, true, false, true,
    ["Roll"], MotorTemperatureReport.Cool);
Check(propwashWeakManeuvers.Changes.Count == 0 && propwashWeakManeuvers.Message.Contains("Manöver", StringComparison.Ordinal),
    "fehlende deutliche Stick-Manöver sperren den D-Term-Versuch");
var dMaxLimitedSettings = cli.TuningSettings
    .Select(setting => setting.Scope == BetaflightTuningScope.PidProfile
        && setting.ProfileIndex == cli.ActivePidProfile
        && setting.Name == "d_max_roll"
            ? setting with { Value = "30" }
            : setting)
    .ToArray();
var dMaxLimited = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli with { TuningSettings = dMaxLimitedSettings }, true, true, true, false, true,
    ["Roll"], MotorTemperatureReport.Cool);
Check(dMaxLimited.Changes.Count == 0 && dMaxLimited.Message.Contains("D-Max", StringComparison.Ordinal),
    "D-Max unter dem Vorschlagswert sperrt den D-Term-Versuch");
var missingDMax = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli with
    {
        TuningSettings = cli.TuningSettings.Where(setting =>
            !(setting.Scope == BetaflightTuningScope.PidProfile
              && setting.ProfileIndex == cli.ActivePidProfile
              && setting.Name == "d_max_roll")).ToArray()
    }, true, true, true, false, true, ["Roll"], MotorTemperatureReport.Cool);
Check(missingDMax.Changes.Count == 0 && missingDMax.Message.Contains("D-Max-Vorherwert", StringComparison.Ordinal),
    "fehlender D-Max-Ausgangswert sperrt den D-Term-Versuch");
var unsupportedDTermFirmware = TuningRecommendationEngine.EvaluatePropwashTrial(
    [propwashSession], cli with { FirmwareVersion = "2026.7.0" }, true, true, true, false, true,
    ["Roll"], MotorTemperatureReport.Cool);
Check(unsupportedDTermFirmware.Changes.Count == 0 && unsupportedDTermFirmware.Message.Contains("nicht freigegeben", StringComparison.Ordinal),
    "nicht geprüfte Betaflight-Version erhält keinen D-Term-Vorschlag");

var logHash = new string('A', 64);
var draft = BetaflightCliProposalExporter.CreateDraft(
    cli, "Betaflight 2026.6.2", "BEFH BETAFPVG473_V3", true, true,
    cli.ActivePidProfile!.Value, cli.ActiveRateProfile!.Value, result.Changes,
    "synthetic-log.BBL", logHash);
Check(draft.Contains($"# Quell-CLI-SHA-256: {cli.SourceSha256}", StringComparison.Ordinal), "CLI-Entwurf bindet den exakten CLI-Quellhash ein");
Check(draft.Contains($"# Quell-Log-SHA-256: {logHash}", StringComparison.Ordinal), "CLI-Entwurf bindet den Quell-Loghash ein");
Check(draft.Contains("rateprofile 0", StringComparison.Ordinal) && draft.Contains("set roll_srate = 63", StringComparison.Ordinal),
    "CLI-Entwurf wählt das bestätigte Rates-Profil und nur den freigegebenen Parameter");
Check(!Regex.IsMatch(draft, @"(?m)^\s*save\s*$"), "CLI-Entwurf schreibt nichts dauerhaft ohne manuelles save");
var dDraft = BetaflightCliProposalExporter.CreateDraft(
    cli, "Betaflight 2026.6.2", "BEFH BETAFPVG473_V3", true, true,
    cli.ActivePidProfile!.Value, cli.ActiveRateProfile!.Value, propwash.Changes,
    "synthetic-propwash-log.BBL", logHash);
Check(dDraft.Contains("profile 0", StringComparison.Ordinal) && dDraft.Contains("set d_roll = 31", StringComparison.Ordinal),
    "CLI-Entwurf kann den begrenzten D-Term-Versuch im PID-Profil abbilden");
Check(!Regex.IsMatch(dDraft, @"(?im)^[ \t]*save(?:[ \t][^\r\n]*)?[ \t]*\r?$"),
    "D-Term-CLI-Entwurf enthält keinen save-Befehl");

var reviewGate = new ProposalReviewGate();
Check(!reviewGate.CanConfirm && !reviewGate.CanExport, "Review-Dialog beginnt mit gesperrter Bestätigung und gesperrtem Export");
reviewGate.SetUserConfirmed(true);
reviewGate.MarkChangeDetailsViewed();
Check(!reviewGate.CanConfirm && !reviewGate.CanExport, "Änderungstabelle allein gibt Bestätigung und Export nicht frei");
reviewGate.MarkCliDraftViewed();
Check(reviewGate.CanConfirm && !reviewGate.CanExport, "beide Ansichten schalten erst die Nutzerbestätigung frei");
reviewGate.SetUserConfirmed(true);
Check(reviewGate.CanExport, "Export wird nach beiden Ansichten und ausdrücklicher Bestätigung freigegeben");
reviewGate.SetUserConfirmed(false);
Check(!reviewGate.CanExport, "Widerruf der Bestätigung sperrt den Export wieder");

var exportPath = Path.Combine(Path.GetTempPath(), $"fpv-workflow-export-{Guid.NewGuid():N}.cli");
string exportedDraftHash;
try
{
    File.WriteAllText(exportPath, "stale destination content");
    exportedDraftHash = BetaflightCliProposalExporter.SaveVerifiedDraft(exportPath, draft);
    var exportedBytes = File.ReadAllBytes(exportPath);
    var expectedDraftBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(draft);
    Check(exportedBytes.SequenceEqual(expectedDraftBytes), "Dateiexport ersetzt vorhandenen Inhalt exakt und speichert UTF-8 ohne BOM");
    Check(exportedDraftHash == Convert.ToHexString(SHA256.HashData(exportedBytes)), "Dateiexport gibt den SHA-256 der tatsächlich gespeicherten Bytes zurück");

    foreach (var unsafeCommand in new[] { "save", "save noreboot" })
    {
        var unsafeDraftRejected = false;
        try
        {
            BetaflightCliProposalExporter.SaveVerifiedDraft(exportPath, draft + Environment.NewLine + unsafeCommand + Environment.NewLine);
        }
        catch (InvalidOperationException)
        {
            unsafeDraftRejected = true;
        }
        Check(unsafeDraftRejected && File.ReadAllBytes(exportPath).SequenceEqual(expectedDraftBytes),
            $"Dateiexport lehnt `{unsafeCommand}` ab und lässt die geprüfte Datei unverändert");
    }
}
finally
{
    if (File.Exists(exportPath))
        File.Delete(exportPath);
}

VerifyReportRoundTrip(cli, result.Changes, logHash, exportedDraftHash);

if (args.Length == 2)
    await VerifyRealBlackboxAsync(args[1], cli);

Console.WriteLine("Alle Workflow-Prüfungen bestanden.");

static void VerifyReportRoundTrip(
    BetaflightCliConfiguration baseline,
    IReadOnlyList<BetaflightCliProposalChange> changes,
    string logHash,
    string draft)
{
    var recommendationId = Guid.NewGuid();
    var draftHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(draft)));
    var reportPath = Path.Combine(Path.GetTempPath(), $"fpv-workflow-report-{Guid.NewGuid():N}.analysis.json");
    var report = new LogAnalysisReport
    {
        Sha256 = logHash,
        TuningRecommendationHistory =
        [
            new TuningRecommendationAuditReport
            {
                RecommendationId = recommendationId,
                SourceLogSha256 = logHash,
                Goal = "LowerActualRate",
                CliSnapshotFileName = baseline.SourceFileName,
                CliSnapshotSha256 = baseline.SourceSha256,
                OutcomeMessage = "Test proposal persisted",
                Changes = changes.ToList()
            }
        ],
        ExportedTuningProposals =
        [
            new ExportedTuningProposalReport
            {
                RecommendationId = recommendationId,
                SourceLogSha256 = logHash,
                DraftFileName = "reviewed-proposal.cli",
                DraftSha256 = draftHash,
                CliBaselineFileName = baseline.SourceFileName,
                CliBaselineSha256 = baseline.SourceSha256,
                FirmwareVersion = baseline.FirmwareVersion,
                TargetPidProfile = baseline.ActivePidProfile!.Value,
                TargetRateProfile = baseline.ActiveRateProfile!.Value,
                Changes = changes.ToList()
            }
        ]
    };

    try
    {
        AnalysisReportStore.Save(reportPath, report);
        var loaded = AnalysisReportStore.Load(reportPath)
            ?? throw new InvalidOperationException("Saved analysis report could not be read back.");
        var savedRecommendation = loaded.TuningRecommendationHistory.Single();
        var savedExport = loaded.ExportedTuningProposals.Single();
        Check(savedRecommendation.SourceLogSha256 == loaded.Sha256
            && savedRecommendation.CliSnapshotSha256 == baseline.SourceSha256,
            "Auswertungsbericht erhält Quell-Log und CLI-Basishash der Empfehlung");
        Check(savedExport.RecommendationId == savedRecommendation.RecommendationId
            && savedExport.SourceLogSha256 == loaded.Sha256
            && savedExport.CliBaselineSha256 == baseline.SourceSha256
            && savedExport.DraftSha256 == draftHash,
            "gespeicherter CLI-Export bleibt über Empfehlung sowie beide Quellhashes verknüpft");
        Check(savedExport.Changes.Select(change => change.Name)
                .SequenceEqual(changes.Select(change => change.Name)),
            "JSON-Rundlauf erhält die konkret vorgeschlagenen Parameteränderungen");
    }
    finally
    {
        if (File.Exists(reportPath))
            File.Delete(reportPath);
        if (File.Exists(reportPath + ".tmp"))
            File.Delete(reportPath + ".tmp");
    }
}

static async Task VerifyRealBlackboxAsync(string path, BetaflightCliConfiguration baseline)
{
    var decoded = await new BlackboxDecoder().DecodeAsync(Path.GetFullPath(path));
    Check(decoded.Sessions.Count > 0, "echte BBL wird mit dem eingebetteten Decoder gelesen");
    Check(decoded.Sessions.All(session =>
            string.Equals(
                BetaflightCliConfigurationParser.ExtractFirmwareVersion(session.Firmware),
                baseline.FirmwareVersion,
                StringComparison.Ordinal)),
        "alle echten BBL-Sessions passen zur importierten CLI-Firmware");
    Check(decoded.Sessions.All(session =>
            BetaflightCliConfigurationParser.BoardNamesMatch(session.Board, baseline.BoardName)),
        "alle echten BBL-Sessions passen zur importierten CLI-Boardkennung");

    foreach (var session in decoded.Sessions)
    {
        Console.WriteLine($"Echte Session {session.LogNumber}/{session.TotalLogs}: {session.FlightFrameCount:N0} Frames");
        foreach (var mode in session.TuningManeuverByMode)
        {
            var roll = mode.Assessment.Axes.FirstOrDefault(axis => axis.Axis == "Roll");
            var pitch = mode.Assessment.Axes.FirstOrDefault(axis => axis.Axis == "Pitch");
            Console.WriteLine($"  {mode.Mode}: {mode.Assessment.SampleRateHz:N0} Hz; Roll {roll?.HighStickExcursionCount ?? 0} >50 % / {roll?.NearFullStickExcursionCount ?? 0} ≥90 %; Pitch {pitch?.HighStickExcursionCount ?? 0} >50 % / {pitch?.NearFullStickExcursionCount ?? 0} ≥90 %");
        }
    }

    var recommendation = TuningRecommendationEngine.EvaluateActualRatesTrial(
        decoded.Sessions,
        baseline,
        sameFlightControllerConfirmed: true,
        activeProfilesConfirmed: true,
        hardwareChecksComplete: true,
        hardwareIssueReported: false,
        ["Roll", "Pitch"]);
    Console.WriteLine($"Echte BBL, ACTUAL-Rates-Ziel (beide Achsen): {recommendation.Message}");
    foreach (var change in recommendation.Changes)
        Console.WriteLine($"  Vorschlag: {change.Name} {change.PreviousValue} → {change.ProposedValue}; {change.Evidence}");
}

static BlackboxSessionSummary CreateSession(TuningManeuverAssessment assessment) => new(
    LogNumber: 1,
    TotalLogs: 1,
    TotalFrames: 10_000,
    IFrames: 5_000,
    PFrames: 5_000,
    LoopTimeUs: 125,
    Firmware: "Betaflight 2026.6.2",
    Board: "BETAFPVG473_V3",
    CsvPath: null,
    MotorAssessment: null)
{
    TuningManeuverByMode =
    [
        new ModeSpecificTuningManeuverAssessment(
            "Manuell/Acro (Betaflight-Standard ohne Angle/Horizon)", assessment)
    ]
};

static BlackboxSessionSummary CreatePropwashSession(TuningManeuverAssessment maneuvers)
{
    const string acroMode = "Manuell/Acro (Betaflight-Standard ohne Angle/Horizon)";
    var pid = new PidActivityAssessment(
        true,
        string.Empty,
        maneuvers.Samples,
        maneuvers.DurationSeconds,
        [
            new PidAxisActivityMetric("Roll", [new PidTermActivityMetric("D", 1.5, 2.2, 5)]),
            new PidAxisActivityMetric("Pitch", [new PidTermActivityMetric("D", 1.4, 2.0, 4.5)])
        ]);
    var tracking = new SetpointTrackingAssessment(
        true,
        string.Empty,
        maneuvers.Samples,
        maneuvers.DurationSeconds,
        [
            new SetpointTrackingAxisMetric("Roll", 20, 40),
            new SetpointTrackingAxisMetric("Pitch", 24, 46)
        ]);

    return CreateSession(maneuvers) with
    {
        PidActivityByMode = [new ModeSpecificPidActivityAssessment(acroMode, pid)],
        SetpointTrackingByMode = [new ModeSpecificSetpointTrackingAssessment(acroMode, tracking)]
    };
}

static TuningManeuverAssessment AnalyzeSyntheticAcroCsv(double stickCommand)
{
    var path = Path.Combine(Path.GetTempPath(), $"fpv-workflow-check-{Guid.NewGuid():N}.csv");
    try
    {
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("time (us),rcCommand[0],rcCommand[1],rcCommand[3],setpoint[0],setpoint[1],flightModeFlags (flags)");
            for (var sample = 0; sample < 4_200; sample++)
            {
                var inExcursion = sample is >= 100 and < 152
                    or >= 200 and < 252
                    or >= 300 and < 352;
                var command = inExcursion ? stickCommand : 0;
                var setpoint = inExcursion ? 600 : 0;
                writer.WriteLine(string.Join(',',
                    (sample * 500).ToString(CultureInfo.InvariantCulture),
                    command.ToString(CultureInfo.InvariantCulture),
                    command.ToString(CultureInfo.InvariantCulture),
                    "1200",
                    setpoint.ToString(CultureInfo.InvariantCulture),
                    setpoint.ToString(CultureInfo.InvariantCulture),
                    "ANGLE_MODE"));
            }
        }

        return TuningManeuverAnalyzer.Analyze(
            path,
            "Betaflight 2026.6.2",
            "Manuell/Acro (Betaflight-Standard ohne Angle/Horizon)");
    }
    finally
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"FEHLGESCHLAGEN: {description}");

    Console.WriteLine($"PASS: {description}");
}
