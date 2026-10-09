# Projektarbeit und Flugdaten

Das private Zielrepository ist `rjelesits/fpv-blackbox-tuning-assistant`, Hauptbranch `main`.

Seit 10.10.2026 ist der vorhandene persönliche OneDrive-Speicher der primäre gemeinsame Flugdaten-Speicher. Die App-Bibliothek liegt unter dem Windows-Dokumente-Ordner in `FPV Blackbox Tuning Assistant`; auf dem eingerichteten Rechner befindet sich dieser bereits in OneDrive. Anleitung und Grenzen stehen in `docs/ONEDRIVE-STORAGE.md`. Keine Microsoft-Tokens in die App einbauen und keine weitere Cloud-Infrastruktur ohne entsprechenden Auftrag einrichten. Den Uploadstatus separat von der lokalen Ablage prüfen.

Der Nutzer hat ausdrücklich genehmigt, projektbezogene Fluglogs und Auswertungen einschließlich persönlicher Flugdaten im Repository zu sichern. Wenn im Rahmen einer Aufgabe weitere solche Dateien hinzukommen, übernimm sie nach Prüfung unter `flight-data/` und berücksichtige sie im zugehörigen Commit/Push. Identische Kopien anhand SHA-256 zusammenführen; zugehörige Originalnamen und Berichte erhalten. Für große Decoder-Ausgaben die Archivstruktur und Hinweise in `flight-data/README.md` verwenden. Anmeldedaten und API-Schlüssel bleiben ausgeschlossen.

Vor einem Push den Remote-Stand prüfen und fremde Änderungen erhalten. Ein erfolgreich vorbereiteter lokaler Commit ist noch keine GitHub-Sicherung: Den tatsächlichen Upload verifizieren und Hindernisse klar benennen. Keine automatische Hintergrundsicherung behaupten.
