using System.Windows;
using System.Windows.Controls;
using FPVBlackboxTuningAssistant.Services;

namespace FPVBlackboxTuningAssistant;

public partial class FlightComparisonWindow : Window
{
    private readonly IReadOnlyList<StoredFlightAnalysis> _analyses;
    private bool _updatingSelections;

    public FlightComparisonWindow(IReadOnlyList<StoredFlightAnalysis> analyses)
    {
        InitializeComponent();
        _analyses = analyses;
        _updatingSelections = true;
        FlightAComboBox.ItemsSource = _analyses;
        FlightBComboBox.ItemsSource = _analyses;
        FlightAComboBox.SelectedItem = _analyses.FirstOrDefault();
        FlightBComboBox.SelectedItem = _analyses.Skip(1).FirstOrDefault() ?? _analyses.FirstOrDefault();
        PopulateSessions(SessionAComboBox, FlightAComboBox.SelectedItem as StoredFlightAnalysis);
        PopulateSessions(
            SessionBComboBox,
            FlightBComboBox.SelectedItem as StoredFlightAnalysis,
            preferSecondSession: FlightAComboBox.SelectedItem is StoredFlightAnalysis flightA
                && FlightBComboBox.SelectedItem is StoredFlightAnalysis flightB
                && flightA.LogId == flightB.LogId);
        _updatingSelections = false;
        UpdateSessionOverview();

        var comparableSessions = _analyses.Sum(analysis => analysis.Report.Sessions.Count(session =>
            session.SetpointTrackingAvailable && session.SetpointTrackingAxes.Count >= 3));
        LibraryStatusText.Text = _analyses.Count == 0
            ? "Noch keine gespeicherten Auswertungen gefunden. Importiere und speichere zuerst mindestens einen Log."
            : $"{_analyses.Count} gespeicherte BBL-Auswertung(en) · {comparableSessions} vergleichbare Session(s)";
    }

    private void Flight_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelections || sender is not ComboBox flightCombo)
            return;

        var selectedFlight = flightCombo.SelectedItem as StoredFlightAnalysis;
        var flightA = FlightAComboBox.SelectedItem as StoredFlightAnalysis;
        var flightB = FlightBComboBox.SelectedItem as StoredFlightAnalysis;
        if (ReferenceEquals(flightCombo, FlightAComboBox))
        {
            PopulateSessions(SessionAComboBox, selectedFlight);
            if (flightA is not null && flightB is not null && flightA.LogId == flightB.LogId)
                PopulateSessions(SessionBComboBox, flightB, preferSecondSession: true);
        }
        else
        {
            PopulateSessions(
                SessionBComboBox,
                selectedFlight,
                preferSecondSession: flightA is not null && selectedFlight is not null && flightA.LogId == selectedFlight.LogId);
        }
        UpdateSessionOverview();
    }

    private void UpdateSessionOverview()
    {
        var selectedAnalyses = new[]
            {
                FlightAComboBox.SelectedItem as StoredFlightAnalysis,
                FlightBComboBox.SelectedItem as StoredFlightAnalysis
            }
            .Where(analysis => analysis is not null)
            .Cast<StoredFlightAnalysis>()
            .GroupBy(analysis => analysis.LogId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        SessionOverviewItems.ItemsSource = selectedAnalyses.Select(analysis =>
            new SessionOverviewGroup(
                $"{analysis.DroneName} · {analysis.Report.StoredBlackboxFileName}",
                analysis.Report.Sessions
                    .OrderBy(session => session.LogNumber)
                    .Select(session =>
                    {
                        var isComparable = session.SetpointTrackingAvailable && session.SetpointTrackingAxes.Count >= 3;
                        var isShortEventOnly = session.IFrames + session.PFrames == 0 && session.EventFrameCount is > 0 and <= 10;
                        var hasUserContext = analysis.Report.ShortLogSessionNumbers.Contains(session.LogNumber)
                            && !string.IsNullOrWhiteSpace(analysis.Report.ShortLogRecoveryContext);
                        var status = isComparable
                            ? $"Vergleichbar · {session.SetpointTrackingSamples:N0} aktive Werte · {session.SetpointTrackingDurationSeconds:N1} s"
                            : isShortEventOnly
                                ? $"Kurzlog · {session.EventFrameCount} Ereignis-Frames · keine Flug-Frames"
                                : string.IsNullOrWhiteSpace(session.SetpointTrackingReason)
                                    ? "Keine Kennzahl gespeichert."
                                    : $"Nicht vergleichbar: {session.SetpointTrackingReason}";
                        if (session.FlightModeAvailable)
                            status += $" · {session.FlightModeTransitionCount} Wechsel im Flug";
                        if (session.FlightModeChangeCount > 0)
                            status += $" · {session.FlightModeChangeCount} Modusmeldungen im Log";
                        var metrics = isComparable
                            ? string.Join("  ·  ", session.SetpointTrackingAxes.Select(axis =>
                                $"{axis.Axis} Ø {axis.MeanAbsoluteErrorDps:N1} / P95 {axis.Percentile95AbsoluteErrorDps:N1} °/s"))
                            : isShortEventOnly
                                ? hasUserContext
                                    ? analysis.Report.ShortLogRecoveryContext
                                    : "Möglicher kurzer Zusatzlog nach einer Bergung, etwa mit Crash Flip/Turtle Mode. Die Daten bestätigen das nicht; von der Flugbewertung ausgeschlossen."
                                : session.TotalFrames <= 0
                                    ? "Keine Flug-Frames in dieser Session."
                                    : "Die Session bleibt in der Übersicht, ist aber nicht für diesen Kennzahlenvergleich auswählbar.";
                        var recognizedModes = FormatFlightModes(session);
                        if (recognizedModes.Length > 0)
                            metrics += $" · Erkannte Modi: {recognizedModes}";
                        return new SessionOverviewRow($"Session {session.LogNumber}", status, metrics);
                    })
                    .ToArray()))
            .ToArray();
    }

    private static void PopulateSessions(ComboBox comboBox, StoredFlightAnalysis? analysis, bool preferSecondSession = false)
    {
        var previousLogNumber = (comboBox.SelectedItem as SessionChoice)?.Session.LogNumber;
        var choices = analysis?.Report.Sessions
            .Where(session => session.SetpointTrackingAvailable && session.SetpointTrackingAxes.Count >= 3)
            .OrderBy(session => session.LogNumber)
            .Select(session => new SessionChoice(session))
            .ToArray() ?? [];

        comboBox.ItemsSource = choices;
        comboBox.SelectedItem = choices.FirstOrDefault(choice => choice.Session.LogNumber == previousLogNumber)
            ?? (preferSecondSession ? choices.Skip(1).FirstOrDefault() : null)
            ?? choices.FirstOrDefault();
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (FlightAComboBox.SelectedItem is not StoredFlightAnalysis flightA
            || FlightBComboBox.SelectedItem is not StoredFlightAnalysis flightB
            || SessionAComboBox.SelectedItem is not SessionChoice choiceA
            || SessionBComboBox.SelectedItem is not SessionChoice choiceB)
        {
            MessageBox.Show(
                this,
                "Für beide Seiten muss eine gespeicherte Session mit ausreichenden Soll-/Ist-Messwerten ausgewählt sein.",
                "Kein vergleichbares Session-Paar",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var sessionA = choiceA.Session;
        var sessionB = choiceB.Session;
        var selectedMode = (ModeFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Acro";
        if (string.Equals(flightA.Report.Sha256, flightB.Report.Sha256, StringComparison.OrdinalIgnoreCase)
            && sessionA.LogNumber == sessionB.LogNumber)
        {
            MessageBox.Show(
                this,
                "Wähle zwei unterschiedliche Sessions aus, damit ein Vergleich einen Aussagewert hat.",
                "Dieselbe Session ausgewählt",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var trackingA = FindModeAssessment(sessionA.SetpointTrackingByMode, selectedMode);
        var trackingB = FindModeAssessment(sessionB.SetpointTrackingByMode, selectedMode);
        if (trackingA?.IsAvailable != true || trackingB?.IsAvailable != true)
        {
            MessageBox.Show(
                this,
                $"Für beide Sessions werden ausreichende {selectedMode}-Kennzahlen benötigt. Ältere gespeicherte Logs müssen ggf. erneut importiert werden, damit die Moduswerte berechnet werden.",
                "Modusvergleich nicht verfügbar",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var pidA = FindModeAssessment(sessionA.PidActivityByMode, selectedMode);
        var pidB = FindModeAssessment(sessionB.PidActivityByMode, selectedMode);

        ComparisonContextText.Text = $"A: {Describe(flightA, sessionA)}{Environment.NewLine}B: {Describe(flightB, sessionB)}{Environment.NewLine}Verglichener Flugmodus: {selectedMode}";
        CompatibilityNoticeText.Text = BuildCompatibilityNotice(flightA, sessionA, flightB, sessionB, selectedMode);
        CompatibilityNoticePanel.Background = HasComparisonWarnings(flightA, sessionA, flightB, sessionB)
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 37, 29))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 46, 34));

        SetAxisValues("Roll", RollAValue, RollBValue, RollDeltaValue, trackingA, trackingB);
        SetAxisValues("Pitch", PitchAValue, PitchBValue, PitchDeltaValue, trackingA, trackingB);
        SetAxisValues("Yaw", YawAValue, YawBValue, YawDeltaValue, trackingA, trackingB);
        SetPidComparison(pidA, pidB);
        SetCliBaselineComparison(flightA, flightB);
        ComparisonResultsPanel.Visibility = Visibility.Visible;
    }

    private void SetCliBaselineComparison(StoredFlightAnalysis flightA, StoredFlightAnalysis flightB)
    {
        SetTuningRecommendationAudit(flightA);
        CliProposalFollowUpText.Visibility = Visibility.Collapsed;
        CliProposalFollowUpText.Text = string.Empty;
        var baselineA = flightA.Report.TuningBaseline;
        var baselineB = flightB.Report.TuningBaseline;
        CliBaselineComparisonPanel.Visibility = Visibility.Visible;
        if (baselineA is null || baselineB is null)
        {
            CliBaselineComparisonText.Text = "Für A und B muss jeweils eine CLI-Konfiguration importiert sein. Ohne zwei gespeicherte Snapshots lässt sich die Übernahme von Einstellungsänderungen nicht vergleichen.";
            return;
        }

        var blockingReasons = new List<string>();
        if (!string.Equals(flightA.DroneName, flightB.DroneName, StringComparison.OrdinalIgnoreCase))
            blockingReasons.Add("beide Logs müssen demselben Drohnenprofil zugeordnet sein");
        if (!IsFullDump(baselineA) || !IsFullDump(baselineB))
            blockingReasons.Add("beide Dateien müssen vollständige `dump all`-Snapshots sein");
        if (!baselineA.SameFlightControllerConfirmed || !baselineB.SameFlightControllerConfirmed)
            blockingReasons.Add("die Herkunft vom jeweiligen Flight Controller muss bestätigt sein");
        if (!baselineA.ActiveProfilesConfirmed || !baselineB.ActiveProfilesConfirmed)
            blockingReasons.Add("die während des jeweiligen Logs aktiven Profile müssen bestätigt sein");
        if (string.IsNullOrWhiteSpace(baselineA.FirmwareVersion)
            || !string.Equals(baselineA.FirmwareVersion, baselineB.FirmwareVersion, StringComparison.OrdinalIgnoreCase))
            blockingReasons.Add("die Firmwareversionen müssen übereinstimmen");
        if (string.IsNullOrWhiteSpace(baselineA.BoardName)
            || !string.Equals(baselineA.BoardName, baselineB.BoardName, StringComparison.OrdinalIgnoreCase))
            blockingReasons.Add("die FC-Boardnamen müssen übereinstimmen");
        if (!string.Equals(baselineA.Target, baselineB.Target, StringComparison.OrdinalIgnoreCase))
            blockingReasons.Add("die CLI-Ziele müssen übereinstimmen");
        if (baselineA.ActivePidProfile is null || baselineA.ActivePidProfile != baselineB.ActivePidProfile)
            blockingReasons.Add("das aktive PID-Profil muss auf beiden Seiten dasselbe sein");
        if (baselineA.ActiveRateProfile is null || baselineA.ActiveRateProfile != baselineB.ActiveRateProfile)
            blockingReasons.Add("das aktive Rates-Profil muss auf beiden Seiten dasselbe sein");

        if (blockingReasons.Count > 0)
        {
            CliBaselineComparisonText.Text = "Kein sicherer CLI-Wertevergleich: " + string.Join("; ", blockingReasons) + ".";
            CliProposalFollowUpText.Visibility = Visibility.Collapsed;
            return;
        }

        var settingsA = baselineA.TuningSettings
            .GroupBy(SettingKey)
            .ToDictionary(group => group.Key, group => group.Last());
        var settingsB = baselineB.TuningSettings
            .GroupBy(SettingKey)
            .ToDictionary(group => group.Key, group => group.Last());
        SetCliProposalFollowUp(flightA, baselineA, settingsA, baselineB, settingsB);
        var comparable = settingsA.Keys.Intersect(settingsB.Keys, StringComparer.Ordinal)
            .Select(key => (A: settingsA[key], B: settingsB[key]))
            .Where(pair => !string.Equals(pair.A.Value, pair.B.Value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.A.Scope)
            .ThenBy(pair => pair.A.ProfileIndex)
            .ThenBy(pair => pair.A.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sharedCount = settingsA.Keys.Intersect(settingsB.Keys, StringComparer.Ordinal).Count();
        var unmatchedCount = Math.Abs(settingsA.Count - sharedCount) + Math.Abs(settingsB.Count - sharedCount);
        var sourceIdentity = $"CLI A: {FormatBaselineSource(baselineA)} · CLI B: {FormatBaselineSource(baselineB)}. ";

        if (comparable.Length == 0)
        {
            var missingText = unmatchedCount > 0
                ? $" {unmatchedCount} Einträge waren nicht auf beiden Seiten vorhanden und wurden nicht verglichen."
                : string.Empty;
            CliBaselineComparisonText.Text = sourceIdentity + $"Keine abweichenden Werte unter {sharedCount} gemeinsam erkannten PID-, Filter- und Rates-Einstellungen.{missingText} Das beweist nicht, dass eine Änderung nicht anderweitig oder nach dem Snapshot vorgenommen wurde.";
            return;
        }

        const int displayLimit = 30;
        var rows = comparable.Take(displayLimit).Select(pair =>
        {
            var scope = pair.A.Scope switch
            {
                BetaflightTuningScope.PidProfile => $"PID {pair.A.ProfileIndex}",
                BetaflightTuningScope.RateProfile => $"Rates {pair.A.ProfileIndex}",
                BetaflightTuningScope.Master => "Master",
                _ => "Unbekannt"
            };
            return $"{scope} · {pair.A.Name}: {pair.A.Value} → {pair.B.Value}";
        }).ToList();
        if (comparable.Length > displayLimit)
            rows.Add($"… und {comparable.Length - displayLimit} weitere abweichende Werte.");
        if (unmatchedCount > 0)
            rows.Add($"Hinweis: {unmatchedCount} Einträge waren nicht auf beiden Seiten vorhanden und wurden nicht verglichen.");

        CliBaselineComparisonText.Text =
            sourceIdentity + $"Betaflight {baselineA.FirmwareVersion} · PID-Profil {baselineA.ActivePidProfile} · Rates-Profil {baselineA.ActiveRateProfile} · {comparable.Length} gemeinsamer Wert(e) abweichend:{Environment.NewLine}"
            + string.Join(Environment.NewLine, rows);
    }

    private void SetTuningRecommendationAudit(StoredFlightAnalysis flight)
    {
        var latest = (flight.Report.TuningRecommendationHistory ?? [])
            .OfType<TuningRecommendationAuditReport>()
            .Where(recommendation => IsSha256(recommendation.SourceLogSha256)
                && string.Equals(recommendation.SourceLogSha256, flight.Report.Sha256, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(recommendation => recommendation.EvaluatedAtUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            TuningRecommendationAuditText.Visibility = Visibility.Collapsed;
            TuningRecommendationAuditText.Text = string.Empty;
            return;
        }

        var axes = latest.AffectedAxes is { Count: > 0 }
            ? string.Join(", ", latest.AffectedAxes)
            : "keine Achse ausgewählt";
        var cliContext = string.IsNullOrWhiteSpace(latest.CliSnapshotKind)
            ? "keine CLI-Basis"
            : $"{latest.CliSnapshotKind} · {FormatCliIdentity(latest.CliSnapshotFileName, latest.CliSnapshotSha256)} · Betaflight {latest.FirmwareVersion} · {latest.BoardName} · PID {latest.ActivePidProfile?.ToString() ?? "?"} / Rates {latest.ActiveRateProfile?.ToString() ?? "?"}";
        var goal = latest.Goal switch
        {
            "Propwash" => "Acro-Propwash reduzieren",
            "LowerActualRate" => "ACTUAL-Maximalrate senken",
            _ => latest.Goal
        };
        var goalInputs = string.Equals(latest.Goal, "Propwash", StringComparison.Ordinal)
            ? $"Propwash bestätigt: {(latest.PropwashObserved ? "ja" : "nein")} · Motoren: {latest.MotorTemperature} · "
            : string.Empty;
        var inputs = $"Eingaben: {goal} · {goalInputs}Achsen: {axes} · Hardwareprüfung: {(latest.HardwareChecksComplete ? "abgeschlossen" : "offen")} · mögliche Auffälligkeit: {(latest.HardwareIssueReported ? "gemeldet" : "keine gemeldet")} · gleicher FC bestätigt: {(latest.SameFlightControllerConfirmed ? "ja" : "nein")} · aktive Profile bestätigt: {(latest.ActiveProfilesConfirmed ? "ja" : "nein")} · CLI: {cliContext}";
        var changes = (latest.Changes ?? [])
            .Select(change => $"{change.Scope} {change.Name}: {change.PreviousValue} → {change.ProposedValue}; {change.Reason} Messbeleg: {change.Evidence}")
            .ToArray();
        var result = changes.Length == 0
            ? $"Kein Zahlenvorschlag. Ergebnis/Blockiergrund: {latest.OutcomeMessage}"
            : $"{latest.OutcomeMessage}{Environment.NewLine}{string.Join(Environment.NewLine, changes)}";
        var hashPrefix = latest.SourceLogSha256[..12];
        TuningRecommendationAuditText.Text =
            $"Letzte Bewertung in Flug A · {latest.EvaluatedAtUtc.ToLocalTime():g} · Log SHA-256 {hashPrefix}…{Environment.NewLine}{inputs}{Environment.NewLine}{result}";
        TuningRecommendationAuditText.Visibility = Visibility.Visible;
    }

    private void SetCliProposalFollowUp(
        StoredFlightAnalysis flightA,
        BetaflightTuningBaselineReport baselineA,
        IReadOnlyDictionary<string, BetaflightTuningSetting> settingsA,
        BetaflightTuningBaselineReport baselineB,
        IReadOnlyDictionary<string, BetaflightTuningSetting> settingsB)
    {
        var exportedProposal = (flightA.Report.ExportedTuningProposals ?? [])
            .OfType<ExportedTuningProposalReport>()
            .Where(proposal => IsSha256(proposal.SourceLogSha256)
                && IsSha256(flightA.Report.Sha256)
                && string.Equals(
                proposal.SourceLogSha256,
                flightA.Report.Sha256,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(proposal => proposal.ExportedAtUtc)
            .FirstOrDefault();
        if (exportedProposal is null)
        {
            CliProposalFollowUpText.Visibility = Visibility.Visible;
            CliProposalFollowUpText.Text = "Zu Flug A ist kein gespeicherter CLI-Export hinterlegt. Daher kann B nicht gegen einen konkreten Vorschlagswert geprüft werden.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(baselineA.SourceSha256)
            || !string.IsNullOrWhiteSpace(exportedProposal.CliBaselineSha256))
        {
            if (!IsSha256(baselineA.SourceSha256)
                || !IsSha256(exportedProposal.CliBaselineSha256)
                || !string.Equals(baselineA.SourceSha256, exportedProposal.CliBaselineSha256, StringComparison.OrdinalIgnoreCase))
            {
                CliProposalFollowUpText.Visibility = Visibility.Visible;
                CliProposalFollowUpText.Text = "Der Export verweist nicht auf dieselbe CLI-Ausgangsdatei wie der gespeicherte Snapshot von A. Der exakte A/B-Abgleich wird gesperrt.";
                return;
            }
        }

        if (exportedProposal.RecommendationId != Guid.Empty)
        {
            var savedRecommendation = (flightA.Report.TuningRecommendationHistory ?? [])
                .OfType<TuningRecommendationAuditReport>()
                .FirstOrDefault(recommendation => recommendation.RecommendationId == exportedProposal.RecommendationId);
            if (savedRecommendation is null
                || !string.Equals(savedRecommendation.SourceLogSha256, flightA.Report.Sha256, StringComparison.OrdinalIgnoreCase)
                || !(savedRecommendation.Changes ?? []).SequenceEqual(exportedProposal.Changes ?? []))
            {
                CliProposalFollowUpText.Visibility = Visibility.Visible;
                CliProposalFollowUpText.Text = $"Der Export {exportedProposal.DraftFileName} lässt sich nicht exakt mit der gespeicherten Empfehlung verknüpfen; der A/B-Abgleich wird nicht als Vorschlagsprüfung ausgegeben.";
                return;
            }
        }

        if (exportedProposal.Changes is not { Count: > 0 }
            || !IsSha256(exportedProposal.DraftSha256)
            || string.IsNullOrWhiteSpace(exportedProposal.Target)
            || exportedProposal.TargetPidProfile != baselineA.ActivePidProfile
            || exportedProposal.TargetRateProfile != baselineA.ActiveRateProfile
            || !string.Equals(exportedProposal.FirmwareVersion, baselineA.FirmwareVersion, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(exportedProposal.Target, baselineA.Target, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(exportedProposal.BoardName, baselineA.BoardName, StringComparison.OrdinalIgnoreCase))
        {
            CliProposalFollowUpText.Visibility = Visibility.Visible;
            CliProposalFollowUpText.Text = $"Der gespeicherte Export {exportedProposal.DraftFileName} passt nicht mehr zur CLI-Basis von A; ein exakter A/B-Abgleich des Vorschlags ist nicht möglich.";
            return;
        }

        var rows = new List<string>();
        foreach (var change in exportedProposal.Changes)
        {
            var key = SettingKey(new BetaflightTuningSetting(change.Name, string.Empty, change.Scope, change.ProfileIndex));
            var prefix = change.Scope switch
            {
                BetaflightTuningScope.PidProfile => $"PID {change.ProfileIndex}",
                BetaflightTuningScope.RateProfile => $"Rates {change.ProfileIndex}",
                BetaflightTuningScope.Master => "Master",
                _ => "Unbekannt"
            };
            if (!settingsA.TryGetValue(key, out var sourceSetting)
                || !string.Equals(sourceSetting.Value, change.PreviousValue, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add($"{prefix} · {change.Name}: A-Basis stimmt nicht mehr mit dem Export-Vorherwert {change.PreviousValue} überein.");
                continue;
            }

            if (!settingsB.TryGetValue(key, out var followUpSetting))
            {
                rows.Add($"{prefix} · {change.Name}: im B-dump-all nicht erkannt; Vorschlagswert {change.ProposedValue} nicht prüfbar.");
                continue;
            }

            var status = string.Equals(followUpSetting.Value, change.ProposedValue, StringComparison.OrdinalIgnoreCase)
                ? $"Vorschlagswert {change.ProposedValue} steht in B"
                : string.Equals(followUpSetting.Value, change.PreviousValue, StringComparison.OrdinalIgnoreCase)
                    ? $"Vorherwert {change.PreviousValue} steht weiterhin in B"
                    : $"B enthält {followUpSetting.Value}; weder Vorherwert {change.PreviousValue} noch Vorschlag {change.ProposedValue}";
            rows.Add($"{prefix} · {change.Name}: {status}.");
        }

        var hashPrefix = exportedProposal.DraftSha256.Length >= 12
            ? exportedProposal.DraftSha256[..12]
            : exportedProposal.DraftSha256;
        CliProposalFollowUpText.Text =
            $"Export aus A: {exportedProposal.DraftFileName} · SHA-256 {hashPrefix}… · Vergleich mit B-dump-all:{Environment.NewLine}"
            + string.Join(Environment.NewLine, rows)
            + $"{Environment.NewLine}Der Snapshot zeigt den gespeicherten CLI-Wert; er beweist nicht, wann oder wie die Änderung übertragen wurde.";
        CliProposalFollowUpText.Visibility = Visibility.Visible;
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string FormatBaselineSource(BetaflightTuningBaselineReport baseline) =>
        FormatCliIdentity(baseline.SourceFileName, baseline.SourceSha256);

    private static string FormatCliIdentity(string fileName, string sha256) =>
        IsSha256(sha256) ? $"{fileName} · SHA-256 {sha256[..12]}…" : string.IsNullOrWhiteSpace(fileName) ? "Dateiquelle nicht gespeichert" : fileName;

    private static bool IsFullDump(BetaflightTuningBaselineReport baseline) =>
        string.Equals(baseline.SnapshotKind, nameof(BetaflightCliSnapshotKind.Dump), StringComparison.Ordinal)
        && baseline.PidProfiles.Count > 0
        && baseline.TuningSettings.Any(setting => setting.Scope == BetaflightTuningScope.PidProfile);

    private static string SettingKey(BetaflightTuningSetting setting) =>
        $"{(int)setting.Scope}|{setting.ProfileIndex?.ToString() ?? "master"}|{setting.Name.ToUpperInvariant()}";

    private void SetPidComparison(PidActivityAssessment? assessmentA, PidActivityAssessment? assessmentB)
    {
        if (assessmentA?.IsAvailable != true || assessmentB?.IsAvailable != true)
        {
            PidComparisonPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = new List<string>();
        foreach (var axisName in new[] { "Roll", "Pitch", "Yaw" })
        {
            var axisA = assessmentA.Axes.FirstOrDefault(axis => axis.Axis == axisName);
            var axisB = assessmentB.Axes.FirstOrDefault(axis => axis.Axis == axisName);
            if (axisA is null || axisB is null)
                continue;

            foreach (var termName in new[] { "P", "I", "D", "F" })
            {
                var termA = axisA.Terms.FirstOrDefault(term => term.Term == termName);
                var termB = axisB.Terms.FirstOrDefault(term => term.Term == termName);
                if (termA is null || termB is null)
                    continue;

                lines.Add(
                    $"{axisName} {termName}: {termA.MeanAbsolute:N1} / {termB.MeanAbsolute:N1} / {termB.MeanAbsolute - termA.MeanAbsolute:+0.0;-0.0;0.0}");
            }
        }

        PidComparisonText.Text = lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : "Keine gemeinsamen P-, I-, D- oder Feedforward-Werte für beide Sessions vorhanden.";
        PidComparisonPanel.Visibility = Visibility.Visible;
    }

    private static string Describe(StoredFlightAnalysis flight, LogSessionAnalysisReport session)
    {
        var hardware = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(session.Board) ? null : session.Board,
            string.IsNullOrWhiteSpace(session.Firmware) ? null : session.Firmware,
            session.LoopTimeMicroseconds > 0 ? $"Loop {session.LoopTimeMicroseconds} µs" : null
        }.Where(value => value is not null));
        var modeChanges = session.FlightModeAvailable
            ? $" · {session.FlightModeTransitionCount} Wechsel im Flug"
            : session.FlightModeChangeCount > 0
                ? $" · {session.FlightModeChangeCount} Modusmeldungen im Log"
                : string.Empty;
        var modes = FormatFlightModes(session);
        var modeSummary = modes.Length > 0 ? $" · Modi: {modes}" : string.Empty;
        return $"{flight.DroneName} · {flight.Report.StoredBlackboxFileName} · Session {session.LogNumber} · {session.SetpointTrackingDurationSeconds:N1} s{modeChanges}{modeSummary} · {session.IFrames + session.PFrames:N0} Flug-Frames · {hardware}";
    }

    private static string BuildCompatibilityNotice(
        StoredFlightAnalysis flightA,
        LogSessionAnalysisReport sessionA,
        StoredFlightAnalysis flightB,
        LogSessionAnalysisReport sessionB,
        string selectedMode)
    {
        var differences = new List<string>();
        if (!string.Equals(flightA.DroneName, flightB.DroneName, StringComparison.OrdinalIgnoreCase))
            differences.Add("Die Drohnenprofile unterscheiden sich.");
        if (!string.Equals(sessionA.Board, sessionB.Board, StringComparison.OrdinalIgnoreCase))
            differences.Add("Das erkannte Flightcontroller-Board unterscheidet sich.");
        if (!string.Equals(sessionA.Firmware, sessionB.Firmware, StringComparison.OrdinalIgnoreCase))
            differences.Add("Die Firmwareangaben unterscheiden sich.");
        if (sessionA.LoopTimeMicroseconds != sessionB.LoopTimeMicroseconds)
            differences.Add("Die gespeicherten Loopzeiten unterscheiden sich.");

        var setupText = differences.Count == 0
            ? "Drohnenprofil, Board, Firmware und Loopzeit stimmen in den gespeicherten Angaben überein."
            : string.Join(" ", differences);
        var sourceText = string.Equals(flightA.Report.Sha256, flightB.Report.Sha256, StringComparison.OrdinalIgnoreCase)
            ? "Beide Sessions stammen aus derselben BBL-Datei."
            : "Die Sessions stammen aus unterschiedlichen BBL-Dateien.";
        var modeText = $"Die Soll-/Ist- und PID-Kennzahlen sind auf {selectedMode} gefiltert. Vergleiche bleiben nur bei ähnlichen Manövern, Stickbewegungen, Lograten und Flugbedingungen aussagekräftig.";
        return $"{setupText} {sourceText} {modeText} Flugmanöver und Rateprofil lassen sich aus diesen Kennzahlen nicht ausreichend normalisieren. Daher sind die Werte beschreibend und keine Empfehlung für eine Änderung.";
    }

    private static bool HasComparisonWarnings(
        StoredFlightAnalysis flightA,
        LogSessionAnalysisReport sessionA,
        StoredFlightAnalysis flightB,
        LogSessionAnalysisReport sessionB) =>
        !string.Equals(flightA.DroneName, flightB.DroneName, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(sessionA.Board, sessionB.Board, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(sessionA.Firmware, sessionB.Firmware, StringComparison.OrdinalIgnoreCase)
        || sessionA.LoopTimeMicroseconds != sessionB.LoopTimeMicroseconds
        || sessionA.FlightModeChangeCount > 0
        || sessionB.FlightModeChangeCount > 0;

    private static string FormatFlightModes(LogSessionAnalysisReport session)
    {
        if (!session.FlightModeAvailable || session.FlightModeDurationSeconds <= 0 || session.FlightModes.Count == 0)
            return string.Empty;

        return string.Join(", ", session.FlightModes.Select(mode =>
        {
            var percent = mode.DurationSeconds / session.FlightModeDurationSeconds * 100;
            return $"{mode.Mode} {percent:N0}%";
        }));
    }

    private static void SetAxisValues(
        string axis,
        TextBlock valueA,
        TextBlock valueB,
        TextBlock delta,
        SetpointTrackingAssessment assessmentA,
        SetpointTrackingAssessment assessmentB)
    {
        var metricA = assessmentA.Axes.FirstOrDefault(metric => metric.Axis == axis);
        var metricB = assessmentB.Axes.FirstOrDefault(metric => metric.Axis == axis);
        if (metricA is null || metricB is null)
        {
            valueA.Text = "–";
            valueB.Text = "–";
            delta.Text = "–";
            return;
        }

        valueA.Text = $"{metricA.MeanAbsoluteErrorDps:N1} / {metricA.Percentile95AbsoluteErrorDps:N1} °/s";
        valueB.Text = $"{metricB.MeanAbsoluteErrorDps:N1} / {metricB.Percentile95AbsoluteErrorDps:N1} °/s";
        delta.Text = $"Ø {metricB.MeanAbsoluteErrorDps - metricA.MeanAbsoluteErrorDps:+0.0;-0.0;0.0} / P95 {metricB.Percentile95AbsoluteErrorDps - metricA.Percentile95AbsoluteErrorDps:+0.0;-0.0;0.0} °/s";
    }

    private static SetpointTrackingAssessment? FindModeAssessment(
        IReadOnlyList<ModeSpecificSetpointTrackingAssessment> assessments,
        string mode) => assessments
            .FirstOrDefault(item => ModeMatches(item.Mode, mode))?.Assessment;

    private static PidActivityAssessment? FindModeAssessment(
        IReadOnlyList<ModeSpecificPidActivityAssessment> assessments,
        string mode) => assessments
            .FirstOrDefault(item => ModeMatches(item.Mode, mode))?.Assessment;

    private static bool ModeMatches(string loggedMode, string selectedMode) => selectedMode switch
    {
        "Acro" => loggedMode.StartsWith("Manuell/Acro", StringComparison.OrdinalIgnoreCase),
        "Angle" => loggedMode.Contains("Angle", StringComparison.OrdinalIgnoreCase),
        "Horizon" => loggedMode.Contains("Horizon", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private sealed record SessionChoice(LogSessionAnalysisReport Session)
    {
        public string DisplayLabel =>
            $"Session {Session.LogNumber} · {Session.SetpointTrackingDurationSeconds:N1} s · {Session.IFrames + Session.PFrames:N0} Frames"
            + (Session.FlightModeAvailable
                ? $" · {Session.FlightModeTransitionCount} Wechsel im Flug"
                : Session.FlightModeChangeCount > 0 ? $" · {Session.FlightModeChangeCount} Modusmeldungen" : string.Empty);
    }

    private sealed record SessionOverviewGroup(string Title, IReadOnlyList<SessionOverviewRow> Sessions);

    private sealed record SessionOverviewRow(string SessionLabel, string StatusText, string MetricsText);
}
