# Gemeinsamer Flugdaten-Speicher über OneDrive

Stand: 10.10.2026. Der Nutzer hat seinen bestehenden persönlichen OneDrive-Speicher als vorläufigen gemeinsamen Speicher für das Projekt gewählt.

## Aktive Bibliothek

Die App verwendet den von Windows konfigurierten Dokumente-Ordner und darin `FPV Blackbox Tuning Assistant`. Auf dem eingerichteten Rechner liegt dieser Ordner bereits unter `OneDrive/Dokumente/`.

- `Logs/`: importierte BBL-Dateien und zugehörige `.analysis.json`-Berichte, nach Drohne getrennt.
- `log-library.json`: Katalog der aktiven App-Bibliothek.
- `Projektarchiv/2026-10-10/`: geprüfte Kopie des ersten vollständigen Projekt-Datenbestands aus dem Repository, einschließlich der zusätzlichen DS16-Logs und komprimierten Decoder-Auswertungen. Diese Archivkopie wird nicht automatisch in den App-Katalog importiert.

Neue in der App importierte Logs werden damit bereits innerhalb des OneDrive-Ordners gespeichert. Die Übertragung in die Cloud übernimmt der OneDrive-Client mit der vorhandenen Microsoft-Anmeldung. Die App speichert dafür keine Microsoft-Zugangstokens. Ein lokaler OneDrive-Pfad allein bestätigt noch keinen erfolgreichen Upload.

## Auf den weiteren Geräten

1. OneDrive mit demselben Microsoft-Konto anmelden und den Dokumente-Ordner synchronisieren. Die App richtet sich nach dem Windows-Dokumente-Ordner; der Name eines beliebigen OneDrive-Ordners allein ändert diesen Pfad nicht.
2. Prüfen, dass dort `FPV Blackbox Tuning Assistant` mit `Logs/` und `log-library.json` ankommt. Den Ordner im Explorer als „Immer auf diesem Gerät behalten“ markieren, wenn die App auch offline arbeiten soll.
3. Vor dem Gerätewechsel die App schließen und OneDrive vollständig synchronisieren lassen. Auf dem nächsten Gerät erst nach abgeschlossener Synchronisierung weiterarbeiten.

Der aktuelle Katalog ist eine JSON-Datei. Die App besitzt keine geräteübergreifende Sperre oder Zusammenführung konkurrierender Änderungen. Deshalb nicht gleichzeitig auf mehreren Geräten Logs importieren, Berichte bearbeiten oder Tuning-Bewertungen speichern.

OneDrive synchronisiert auch Änderungen und Löschungen. Die GitHub-Datenstände bleiben zusätzliche nachvollziehbare Sicherungspunkte. Neue für die Projektentwicklung verwendete Logs können weiterhin nach Prüfung unter `flight-data/` aufgenommen und mit einem Commit gesichert werden.

## Kontrolle

Im OneDrive-Menü sollte nach dem Upload ein abgeschlossener Synchronisierungsstatus erscheinen. Den Ordner zusätzlich auf der OneDrive-Webseite oder einem zweiten Gerät öffnen und mindestens eine neue Datei prüfen. Erst dann ist die Cloud-Sicherung dieses Datenstands bestätigt.

Microsoft-Anleitung: https://support.microsoft.com/en-us/onedrive/sync-your-computer-s-files-and-folders-with-onedrive
