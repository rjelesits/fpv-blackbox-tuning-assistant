using System.IO;
using System.Windows;
using System.Windows.Controls;
using FPVBlackboxTuningAssistant.Services;
using Microsoft.Win32;

namespace FPVBlackboxTuningAssistant;

public partial class TuningProposalReviewWindow : Window
{
    private readonly BetaflightCliConfiguration _source;
    private readonly string _logFirmware;
    private readonly string _logBoard;
    private readonly bool _sameFlightControllerConfirmed;
    private readonly bool _activeProfilesConfirmed;
    private readonly int _targetPidProfile;
    private readonly int _targetRateProfile;
    private readonly string _sourceLogName;
    private readonly string _sourceLogSha256;
    private readonly IReadOnlyList<BetaflightCliProposalChange> _changes;
    private readonly string _draft;
    private readonly ProposalReviewGate _reviewGate = new();

    public ExportedTuningProposalReport? ExportedProposal { get; private set; }

    public TuningProposalReviewWindow(
        BetaflightCliConfiguration source,
        string logFirmware,
        string logBoard,
        bool sameFlightControllerConfirmed,
        bool activeProfilesConfirmed,
        int targetPidProfile,
        int targetRateProfile,
        IReadOnlyList<BetaflightCliProposalChange> changes,
        string sourceLogName,
        string sourceLogSha256)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(changes);
        if (!sameFlightControllerConfirmed)
            throw new InvalidOperationException("Der Nutzer muss bestätigen, dass die CLI-Basis von genau diesem Controller stammt.");
        if (!activeProfilesConfirmed)
            throw new InvalidOperationException("Der Nutzer muss bestätigen, dass die aktiven PID- und Rates-Profile während des Logs galten.");
        if (!source.HasFullTuningBaseline)
            throw new InvalidOperationException("Für einen prüfbaren Entwurf wird `dump all` benötigt.");
        if (!string.Equals(
                source.FirmwareVersion,
                BetaflightCliConfigurationParser.ExtractFirmwareVersion(logFirmware),
                StringComparison.Ordinal))
            throw new InvalidOperationException("CLI-Basis und Log verwenden unterschiedliche Betaflight-Versionen.");
        if (!string.IsNullOrWhiteSpace(logBoard)
            && !string.IsNullOrWhiteSpace(source.BoardName)
            && !BetaflightCliConfigurationParser.BoardNamesMatch(logBoard, source.BoardName))
            throw new InvalidOperationException("Der Boardname der CLI-Basis passt nicht zum Blackbox-Log.");
        if (changes.Count == 0)
            throw new InvalidOperationException("Es gibt keine begründeten Änderungen zum Prüfen.");
        if (!source.RateProfiles.Contains(targetRateProfile))
            throw new InvalidOperationException("Das ausgewählte Rates-Profil fehlt in der CLI-Basis.");

        InitializeComponent();
        _source = source;
        _logFirmware = logFirmware;
        _logBoard = logBoard;
        _sameFlightControllerConfirmed = sameFlightControllerConfirmed;
        _activeProfilesConfirmed = activeProfilesConfirmed;
        _targetPidProfile = targetPidProfile;
        _targetRateProfile = targetRateProfile;
        _sourceLogName = sourceLogName;
        _sourceLogSha256 = sourceLogSha256;
        _changes = changes.ToArray();
        _draft = BetaflightCliProposalExporter.CreateDraft(
            _source,
            _logFirmware,
            _logBoard,
            _sameFlightControllerConfirmed,
            _activeProfilesConfirmed,
            _targetPidProfile,
            _targetRateProfile,
            _changes,
            _sourceLogName,
            _sourceLogSha256);

        ConfigurationSummaryText.Text =
            $"Log: {Path.GetFileName(sourceLogName)} · Betaflight {source.FirmwareVersion} · Board {source.BoardName} · PID-Profil {targetPidProfile} · Rates-Profil {targetRateProfile} · {changes.Count} vorgeschlagene Änderung(en).";
        ChangesGrid.ItemsSource = _changes.Select(change => new ProposalReviewRow(
            change.Scope switch
            {
                BetaflightTuningScope.PidProfile => $"PID {change.ProfileIndex}",
                BetaflightTuningScope.Master => "Master",
                BetaflightTuningScope.RateProfile => $"Rates {change.ProfileIndex}",
                _ => "Unbekannt"
            },
            change.Name,
            change.PreviousValue,
            change.ProposedValue,
            change.Reason,
            change.Evidence,
            change.ExpectedEffect,
            change.Risk,
            change.ValidationPlan)).ToArray();
        CliDraftPreviewText.Text = _draft;
        if (ReferenceEquals(ReviewTabs.SelectedItem, ChangeReviewTab))
            _reviewGate.MarkChangeDetailsViewed();
        ReviewTabs.SelectionChanged += ReviewTabs_SelectionChanged;
    }

    private void ReviewTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(ReviewTabs.SelectedItem, ChangeReviewTab))
            _reviewGate.MarkChangeDetailsViewed();
        else if (ReferenceEquals(ReviewTabs.SelectedItem, CliDraftTab))
            _reviewGate.MarkCliDraftViewed();

        ReviewConfirmedCheckBox.IsEnabled = _reviewGate.CanConfirm;
        if (!ReviewConfirmedCheckBox.IsEnabled)
            ReviewConfirmedCheckBox.IsChecked = false;

        UpdateExportAvailability();
    }

    private void ReviewConfirmed_Checked(object sender, RoutedEventArgs e)
    {
        _reviewGate.SetUserConfirmed(ReviewConfirmedCheckBox.IsChecked == true);
        UpdateExportAvailability();
    }

    private void UpdateExportAvailability() => ExportButton.IsEnabled = _reviewGate.CanExport;

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_reviewGate.CanExport)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Geprüften Betaflight-CLI-Entwurf speichern",
            Filter = "Betaflight CLI (*.cli)|*.cli|Textdatei (*.txt)|*.txt",
            DefaultExt = ".cli",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"{Path.GetFileNameWithoutExtension(_sourceLogName)}-tuning-entwurf.cli"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var savedHash = BetaflightCliProposalExporter.SaveVerifiedDraft(dialog.FileName, _draft);

            ExportedProposal = new ExportedTuningProposalReport
            {
                DraftFileName = Path.GetFileName(dialog.FileName),
                DraftSha256 = savedHash,
                FirmwareVersion = _source.FirmwareVersion,
                Target = _source.Target,
                BoardName = _source.BoardName,
                CliBaselineFileName = _source.SourceFileName,
                CliBaselineSha256 = _source.SourceSha256,
                SourceLogSha256 = _sourceLogSha256,
                TargetPidProfile = _targetPidProfile,
                TargetRateProfile = _targetRateProfile,
                ExportedAtUtc = DateTime.UtcNow,
                Changes = _changes.ToList()
            };
            MessageBox.Show(
                this,
                $"Der CLI-Entwurf wurde gespeichert und sein Inhalt geprüft. SHA-256: {savedHash[..12]}… Er wurde nicht an den Controller übertragen. Prüfe die Datei vor dem manuellen Einspielen und sichere vorher `dump all`.",
                "CLI-Entwurf gespeichert",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(
                this,
                $"Der CLI-Entwurf konnte nicht gespeichert werden.\n\n{ex.Message}",
                "Export fehlgeschlagen",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ProposalReviewRow(
        string ScopeLabel,
        string Name,
        string PreviousValue,
        string ProposedValue,
        string Reason,
        string Evidence,
        string ExpectedEffect,
        string Risk,
        string ValidationPlan);
}
