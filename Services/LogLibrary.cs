using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace FPVBlackboxTuningAssistant.Services;

public sealed class LogLibrary
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _libraryRoot;
    private readonly string _logsRoot;
    private readonly string _catalogPath;

    public LogLibrary()
    {
        _libraryRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "FPV Blackbox Tuning Assistant");
        _logsRoot = Path.Combine(_libraryRoot, "Logs");
        _catalogPath = Path.Combine(_libraryRoot, "log-library.json");
    }

    public string StorageDescription => _libraryRoot;

    public IReadOnlyList<string> GetDroneNames()
    {
        lock (Sync)
            return LoadCatalog().Drones
                .Select(drone => drone.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
    }

    public IReadOnlyList<StoredFlightAnalysis> GetStoredAnalyses()
    {
        lock (Sync)
        {
            var catalog = LoadCatalog();
            var droneNames = catalog.Drones.ToDictionary(drone => drone.Id, drone => drone.Name, StringComparer.Ordinal);
            var analyses = new List<StoredFlightAnalysis>();
            foreach (var log in catalog.Logs)
            {
                var blackboxPath = Path.GetFullPath(Path.Combine(_libraryRoot, log.RelativePath));
                if (!File.Exists(blackboxPath))
                    continue;

                var reportPath = AnalysisReportStore.GetReportPath(blackboxPath);
                LogAnalysisReport? report;
                try
                {
                    report = AnalysisReportStore.Load(reportPath);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (report is null)
                    continue;

                analyses.Add(new StoredFlightAnalysis(
                    log.Id,
                    droneNames.GetValueOrDefault(log.DroneId, report.DroneName),
                    blackboxPath,
                    reportPath,
                    log.ImportedUtc,
                    report));
            }

            return analyses
                .OrderByDescending(analysis => analysis.Report.AnalyzedAtUtc)
                .ThenBy(analysis => analysis.DroneName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public LogStorageResult SaveLog(string sourcePath, string droneName, string requestedFileName)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Die ausgewählte Blackbox-Datei wurde nicht gefunden.", sourcePath);

        var normalizedName = droneName.Trim();
        if (normalizedName.Length == 0)
            throw new ArgumentException("Bitte gib einen Namen für die Drohne ein.", nameof(droneName));

        var storedFileName = MakeBlackboxFileName(requestedFileName);

        lock (Sync)
        {
            var catalog = LoadCatalog();
            var drone = catalog.Drones.FirstOrDefault(existing =>
                string.Equals(existing.Name, normalizedName, StringComparison.OrdinalIgnoreCase));

            if (drone is null)
            {
                drone = new DroneProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = normalizedName
                };
                catalog.Drones.Add(drone);
            }

            Directory.CreateDirectory(_logsRoot);
            var hash = ComputeSha256(sourcePath);
            var existingLog = catalog.Logs.FirstOrDefault(log =>
                log.DroneId == drone.Id && string.Equals(log.Sha256, hash, StringComparison.OrdinalIgnoreCase));
            if (existingLog is not null)
            {
                var existingPath = Path.GetFullPath(Path.Combine(_libraryRoot, existingLog.RelativePath));
                if (File.Exists(existingPath))
                    return new LogStorageResult(drone.Name, existingPath, true, _libraryRoot, existingLog.OriginalFileName, existingLog.Sha256);

                catalog.Logs.Remove(existingLog);
            }

            var folderName = $"{MakeSafeSegment(drone.Name, 48)}--{drone.Id[..8]}";
            var droneFolder = Path.Combine(_logsRoot, folderName);
            Directory.CreateDirectory(droneFolder);

            var originalName = Path.GetFileName(sourcePath);
            var destination = Path.Combine(droneFolder, storedFileName);
            if (File.Exists(destination))
                throw new LogNameConflictException(
                    $"Unter dem Namen \"{storedFileName}\" ist bei dieser Drohne bereits ein anderer Log gespeichert. Bitte wähle einen anderen Dateinamen.");

            File.Copy(sourcePath, destination, overwrite: false);

            try
            {
                catalog.Logs.Add(new StoredBlackboxLog
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DroneId = drone.Id,
                    OriginalFileName = originalName,
                    RelativePath = Path.GetRelativePath(_libraryRoot, destination),
                    Sha256 = hash,
                    ImportedUtc = DateTime.UtcNow
                });
                SaveCatalog(catalog);
            }
            catch
            {
                try { File.Delete(destination); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }

            return new LogStorageResult(drone.Name, destination, false, _libraryRoot, originalName, hash);
        }
    }

    private LogLibraryCatalog LoadCatalog()
    {
        if (!File.Exists(_catalogPath))
            return new LogLibraryCatalog();

        try
        {
            var json = File.ReadAllText(_catalogPath);
            return JsonSerializer.Deserialize<LogLibraryCatalog>(json) ?? new LogLibraryCatalog();
        }
        catch (JsonException exception)
        {
            throw new IOException(
                $"Die lokale Log-Bibliothek ist beschädigt und konnte nicht gelesen werden: {_catalogPath}",
                exception);
        }
    }

    private void SaveCatalog(LogLibraryCatalog catalog)
    {
        Directory.CreateDirectory(_libraryRoot);
        var temporaryPath = _catalogPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(catalog, JsonOptions));
        File.Move(temporaryPath, _catalogPath, overwrite: true);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string MakeSafeSegment(string value, int maximumLength)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value
            .Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        if (safe.Length == 0)
            safe = "Drohne";

        return safe.Length <= maximumLength ? safe : safe[..maximumLength].TrimEnd(' ', '.');
    }

    private static string MakeSafeFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var safeBase = MakeSafeSegment(baseName, 100);
        var safeExtension = MakeSafeSegment(extension, 16);
        return safeBase + safeExtension;
    }

    private static string MakeBlackboxFileName(string requestedFileName)
    {
        var enteredName = requestedFileName.Trim();
        if (enteredName.EndsWith(".BBL", StringComparison.OrdinalIgnoreCase))
            enteredName = enteredName[..^4];

        var safeBase = MakeSafeSegment(enteredName, 100);
        return safeBase + ".BBL";
    }
}

public sealed class LogLibraryCatalog
{
    public int Version { get; set; } = 1;
    public List<DroneProfile> Drones { get; set; } = [];
    public List<StoredBlackboxLog> Logs { get; set; } = [];
}

public sealed class DroneProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class StoredBlackboxLog
{
    public string Id { get; set; } = string.Empty;
    public string DroneId { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTime ImportedUtc { get; set; }
}

public sealed record LogStorageResult(
    string DroneName,
    string StoredPath,
    bool AlreadyStored,
    string LibraryPath,
    string OriginalFileName,
    string Sha256);

public sealed record StoredFlightAnalysis(
    string LogId,
    string DroneName,
    string BlackboxPath,
    string ReportPath,
    DateTime ImportedUtc,
    LogAnalysisReport Report)
{
    public string DisplayLabel => $"{DroneName} · {Report.StoredBlackboxFileName} · {Report.AnalyzedAtUtc.ToLocalTime():g}";
}

public sealed class LogNameConflictException(string message) : IOException(message);
