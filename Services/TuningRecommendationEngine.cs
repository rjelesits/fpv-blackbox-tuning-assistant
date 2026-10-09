using System.Globalization;

namespace FPVBlackboxTuningAssistant.Services;

public enum MotorTemperatureReport
{
    Unreported,
    Cool,
    Warm,
    Hot
}

public sealed record TuningRecommendationResult(
    IReadOnlyList<BetaflightCliProposalChange> Changes,
    string Message)
{
    public bool HasProposal => Changes.Count > 0;
}

/// <summary>
/// Produces one deliberately narrow, user-driven D-term trial. Log metrics establish
/// that the selected mode has enough data; they do not diagnose propwash or prove that
/// D is too low.
/// </summary>
public static class TuningRecommendationEngine
{
    public static TuningRecommendationResult EvaluatePropwashTrial(
        IReadOnlyList<BlackboxSessionSummary> sessions,
        BetaflightCliConfiguration? configuration,
        bool sameFlightControllerConfirmed,
        bool activeProfilesConfirmed,
        bool hardwareChecksComplete,
        bool hardwareIssueReported,
        bool propwashObserved,
        IReadOnlyCollection<string> affectedAxes,
        MotorTemperatureReport motorTemperature)
    {
        if (!hardwareChecksComplete || hardwareIssueReported)
            return Block("Schließe zuerst die mechanische Prüfung ab. Bei einer gemeldeten Auffälligkeit gibt es keinen PID-Vorschlag.");
        if (!propwashObserved)
            return Block("Bestätige zuerst, dass Propwash oder Bounceback im Acro-Flug tatsächlich beobachtet wurde.");
        var selectedAxes = affectedAxes
            .Where(axis => axis is "Roll" or "Pitch")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedAxes.Length == 0)
            return Block("Wähle mindestens Roll oder Pitch als betroffene Achse. Nur ausgewählte Achsen werden vorgeschlagen.");
        if (motorTemperature != MotorTemperatureReport.Cool)
            return Block(motorTemperature switch
            {
                MotorTemperatureReport.Hot => "Die Motoren wurden als heiß gemeldet. Nicht weiter tunen; Ursache prüfen und erst nach einem neuen Log fortfahren.",
                MotorTemperatureReport.Warm => "Die Motoren wurden als warm gemeldet. Für diesen D-Term-Versuch ist das keine ausreichende Temperaturfreigabe.",
                _ => "Gib die Motortemperatur nach dem Flug an. Ohne Temperaturangabe entsteht kein D-Term-Vorschlag."
            });
        if (configuration is null || !configuration.HasFullTuningBaseline)
            return Block("Importiere zuerst `dump all` mit vollständigen Profilwerten.");
        if (!sameFlightControllerConfirmed)
            return Block("Bestätige, dass das `dump all` von genau diesem Flight Controller stammt.");
        if (!activeProfilesConfirmed)
            return Block("Bestätige, dass das aktive PID- und Rates-Profil aus `dump all` während dieses Logs verwendet wurde.");
        if (configuration.ActivePidProfile is not { } pidProfile)
            return Block("In `dump all` wurde kein aktives PID-Profil erkannt.");
        if (configuration.ActiveRateProfile is null)
            return Block("In `dump all` wurde kein aktives Rates-Profil erkannt.");

        var acroSessions = sessions
            .SelectMany(session => session.PidActivityByMode
                .Where(mode => mode.Mode.StartsWith("Manuell/Acro", StringComparison.Ordinal))
                .Select(mode => (Session: session, Pid: mode.Assessment,
                    Tracking: session.SetpointTrackingByMode.FirstOrDefault(item =>
                        item.Mode.StartsWith("Manuell/Acro", StringComparison.Ordinal))?.Assessment,
                    Maneuvers: session.TuningManeuverByMode.FirstOrDefault(item =>
                        item.Mode.StartsWith("Manuell/Acro", StringComparison.Ordinal))?.Assessment)))
            .ToArray();
        var eligibleSessions = acroSessions
            .Where(item => item.Pid.IsAvailable && item.Tracking?.IsAvailable == true && item.Maneuvers?.IsAvailable == true)
            .ToArray();
        if (eligibleSessions.Length == 0)
        {
            var observedReason = acroSessions
                .Select(item => item.Maneuvers?.Reason ?? item.Tracking?.Reason ?? item.Pid.Reason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return Block(string.IsNullOrWhiteSpace(observedReason)
                ? "Im Log gibt es keine ausreichend lange Acro-Phase mit PID-, Soll-/Ist- und Manöverdaten."
                : $"Die Acro-Daten reichen für diesen Versuch nicht aus: {observedReason}");
        }

        var bestSession = eligibleSessions.OrderByDescending(item => item.Maneuvers!.Samples).First();
        var maneuverData = bestSession.Maneuvers!;
        if (maneuverData.SampleRateHz < TuningManeuverAnalyzer.DetailedTuningMinimumSampleRateHz)
        {
            var sampleRateHint = bestSession.Session.LoopTimeUs == 125
                ? "Bei einem 125-µs-PID-Loop entspricht `blackbox_sample_rate = 1/4` ungefähr 2 kHz; prüfe danach, ob der Logger die Frames fehlerfrei aufzeichnet."
                : "Stelle eine passende Blackbox-Abtastrate ein und prüfe danach, ob der Logger die Frames fehlerfrei aufzeichnet.";
            return Block($"Die Acro-Aufzeichnung hat etwa {maneuverData.SampleRateHz:N0} Hz. Für detaillierte PID-Tuning-Entscheidungen braucht es einen Log mit ungefähr 2 kHz; dieser Log reicht für einen Zahlenvorschlag nicht aus. {sampleRateHint}");
        }

        var insufficientAxes = selectedAxes
            .Select(axis => (Axis: axis, Metric: maneuverData.Axes.FirstOrDefault(metric => metric.Axis == axis)))
            .Where(item => item.Metric is null
                || item.Metric.HighStickExcursionCount < TuningManeuverAnalyzer.MinimumHighStickExcursionsPerAxis)
            .ToArray();
        if (insufficientAxes.Length > 0)
        {
            var coverage = string.Join(", ", insufficientAxes.Select(item =>
                $"{item.Axis}: {item.Metric?.HighStickExcursionCount ?? 0}/{TuningManeuverAnalyzer.MinimumHighStickExcursionsPerAxis} deutliche Stick-Manöver"));
            return Block($"Im Acro-Teil fehlen gezielte Manöver für die betroffenen Achsen (Stickausschlag über 50 % für mindestens 25 ms): {coverage}. Fliege für einen neuen Log mehrere saubere, getrennte Manöver der betroffenen Achse(n) im freien Bereich.");
        }

        var changes = new List<BetaflightCliProposalChange>();
        foreach (var axisName in selectedAxes)
        {
            var axis = axisName.Equals("Roll", StringComparison.Ordinal) ? "roll" : "pitch";
            if (!BetaflightPidSemantics.TryGetDTermParameters(configuration.FirmwareVersion, axis, out var dParameters))
                return Block($"Die D-Term-Variablen dieser Betaflight-Version ({configuration.FirmwareVersion}) sind für automatische Vorschläge noch nicht freigegeben.");

            var name = dParameters.BaseParameter;
            var baseline = configuration.FindSetting(name, BetaflightTuningScope.PidProfile, pidProfile);
            if (baseline is null
                || !decimal.TryParse(baseline.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var current)
                || current <= 0)
                return Block($"Der Vorherwert für `{name}` fehlt oder ist nicht als positiver Zahlenwert aus `dump all` lesbar.");

            var proposed = decimal.Floor(current * 1.05m);
            if (proposed <= current || proposed - current > Math.Max(current * 0.10m, 1m))
                return Block($"Für `{name}` lässt sich aus dem Ausgangswert kein kleiner, zulässiger +5-%-Versuch bilden.");
            if (dParameters.MaximumParameter is { } maximumParameter)
            {
                var maximumSetting = configuration.FindSetting(maximumParameter, BetaflightTuningScope.PidProfile, pidProfile);
                if (maximumSetting is null
                    || !decimal.TryParse(maximumSetting.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var currentMaximum))
                    return Block($"Der D-Max-Vorherwert `{maximumParameter}` fehlt in `dump all`; der D-Term-Vorschlag kann nicht auf die Firmwaregrenze geprüft werden.");
                if (proposed > currentMaximum)
                    return Block($"Der Vorschlagswert für `{name}` würde den vorhandenen D-Max `{maximumParameter}={maximumSetting.Value}` überschreiten.");
            }

            var dMetric = bestSession.Pid.Axes
                .FirstOrDefault(metric => metric.Axis.Equals(axisName, StringComparison.Ordinal))?
                .Terms.FirstOrDefault(term => term.Term.Equals("D", StringComparison.Ordinal));
            var trackingMetric = bestSession.Tracking!.Axes
                .FirstOrDefault(metric => metric.Axis.Equals(axisName, StringComparison.Ordinal));
            var maneuverMetric = maneuverData.Axes.FirstOrDefault(metric => metric.Axis == axisName);
            if (dMetric is null || trackingMetric is null || maneuverMetric is null)
                return Block($"Für {axisName} fehlt im Acro-Log der D-Term-, Setpoint- oder Manöver-Messwert.");

            var reason = "Kleiner D-Term-Versuch (höchstens +5 %) aufgrund des vom Nutzer bestätigten Propwash-Symptoms im Acro-Modus. Der Log enthält ausreichend schnelle und deutliche Stick-Manöver; seine Kennzahlen belegen aber nicht, dass D zu niedrig ist.";
            var evidence = $"Acro, Session {bestSession.Session.LogNumber}: {maneuverData.SampleRateHz:N0} Hz; {bestSession.Pid.Samples:N0} aktive PID-Samples / {bestSession.Pid.DurationSeconds:N1} s; {axisName} {maneuverMetric.HighStickExcursionCount} deutliche Stick-Manöver, Spitzen-Setpoint {maneuverMetric.PeakSetpointDps:N0} °/s, D-RMS {dMetric.RootMeanSquare:N2}, Soll-Ist-Abweichung P95 {trackingMetric.Percentile95AbsoluteErrorDps:N1} °/s. Motortemperatur als kühl gemeldet. Diese Kennzahlen prüfen Log-Eignung und liefern Kontext, keine Propwash-Diagnose.";
            var expectedEffect = "Mehr D-Dämpfung kann die vom Nutzer beobachteten Propwash-Schwingungen verringern; eine Verbesserung ist nicht garantiert.";
            var risk = "D verstärkt Gyro-Rauschen. Zu viel D kann Motoren überhitzen oder neue Schwingungen verursachen; bei auffälligem Geräusch oder warmen/heißeren Motoren sofort landen und zurücksetzen.";
            var validation = "Nur kurz und kontrolliert im freien Bereich testen. Danach Motoren prüfen und einen vergleichbaren Acro-Log aufnehmen. Propwash, Tracking und neue Schwingungen mit dem Ausgangslog vergleichen; bei Verschlechterung die gesicherte Originalbasis wiederherstellen.";
            changes.Add(new BetaflightCliProposalChange(
                name,
                baseline.Value,
                proposed.ToString(CultureInfo.InvariantCulture),
                BetaflightTuningScope.PidProfile,
                pidProfile,
                reason,
                evidence,
                expectedEffect,
                risk,
                validation));
        }

        return new TuningRecommendationResult(
            changes,
            "Ein kleiner Acro-Propwash-Versuch (D-Werte höchstens +5 %) ist zur manuellen Prüfung bereit. Er ist keine automatische Diagnose: zuerst im Review prüfen, in kleinen Schritten fliegen und danach mit einem vergleichbaren Log bewerten.");
    }

    public static TuningRecommendationResult EvaluateActualRatesTrial(
        IReadOnlyList<BlackboxSessionSummary> sessions,
        BetaflightCliConfiguration? configuration,
        bool sameFlightControllerConfirmed,
        bool activeProfilesConfirmed,
        bool hardwareChecksComplete,
        bool hardwareIssueReported,
        IReadOnlyCollection<string> affectedAxes)
    {
        if (!hardwareChecksComplete || hardwareIssueReported)
            return Block("Schließe zuerst die mechanische Prüfung ab. Bei einer gemeldeten Auffälligkeit gibt es keinen Rate-Vorschlag.");

        var selectedAxes = affectedAxes
            .Where(axis => axis is "Roll" or "Pitch")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selectedAxes.Length == 0)
            return Block("Wähle mindestens Roll oder Pitch als zu schnelle Achse.");
        if (configuration is null || !configuration.HasFullTuningBaseline)
            return Block("Importiere zuerst `dump all` mit vollständigen Profilwerten.");
        if (!sameFlightControllerConfirmed)
            return Block("Bestätige, dass das `dump all` von genau diesem Flight Controller stammt.");
        if (!activeProfilesConfirmed)
            return Block("Bestätige, dass das aktive PID- und Rates-Profil aus `dump all` während dieses Logs verwendet wurde.");
        if (configuration.ActiveRateProfile is not { } rateProfile
            || !configuration.RateProfiles.Contains(rateProfile))
            return Block("In `dump all` wurde kein aktives Rates-Profil erkannt.");
        if (!BetaflightPidSemantics.SupportsActualRatesTrial(configuration.FirmwareVersion))
            return Block($"Die ACTUAL-Rates-Regel ist für Betaflight {configuration.FirmwareVersion} nicht freigegeben.");

        var ratesType = configuration.FindSetting("rates_type", BetaflightTuningScope.RateProfile, rateProfile);
        if (!string.Equals(ratesType?.Value, "ACTUAL", StringComparison.OrdinalIgnoreCase))
            return Block($"Das aktive Rates-Profil verwendet `{ratesType?.Value ?? "keinen erkannten rates_type"}`. Dieser Vorschlag gilt nur für `rates_type = ACTUAL`.");

        var acroManeuvers = sessions
            .SelectMany(session => session.TuningManeuverByMode
                .Where(mode => mode.Mode.StartsWith("Manuell/Acro", StringComparison.Ordinal))
                .Select(mode => (Session: session, Assessment: mode.Assessment)))
            .Where(item => item.Assessment.IsAvailable)
            .ToArray();
        if (acroManeuvers.Length == 0)
            return Block("Im Log gibt es keine ausreichend lange Acro-Phase mit auswertbaren Stick- und Setpoint-Daten.");

        var detailedAcroManeuvers = acroManeuvers
            .Where(item => item.Assessment.SampleRateHz >= TuningManeuverAnalyzer.DetailedTuningMinimumSampleRateHz)
            .ToArray();
        if (detailedAcroManeuvers.Length == 0)
        {
            var bestSampleRate = acroManeuvers.Max(item => item.Assessment.SampleRateHz);
            return Block($"Die Acro-Aufzeichnung erreicht höchstens etwa {bestSampleRate:N0} Hz. Für einen belastbaren Vergleich nahe der maximalen Stickrate braucht es ungefähr 2 kHz; dieser Log reicht für einen Zahlenvorschlag nicht aus.");
        }

        var bestSession = detailedAcroManeuvers
            .OrderByDescending(item => selectedAxes.Min(axisName =>
                item.Assessment.Axes.FirstOrDefault(axis => axis.Axis == axisName)?.NearFullStickExcursionCount ?? 0))
            .ThenByDescending(item => item.Assessment.Samples)
            .First();

        var changes = new List<BetaflightCliProposalChange>();
        foreach (var axisName in selectedAxes)
        {
            var metric = bestSession.Assessment.Axes.FirstOrDefault(axis => axis.Axis == axisName);
            if (metric is null || metric.NearFullStickExcursionCount < TuningManeuverAnalyzer.MinimumNearFullStickExcursionsPerAxis)
                return Block($"Im Acro-Teil fehlen gezielte {axisName}-Manöver nahe dem Stickanschlag: {metric?.NearFullStickExcursionCount ?? 0}/{TuningManeuverAnalyzer.MinimumNearFullStickExcursionsPerAxis} getrennte Ausschläge ab 90 % für mindestens 25 ms. Die maximale Drehrate lässt sich aus mittleren Stickausschlägen nicht belegen.");
            if (metric.PeakSetpointAtNearFullStickDps <= 0)
                return Block($"Für {axisName} fehlt ein positiver Setpoint-Messwert nahe dem Stickanschlag.");

            var axis = axisName.ToLowerInvariant();
            var srateName = $"{axis}_srate";
            var rcRateName = $"{axis}_rc_rate";
            var rateLimitName = $"{axis}_rate_limit";
            if (!TryReadInteger(srateName, 0, 255, out var currentSuperRate)
                || !TryReadInteger(rcRateName, 1, 255, out var centerRate)
                || !TryReadInteger(rateLimitName, 200, 1998, out var configuredLimit))
                return Block($"Für {axisName} fehlen ganzzahlige `dump all`-Werte für `{srateName}`, `{rcRateName}` oder `{rateLimitName}`.");

            var superRateSetting = configuration.FindSetting(srateName, BetaflightTuningScope.RateProfile, rateProfile)!;
            var currentMaximum = Math.Min(Math.Max(centerRate * 10, currentSuperRate * 10), configuredLimit);
            var proposedSuperRate = (int)decimal.Floor(currentSuperRate * 0.95m);
            if (proposedSuperRate >= currentSuperRate
                || currentSuperRate == 0
                || (currentSuperRate - proposedSuperRate) / (double)currentSuperRate > 0.10)
                return Block($"Für `{srateName}` lässt sich aus dem Vorherwert kein kleiner, höchstens 10 % großer Änderungsschritt bilden.");

            var proposedMaximum = Math.Min(Math.Max(centerRate * 10, proposedSuperRate * 10), configuredLimit);
            if (proposedMaximum >= currentMaximum)
                return Block($"Eine Änderung von `{srateName}` würde die wirksame Maximalrate nicht verringern; die Center-Rate oder `{rateLimitName}` begrenzt sie bereits.");

            var reason = $"Du hast angegeben, dass die maximale {axisName}-Drehrate zu hoch ist. Bei ACTUAL-Rates bestimmt `{srateName}` die Maximalrate; der kleine -5-%-Schritt lässt Center-Rate und Expo unverändert.";
            var evidence = $"Acro, Session {bestSession.Session.LogNumber}: {bestSession.Assessment.SampleRateHz:N0} Hz; {metric.NearFullStickExcursionCount} getrennte {axisName}-Manöver ab 90 % Stick; gemessener Spitzen-Setpoint nahe Anschlag {metric.PeakSetpointAtNearFullStickDps:N0} °/s. CLI-Basis: ACTUAL, `{srateName}={currentSuperRate}`, `{rcRateName}={centerRate}`, `{rateLimitName}={configuredLimit}`; aus der Betaflight-Rates-Kurve berechnete Maximalrate {currentMaximum} → {proposedMaximum} °/s.";
            var expectedEffect = $"Die theoretische maximale {axisName}-Drehrate sinkt in der ACTUAL-Kurve von {currentMaximum} auf {proposedMaximum} °/s; Center-Rate und Expo bleiben gleich.";
            var risk = "Die Steuerreaktion am Stickanschlag wird langsamer und kann sich ungewohnt anfühlen. Die Änderung gilt nur für das gewählte Rates-Profil; sie ist keine PID- oder Filterkorrektur.";
            var validation = "Vorher `dump all` sichern. Im freien Bereich zunächst kurz und mit moderaten Ausschlägen testen. Danach einen vergleichbaren Acro-Log aufnehmen und Setpoint-Verlauf sowie subjektive Steuerbarkeit prüfen; bei unpassendem Gefühl den gespeicherten Vorherwert wiederherstellen.";
            changes.Add(new BetaflightCliProposalChange(
                srateName,
                superRateSetting.Value,
                proposedSuperRate.ToString(CultureInfo.InvariantCulture),
                BetaflightTuningScope.RateProfile,
                rateProfile,
                reason,
                evidence,
                expectedEffect,
                risk,
                validation));
        }

        return new TuningRecommendationResult(
            changes,
            "Ein kleiner ACTUAL-Rates-Versuch zur Senkung der gewählten Maximalrate ist bereit. Er ändert nur die ausgewählten Achsen im aktiven Rates-Profil und muss manuell geprüft sowie schrittweise erprobt werden.");

        bool TryReadInteger(string name, int minimum, int maximum, out int value)
        {
            value = 0;
            var setting = configuration.FindSetting(name, BetaflightTuningScope.RateProfile, rateProfile);
            if (setting is null
                || !decimal.TryParse(setting.Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                || decimal.Truncate(parsed) != parsed
                || parsed < minimum
                || parsed > maximum)
                return false;

            value = (int)parsed;
            return true;
        }
    }

    private static TuningRecommendationResult Block(string message) => new([], message);
}
