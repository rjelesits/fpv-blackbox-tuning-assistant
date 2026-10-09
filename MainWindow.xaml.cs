using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FPVBlackboxTuningAssistant.Services;

namespace FPVBlackboxTuningAssistant;

public partial class MainWindow : Window
{
    private const string BlackboxSignature = "H Product:Blackbox flight data recorder";
    private static readonly Brush GoodBrush = new SolidColorBrush(Color.FromRgb(184, 239, 123));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(241, 201, 120));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(154, 169, 159));
    private HardwareCheckStep _hardwareCheckStep;
    private LogAnalysisReport? _currentReport;
    private string? _currentReportPath;
    private BetaflightCliConfiguration? _currentCliConfiguration;
    private IReadOnlyList<BetaflightCliProposalChange> _currentProposalChanges = [];
    private Guid? _currentRecommendationId;
    private bool _currentRecommendationPersisted;
    private IReadOnlyList<BlackboxSessionSummary> _currentSessions = [];
    private bool _hardwareIssueReported;
    private string _currentLogFirmware = string.Empty;
    private string _currentLogBoard = string.Empty;
    private string _currentLogFileName = string.Empty;
    private string _currentCliConfigurationStatus = "Zuerst einen Betaflight-Log laden.";
    private int[] _shortLogSessionNumbers = [];
    private string _shortLogRecoveryContext = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        TuningGoalComboBox.SelectionChanged += TuningGoal_SelectionChanged;
        UpdateTuningGoalInputs();
    }

    private void TuningGoal_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PropwashObservedCheckBox.IsChecked = false;
        PropwashRollAffectedCheckBox.IsChecked = false;
        PropwashPitchAffectedCheckBox.IsChecked = false;
        MotorTemperatureComboBox.SelectedIndex = 0;
        UpdateTuningGoalInputs();
        if (_currentRecommendationId is not null || _currentProposalChanges.Count > 0)
            InvalidateTuningProposal();
    }

    private void UpdateTuningGoalInputs()
    {
        var goalTag = (TuningGoalComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var propwashGoal = string.Equals(goalTag, "Propwash", StringComparison.Ordinal);
        var ratesGoal = string.Equals(goalTag, "LowerActualRate", StringComparison.Ordinal);
        PropwashObservedCheckBox.Visibility = propwashGoal ? Visibility.Visible : Visibility.Collapsed;
        MotorTemperatureComboBox.Visibility = propwashGoal ? Visibility.Visible : Visibility.Collapsed;
        PropwashRollAffectedCheckBox.Visibility = propwashGoal || ratesGoal ? Visibility.Visible : Visibility.Collapsed;
        PropwashPitchAffectedCheckBox.Visibility = propwashGoal || ratesGoal ? Visibility.Visible : Visibility.Collapsed;
        PropwashRollAffectedCheckBox.Content = ratesGoal ? "Roll zu schnell" : "Roll betroffen";
        PropwashPitchAffectedCheckBox.Content = ratesGoal ? "Pitch zu schnell" : "Pitch betroffen";
        TuningGoalHelpText.Text = goalTag switch
        {
            "Propwash" => "Nur bei tatsächlich beobachtetem Propwash/Bounceback in Acro. Motoren müssen kühl gemeldet sein; Logwerte allein begründen keinen höheren D-Term.",
            "LowerActualRate" => "Nur wenn dir die maximale Drehrate auf den ausgewählten Achsen zu hoch ist. Unterstützt wird ACTUAL; die App prüft die CLI-Kurve und braucht wiederholte Acro-Manöver nahe Vollauschlag im Log.",
            _ => "Wähle ein Tuning-Ziel, damit nur dafür passende Messwerte und CLI-Parameter bewertet werden."
        };
    }

    private async void OpenFlightComparison_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var analyses = await Task.Run(() => new LogLibrary().GetStoredAnalyses());
            if (analyses.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "Es gibt noch keine gespeicherten Auswertungen zum Vergleichen. Importiere und speichere zuerst einen Blackbox-Log.",
                    "Noch keine Flugvergleiche",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            new FlightComparisonWindow(analyses) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(
                this,
                $"Die gespeicherten Auswertungen konnten nicht geladen werden.\n\n{ex.Message}",
                "Flugvergleiche nicht verfügbar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Betaflight-Blackbox-Log auswählen",
            Filter = "Betaflight-Blackbox-Logs (*.BBL)|*.BBL|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
            await InspectFileAsync(dialog.FileName);
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(82, 102, 87));
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await InspectFileAsync(files[0]);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(184, 239, 123));
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = new SolidColorBrush(Color.FromRgb(82, 102, 87));
    }

    private async Task InspectFileAsync(string path)
    {
        var logLibrary = new LogLibrary();
        ResetHardwarePrompt();
        TuningMetricsPanel.Visibility = Visibility.Collapsed;
        PidActivityPanel.Visibility = Visibility.Collapsed;
        SetpointTrackingText.Text = string.Empty;
        PidActivityText.Text = string.Empty;
        FlightModeNoticeText.Text = string.Empty;
        FlightModeNoticeText.Visibility = Visibility.Collapsed;
        ShortLogContextPanel.Visibility = Visibility.Collapsed;
        ShortLogContextText.Text = string.Empty;
        ShortLogContextStatus.Text = string.Empty;
        ShortLogContextButtons.Visibility = Visibility.Visible;
        _shortLogSessionNumbers = [];
        _shortLogRecoveryContext = string.Empty;
        _currentReport = null;
        _currentReportPath = null;
        _currentCliConfiguration = null;
        _currentProposalChanges = [];
        _currentRecommendationId = null;
        _currentRecommendationPersisted = false;
        _currentSessions = [];
        _hardwareIssueReported = false;
        _currentLogFirmware = string.Empty;
        _currentLogBoard = string.Empty;
        _currentLogFileName = string.Empty;
        ImportCliConfigurationButton.IsEnabled = false;
        ContinueTuningButton.Visibility = Visibility.Collapsed;
        TuningAnalysisPanel.Visibility = Visibility.Collapsed;
        TuningReadinessText.Text = string.Empty;
        TuningModeCoverageText.Text = string.Empty;
        TuningBaselineReadinessText.Text = string.Empty;
        SameFlightControllerCheckBox.IsChecked = false;
        SameFlightControllerCheckBox.IsEnabled = false;
        ProfileMatchesLogCheckBox.IsChecked = false;
        ProfileMatchesLogCheckBox.IsEnabled = false;
        HardwareChecksCompleteCheckBox.IsChecked = false;
        PropwashObservedCheckBox.IsChecked = false;
        PropwashRollAffectedCheckBox.IsChecked = false;
        PropwashPitchAffectedCheckBox.IsChecked = false;
        TuningGoalComboBox.SelectedIndex = 0;
        MotorTemperatureComboBox.SelectedIndex = 0;
        _currentCliConfigurationStatus = "Zuerst einen Betaflight-Log laden.";
        CliConfigurationStatus.Text = _currentCliConfigurationStatus;
        FileNameText.Text = System.IO.Path.GetFileName(path);
        _currentLogFileName = System.IO.Path.GetFileName(path);
        FileStatus.Text = "Wird gelesen …";
        DroneStatus.Text = "Nach Analyse auswählen";
        StorageStatus.Text = "Wartet auf Analyse";
        StorageStatus.ToolTip = logLibrary.StorageDescription;
        HeaderStatus.Text = "Wird geprüft …";
        PayloadStatus.Text = "Wird geprüft …";
        FramesStatus.Text = "Decoder erforderlich";
        HardwareStatus.Text = "Noch nicht geprüft";
        SetTone(FramesStatus, WarnBrush);
        SetTone(HardwareStatus, MutedBrush);

        try
        {
            var inspection = await Task.Run(() => InspectBlackbox(path));
            FileStatus.Text = $"{System.IO.Path.GetFileName(path)} · {FormatBytes(inspection.FileSize)}";
            if (!inspection.SignatureFound)
            {
                HeaderStatus.Text = "Nicht erkannt";
                PayloadStatus.Text = "Nicht geprüft";
                FramesStatus.Text = "Keine BBL-Dekodierung";
                HardwareStatus.Text = "Gesperrt";
                SetTone(HeaderStatus, WarnBrush);
                SetTone(PayloadStatus, MutedBrush);
                SetTone(FramesStatus, WarnBrush);
                SetTone(HardwareStatus, WarnBrush);
                FileNameText.Text = "Keine Betaflight-Blackbox-Signatur gefunden.";
                MarkLogNotSaved("Keine gültige BBL-Analyse");
                return;
            }

            HeaderStatus.Text = "Signatur erkannt";
            SetTone(HeaderStatus, GoodBrush);
            if (!inspection.PayloadFound)
            {
                PayloadStatus.Text = "Kein Datenbereich";
                FramesStatus.Text = "Keine Flugframes";
                HardwareStatus.Text = "Tuning gesperrt";
                SetTone(PayloadStatus, WarnBrush);
                SetTone(FramesStatus, WarnBrush);
                SetTone(HardwareStatus, WarnBrush);
                FileNameText.Text = "Der Header ist vorhanden, aber danach folgen keine Nutzdaten.";
                MarkLogNotSaved("Keine Nutzdaten zum Analysieren");
                return;
            }

            PayloadStatus.Text = "Wird fachgerecht dekodiert …";
            FramesStatus.Text = "BBL-Decoder läuft …";
            HardwareStatus.Text = "Gesperrt bis Datenprüfung";
            SetTone(PayloadStatus, MutedBrush);
            SetTone(FramesStatus, WarnBrush);
            SetTone(HardwareStatus, WarnBrush);

            var decoded = await new BlackboxDecoder().DecodeAsync(path);
            _currentSessions = decoded.Sessions;
            var firmwareSession = decoded.Sessions.FirstOrDefault(session => session.FlightFrameCount > 0)
                ?? decoded.Sessions.FirstOrDefault();
            _currentLogFirmware = firmwareSession?.Firmware ?? string.Empty;
            _currentLogBoard = firmwareSession?.Board ?? string.Empty;
            ImportCliConfigurationButton.IsEnabled = !string.IsNullOrWhiteSpace(_currentLogFirmware);
            _currentCliConfigurationStatus = ImportCliConfigurationButton.IsEnabled
                ? $"Log-Firmware erkannt: {_currentLogFirmware}. Lade `dump all` dieses Controllers."
                : "Im Log wurde keine Firmwareversion erkannt.";
            CliConfigurationStatus.Text = _currentCliConfigurationStatus;
            HeaderStatus.Text = decoded.Sessions.Count > 0 ? "Header dekodiert" : "Header erkannt";
            PayloadStatus.Text = decoded.Sessions.Count > 0
                ? $"{decoded.Sessions.Count} Sessions in Datei erkannt"
                : "Keine Log-Session erkannt";
            SetTone(HeaderStatus, decoded.Sessions.Count > 0 ? GoodBrush : WarnBrush);
            SetTone(PayloadStatus, decoded.Sessions.Count > 0 ? GoodBrush : WarnBrush);
            ApplyShortLogContextPrompt(decoded.Sessions);

            if (decoded.Sessions.Count == 0)
            {
                FramesStatus.Text = "Keine Log-Session dekodiert";
                HardwareStatus.Text = "Tuning gesperrt";
                SetTone(FramesStatus, WarnBrush);
                SetTone(HardwareStatus, WarnBrush);
                FileNameText.Text = "Der Decoder konnte keine vollständige Log-Session aus der Datei lesen.";
                var storedLog = await PromptAndSaveAnalyzedLogAsync(path, logLibrary, GetDetectedFlightControllerModel(decoded.Sessions));
                SaveAnalysisReport(storedLog, decoded, "Keine Log-Session dekodiert", "Keine Hardwarebewertung möglich");
                return;
            }

            if (!decoded.HasFlightFrames)
            {
                FramesStatus.Text = "Keine I-/P-Flugframes";
                HardwareStatus.Text = "Tuning gesperrt";
                SetTone(FramesStatus, WarnBrush);
                SetTone(HardwareStatus, WarnBrush);
                FileNameText.Text = "Metadaten erkannt, aber keine verwertbaren Flug-Frames gefunden.";
                var storedLog = await PromptAndSaveAnalyzedLogAsync(path, logLibrary, GetDetectedFlightControllerModel(decoded.Sessions));
                SaveAnalysisReport(storedLog, decoded, "Keine I-/P-Flug-Frames", "Keine Hardwarebewertung möglich");
                return;
            }

            var iFrames = decoded.Sessions.Sum(session => session.IFrames);
            var pFrames = decoded.Sessions.Sum(session => session.PFrames);
            FramesStatus.Text = $"{decoded.FlightFrameCount:N0} · I {iFrames:N0} / P {pFrames:N0}";
            HardwareStatus.Text = "Hardwarecheck folgt";
            SetTone(FramesStatus, GoodBrush);
            SetTone(HardwareStatus, WarnBrush);

            var primary = decoded.Sessions[0];
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(primary.Board)) details.Add(primary.Board);
            if (!string.IsNullOrWhiteSpace(primary.Firmware)) details.Add(primary.Firmware);
            if (primary.LoopTimeUs > 0) details.Add($"Loop {primary.LoopTimeUs} µs");
            FileNameText.Text = details.Count > 0
                ? $"{decoded.Sessions.Count} Sessions dekodiert · {string.Join(" · ", details)}"
                : $"{decoded.Sessions.Count} Sessions dekodiert. Die Datei bleibt auf diesem Gerät.";

            var hardwareAnalysisStatus = ApplyMotorAssessment(decoded.Sessions);
            ApplySetpointTrackingAssessment(decoded.Sessions);
            ApplyPidActivityAssessment(decoded.Sessions);
            var savedLog = await PromptAndSaveAnalyzedLogAsync(path, logLibrary, GetDetectedFlightControllerModel(decoded.Sessions));
            SaveAnalysisReport(savedLog, decoded, "I-/P-Flug-Frames dekodiert", hardwareAnalysisStatus);
            if (HardwarePromptPanel.Visibility == Visibility.Visible)
                HardwarePromptPanel.BringIntoView();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BlackboxDecodeException or InvalidOperationException)
        {
            HeaderStatus.Text = "Dekodierung fehlgeschlagen";
            PayloadStatus.Text = "Nicht bestätigt";
            FramesStatus.Text = "Keine verlässlichen Frames";
            HardwareStatus.Text = "Tuning gesperrt";
            SetTone(HeaderStatus, WarnBrush);
            SetTone(PayloadStatus, WarnBrush);
            SetTone(FramesStatus, WarnBrush);
            SetTone(HardwareStatus, WarnBrush);
            FileNameText.Text = ex is BlackboxDecodeException decodeException
                ? decodeException.IsMissingVisualCppRuntime
                    ? "Erforderliche Microsoft-VC++-Laufzeit fehlt. Installationshinweis geöffnet."
                    : decodeException.Message
                : "Die Logdatei konnte nicht vollständig gelesen oder dekodiert werden.";

            if (ex is BlackboxDecodeException { IsMissingVisualCppRuntime: true } runtimeError)
            {
                MessageBox.Show(
                    this,
                    $"Der BBL-Decoder benötigt die Microsoft Visual C++ Laufzeit (x64).\n\n{runtimeError.Message}\n\nNach der Installation die App neu starten.",
                    "Microsoft Visual C++ Laufzeit fehlt",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            MarkLogNotSaved("Dekodierung fehlgeschlagen");
        }
    }

    private async Task<LogStorageResult?> PromptAndSaveAnalyzedLogAsync(
        string sourcePath,
        LogLibrary logLibrary,
        string detectedFlightControllerModel)
    {
        IReadOnlyList<string> existingDrones;
        try
        {
            existingDrones = await Task.Run(logLibrary.GetDroneNames);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MarkLogNotSaved("Log-Bibliothek nicht verfügbar");
            MessageBox.Show(
                this,
                $"Die Analyse ist abgeschlossen, aber die lokale Log-Bibliothek konnte nicht gelesen werden.\n\n{ex.Message}",
                "Log-Bibliothek nicht verfügbar",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return null;
        }

        var suggestedName = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        var selectedDrone = detectedFlightControllerModel;
        var selectedLogName = suggestedName;
        while (true)
        {
            var saveDialog = new DroneSelectionWindow(
                existingDrones,
                suggestedName,
                detectedFlightControllerModel,
                selectedDrone,
                selectedLogName)
            {
                Owner = this
            };
            if (saveDialog.ShowDialog() != true)
            {
                MarkLogNotSaved("Analyse abgeschlossen · nicht gespeichert");
                return null;
            }

            selectedDrone = saveDialog.SelectedDroneName;
            selectedLogName = saveDialog.SelectedLogName;
            try
            {
                var storedLog = await Task.Run(() => logLibrary.SaveLog(sourcePath, selectedDrone, selectedLogName));
                DroneStatus.Text = storedLog.DroneName;
                DroneStatus.ToolTip = storedLog.DroneName;
                StorageStatus.Text = storedLog.AlreadyStored ? "Log bereits gespeichert" : "Log gespeichert";
                StorageStatus.ToolTip = storedLog.StoredPath;
                SetTone(StorageStatus, GoodBrush);
                return storedLog;
            }
            catch (LogNameConflictException ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    "Dateiname bereits vergeben",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MarkLogNotSaved("Speichern fehlgeschlagen");
                MessageBox.Show(
                    this,
                    $"Die Analyse ist abgeschlossen, aber die BBL-Datei konnte nicht gespeichert werden. Das Original wurde nicht verändert.\n\n{ex.Message}",
                    "Speichern fehlgeschlagen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return null;
            }
        }
    }

    private void ImportCliConfiguration_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Betaflight-CLI-Konfiguration auswählen",
            Filter = "Betaflight-CLI-Dateien (*.txt;*.cli)|*.txt;*.cli|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _currentCliConfiguration = BetaflightCliConfigurationParser.ParseFile(dialog.FileName);
            SameFlightControllerCheckBox.IsChecked = false;
            ProfileMatchesLogCheckBox.IsChecked = false;

            var configuration = _currentCliConfiguration;
            var kind = configuration.Kind switch
            {
                BetaflightCliSnapshotKind.Dump => "dump all",
                BetaflightCliSnapshotKind.Diff => "diff all",
                _ => "CLI-Ausgabe ohne erkannten dump-all/diff-all-Kopf"
            };
            var pidCount = configuration.TuningSettings.Count(setting => setting.Scope == BetaflightTuningScope.PidProfile);
            var filterCount = configuration.TuningSettings.Count(setting => setting.Scope == BetaflightTuningScope.Master
                || setting.Name.StartsWith("dterm_", StringComparison.OrdinalIgnoreCase));
            var rateCount = configuration.TuningSettings.Count(setting => setting.Scope == BetaflightTuningScope.RateProfile);
            var profileSummary = string.Join(", ", configuration.PidProfiles.Select(profile => $"PID {profile}"));
            var rateProfileSummary = string.Join(", ", configuration.RateProfiles.Select(profile => $"Rates {profile}"));
            var versionMatches = string.Equals(
                BetaflightCliConfigurationParser.ExtractFirmwareVersion(_currentLogFirmware),
                configuration.FirmwareVersion,
                StringComparison.Ordinal);
            var boardNamesAvailable = !string.IsNullOrWhiteSpace(_currentLogBoard)
                && !string.IsNullOrWhiteSpace(configuration.BoardName);
            var boardMatches = !boardNamesAvailable
                || BetaflightCliConfigurationParser.BoardNamesMatch(_currentLogBoard, configuration.BoardName);

            var sourceHash = IsSha256(configuration.SourceSha256) ? $"SHA-256 {configuration.SourceSha256[..12]}…" : "kein Datei-Hash";
            var summary = $"Eingelesen: {kind} · {configuration.SourceFileName} · {sourceHash} · Betaflight {configuration.FirmwareVersion} · Ziel {configuration.Target} · Board {configuration.BoardName}. "
                + $"Profile: {profileSummary}; {rateProfileSummary}. Erkannte PID-/Filter-/Rates-Einträge: {pidCount}/{filterCount}/{rateCount}.";

            if (configuration.FirmwareVersion.Length == 0)
            {
                SameFlightControllerCheckBox.IsEnabled = false;
                _currentCliConfigurationStatus = summary + " Firmwareversion fehlt; keine passende Basis bestätigt.";
            }
            else if (!versionMatches)
            {
                SameFlightControllerCheckBox.IsEnabled = false;
                _currentCliConfigurationStatus = summary + $" Versionsabweichung zum Log ({_currentLogFirmware}); diese Datei ist keine passende Tuning-Basis.";
            }
            else if (!boardMatches)
            {
                SameFlightControllerCheckBox.IsEnabled = false;
                _currentCliConfigurationStatus = summary + $" Boardname passt nicht zum Blackbox-Log ({_currentLogBoard}); diese Datei ist keine passende Tuning-Basis.";
            }
            else
            {
                SameFlightControllerCheckBox.IsEnabled = true;
                ProfileMatchesLogCheckBox.IsEnabled = true;
                var baselineStatus = configuration.HasFullTuningBaseline
                    ? "Vollständige Profilwerte erkannt."
                    : "Diese Datei enthält nur Abweichungen vom Firmwarestandard. Für genaue Vorherwerte und einen vollständigen Rollback wird `dump all` benötigt.";
                _currentCliConfigurationStatus = summary + " Version passt zum Log. " + baselineStatus + " Prüfe, dass die Datei vom selben Controller stammt.";
            }
            CliConfigurationStatus.Text = _currentCliConfigurationStatus;
            SaveTuningBaselineSnapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _currentCliConfiguration = null;
            SameFlightControllerCheckBox.IsChecked = false;
            SameFlightControllerCheckBox.IsEnabled = false;
            ProfileMatchesLogCheckBox.IsChecked = false;
            ProfileMatchesLogCheckBox.IsEnabled = false;
            _currentCliConfigurationStatus = "Die CLI-Datei konnte nicht gelesen werden. Wähle eine von Betaflight gespeicherte Textausgabe.";
            CliConfigurationStatus.Text = _currentCliConfigurationStatus;
            MessageBox.Show(
                this,
                $"Die Betaflight-CLI-Datei konnte nicht eingelesen werden.\n\n{ex.Message}",
                "CLI-Konfiguration nicht lesbar",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SameFlightController_Checked(object sender, RoutedEventArgs e)
    {
        InvalidateTuningProposal();
        if (_currentCliConfiguration is null)
            return;

        CliConfigurationStatus.Text = SameFlightControllerCheckBox.IsChecked == true
            ? _currentCliConfigurationStatus + $" Bestätigt: derselbe Controller ({_currentLogBoard})."
            : _currentCliConfigurationStatus;
        if (_currentReport?.TuningBaseline is { } baseline)
        {
            baseline.SameFlightControllerConfirmed = SameFlightControllerCheckBox.IsChecked == true;
            baseline.ActiveProfilesConfirmed = ProfileMatchesLogCheckBox.IsChecked == true;
            PersistCurrentReport();
        }
        UpdateProposalReviewAvailability();
    }

    private void TuningInput_Changed(object sender, RoutedEventArgs e) => InvalidateTuningProposal();

    private void InvalidateTuningProposal()
    {
        _currentProposalChanges = [];
        _currentRecommendationId = null;
        _currentRecommendationPersisted = false;
        if (ReviewCliProposalButton is not null)
            ReviewCliProposalButton.IsEnabled = false;
        if (CliProposalStatusText is not null)
            CliProposalStatusText.Text = "Eingaben geändert – Empfehlung bitte erneut berechnen.";
    }

    private void SaveTuningBaselineSnapshot()
    {
        if (_currentReport is null || _currentCliConfiguration is null)
            return;

        var configuration = _currentCliConfiguration;
        _currentReport.TuningBaseline = new BetaflightTuningBaselineReport
        {
            SnapshotKind = configuration.Kind.ToString(),
            SourceFileName = configuration.SourceFileName,
            SourceSha256 = configuration.SourceSha256,
            FirmwareVersion = configuration.FirmwareVersion,
            Target = configuration.Target,
            BoardName = configuration.BoardName,
            ActivePidProfile = configuration.ActivePidProfile,
            ActiveRateProfile = configuration.ActiveRateProfile,
            PidProfiles = configuration.PidProfiles.ToList(),
            RateProfiles = configuration.RateProfiles.ToList(),
            TuningSettings = configuration.TuningSettings.ToList(),
            SameFlightControllerConfirmed = SameFlightControllerCheckBox.IsChecked == true,
            ActiveProfilesConfirmed = ProfileMatchesLogCheckBox.IsChecked == true,
            ImportedAtUtc = DateTime.UtcNow
        };
        PersistCurrentReport();
    }

    private static string GetDetectedFlightControllerModel(IReadOnlyList<BlackboxSessionSummary> sessions) => sessions
        .Select(session => session.Board?.Trim() ?? string.Empty)
        .Where(board => board.Length > 0)
        .GroupBy(board => board, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .Select(group => group.First())
        .FirstOrDefault() ?? string.Empty;

    private void ApplyShortLogContextPrompt(IReadOnlyList<BlackboxSessionSummary> sessions)
    {
        var shortEventOnlySessions = sessions
            .Where(session => session.FlightFrameCount == 0 && session.EventFrameCount is > 0 and <= 10)
            .OrderBy(session => session.LogNumber)
            .ToArray();
        if (shortEventOnlySessions.Length == 0)
            return;

        _shortLogSessionNumbers = shortEventOnlySessions.Select(session => session.LogNumber).ToArray();
        var segments = string.Join(
            ", ",
            shortEventOnlySessions.Select(session => $"Session {session.LogNumber} ({session.EventFrameCount} Ereignis-Frames)"));
        ShortLogContextText.Text =
            $"{segments} enthalten keine I-/P-Flugframes. Solche Kurzlogs können bei kurzen Bedienvorgängen nach einer Landung entstehen, zum Beispiel beim Aufrichten nach einem Absturz mit Crash Flip/Turtle Mode. Der Log allein bestätigt das nicht. Passt diese Erklärung?";
        ShortLogContextStatus.Text = "Deine Antwort wird als Nutzerkontext gespeichert. Ohne Antwort bleibt die Ursache offen.";
        ShortLogContextPanel.Visibility = Visibility.Visible;
    }

    private void ShortLogCrashFlip_Click(object sender, RoutedEventArgs e) =>
        SetShortLogRecoveryContext("Nutzerangabe: wahrscheinlich Crash Flip/Turtle Mode zum Aufrichten nach einem Absturz verwendet");

    private void ShortLogOther_Click(object sender, RoutedEventArgs e) =>
        SetShortLogRecoveryContext("Nutzerangabe: Crash Flip nicht als Ursache der Kurzlogs angegeben");

    private void ShortLogUnknown_Click(object sender, RoutedEventArgs e) =>
        SetShortLogRecoveryContext("Nutzerangabe: Ursache der Kurzlogs unklar");

    private void SetShortLogRecoveryContext(string context)
    {
        _shortLogRecoveryContext = context;
        ShortLogContextStatus.Text = $"{context}.";
        ShortLogContextButtons.Visibility = Visibility.Collapsed;
        if (_currentReport is not null)
        {
            _currentReport.ShortLogSessionNumbers = _shortLogSessionNumbers;
            _currentReport.ShortLogRecoveryContext = context;
            PersistCurrentReport();
        }
    }

    private void ApplySetpointTrackingAssessment(IReadOnlyList<BlackboxSessionSummary> sessions)
    {
        if (sessions.Count == 0)
            return;

        var modeSessions = sessions
            .Where(session => session.FlightFrameCount > 0)
            .OrderBy(session => session.LogNumber)
            .ToArray();
        if (modeSessions.Any(session => session.FlightModeAssessment?.IsAvailable == true
            || session.FlightModeChangeCount > 0))
        {
            var modeSummary = string.Join(
                Environment.NewLine,
                modeSessions.Select(session =>
                {
                    var assessment = session.FlightModeAssessment;
                    var modes = assessment?.IsAvailable == true
                        ? string.Join(", ", assessment.Modes.Select(mode =>
                        {
                            var percent = assessment.DurationSeconds > 0
                                ? mode.DurationSeconds / assessment.DurationSeconds * 100
                                : 0;
                            return $"{mode.Mode}: {FormatModeDuration(mode.DurationSeconds)} ({percent:N1} %)";
                        }))
                        : assessment?.Reason ?? "Flugmodusdaten fehlen";
                    var transitions = assessment?.IsAvailable == true
                        ? $"{assessment.TransitionCount} Wechsel während des Flugs"
                        : string.Empty;
                    var eventCount = session.FlightModeChangeCount > 0
                        ? $"{session.FlightModeChangeCount} Modusmeldungen im Log"
                        : string.Empty;
                    var boundaryEvents = assessment is { BoundaryEventCount: > 0 }
                        ? $"{assessment.BoundaryEventCount} davon am Fluganfang/-ende"
                        : string.Empty;
                    var counts = string.Join(" · ", new[] { transitions, eventCount, boundaryEvents }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    var lines = new List<string> { $"Session {session.LogNumber}: {modes}" };
                    if (counts.Length > 0)
                        lines.Add($"  {counts}");
                    if (assessment is { Transitions.Count: > 0 })
                    {
                        var sequence = string.Join("  ·  ", assessment.Transitions.Select(transition =>
                            $"{FormatFlightTransitionMode(transition.FromMode)} → {FormatFlightTransitionMode(transition.ToMode)} ({FormatFlightTime(transition.TimeFromFlightStartSeconds)})"));
                        lines.Add($"  Wechselverlauf: {sequence}");
                    }
                    else if (assessment?.IsAvailable == true && assessment.BoundaryEventCount > 0)
                    {
                        lines.Add("  Diese Meldungen lagen am Fluganfang oder -ende; während des Flugs wurde kein Wechsel erkannt.");
                    }
                    else if (assessment?.IsAvailable == true)
                    {
                        lines.Add("  Während des Flugs wurde kein Wechsel zwischen den erkannten Modi festgestellt.");
                    }
                    if (assessment?.Modes.Any(mode => mode.Mode == "Angle und Horizon zugleich aktiv") == true)
                        lines.Add("  Angle und Horizon waren zugleich aktiv; möglicherweise überschneiden sich Schalterbereiche.");
                    if (assessment?.Modes.Any(mode => mode.Mode.StartsWith("Manuell/Acro", StringComparison.Ordinal)) != true)
                    {
                        if (assessment?.Modes.Any(mode => mode.Mode.Contains("Angle", StringComparison.Ordinal)) == true)
                            lines.Add("  Betaflight nutzt Manuell/Acro automatisch, wenn Angle und Horizon aus sind. Im Log wird Angle während der erkannten Flugabschnitte gemeldet; ein Manuell/Acro-Abschnitt ist deshalb hier nicht belegbar. Falls du manuell geflogen bist, passen Flugerlebnis und Logsignal nicht zusammen.");
                        else
                            lines.Add("  Betaflight nutzt Manuell/Acro automatisch, wenn Angle und Horizon aus sind. Ein solcher Abschnitt ist in den aufgezeichneten Flugmodus-Signalen nicht erkennbar.");
                    }
                    return string.Join(Environment.NewLine, lines);
                }));
            FlightModeNoticeText.Text =
                $"Flugmodi und Wechselverlauf während des Flugs:{Environment.NewLine}Angle = richtet sich waagerecht aus · Horizon = richtet sich nahe der Stick-Mitte wieder auf · Manuell/Acro = Betaflight-Standard, wenn Angle und Horizon aus sind.{Environment.NewLine}{modeSummary}{Environment.NewLine}Gesamtwerte bleiben sichtbar; darunter werden verfügbare Soll-/Ist- und PID-Kennzahlen zusätzlich je Flugmodus getrennt angezeigt.";
            FlightModeNoticeText.Visibility = Visibility.Visible;
        }

        SetpointTrackingText.Text = string.Join(
            Environment.NewLine,
            sessions.OrderBy(session => session.LogNumber).Select(session =>
            {
                var assessment = session.SetpointTrackingAssessment;
                if (session.FlightFrameCount <= 0)
                {
                    if (session.EventFrameCount is > 0 and <= 10)
                        return $"Session {session.LogNumber}: sehr kurzes Ereignis-Log ({session.EventFrameCount} Frames), keine Flug-Frames; nicht als Flugvergleich gewertet.";
                    return $"Session {session.LogNumber}: keine Flug-Frames für diese Kennzahl.";
                }
                var overall = assessment is null || !assessment.IsAvailable
                    ? $"Session {session.LogNumber}: Gesamtwert nicht vergleichbar – {assessment?.Reason ?? "keine Kennzahl verfügbar"}"
                    : $"Session {session.LogNumber} · Gesamt {assessment.DurationSeconds:N1} s · {string.Join("  |  ", assessment.Axes.Select(axis => $"{axis.Axis}: Ø {axis.MeanAbsoluteErrorDps:N1} °/s · P95 {axis.Percentile95AbsoluteErrorDps:N1} °/s"))}";
                var byMode = FormatSetpointTrackingByMode(session.SetpointTrackingByMode);
                return byMode.Length == 0 ? overall : $"{overall}{Environment.NewLine}  Je Modus:{Environment.NewLine}{byMode}";
            }));
        TuningMetricsPanel.Visibility = Visibility.Visible;
    }

    private static string FormatSetpointTrackingByMode(IReadOnlyList<ModeSpecificSetpointTrackingAssessment> assessments) =>
        string.Join(
            Environment.NewLine,
            assessments.Select(item => item.Assessment.IsAvailable
                ? $"  {FormatFlightTransitionMode(item.Mode)} · {item.Assessment.DurationSeconds:N1} s · {string.Join("  |  ", item.Assessment.Axes.Select(axis => $"{axis.Axis}: Ø {axis.MeanAbsoluteErrorDps:N1} °/s · P95 {axis.Percentile95AbsoluteErrorDps:N1} °/s"))}"
                : $"  {FormatFlightTransitionMode(item.Mode)}: nicht vergleichbar – {item.Assessment.Reason}"));

    private void ApplyPidActivityAssessment(IReadOnlyList<BlackboxSessionSummary> sessions)
    {
        var flightSessions = sessions
            .Where(session => session.FlightFrameCount > 0)
            .OrderBy(session => session.LogNumber)
            .ToArray();
        if (flightSessions.Length == 0)
            return;

        PidActivityText.Text = string.Join(
            Environment.NewLine,
            flightSessions.Select(session =>
            {
                var assessment = session.PidActivityAssessment;
                var overall = assessment is null || !assessment.IsAvailable
                    ? $"Session {session.LogNumber}: Gesamt-PID-Term-Kennzahl nicht verfügbar – {assessment?.Reason ?? "nicht verfügbar"}"
                    : $"Session {session.LogNumber} · Gesamt {assessment.DurationSeconds:N1} s{Environment.NewLine}{FormatPidAxes(assessment.Axes)}";
                var byMode = FormatPidActivityByMode(session.PidActivityByMode);
                return byMode.Length == 0 ? overall : $"{overall}{Environment.NewLine}  Je Modus:{Environment.NewLine}{byMode}";
            }));
        PidActivityPanel.Visibility = Visibility.Visible;
    }

    private static string FormatPidAxes(IReadOnlyList<PidAxisActivityMetric> axes) => string.Join(
        Environment.NewLine,
        axes.Select(axis =>
        {
            var terms = string.Join(
                "  |  ",
                axis.Terms.Select(term =>
                    $"{term.Term}: Øabs {term.MeanAbsolute:N1} · RMS {term.RootMeanSquare:N1} · Spitze {term.PeakAbsolute:N1}"));
            var missing = axis.MissingTerms.Count > 0
                ? $" · im Log nicht aufgezeichnet: {string.Join(", ", axis.MissingTerms)}"
                : string.Empty;
            return $"  {axis.Axis}: {terms}{missing}";
        }));

    private static string FormatPidActivityByMode(IReadOnlyList<ModeSpecificPidActivityAssessment> assessments) =>
        string.Join(
            Environment.NewLine,
            assessments.Select(item => item.Assessment.IsAvailable
                ? $"  {FormatFlightTransitionMode(item.Mode)} · {item.Assessment.DurationSeconds:N1} s{Environment.NewLine}{FormatPidAxes(item.Assessment.Axes)}"
                : $"  {FormatFlightTransitionMode(item.Mode)}: nicht vergleichbar – {item.Assessment.Reason}"));

    private static string FormatModeDuration(double durationSeconds) => durationSeconds < 1
        ? $"{durationSeconds * 1000:N0} ms"
        : $"{durationSeconds:N1} s";

    private static string FormatFlightTransitionMode(string mode)
    {
        if (mode.StartsWith("Manuell/Acro", StringComparison.Ordinal))
            return "Manuell/Acro";

        return mode switch
        {
            "Stabilisiert sich selbst (Angle)" => "Stabilisiert (Angle)",
            "Horizon (Selbstaufrichtung nahe Stick-Mitte)" => "Horizon",
            "Angle und Horizon zugleich aktiv" => "Angle+Horizon",
            _ => mode
        };
    }

    private static string FormatFlightTime(double elapsedSeconds) =>
        TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds)).ToString(@"mm\:ss");

    private void MarkLogNotSaved(string status)
    {
        DroneStatus.Text = "Nicht zugeordnet";
        DroneStatus.ToolTip = null;
        StorageStatus.Text = status;
        StorageStatus.ToolTip = null;
        SetTone(StorageStatus, WarnBrush);
    }

    private string ApplyMotorAssessment(IReadOnlyList<BlackboxSessionSummary> sessions)
    {
        var assessments = sessions
            .Where(session => session.FlightFrameCount > 0 && session.MotorAssessment is { IsAvailable: true })
            .Select(session => (Session: session, Assessment: session.MotorAssessment!))
            .ToArray();

        var deviation = assessments
            .Where(item => item.Assessment.HasStrongDeviation)
            .OrderByDescending(item => item.Assessment.StrongestDeviationPercent)
            .FirstOrDefault();

        if (deviation.Assessment is not null)
        {
            var motorNumber = deviation.Assessment.MotorIndexForStrongestSignal + 1;
            HardwareStatus.Text = "Prüfhinweis vorhanden";
            SetTone(HardwareStatus, WarnBrush);
            ShowHardwareCheckPrompt(
                $"Session {deviation.Session.LogNumber}: Die Flugmesswerte zeigen bei Motorausgang {motorNumber} eine deutliche Abweichung (bis {deviation.Assessment.StrongestDeviationPercent:N0} % relativ zum Median; {deviation.Assessment.StrongestSignalName}). Das ist ein Prüfhinweis, keine Diagnose. Könnte es an den Props liegen? Akku abziehen und alle Propeller auf Beschädigung, Verformung, ungleichmäßige Abnutzung sowie korrekten und festen Sitz prüfen. Hast du eine grobe sichtbare Auffälligkeit gefunden?");
            DiagnosticNoticeTitle.Text = "MESSABWEICHUNG · KEINE DEFEKTAUSSAGE";
            DiagnosticNoticeText.Text =
                "Die Motor-Ausgänge und verfügbaren eRPM-Werte wurden aus dekodierten Flug-Frames bewertet. Eine auffällige Messung führt zu gezielten Nutzerprüfungen, beweist aber keinen Hardwaredefekt.";
            HardwarePositiveButton.Content = "Ja, Mangel gefunden";
            HardwareNegativeButton.Content = "Keine groben Schäden sichtbar";
            HardwarePromptPanel.Visibility = Visibility.Visible;
            return "Propellerprüfung ausstehend";
        }

        if (assessments.Length == 0)
        {
            HardwareStatus.Text = "Keine Motorbewertung möglich";
            SetTone(HardwareStatus, WarnBrush);
            DiagnosticNoticeTitle.Text = "MOTORWERTE NICHT AUSWERTBAR";
            DiagnosticNoticeText.Text =
                "Die Log-Sessions enthalten Flug-Frames, aber keine ausreichend langen, verwertbaren Motor-Messwerte für einen Plausibilitätscheck.";
            FileNameText.Text += " · Motor-Ausgangswerte fehlen oder reichen für eine belastbare Prüfung nicht aus.";
            ShowHardwareCheckPrompt(
                "Die Motor-Telemetrie reicht für einen Plausibilitätscheck nicht aus. Prüfe trotzdem vor weiteren Einstellungsänderungen die Hardware: Akku abziehen und Propeller abnehmen. Gibt es beschädigte, verformte, stark abgenutzte oder locker sitzende Propeller?");
            return "Motor-Messwerte nicht auswertbar";
        }

        HardwareStatus.Text = "Keine starke Abweichung erkannt";
        SetTone(HardwareStatus, GoodBrush);
        DiagnosticNoticeTitle.Text = "MOTOR-PLAUSIBILITÄT OHNE STARKEN HINWEIS";
        DiagnosticNoticeText.Text =
            "In den ausreichend langen Log-Sessions wurde keine starke Motor-Ausgangs- oder eRPM-Abweichung erkannt. Das schließt einen Hardwarefehler oder feine Propellerabnutzung nicht sicher aus.";
        ShowHardwareCheckPrompt(
            "Die Telemetrie zeigt keinen starken Motorhinweis, ersetzt aber keine Sicht- und mechanische Prüfung. Akku abziehen und Propeller abnehmen. Gibt es beschädigte, verformte, stark abgenutzte oder locker sitzende Propeller?");
        return "Keine starke Telemetrieabweichung erkannt";
    }

    private void ShowHardwareCheckPrompt(string initialPrompt)
    {
        _hardwareCheckStep = HardwareCheckStep.Propellers;
        HardwarePromptText.Text = initialPrompt;
        HardwarePositiveButton.Content = "Ja, Auffälligkeit gefunden";
        HardwareNegativeButton.Content = "Nein, nichts Auffälliges";
        HardwarePositiveButton.Visibility = Visibility.Visible;
        HardwareNegativeButton.Visibility = Visibility.Visible;
        HardwarePromptPanel.Visibility = Visibility.Visible;
    }

    private void SaveAnalysisReport(
        LogStorageResult? storedLog,
        BlackboxDecodeResult decoded,
        string dataQualityStatus,
        string hardwareCheckStatus)
    {
        if (storedLog is null)
            return;

        _currentReport = AnalysisReportStore.Create(storedLog, decoded, dataQualityStatus, hardwareCheckStatus);
        _currentReport.ShortLogSessionNumbers = _shortLogSessionNumbers;
        _currentReport.ShortLogRecoveryContext = _shortLogRecoveryContext;
        _currentReportPath = AnalysisReportStore.GetReportPath(storedLog.StoredPath);
        PersistCurrentReport();
    }

    private bool PersistCurrentReport()
    {
        if (_currentReport is null || string.IsNullOrWhiteSpace(_currentReportPath))
            return false;

        try
        {
            AnalysisReportStore.Save(
                _currentReportPath,
                _currentReport);
            StorageStatus.Text = "Log + Auswertung gespeichert";
            StorageStatus.ToolTip = _currentReportPath;
            SetTone(StorageStatus, GoodBrush);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StorageStatus.Text = "Log gespeichert · Bericht fehlgeschlagen";
            StorageStatus.ToolTip = _currentReportPath;
            SetTone(StorageStatus, WarnBrush);
            MessageBox.Show(
                this,
                $"Die BBL-Datei ist gespeichert, aber der Auswertungsbericht konnte nicht geschrieben werden.\n\n{ex.Message}",
                "Auswertung nicht gespeichert",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private void RecordHardwareCheck(string step, string result)
    {
        if (_currentReport is null)
            return;

        _currentReport.UserHardwareChecks.Add(new RecordedHardwareCheck
        {
            Step = step,
            Result = result,
            RecordedAtUtc = DateTime.UtcNow
        });
        PersistCurrentReport();
    }

    private void HardwarePositive_Click(object sender, RoutedEventArgs e)
    {
        _hardwareIssueReported = true;
        switch (_hardwareCheckStep)
        {
            case HardwareCheckStep.Propellers:
                RecordHardwareCheck("Propeller-Sichtprüfung", "Grobe sichtbare Auffälligkeit gemeldet");
                FinishHardwareCheck(
                    "Propeller-Mangel gemeldet",
                    "Bitte beschädigte, verformte oder ungleich abgenutzte Propeller vor dem nächsten Flug ersetzen. Danach mit einem neuen Log erneut prüfen. Die App stellt die Hardwareursache nicht als bewiesen dar.");
                break;
            case HardwareCheckStep.MotorFeel:
                RecordHardwareCheck("Mechanische Motorprüfung", "Schwergängigkeit, Kratzen oder Haken gemeldet");
                FinishHardwareCheck(
                    "Mechanische Auffälligkeit gemeldet",
                    "Ein Motor fühlt sich schwergängig an, kratzt oder hakt. Akku abgezogen und Propeller entfernt lassen; den Motor vor dem nächsten Flug prüfen oder ersetzen.");
                break;
            case HardwareCheckStep.Connections:
                RecordHardwareCheck("Kabel-/Lötstellenprüfung", "Sichtbare Auffälligkeit gemeldet");
                FinishHardwareCheck(
                    "Verbindung auffällig",
                    "Prüfe die Motorkabel, Stecker und Lötstellen vor dem nächsten Flug. Beschädigte oder lose Verbindungen reparieren lassen und danach einen neuen Log aufnehmen.");
                break;
        }
    }

    private void HardwareNegative_Click(object sender, RoutedEventArgs e)
    {
        switch (_hardwareCheckStep)
        {
            case HardwareCheckStep.Propellers:
                RecordHardwareCheck("Propeller-Sichtprüfung", "Keine grobe sichtbare Auffälligkeit; feine Abnutzung nicht ausgeschlossen");
                _hardwareCheckStep = HardwareCheckStep.MotorFeel;
                HardwarePromptText.Text =
            "Du hast keine groben Propellerschäden erkannt. Feine oder schwer erkennbare Abnutzung ist damit nicht ausgeschlossen; ein Propellerproblem bleibt möglich. Als Nächstes: Akku abziehen und Propeller abnehmen. Drehe die Motoren vorsichtig von Hand. Fühlt sich einer schwergängig an, kratzt oder hakt?";
                HardwarePositiveButton.Content = "Ja, Motor auffällig";
            HardwareNegativeButton.Content = "Kein auffälliger Widerstand";
                break;
            case HardwareCheckStep.MotorFeel:
                RecordHardwareCheck("Mechanische Motorprüfung", "Kein auffälliger Widerstand bemerkt; Motorfehler nicht sicher ausgeschlossen");
                _hardwareCheckStep = HardwareCheckStep.Connections;
                HardwarePromptText.Text =
                    "Du hast beim vorsichtigen Drehen keinen auffälligen Widerstand bemerkt. Das schließt einen Lager- oder anderen Motorfehler nicht sicher aus. Akku abgezogen lassen: Prüfe Motorkabel, Stecker und Lötstellen auf lose, beschädigte oder verfärbte Verbindungen. Hast du etwas Auffälliges gefunden?";
                HardwarePositiveButton.Content = "Ja, Verbindung auffällig";
                HardwareNegativeButton.Content = "Nichts Sichtbares gefunden";
                break;
            case HardwareCheckStep.Connections:
                RecordHardwareCheck("Kabel-/Lötstellenprüfung", "Keine sichtbare Auffälligkeit gemeldet");
                FinishHardwareCheck(
                    "Keine groben Auffälligkeiten gemeldet",
                    "Bei der Sichtprüfung wurden keine groben Propellerschäden gemeldet; feine oder schwer erkennbare Abnutzung ist dadurch nicht ausgeschlossen und ein Propellerproblem bleibt möglich. Auch bei Motordreh und sichtbaren Verbindungen wurde nichts Grobes gemeldet. Die Messabweichung bleibt bestehen; nimm nach der Prüfung einen neuen Log auf.");
                break;
        }

        if (_hardwareCheckStep == HardwareCheckStep.Complete)
            ContinueTuningButton.BringIntoView();
        else
            HardwarePromptPanel.BringIntoView();
    }

    private void FinishHardwareCheck(string status, string message)
    {
        HardwareStatus.Text = status;
        SetTone(HardwareStatus, WarnBrush);
        HardwarePromptText.Text = message;
        if (_currentReport is not null)
        {
            _currentReport.HardwareCheckStatus = status;
            PersistCurrentReport();
        }
        HardwareChecksCompleteCheckBox.IsChecked = !_hardwareIssueReported;
        HardwarePositiveButton.Visibility = Visibility.Collapsed;
        HardwareNegativeButton.Visibility = Visibility.Collapsed;
        _hardwareCheckStep = HardwareCheckStep.Complete;
        ContinueTuningButton.Visibility = Visibility.Visible;
        ContinueTuningButton.BringIntoView();
    }

    private void ContinueTuning_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSessions.Count == 0 || _hardwareCheckStep != HardwareCheckStep.Complete)
            return;

        TuningReadinessText.Text = _hardwareIssueReported
            ? "Bei der Nutzerprüfung wurde eine mögliche Hardwareauffälligkeit gemeldet. Bitte behebe sie und nimm einen neuen Log auf, bevor du die PID- oder Filterwerte änderst. Die Messung bleibt als Referenz sichtbar."
            : "Keine groben Auffälligkeiten wurden gemeldet. Das schließt einen Defekt nicht sicher aus; Messabweichungen bleiben ein Prüfhinweis und sind für sich kein Defektnachweis.";

        TuningModeCoverageText.Text = string.Join(
            Environment.NewLine,
            _currentSessions.Select(session =>
            {
                var assessment = session.FlightModeAssessment;
                if (assessment is null || !assessment.IsAvailable)
                    return $"Session {session.LogNumber}: Flugmodi nicht verlässlich zuordenbar — {assessment?.Reason ?? "keine Modusmessung vorhanden"}";

                var modes = assessment.Modes.Count == 0
                    ? "keine Modi mit mindestens 0,1 s aktiver Messdauer"
                    : string.Join(", ", assessment.Modes.Select(mode => $"{mode.Mode}: {mode.DurationSeconds:N1} s"));
                var maneuverCoverage = string.Join(" · ", session.TuningManeuverByMode.Select(item =>
                    item.Assessment.IsAvailable
                        ? $"{FormatFlightTransitionMode(item.Mode)}: {item.Assessment.SampleRateHz:N0} Hz; Roll {item.Assessment.Axes.FirstOrDefault(axis => axis.Axis == "Roll")?.HighStickExcursionCount ?? 0} >50 % / {item.Assessment.Axes.FirstOrDefault(axis => axis.Axis == "Roll")?.NearFullStickExcursionCount ?? 0} ≥90 %; Pitch {item.Assessment.Axes.FirstOrDefault(axis => axis.Axis == "Pitch")?.HighStickExcursionCount ?? 0} >50 % / {item.Assessment.Axes.FirstOrDefault(axis => axis.Axis == "Pitch")?.NearFullStickExcursionCount ?? 0} ≥90 %"
                        : $"{FormatFlightTransitionMode(item.Mode)}: Manöverdaten nicht verfügbar"));
                return maneuverCoverage.Length == 0
                    ? $"Session {session.LogNumber}: {modes}; {assessment.TransitionCount} Moduswechsel"
                    : $"Session {session.LogNumber}: {modes}; {assessment.TransitionCount} Moduswechsel{Environment.NewLine}  Tuning-Datenabdeckung: {maneuverCoverage}";
            }));

        TuningBaselineReadinessText.Text = DescribeTuningBaselineReadiness();
        UpdateProposalReviewAvailability();
        CliProposalStatusText.Text = _hardwareIssueReported
            ? "Kein CLI-Entwurf: Eine mögliche Hardwareauffälligkeit wurde gemeldet. Erst beheben und neu loggen."
            : _currentProposalChanges.Count == 0
                ? "Kein numerischer CLI-Vorschlag: Die vorhandenen Kennzahlen belegen noch keine konkrete Parameteränderung."
                : _currentReport is null
                    ? "Export gesperrt: Speichere Log und Auswertungsbericht, damit Vorschlag und Datei-Hash nachvollziehbar verknüpft werden können."
                : ReviewCliProposalButton.IsEnabled
                    ? $"{_currentProposalChanges.Count} begründete Änderung(en) zur Prüfung bereit."
                    : "Vorschlag vorhanden, aber eine Freigabe fehlt: vollständige `dump all`-Basis, derselbe Controller oder bestätigte aktive Profile.";
        TuningAnalysisPanel.Visibility = Visibility.Visible;
        TuningGoalComboBox.BringIntoView();
    }

    private void CalculateTuningRecommendations_Click(object sender, RoutedEventArgs e)
    {
        _currentProposalChanges = [];
        _currentRecommendationId = null;
        _currentRecommendationPersisted = false;
        ReviewCliProposalButton.IsEnabled = false;

        var goalTag = (TuningGoalComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (goalTag is not ("Propwash" or "LowerActualRate"))
        {
            CliProposalStatusText.Text = "Wähle zuerst ein unterstütztes Tuning-Ziel.";
            return;
        }

        var temperatureTag = (MotorTemperatureComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var temperature = temperatureTag switch
        {
            "Cool" => MotorTemperatureReport.Cool,
            "Warm" => MotorTemperatureReport.Warm,
            "Hot" => MotorTemperatureReport.Hot,
            _ => MotorTemperatureReport.Unreported
        };
        var affectedAxes = new List<string>();
        if (PropwashRollAffectedCheckBox.IsChecked == true)
            affectedAxes.Add("Roll");
        if (PropwashPitchAffectedCheckBox.IsChecked == true)
            affectedAxes.Add("Pitch");

        var result = goalTag switch
        {
            "Propwash" => TuningRecommendationEngine.EvaluatePropwashTrial(
                _currentSessions,
                _currentCliConfiguration,
                SameFlightControllerCheckBox.IsChecked == true,
                ProfileMatchesLogCheckBox.IsChecked == true,
                HardwareChecksCompleteCheckBox.IsChecked == true,
                _hardwareIssueReported,
                PropwashObservedCheckBox.IsChecked == true,
                affectedAxes,
                temperature),
            "LowerActualRate" => TuningRecommendationEngine.EvaluateActualRatesTrial(
                _currentSessions,
                _currentCliConfiguration,
                SameFlightControllerCheckBox.IsChecked == true,
                ProfileMatchesLogCheckBox.IsChecked == true,
                HardwareChecksCompleteCheckBox.IsChecked == true,
                _hardwareIssueReported,
                affectedAxes),
            _ => new TuningRecommendationResult([], "Wähle ein unterstütztes Tuning-Ziel aus.")
        };
        _currentProposalChanges = result.Changes;
        CliProposalStatusText.Text = result.Message;
        _currentRecommendationId = Guid.NewGuid();
        if (_currentReport is not null)
        {
            _currentReport.TuningRecommendationHistory ??= [];
            _currentReport.TuningRecommendationHistory.Add(new TuningRecommendationAuditReport
            {
                RecommendationId = _currentRecommendationId.Value,
                SourceLogSha256 = _currentReport.Sha256,
                EvaluatedAtUtc = DateTime.UtcNow,
                Goal = goalTag ?? string.Empty,
                HardwareChecksComplete = HardwareChecksCompleteCheckBox.IsChecked == true,
                HardwareIssueReported = _hardwareIssueReported,
                SameFlightControllerConfirmed = SameFlightControllerCheckBox.IsChecked == true,
                ActiveProfilesConfirmed = ProfileMatchesLogCheckBox.IsChecked == true,
                PropwashObserved = string.Equals(goalTag, "Propwash", StringComparison.Ordinal)
                    && PropwashObservedCheckBox.IsChecked == true,
                MotorTemperature = string.Equals(goalTag, "Propwash", StringComparison.Ordinal)
                    ? temperature.ToString()
                    : "Nicht relevant für Rate-Ziel",
                AffectedAxes = affectedAxes.ToList(),
                FirmwareVersion = _currentCliConfiguration?.FirmwareVersion ?? string.Empty,
                CliSnapshotKind = _currentCliConfiguration?.Kind.ToString() ?? string.Empty,
                CliSnapshotFileName = _currentCliConfiguration?.SourceFileName ?? string.Empty,
                CliSnapshotSha256 = _currentCliConfiguration?.SourceSha256 ?? string.Empty,
                Target = _currentCliConfiguration?.Target ?? string.Empty,
                BoardName = _currentCliConfiguration?.BoardName ?? string.Empty,
                ActivePidProfile = _currentCliConfiguration?.ActivePidProfile,
                ActiveRateProfile = _currentCliConfiguration?.ActiveRateProfile,
                OutcomeMessage = result.Message,
                Changes = result.Changes.ToList()
            });
            _currentRecommendationPersisted = PersistCurrentReport();
            CliProposalStatusText.Text += _currentRecommendationPersisted
                ? " Die Bewertung und ihre Eingaben wurden im Auswertungsbericht gespeichert."
                : " Die Bewertung konnte nicht dauerhaft gespeichert werden; CLI-Export bleibt gesperrt.";
        }
        else
        {
            CliProposalStatusText.Text += " Kein gespeicherter Flugbericht vorhanden; die Bewertung bleibt nur in dieser Sitzung und kann nicht exportiert werden.";
        }
        UpdateProposalReviewAvailability();
    }

    private bool HasSavedCurrentRecommendation =>
        _currentReport is not null
        && _currentRecommendationId is { } recommendationId
        && _currentRecommendationPersisted
        && (_currentReport.TuningRecommendationHistory ?? []).Any(recommendation =>
            recommendation.RecommendationId == recommendationId
            && string.Equals(recommendation.SourceLogSha256, _currentReport.Sha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(recommendation.CliSnapshotSha256, _currentCliConfiguration?.SourceSha256, StringComparison.OrdinalIgnoreCase)
            && (recommendation.Changes ?? []).SequenceEqual(_currentProposalChanges));

    private void UpdateProposalReviewAvailability()
    {
        ReviewCliProposalButton.IsEnabled = _currentProposalChanges.Count > 0
            && HasSavedCurrentRecommendation
            && !_hardwareIssueReported
            && HardwareChecksCompleteCheckBox.IsChecked == true
            && _currentReport is not null
            && _currentCliConfiguration is { HasFullTuningBaseline: true }
            && IsSha256(_currentCliConfiguration.SourceSha256)
            && SameFlightControllerCheckBox.IsChecked == true
            && ProfileMatchesLogCheckBox.IsChecked == true;
    }

    private void ReviewCliProposal_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCliConfiguration is null || _currentProposalChanges.Count == 0 || _currentReport is null || !HasSavedCurrentRecommendation)
            return;

        var targetProfile = _currentCliConfiguration.ActivePidProfile;
        var targetRateProfile = _currentCliConfiguration.ActiveRateProfile;
        if (targetProfile is null || targetRateProfile is null)
        {
            MessageBox.Show(
                this,
                "In der CLI-Datei wurde kein aktives PID- und Rates-Profil erkannt.",
                "Zielprofil fehlt",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var reviewWindow = new TuningProposalReviewWindow(
                _currentCliConfiguration,
                _currentLogFirmware,
                _currentLogBoard,
                SameFlightControllerCheckBox.IsChecked == true,
                ProfileMatchesLogCheckBox.IsChecked == true,
                targetProfile.Value,
                targetRateProfile.Value,
                _currentProposalChanges,
                _currentReport.StoredBlackboxFileName,
                _currentReport.Sha256)
            {
                Owner = this
            };
            if (reviewWindow.ShowDialog() == true && reviewWindow.ExportedProposal is { } exportedProposal)
            {
                exportedProposal.RecommendationId = _currentRecommendationId!.Value;
                exportedProposal.SourceLogSha256 = _currentReport.Sha256;
                exportedProposal.SourceLogFileName = _currentReport.StoredBlackboxFileName;
                exportedProposal.CliBaselineFileName = _currentCliConfiguration.SourceFileName;
                exportedProposal.CliBaselineSha256 = _currentCliConfiguration.SourceSha256;
                _currentReport.ExportedTuningProposals.Add(exportedProposal);
                if (!PersistCurrentReport())
                {
                    MessageBox.Show(
                        this,
                        "Die CLI-Datei wurde gespeichert, aber ihre Verknüpfung mit Log, Empfehlung und CLI-Ausgangsdatei konnte nicht dauerhaft gespeichert werden. Verwende den Export deshalb nicht für einen späteren exakten Flugvergleich. Der Datei-Hash steht in der Exportmeldung; sichere den Entwurf und den vollständigen `dump all` separat.",
                        "Export-Verknüpfung nicht gespeichert",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "CLI-Entwurf nicht verfügbar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string DescribeTuningBaselineReadiness()
    {
        if (_currentCliConfiguration is null)
            return "Noch keine CLI-Konfiguration geladen. Für exakte Vorherwerte wird `dump all` vom Controller benötigt, auf dem dieser Log aufgezeichnet wurde.";

        var configuration = _currentCliConfiguration;
        if (!configuration.HasFullTuningBaseline)
            return "Die geladene Datei ist keine vollständige `dump all`-Ausgangsbasis.";

        if (!string.Equals(
                configuration.FirmwareVersion,
                BetaflightCliConfigurationParser.ExtractFirmwareVersion(_currentLogFirmware),
                StringComparison.Ordinal))
            return $"Firmware passt nicht zum Log ({configuration.FirmwareVersion} gegenüber {_currentLogFirmware}).";

        if (!string.IsNullOrWhiteSpace(_currentLogBoard)
            && !string.IsNullOrWhiteSpace(configuration.BoardName)
            && !BetaflightCliConfigurationParser.BoardNamesMatch(_currentLogBoard, configuration.BoardName))
            return $"Boardname passt nicht zum Log ({configuration.BoardName} gegenüber {_currentLogBoard}).";

        if (SameFlightControllerCheckBox.IsChecked != true)
            return $"Vollständige Werte erkannt: Betaflight {configuration.FirmwareVersion}, PID-Profil {configuration.ActivePidProfile?.ToString() ?? "unbekannt"}. Bitte bestätige, dass diese CLI-Datei von genau diesem Controller stammt.";

        if (ProfileMatchesLogCheckBox.IsChecked != true)
            return $"Controller bestätigt. Bitte zusätzlich bestätigen, dass PID-Profil {configuration.ActivePidProfile?.ToString() ?? "unbekannt"} und Rates-Profil {configuration.ActiveRateProfile?.ToString() ?? "unbekannt"} auch während dieses Logs aktiv waren.";

        var hashPrefix = IsSha256(configuration.SourceSha256) ? $" · CLI SHA-256 {configuration.SourceSha256[..12]}…" : string.Empty;
        return $"Vollständige `dump all`-Basis bestätigt: {configuration.SourceFileName} · Betaflight {configuration.FirmwareVersion}, Ziel {configuration.Target}, PID-Profil {configuration.ActivePidProfile?.ToString() ?? "unbekannt"}, Rates-Profil {configuration.ActiveRateProfile?.ToString() ?? "unbekannt"}; {configuration.TuningSettings.Count} erlaubte Tuning-Werte gespeichert{hashPrefix}.";
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private void ResetHardwarePrompt()
    {
        _hardwareCheckStep = HardwareCheckStep.None;
        HardwarePromptPanel.Visibility = Visibility.Collapsed;
        HardwarePositiveButton.Visibility = Visibility.Visible;
        HardwareNegativeButton.Visibility = Visibility.Visible;
    }

    private static BlackboxInspection InspectBlackbox(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var searchLength = Math.Min(bytes.Length, 64 * 1024);
        var prefix = Encoding.ASCII.GetString(bytes, 0, searchLength);
        var signaturePosition = prefix.IndexOf(BlackboxSignature, StringComparison.Ordinal);
        if (signaturePosition < 0)
            return new BlackboxInspection(bytes.LongLength, false, false, 0);

        var cursor = signaturePosition;
        while (cursor < bytes.Length)
        {
            var lineEnd = Array.IndexOf(bytes, (byte)'\n', cursor);
            if (lineEnd < 0)
                lineEnd = bytes.Length;

            var line = Encoding.ASCII.GetString(bytes, cursor, lineEnd - cursor).TrimEnd('\r');
            if (!line.StartsWith("H ", StringComparison.Ordinal))
            {
                var payloadLength = bytes.Length - cursor;
                return new BlackboxInspection(bytes.LongLength, true, payloadLength > 0, payloadLength);
            }

            cursor = lineEnd + 1;
        }

        return new BlackboxInspection(bytes.LongLength, true, false, 0);
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:N2} MB"
        : $"{bytes / 1024d:N1} KB";

    private static void SetTone(System.Windows.Controls.TextBlock textBlock, Brush brush)
    {
        textBlock.Foreground = brush;
    }

    private sealed record BlackboxInspection(long FileSize, bool SignatureFound, bool PayloadFound, long PayloadLength);

    private enum HardwareCheckStep
    {
        None,
        Propellers,
        MotorFeel,
        Connections,
        Complete
    }
}
