using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FPVBlackboxTuningAssistant.Services;

public enum BetaflightCliSnapshotKind
{
    Unknown,
    Diff,
    Dump
}

public enum BetaflightTuningScope
{
    PidProfile,
    RateProfile,
    Master
}

public sealed record BetaflightTuningSetting(
    string Name,
    string Value,
    BetaflightTuningScope Scope,
    int? ProfileIndex);

public sealed record BetaflightCliConfiguration(
    BetaflightCliSnapshotKind Kind,
    string FirmwareHeader,
    string FirmwareVersion,
    string Target,
    int? ActivePidProfile,
    int? ActiveRateProfile,
    IReadOnlyList<int> PidProfiles,
    IReadOnlyList<int> RateProfiles,
    IReadOnlyList<BetaflightTuningSetting> TuningSettings,
    int ParsedTuningSettingCount)
{
    public string BoardName { get; init; } = string.Empty;
    public string SourceFileName { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;

    public bool HasFullTuningBaseline => Kind == BetaflightCliSnapshotKind.Dump
        && PidProfiles.Count > 0
        && TuningSettings.Any(setting => setting.Scope == BetaflightTuningScope.PidProfile);

    public BetaflightTuningSetting? FindSetting(string name, BetaflightTuningScope scope, int? profileIndex = null) =>
        TuningSettings.LastOrDefault(setting =>
            setting.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && setting.Scope == scope
            && setting.ProfileIndex == profileIndex);
}

/// <summary>
/// Reads Betaflight CLI output as inert text. It never executes commands and only
/// retains recognized PID, filter, and rate settings; unrelated configuration
/// values such as receiver, VTX, failsafe, and motor mapping are discarded.
/// </summary>
public static class BetaflightCliConfigurationParser
{
    private static readonly Regex FirmwareHeaderPattern = new(
        @"^#\s*(?<header>Betaflight\s*/\s*(?<target>.+?)\s+(?<version>\d{4}\.\d+(?:\.\d+)?)(?:\s|$).*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ProfilePattern = new(
        @"^profile\s+(?<index>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RateProfilePattern = new(
        @"^rateprofile\s+(?<index>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SetPattern = new(
        @"^set\s+(?<name>[a-z][a-z0-9_]*)\s*=\s*(?<value>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PidParameterPattern = new(
        @"^(?:p|i|d|f|d_min|d_max|ff)_(?:roll|pitch|yaw)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FilterParameterPattern = new(
        @"^(?:gyro_(?:hardware_lpf|lpf[12]_[a-z0-9_]+|notch[12]_(?:hz|cutoff)|rpm_notch_[a-z0-9_]+)|dterm_(?:lpf[12]_[a-z0-9_]+|notch_(?:hz|cutoff))|dyn_notch_[a-z0-9_]+|rpm_filter_[a-z0-9_]+|gyro_rpm_[a-z0-9_]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RateParameterPattern = new(
        @"^(?:rates_type|(?:roll|pitch|yaw)_(?:rc_rate|srate|expo|rate_limit)|thr_(?:mid|expo)|throttle_limit_(?:type|percent)|tpa_(?:rate|breakpoint))$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SafeValuePattern = new(
        @"^(?:-?\d+(?:\.\d+)?(?:/\d+)?|[a-z][a-z0-9_+-]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static BetaflightCliConfiguration ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);

        return Parse(lines) with
        {
            SourceFileName = Path.GetFileName(path),
            SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
    }

    public static BetaflightCliConfiguration Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var kind = BetaflightCliSnapshotKind.Unknown;
        var firmwareHeader = string.Empty;
        var firmwareVersion = string.Empty;
        var target = string.Empty;
        var boardName = string.Empty;
        int? activePidProfile = null;
        int? activeRateProfile = null;
        int? currentPidProfile = null;
        int? currentRateProfile = null;
        var pidProfiles = new SortedSet<int>();
        var rateProfiles = new SortedSet<int>();
        var settings = new Dictionary<(string Name, BetaflightTuningScope Scope, int? Profile), BetaflightTuningSetting>();
        var parsedTuningSettingCount = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith('#'))
            {
                if (line.Equals("# diff all", StringComparison.OrdinalIgnoreCase))
                    kind = BetaflightCliSnapshotKind.Diff;
                else if (line.Equals("# dump all", StringComparison.OrdinalIgnoreCase))
                    kind = BetaflightCliSnapshotKind.Dump;

                var firmwareMatch = FirmwareHeaderPattern.Match(line);
                if (firmwareMatch.Success)
                {
                    firmwareHeader = firmwareMatch.Groups["header"].Value.Trim();
                    target = firmwareMatch.Groups["target"].Value.Trim();
                    firmwareVersion = firmwareMatch.Groups["version"].Value.Trim();
                }
                continue;
            }

            if (line.StartsWith("board_name ", StringComparison.OrdinalIgnoreCase))
            {
                boardName = line["board_name ".Length..].Trim();
                continue;
            }

            var profileMatch = ProfilePattern.Match(line);
            if (profileMatch.Success && int.TryParse(profileMatch.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pidIndex))
            {
                currentPidProfile = pidIndex;
                currentRateProfile = null;
                activePidProfile = pidIndex;
                pidProfiles.Add(pidIndex);
                continue;
            }

            var rateProfileMatch = RateProfilePattern.Match(line);
            if (rateProfileMatch.Success && int.TryParse(rateProfileMatch.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var rateIndex))
            {
                currentRateProfile = rateIndex;
                currentPidProfile = null;
                activeRateProfile = rateIndex;
                rateProfiles.Add(rateIndex);
                continue;
            }

            var setMatch = SetPattern.Match(line);
            if (!setMatch.Success)
                continue;

            var name = setMatch.Groups["name"].Value.ToLowerInvariant();
            var value = setMatch.Groups["value"].Value;
            if (!SafeValuePattern.IsMatch(value) || !TryGetScope(name, out var scope))
                continue;

            parsedTuningSettingCount++;
            var profile = scope switch
            {
                BetaflightTuningScope.PidProfile => currentPidProfile,
                BetaflightTuningScope.RateProfile => currentRateProfile,
                _ => null
            };
            if (scope == BetaflightTuningScope.PidProfile && profile is not null)
                pidProfiles.Add(profile.Value);
            if (scope == BetaflightTuningScope.RateProfile && profile is not null)
                rateProfiles.Add(profile.Value);

            settings[(name, scope, profile)] = new BetaflightTuningSetting(name, value, scope, profile);
        }

        return new BetaflightCliConfiguration(
            kind,
            firmwareHeader,
            firmwareVersion,
            target,
            activePidProfile,
            activeRateProfile,
            pidProfiles.ToArray(),
            rateProfiles.ToArray(),
            settings.Values.OrderBy(setting => setting.Scope).ThenBy(setting => setting.ProfileIndex).ThenBy(setting => setting.Name, StringComparer.Ordinal).ToArray(),
            parsedTuningSettingCount)
        {
            BoardName = boardName
        };
    }

    public static bool IsAllowedTuningParameter(string name) =>
        PidParameterPattern.IsMatch(name)
        || FilterParameterPattern.IsMatch(name)
        || RateParameterPattern.IsMatch(name);

    public static bool IsAllowedTuningParameter(string name, BetaflightTuningScope scope) => scope switch
    {
        BetaflightTuningScope.PidProfile => PidParameterPattern.IsMatch(name)
            || (name.StartsWith("dterm_", StringComparison.OrdinalIgnoreCase) && FilterParameterPattern.IsMatch(name)),
        BetaflightTuningScope.Master => FilterParameterPattern.IsMatch(name)
            && (name.StartsWith("gyro_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("dyn_notch_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("rpm_filter_", StringComparison.OrdinalIgnoreCase)),
        BetaflightTuningScope.RateProfile => RateParameterPattern.IsMatch(name),
        _ => false
    };

    public static string ExtractFirmwareVersion(string firmware)
    {
        var match = Regex.Match(firmware ?? string.Empty, @"\b(?<version>\d{4}\.\d+(?:\.\d+)?)\b", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["version"].Value : string.Empty;
    }

    public static bool BoardNamesMatch(string logBoard, string configurationBoard)
    {
        static string Normalize(string value) => new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        var logName = Normalize(logBoard ?? string.Empty);
        var configName = Normalize(configurationBoard ?? string.Empty);
        return logName.Length > 0
            && configName.Length > 0
            && (string.Equals(logName, configName, StringComparison.Ordinal)
                || logName.EndsWith(configName, StringComparison.Ordinal)
                || configName.EndsWith(logName, StringComparison.Ordinal));
    }

    private static bool TryGetScope(string name, out BetaflightTuningScope scope)
    {
        if (PidParameterPattern.IsMatch(name) || FilterParameterPattern.IsMatch(name))
        {
            scope = name.StartsWith("gyro_", StringComparison.Ordinal)
                || name.StartsWith("dyn_notch_", StringComparison.Ordinal)
                || name.StartsWith("rpm_filter_", StringComparison.Ordinal)
                || name.StartsWith("gyro_rpm_", StringComparison.Ordinal)
                ? BetaflightTuningScope.Master
                : BetaflightTuningScope.PidProfile;
            return true;
        }

        if (RateParameterPattern.IsMatch(name))
        {
            scope = BetaflightTuningScope.RateProfile;
            return true;
        }

        scope = default;
        return false;
    }
}

public sealed record BetaflightCliProposalChange(
    string Name,
    string PreviousValue,
    string ProposedValue,
    BetaflightTuningScope Scope,
    int? ProfileIndex,
    string Reason,
    string Evidence,
    string ExpectedEffect,
    string Risk,
    string ValidationPlan);

public static class BetaflightCliProposalExporter
{
    private static readonly Regex NumericValuePattern = new(
        @"^\d+(?:\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex DraftSetCommandPattern = new(
        @"^set\s+(?<name>[a-z][a-z0-9_]*)\s*=\s*(?<value>\d+(?:\.\d+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SaveCommandPattern = new(
        @"(?im)^[ \t]*save(?:[ \t][^\r\n]*)?[ \t]*\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string SaveVerifiedDraft(string filePath, string draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(draft);
        if (SaveCommandPattern.IsMatch(draft))
            throw new InvalidOperationException("Der CLI-Entwurf darf keinen `save`-Befehl enthalten.");

        var expectedBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(draft);
        var expectedHash = Convert.ToHexString(SHA256.HashData(expectedBytes));
        var fullPath = Path.GetFullPath(filePath);
        File.WriteAllBytes(fullPath, expectedBytes);

        var savedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        if (!string.Equals(expectedHash, savedHash, StringComparison.Ordinal))
            throw new IOException("Die gespeicherte Datei stimmt nicht exakt mit dem geprüften CLI-Entwurf überein.");

        return savedHash;
    }

    public static string CreateDraft(
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

        if (source.Kind != BetaflightCliSnapshotKind.Dump)
            throw new InvalidOperationException("Für einen rückprüfbaren Entwurf wird `dump all` mit den vollständigen Profilwerten benötigt.");
        if (string.IsNullOrWhiteSpace(source.SourceFileName) || !IsSha256(source.SourceSha256))
            throw new InvalidOperationException("Die CLI-Ausgangsdatei ist nicht mit Dateiname und SHA-256 identifiziert. Lade die `dump all`-Datei erneut über den Import.");
        if (!IsSha256(sourceLogSha256))
            throw new InvalidOperationException("Der Quell-Log ist nicht mit einem gültigen SHA-256 im Auswertungsbericht identifiziert.");
        if (!sameFlightControllerConfirmed)
            throw new InvalidOperationException("Bestätige zuerst, dass `dump all` vom selben Flight Controller stammt.");
        if (!activeProfilesConfirmed)
            throw new InvalidOperationException("Bestätige zuerst, dass PID- und Rates-Profil im Log aktiv waren.");
        if (string.IsNullOrWhiteSpace(source.FirmwareVersion)
            || !string.Equals(source.FirmwareVersion, BetaflightCliConfigurationParser.ExtractFirmwareVersion(logFirmware), StringComparison.Ordinal))
            throw new InvalidOperationException("Die CLI-Konfiguration und der Blackbox-Log haben keine übereinstimmende Betaflight-Version.");
        if (!string.IsNullOrWhiteSpace(logBoard)
            && !string.IsNullOrWhiteSpace(source.BoardName)
            && !BetaflightCliConfigurationParser.BoardNamesMatch(logBoard, source.BoardName))
            throw new InvalidOperationException("Der Boardname der CLI-Konfiguration passt nicht zum Blackbox-Log.");
        if (!source.PidProfiles.Contains(targetPidProfile))
            throw new InvalidOperationException($"PID-Profil {targetPidProfile} ist in der importierten FC-Konfiguration nicht vorhanden.");
        if (!source.RateProfiles.Contains(targetRateProfile))
            throw new InvalidOperationException($"Rates-Profil {targetRateProfile} ist in der importierten FC-Konfiguration nicht vorhanden.");
        if (changes.Count == 0)
            throw new InvalidOperationException("Es gibt keine freigegebenen Änderungen für den CLI-Entwurf.");

        var normalizedChanges = changes
            .OrderBy(change => change.Scope)
            .ThenBy(change => change.ProfileIndex)
            .ThenBy(change => change.Name, StringComparer.Ordinal)
            .ToArray();
        if (normalizedChanges
            .Select(change => (change.Scope, change.ProfileIndex, Name: change.Name.ToLowerInvariant()))
            .Distinct()
            .Count() != normalizedChanges.Length)
            throw new InvalidOperationException("Der Entwurf enthält denselben Parameter mehrfach.");
        foreach (var change in normalizedChanges)
        {
            var validScope = change.Scope switch
            {
                BetaflightTuningScope.PidProfile => change.ProfileIndex == targetPidProfile,
                BetaflightTuningScope.Master => change.ProfileIndex is null,
                BetaflightTuningScope.RateProfile => change.ProfileIndex == targetRateProfile,
                _ => false
            };
            if (!validScope
                || !BetaflightCliConfigurationParser.IsAllowedTuningParameter(change.Name, change.Scope)
                || !NumericValuePattern.IsMatch(change.ProposedValue)
                || string.IsNullOrWhiteSpace(change.Reason)
                || string.IsNullOrWhiteSpace(change.Evidence)
                || string.IsNullOrWhiteSpace(change.ExpectedEffect)
                || string.IsNullOrWhiteSpace(change.Risk)
                || string.IsNullOrWhiteSpace(change.ValidationPlan)
                || !IsConservativeNumericChange(change))
                throw new InvalidOperationException("Der Entwurf enthält eine nicht freigegebene oder nicht prüfbare Änderung.");

            var baseline = source.FindSetting(change.Name, change.Scope, change.ProfileIndex);
            if (baseline is null || !string.Equals(baseline.Value, change.PreviousValue, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Der Ausgangswert für `{change.Name}` stimmt nicht mit `dump all` überein.");
        }

        ValidateDTermBounds(source, targetPidProfile, normalizedChanges);

        var builder = new StringBuilder();
        builder.AppendLine("# FPV Blackbox Tuning Assistant – überprüfbarer Tuning-Entwurf");
        builder.AppendLine($"# Betaflight-Version: {source.FirmwareVersion}");
        builder.AppendLine($"# FC-Board: {source.BoardName}");
        builder.AppendLine($"# Quell-CLI-Datei: {Path.GetFileName(source.SourceFileName)}");
        builder.AppendLine($"# Quell-CLI-SHA-256: {source.SourceSha256}");
        builder.AppendLine($"# Quell-Log: {Path.GetFileName(sourceLogName)}");
        builder.AppendLine($"# Quell-Log-SHA-256: {sourceLogSha256}");
        builder.AppendLine($"# Ziel-PID-Profil: {targetPidProfile}");
        builder.AppendLine($"# Ziel-Rates-Profil: {targetRateProfile}");
        builder.AppendLine("# Vorher `dump all` sichern. Werte einzeln prüfen; dieser Entwurf führt kein `save` aus.");
        builder.AppendLine("# Die Änderungen werden erst nach manueller Prüfung im Betaflight CLI mit `save` übernommen.");
        builder.AppendLine("batch start");
        foreach (var change in normalizedChanges.Where(change => change.Scope == BetaflightTuningScope.Master))
        {
            builder.AppendLine($"# vorher: set {change.Name} = {change.PreviousValue}");
            builder.AppendLine($"# warum: {SanitizeComment(change.Reason)}");
            builder.AppendLine($"# messbeleg: {SanitizeComment(change.Evidence)}");
            builder.AppendLine($"# erwartete Wirkung: {SanitizeComment(change.ExpectedEffect)}");
            builder.AppendLine($"# Risiko: {SanitizeComment(change.Risk)}");
            builder.AppendLine($"# Nachflug-Pruefung: {SanitizeComment(change.ValidationPlan)}");
            builder.AppendLine($"set {change.Name} = {change.ProposedValue}");
        }
        if (normalizedChanges.Any(change => change.Scope == BetaflightTuningScope.PidProfile))
        {
            builder.AppendLine($"profile {targetPidProfile}");
            foreach (var change in normalizedChanges.Where(change => change.Scope == BetaflightTuningScope.PidProfile))
            {
                builder.AppendLine($"# vorher: set {change.Name} = {change.PreviousValue}");
                builder.AppendLine($"# warum: {SanitizeComment(change.Reason)}");
                builder.AppendLine($"# messbeleg: {SanitizeComment(change.Evidence)}");
                builder.AppendLine($"# erwartete Wirkung: {SanitizeComment(change.ExpectedEffect)}");
                builder.AppendLine($"# Risiko: {SanitizeComment(change.Risk)}");
                builder.AppendLine($"# Nachflug-Pruefung: {SanitizeComment(change.ValidationPlan)}");
                builder.AppendLine($"set {change.Name} = {change.ProposedValue}");
            }
        }
        if (normalizedChanges.Any(change => change.Scope == BetaflightTuningScope.RateProfile))
        {
            builder.AppendLine($"rateprofile {targetRateProfile}");
            foreach (var change in normalizedChanges.Where(change => change.Scope == BetaflightTuningScope.RateProfile))
            {
                builder.AppendLine($"# vorher: set {change.Name} = {change.PreviousValue}");
                builder.AppendLine($"# warum: {SanitizeComment(change.Reason)}");
                builder.AppendLine($"# messbeleg: {SanitizeComment(change.Evidence)}");
                builder.AppendLine($"# erwartete Wirkung: {SanitizeComment(change.ExpectedEffect)}");
                builder.AppendLine($"# Risiko: {SanitizeComment(change.Risk)}");
                builder.AppendLine($"# Nachflug-Pruefung: {SanitizeComment(change.ValidationPlan)}");
                builder.AppendLine($"set {change.Name} = {change.ProposedValue}");
            }
        }
        builder.AppendLine("batch end");
        var draft = builder.ToString();
        ValidateGeneratedDraft(draft, normalizedChanges, targetPidProfile, targetRateProfile);
        return draft;
    }

    private static void ValidateGeneratedDraft(
        string draft,
        IReadOnlyList<BetaflightCliProposalChange> changes,
        int targetPidProfile,
        int targetRateProfile)
    {
        var expected = changes.ToDictionary(
            change => (change.Scope, change.ProfileIndex, Name: change.Name.ToLowerInvariant()),
            change => change.ProposedValue,
            EqualityComparer<(BetaflightTuningScope Scope, int? ProfileIndex, string Name)>.Default);
        var emitted = new HashSet<(BetaflightTuningScope Scope, int? ProfileIndex, string Name)>();
        var batchState = 0;
        int? selectedPidProfile = null;
        int? selectedRateProfile = null;

        foreach (var rawLine in draft.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.Equals("batch start", StringComparison.OrdinalIgnoreCase))
            {
                if (batchState != 0)
                    throw new InvalidOperationException("Der CLI-Entwurf enthält eine ungültige Batch-Struktur.");
                batchState = 1;
                continue;
            }

            if (line.Equals("batch end", StringComparison.OrdinalIgnoreCase))
            {
                if (batchState != 1)
                    throw new InvalidOperationException("Der CLI-Entwurf enthält eine ungültige Batch-Struktur.");
                batchState = 2;
                continue;
            }

            var pidProfileMatch = Regex.Match(line, @"^profile\s+(?<index>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (pidProfileMatch.Success)
            {
                if (batchState != 1
                    || !int.TryParse(pidProfileMatch.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index != targetPidProfile)
                    throw new InvalidOperationException("Der CLI-Entwurf wählt ein nicht freigegebenes PID-Profil.");
                selectedPidProfile = index;
                selectedRateProfile = null;
                continue;
            }

            var rateProfileMatch = Regex.Match(line, @"^rateprofile\s+(?<index>\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (rateProfileMatch.Success)
            {
                if (batchState != 1
                    || !int.TryParse(rateProfileMatch.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index != targetRateProfile)
                    throw new InvalidOperationException("Der CLI-Entwurf wählt ein nicht freigegebenes Rates-Profil.");
                selectedRateProfile = index;
                selectedPidProfile = null;
                continue;
            }

            var setMatch = DraftSetCommandPattern.Match(line);
            if (batchState != 1 || !setMatch.Success)
                throw new InvalidOperationException("Der CLI-Entwurf enthält einen nicht freigegebenen CLI-Befehl.");

            var scope = selectedPidProfile is not null
                ? BetaflightTuningScope.PidProfile
                : selectedRateProfile is not null
                    ? BetaflightTuningScope.RateProfile
                    : BetaflightTuningScope.Master;
            var profile = scope switch
            {
                BetaflightTuningScope.PidProfile => selectedPidProfile,
                BetaflightTuningScope.RateProfile => selectedRateProfile,
                _ => null
            };
            var key = (scope, profile, setMatch.Groups["name"].Value.ToLowerInvariant());
            if (!expected.TryGetValue(key, out var expectedValue)
                || !string.Equals(setMatch.Groups["value"].Value, expectedValue, StringComparison.Ordinal)
                || !emitted.Add(key))
                throw new InvalidOperationException("Die erzeugten `set`-Befehle stimmen nicht exakt mit den geprüften Änderungen überein.");
        }

        if (batchState != 2 || emitted.Count != expected.Count)
            throw new InvalidOperationException("Der CLI-Entwurf enthält nicht alle geprüften Änderungen oder die Batch-Struktur ist unvollständig.");
    }

    private static void ValidateDTermBounds(
        BetaflightCliConfiguration source,
        int targetPidProfile,
        IReadOnlyList<BetaflightCliProposalChange> changes)
    {
        foreach (var axis in new[] { "roll", "pitch" })
        {
            if (!BetaflightPidSemantics.TryGetDTermParameters(source.FirmwareVersion, axis, out var pair))
            {
                if (changes.Any(change => change.Scope == BetaflightTuningScope.PidProfile
                    && change.ProfileIndex == targetPidProfile
                    && (change.Name.Equals($"d_{axis}", StringComparison.OrdinalIgnoreCase)
                        || change.Name.Equals($"d_min_{axis}", StringComparison.OrdinalIgnoreCase)
                        || change.Name.Equals($"d_max_{axis}", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException($"Die D-Term-Variablen von Betaflight {source.FirmwareVersion} können nicht sicher auf ihre Profilgrenzen geprüft werden.");

                continue;
            }

            var baseChange = changes.LastOrDefault(change => change.Scope == BetaflightTuningScope.PidProfile
                && change.ProfileIndex == targetPidProfile
                && change.Name.Equals(pair.BaseParameter, StringComparison.OrdinalIgnoreCase));
            var maximumParameterForChanges = pair.MaximumParameter;
            var hasRelevantChange = baseChange is not null
                || maximumParameterForChanges is not null && changes.Any(change => change.Scope == BetaflightTuningScope.PidProfile
                    && change.ProfileIndex == targetPidProfile
                    && change.Name.Equals(maximumParameterForChanges, StringComparison.OrdinalIgnoreCase));
            if (!hasRelevantChange)
                continue;

            var baseValueText = baseChange?.ProposedValue
                ?? source.FindSetting(pair.BaseParameter, BetaflightTuningScope.PidProfile, targetPidProfile)?.Value;
            if (baseValueText is null
                || !decimal.TryParse(baseValueText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var baseValue))
                throw new InvalidOperationException($"Der D-Term-Basiswert `{pair.BaseParameter}` lässt sich nicht gegen die Profilgrenze prüfen.");

            if (pair.MaximumParameter is not { } maximumParameter)
                continue;

            var maximumChange = changes.LastOrDefault(change => change.Scope == BetaflightTuningScope.PidProfile
                && change.ProfileIndex == targetPidProfile
                && change.Name.Equals(maximumParameter, StringComparison.OrdinalIgnoreCase));
            var maximumValueText = maximumChange?.ProposedValue
                ?? source.FindSetting(maximumParameter, BetaflightTuningScope.PidProfile, targetPidProfile)?.Value;
            if (maximumValueText is null
                || !decimal.TryParse(maximumValueText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var maximumValue)
                || baseValue > maximumValue)
                throw new InvalidOperationException($"Der D-Term-Basiswert `{pair.BaseParameter}` darf den D-Max `{maximumParameter}` nicht überschreiten.");
        }
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsConservativeNumericChange(BetaflightCliProposalChange change)
    {
        if (!decimal.TryParse(change.PreviousValue, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var previous)
            || !decimal.TryParse(change.ProposedValue, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var proposed)
            || previous < 0
            || proposed < 0
            || previous == proposed)
            return false;

        var maximumStep = Math.Max(Math.Abs(previous) * 0.10m, 1m);
        return Math.Abs(proposed - previous) <= maximumStep;
    }

    private static string SanitizeComment(string value) =>
        new string(value.Where(character => character is '\r' or '\n' ? character == ' ' : !char.IsControl(character)).ToArray()).Trim();
}
