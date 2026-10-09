# Flugdaten für das private Projekt

Der Nutzer hat am 10.10.2026 ausdrücklich freigegeben, bisherige und weitere projektbezogene Fluglogs und Auswertungen in diesem privaten Repository zu sichern.

## Bestand vom 10.10.2026

- `library/`: vollständiger damaliger Stand der lokalen App-Bibliothek mit drei BBL-Dateien, drei JSON-Auswertungen und `log-library.json`. Die ursprüngliche Ordnerstruktur ist erhalten.
- `raw/`: zwei zusätzliche DS16-Logs vom 07.10.2026, darunter der Chirp-Flug. Insgesamt sind fünf unterschiedliche BBL-Dateien gespeichert.
- `reports/`: vier unterschiedliche Markdown-Auswertungen, einschließlich beider vorhandener Fassungen zum Flug vom 29.09.
- `archives/2026-09-25-decoded.zip`: zwölf vorhandene CSV-, Header- und Event-Dateien. Die ursprüngliche BBL dieses Flugs wurde in den geprüften Projekt- und Bibliotheksordnern nicht gefunden.
- `archives/2026-10-07-DS16-decoded.zip`: vollständige vorhandene DS16-Auswertung mit fünf Sessions und insgesamt 15 Dateien.
- `manifest.json`: SHA-256-Prüfsummen der gesicherten Dateien und sämtlicher Archiveinträge sowie Zuordnung abweichender Originalnamen zu identischen Logs. Dies dokumentiert den Erstbestand; bei weiteren Sicherungen aktualisieren.

Alle Kopien und Archiveinträge wurden gegen ihre lokalen Quellen geprüft. Identische BBL-Kopien und das zusätzlich vorhandene DS16-ZIP mit denselben zwei BBL-Dateien werden nicht doppelt gespeichert. CSVs innerhalb der Archive lassen sich nach dem Klonen wieder entpacken. Die Berichte sind historische Arbeitsstände und können überholte Einschätzungen enthalten.

## Weitere Logs sichern

Neue Logs und zugehörige Berichte gehören unter `flight-data/`, beispielsweise in `raw/2026-10-11/`. Die Git-Ausschlüsse lassen dort BBL/BFL, CSV/Event, Analyse-JSON und CLI-Dateien ausdrücklich zu. Umfangreiche Decoder-Ausgaben können als ZIP unter `archives/` abgelegt werden. Vor dem Commit Größen und Dubletten prüfen und den Bestand im Manifest ergänzen.

Die App speichert weiterhin in ihrem Windows-Dokumente-Ordner. Dateien von dort müssen in diesen Repository-Ordner übernommen werden; anschließend werden Änderungen mit Commit und Push gesichert. Es läuft keine automatische Hintergrundsynchronisierung. Auf weiteren Geräten das Repository klonen beziehungsweise vor der Arbeit aktualisieren.

Die App-Bibliothek lässt sich aus `library/` wiederherstellen. Beim Zusammenführen mit einer bestehenden Bibliothek deren Katalog und Dateien zuerst sichern; `log-library.json` nicht ungeprüft überschreiben.

Passwörter, API-Schlüssel und sonstige Anmeldedaten bleiben außerhalb der Flugdatenablage.
