# Authentifizierung und Token-Handhabung

## Ergebnis

Im geprüften Stand 0.2.0-beta.18 existiert keine Anwendungs-Authentifizierung: kein Login, Benutzerkonto, OAuth/JWT, API-Key oder Session-/Refresh-Token. Die WPF-App verarbeitet lokale Dateien. Im eigenen C#/HTML-Code wurden keine HTTP-Clients, Netzwerk-Sockets oder Cloud-Aufrufe gefunden. Diese Aussage beruht auf statischer Prüfung; sie ist keine Laufzeit-Netzwerkanalyse oder vollständige Prüfung der mitgelieferten Decoder-Binärdatei.

## Komponenten und Ablauf

1. `MainWindow.xaml.cs:232` startet `BlackboxDecoder.DecodeAsync` nach Dateiauswahl oder Drag-and-drop.
2. `Services/BlackboxDecoder.cs:41` startet den lokalen `bbl_parser.exe` mit getrennten Argumenten (`UseShellExecute=false`). CSV-/Event-Dateien entstehen in einem zufälligen temporären Verzeichnis; der Prozess hat ein Zeitlimit von zwei Minuten. Das Verzeichnis wird im finally-Block gelöscht, wobei Fehler beim Löschen toleriert werden. Reste sensibler Flugdaten können daher zurückbleiben.
3. `MainWindow.xaml.cs:374` und `Services/LogLibrary.cs:15` speichern ausgewählte Logs unter Dokumente/FPV Blackbox Tuning Assistant/Logs und verwalten den lokalen JSON-Katalog.
4. `Services/AnalysisReportStore.cs:40` speichert Auswertungen als unverschlüsseltes JSON neben dem Log. Drohnenname, Dateiname, Firmware, Board, Messwerte und Nutzerantworten sind lokale Daten. Der Zugriff hängt von Windows-Dateiberechtigungen und gegebenenfalls der Synchronisierung des Dokumente-Ordners ab; die App implementiert keine eigene Zugriffskontrolle oder Verschlüsselung.
5. `Services/BetaflightCliConfiguration.cs:91` liest CLI-Snapshots als Text, führt keine Befehle aus und behält nur erlaubte PID-/Filter-/Rates-Einstellungen plus Metadaten. Andere Konfigurationsbereiche werden verworfen.
6. `Services/ProposalReviewGate.cs:6` verlangt das Öffnen beider Review-Ansichten und eine Bestätigung vor dem CLI-Export. Dies ist eine Bedienfreigabe und kein Nachweis einer Nutzeridentität. Die App überträgt den Entwurf nicht selbst an den Flight Controller.

## Bedeutung von Tokens und Prüfsummen

`CancellationToken` und `timeout.Token` in `BlackboxDecoder` steuern Abbruch und Zeitlimit des lokalen Prozesses. Sie sind keine Zugangstokens.

SHA-256-Werte verknüpfen Logs, CLI-Basis und Exportbytes für Duplikaterkennung und Nachvollziehbarkeit. Sie sind weder Passwörter noch kryptografische Signaturen und beweisen nicht die Herkunft oder Berechtigung eines Nutzers. Wer Dateien und gespeicherte Hashwerte ändern kann, kann diese Verknüpfung manipulieren.

## GitHub-Zugang

Der GitHub-Zugang für die Projektübertragung gehört zur Entwicklungsumgebung bzw. zum verbundenen GitHub-Plugin, nicht zur App. Die Remote-URL enthält keine Zugangsdaten. Keine Tokens in Quellcode, Dokumentation oder Git-Konfiguration eintragen. Der bestehende private Repository-Zugriff wurde über das GitHub-Plugin geprüft.

## Umfang der Prüfung

Geprüft wurden sämtliche für den neuen Commit vorgesehenen Textdateien, die lokale Git-Dateiliste sowie der entpackte Decoder-Quellcode auf typische Secret-Muster. Keine entsprechenden Secret-Treffer. Persönliche Flugberichte, Logs, CSVs, CLI-Exporte, lokale Toolzustände und Releasepakete sind nicht Bestandteil des neuen Commits. Der Decoder wird mit Binary, Lizenz und dem vom Projekt referenzierten Quellcodearchiv übernommen. Seine Binary wurde nicht unabhängig aus dem Quellcode reproduziert.
