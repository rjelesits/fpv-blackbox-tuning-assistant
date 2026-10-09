# FPV Blackbox Tuning Assistant

## Aktueller Stand

### Beta 0.2.0-beta.18

- Korrigiert den Hinweis nach unauffälligen Nutzerprüfungen: Er meldet keine groben Auffälligkeiten, ohne Messabweichungen als Defekt zu bezeichnen oder fälschlich einen offenen Hardwarehinweis zu nennen.
- Zeigt in App, Voraussetzungen und portablem Paket dieselbe Beta-Version an.

### Beta 0.2.0-beta.17

- Erfasst zusätzlich getrennte Roll-/Pitch-Manöver nahe dem Stickanschlag (mindestens 90 % Stick).
- Ein ACTUAL-Maximalraten-Vorschlag erfordert jetzt drei solche Acro-Manöver je ausgewählter Achse. Mittlere Ausschläge genügen nicht mehr, um einen Vorschlag zur maximalen Rate freizugeben.
- Der geprüfte CLI-Entwurf wird exakt als UTF-8-Datei gespeichert, per SHA-256 aus den gespeicherten Bytes geprüft und vor dem Schreiben gegen `save`-Befehle geschützt.
- Der Export wird erst nach Ansicht von Änderungsdetails und CLI-Entwurf sowie ausdrücklicher Bestätigung freigegeben.

### Beta 0.2.0-beta.14

- Verknüpft den CLI-Entwurf mit SHA-256-Prüfsummen der importierten `dump all`-Datei und des Quell-Logs. Beide Kennungen stehen auch im CLI-Text und im gespeicherten Flugbericht.
- Der Flugvergleich prüft zusätzlich, ob der Export auf genau dieselbe CLI-Ausgangsdatei wie Flug A verweist.
- Meldet ausdrücklich, wenn die CLI-Datei zwar gespeichert, ihre Verknüpfung im Auswertungsbericht aber nicht gesichert werden konnte.

### Beta 0.2.0-beta.13

- Ergänzt einen eng begrenzten ACTUAL-Rates-Vorschlag zum Senken der Maximalrate auf ausgewählten Roll-/Pitch-Achsen.
- Berechnet einen höchstens 5-%-Schritt nur mit passender `dump all`-Basis, bestätigtem Flight Controller und aktivem Profil, abgeschlossener Hardwareprüfung und mindestens drei deutlichen Acro-Manövern je gewählter Achse.
- Belegt den Vorschlag mit Lograte, Setpoint bei hohem Stick und den tatsächlich wirksamen ACTUAL-Rates-Parametern. Die App ändert nichts automatisch am Flight Controller.

### Beta 0.2.0-beta.12

- Zeigt im Flugvergleich die letzte gespeicherte Tuning-Bewertung von Flug A einschließlich Eingaben, FC-/Profilbestätigungen, CLI-Basis, Blockiergrund und Messbelegen.
- Speichert, ob derselbe Flight Controller und die aktiven Profile bestätigt wurden, sowie den Typ der eingelesenen CLI-Datei.
- Die bisherige Empfehlung bleibt auf den belegten Acro-Propwash-Fall begrenzt; andere Rate- und Filteränderungen erhalten ohne passende, validierte Auswertung keinen Zahlenvorschlag.

### Beta 0.2.0-beta.11

- Speichert jede durchgeführte Tuning-Bewertung im Flugbericht – auch wenn Daten, Nutzerfreigabe oder Konfigurationsbasis einen Vorschlag blockieren.
- Hält Eingaben, Blockiergrund, Firmware/Board/Profile sowie Messbelege und vorgeschlagene Änderungen als nachvollziehbare Bewertung fest.
- Verknüpft den exportierten CLI-Entwurf mit genau dieser gespeicherten Bewertung. Fehlt oder weicht die Verknüpfung später ab, wird der exakte Nachflugabgleich gesperrt.

### Beta 0.2.0-beta.10

- Verknüpft einen exportierten CLI-Entwurf über den SHA-256 des Ausgangslogs und des Entwurfs mit dessen späterem Flugvergleich.
- Prüft für jeden vorgeschlagenen Wert, ob der bestätigte Nachflug-`dump all` den Vorschlagswert, den Vorherwert oder einen anderen Wert enthält.
- Sperrt den exakten Abgleich bei abweichender Firmware, CLI-Ziel, Board oder Zielprofil; ein Snapshot belegt den gespeicherten Wert, aber nicht den Übertragungsweg.

### Beta 0.2.0-beta.9

- Vergleicht nun importierte `dump all`-Snapshots A/B und zeigt abweichende, gemeinsam erkannte PID-, Filter- und Rates-Werte.
- Sperrt den CLI-Konfigurationsvergleich bei nicht passenden Drohnenprofilen, Firmware-/Board-/Zielangaben, aktiven Profilen oder unbestätigten Snapshots.
- Gibt D-Term-Vorschläge nur für geprüfte Firmwarezweige aus (BF 4.0–4.5, 2025.12 und 2026.6); unbekannte Versionen werden gesperrt.
- Zeigt im Review zusätzlich den exakten, geprüften CLI-Text, der anschließend gespeichert wird.
- Aktiviert die Exportfreigabe erst, nachdem beide Review-Ansichten geöffnet und die Prüfung bestätigt wurden.
- Führt nach jedem dekodierten Flug durch eine dreiteilige Hardwareprüfung; ein Tuningvorschlag kann die Prüfung nicht überspringen.
- Scrollt automatisch zur aktuellen Hardwarefrage, zum Weiterschritt und anschließend zu den Tuning-Eingaben.
- Übernimmt eine vollständig unauffällige Nutzerprüfung in die Freigabe. Sie wird ausdrücklich nicht als Beweis für einen defektfreien Zustand gewertet.

Die erste portable Beta enthält eine firmwarebezogene Korrektur für die Flugmodusauswertung von Betaflight 2026.6.2 mit `bbl_parser` 1.1.1. Dieser Decoder ordnet die betreffenden Bits noch nach älteren Namen zu: `ANGLE_MODE` steht in dieser Firmware für ARM, `HORIZON_MODE` für Angle und `MAG` für Horizon. Bei anderen Betaflight-2026-Versionen meldet die App die Moduszuordnung vorsichtshalber als ungeprüft. Die Beta ist für Erprobung und Feedback gedacht; die Tuning-Metriken bleiben beschreibend und ersetzen keine schrittweise Abstimmung.

Das native WPF-Projekt kann `.BBL`-Dateien lokal per Dateiauswahl oder Drag-and-drop einlesen. Nach abgeschlossener Dekodierung fragt die App nach der Drohne und dem gewünschten Dateinamen. Das im Log erkannte Flightcontroller-Board wird als Drohnenname vorgeschlagen und als Ordnerprofil genutzt; der Nutzer kann den Vorschlag anpassen oder eine vorhandene Drohne auswählen. Da dasselbe FC-Modell in mehreren Fluggeräten stecken kann, lassen sich diese mit unterschiedlichen Profilnamen getrennt halten. Logs und Auswertungen liegen je Profil in einem eigenen Verzeichnis unter `Dokumente/FPV Blackbox Tuning Assistant/Logs/`. Eine lokale Bibliothek `log-library.json` katalogisiert die Dateien; identische Dateien werden je Drohnenprofil nicht doppelt abgelegt. Das Flightcontroller-Modell wird außerdem in den Sessiondaten des Auswertungsberichts für spätere Vergleiche festgehalten.

Nach der Auswahl des Speicher-Namens speichert die App Log und JSON-Auswertungsbericht mit demselben Dateistamm, etwa `Mein Flug.BBL` und `Mein Flug.analysis.json`. Der Bericht enthält Dekodierstatus, Frames, Session-Metadaten, Motor-Plausibilitätswerte sowie Antworten der Nutzerprüfungen. Vorhandene Dateien werden nicht überschrieben: Ist der gewünschte Name bei derselben Drohne bereits für einen anderen Log vergeben, fragt die App nach einem anderen Namen.

Der Decoder `bbl_parser` 1.1.1 ist als separater Unterprozess integriert. Eine einzelne BBL-Datei kann mehrere interne Log-Sessions enthalten; die App benennt diese ausdrücklich als „Session 1“, „Session 2“ usw. und zeigt I-/P-Frame-Zahlen, Firmware, Board und Loopzeit an. Fehlen verwertbare Flug-Frames, bleibt das Tuning gesperrt. Bei ausreichend langen Sessions werden aktive Motor-Ausgänge und – falls vorhanden – eRPM auf deutliche Abweichungen geprüft. Jeder dekodierte Flug führt nun durch die Nutzerprüfung von Propellern, Motoren und Verbindungen; bei Messabweichungen erscheint zusätzlich ein gezielter Prüfhinweis. Die App scrollt automatisch zur nächsten Frage und schaltet erst nach Abschluss zur Tuning-Analyse weiter. Die Messwerte sind Hinweise und keine Defektdiagnose.

Als erste Tuning-Vergleichskennzahl zeigt die App außerdem die Abweichung zwischen angeforderter und gemessener Drehrate je Achse (Mittelwert und 95. Perzentil). Die Gyro-Skalierung aus dem BBL-Header wird berücksichtigt. Die Kennzahl bleibt beschreibend: Aussagekraft hat der Vergleich vor allem bei ähnlichen Manövern, Rateprofilen und Flightcontrollern; daraus wird allein keine PID- oder Filteränderung empfohlen. Die Werte landen auch im JSON-Auswertungsbericht.

Über „Flugvergleiche“ lassen sich gespeicherte BBL-Auswertungen und einzelne interne Sessions paarweise gegenüberstellen. Die Übersicht führt alle erkannten Sessions der ausgewählten Dateien auf und kennzeichnet, wenn für eine Session nicht genügend aktive Messwerte für die Soll-/Ist-Kennzahl vorhanden sind. Der Vergleich kann auf Manuell/Acro, Angle oder Horizon gefiltert werden und zeigt die verfügbaren Setpoint-/Gyro- und PID-Term-Kennzahlen des gewählten Modus. Damit lässt sich ein Ausgangs- und Nachflug vergleichen; ähnliche Manöver, Lograten und Flugbedingungen bleiben wichtig. Eine BBL-Datei kann mehrere Sessions enthalten; diese werden separat angezeigt und nicht zu einem Flug zusammengefasst. Board-, Firmware- und Loopzeitabweichungen werden als Kontext ausgewiesen.

Die App wertet zusätzlich die Betaflight-Spalte `flightModeFlags (flags)` aus und zeigt mit kurzen Erklärungen die Modusanteile und den zeitlichen Wechselverlauf während aktiver Flugphasen. Ereignisse am Start oder Ende werden separat markiert, damit sie nicht als Umschaltung mitten im Flug erscheinen. Die Zuordnung berücksichtigt für Betaflight 2026.6.2 die abweichende Bitbelegung des mitgelieferten Decoders; andere 2026-Versionen bleiben bis zur Prüfung als ungeklärt markiert. Gesamtkennzahlen bleiben sichtbar; bei ausreichend aktiven Messwerten werden Soll-/Ist- und PID-Kennzahlen zusätzlich nach Manuell/Acro, Angle und Horizon getrennt.

Die App wertet P-, I-, D- und Feedforward-Terme aus aktiven Flug-Frames aus und zeigt je Achse Mittelwert der Absolutwerte, RMS und Spitzenwert. Fehlende Kanäle, etwa ein im Log nicht aufgezeichneter Yaw-D-Term, blenden die übrigen vorhandenen Kennzahlen nicht mehr aus und werden ausdrücklich angezeigt. In gespeicherten Flugvergleichen werden gemeinsame Term-Mittelwerte gegenübergestellt. Diese Werte sind nur beschreibende Decoder-Kennzahlen: Sie werden nicht direkt in PID-Werte umgerechnet und begründen allein keine Einstellungsänderung. Flugmodus und Manöver beeinflussen sie.

Die Beta enthält jetzt einen lokalen Betaflight-CLI-Importer. Er liest `dump all` oder `diff all` als Text, vergleicht Betaflight-Version und verfügbaren Boardnamen mit dem Log und behält nur freigegebene PID-, Filter- und Rates-Werte. Die gefilterte Basis sowie Dateiname und SHA-256 der importierten Textdatei werden mit dem Flugbericht gespeichert; Receiver-, Failsafe-, VTX-, Motorzuordnungs- und andere Konfigurationswerte werden nicht übernommen. `dump all` liefert die vollständigen Vorherwerte; bei `diff all` bleibt diese Voraussetzung offen.

Nach der Hardwareprüfung führt „Weiter zur Tuning-Analyse“ zur Modusabdeckung und zum Status der Ausgangskonfiguration. Die App zeigt Gesamtkennzahlen sowie bei genügend aktiven Daten getrennte Soll-/Ist- und PID-Kennzahlen für Acro, Angle und Horizon. Für den bestätigten Fall „Propwash in Acro“ kann sie einen kleinen D-Term-Versuch von höchstens +5 % vorbereiten. Zusätzlich kann sie für Betaflight 2025.12 und 2026.6 bei `rates_type = ACTUAL` einen höchstens 5-%-Schritt zur Senkung der ausgewählten Roll-/Pitch-Maximalrate berechnen, sofern das Acro-Log ausreichend schnell ist und je ausgewählter Achse mindestens drei getrennte Manöver nahe dem Stickanschlag (ab 90 % Stick) enthält. Der Rates-Vorschlag prüft Center-Rate, Super-Rate und Rate-Limit gemeinsam, damit sich der wirksame Maximalwert tatsächlich verringert. Messwerte belegen keine PID-Ursache; jede Änderung bleibt eine Hypothese und muss manuell geprüft und anschließend mit einem vergleichbaren Flug bewertet werden. Filteränderungen bleiben ohne Frequenz-/Rauschdiagnose gesperrt.

Für diesen Entwurf braucht es `dump all` vom selben Flight Controller und die Bestätigung, dass die darin aktiven PID-/Rates-Profile während des Logs verwendet wurden. Hardwareprüfung, beobachtetes Propwash-Symptom und kühle Motoren sind ebenfalls erforderlich. Ein Zahlenvorschlag verlangt ungefähr 2 kHz Lograte und mehrere deutliche Manöver jeder ausgewählten Achse in Acro; ein ruhiger oder langsamer Log reicht nicht. Die Prüfung zeigt Messbeleg, erwartete Wirkung, Risiko und Nachflug-Prüfplan. Die exportierte `.cli`-Datei ist ein geprüfter Textentwurf, kein Firmware-Image: sie flasht nichts, enthält kein `save` und muss vor der manuellen Übernahme nochmals geprüft werden.

Sehr kurze Einträge ohne I-/P-Flugframes, aber nur mit wenigen Ereignis-Frames, werden als Kurzlogs aus der Flugbewertung herausgehalten. Die App fragt, ob sie wahrscheinlich während einer Bergung nach einem Absturz entstanden sind, etwa durch Crash Flip/Turtle Mode. Diese Ursache wird nicht automatisch behauptet; die Nutzerantwort kann im Auswertungsbericht gespeichert werden.

`index.html` bleibt als anklickbarer UI-Prototyp erhalten. Er kann unabhängig von .NET direkt im Browser geöffnet werden.

## Architekturentscheidung

Zielplattform ist Windows 11. Für eine native Desktop-App ist WPF mit modernem .NET eine gute Basis: stabil, in Windows integriert und geeignet für lokale Dateien und umfangreiche Diagramme. Die Analyse-Engine bleibt als eigenständige .NET-Bibliothek vom UI getrennt. Für BBL wird der offizielle Betaflight-Decoder (`blackbox-tools`) eingebunden bzw. als Prozessadapter verwendet; der Parser wird nicht aus vereinfachten Annahmen nachgebaut.

Das WPF-Projekt zielt auf `net10.0-windows`. Das portable Beta-Paket wird unter `beta-win-x64-0.2.0-beta.18/` erzeugt; `FPVBlackboxTuningAssistant.exe` benötigt die .NET-10-Windows-Desktop-Laufzeit. Der separate BBL-Decoder benötigt außerdem das Microsoft Visual C++ Redistributable (x64), insbesondere `VCRUNTIME140.dll`. Der offizielle Microsoft-Download ist <https://aka.ms/vc14/vc_redist.x64.exe>. Die App prüft diese Abhängigkeit vor der Dekodierung und zeigt bei fehlender Laufzeit einen verständlichen Installationshinweis.

Der gebündelte BBL-Parser ist unter AGPL-3.0-or-later lizenziert; Hinweise und der Quellcode-Snapshot liegen im Projekt unter `THIRD-PARTY-NOTICES.md` und `third_party/bbl_parser/v1.1.1/`. Für Änderungen lassen sich Projekt und Release-Ausgabe mit `dotnet build` und `dotnet publish -c Release -o .\publish` neu erstellen.

## Nächste Ausbaustufen

1. Den vollständigen Ablauf von Logimport bis CLI-Entwurf und Flugvergleich mit echten, repräsentativen Logs prüfen, darunter eine zweite Drohne.
2. Frequenz- und Rauschdiagnose aus Gyro-/Motor-Spektren entwickeln, bevor die App Filteränderungen vorschlägt.
3. ACTUAL-Rates auf weitere Betaflight-Zweige ausweiten, sobald Formeln und CLI-Parameter versionsbezogen geprüft sind.
4. Weitere PID-Vorschläge nur mit geeigneten Messdaten, klaren Nutzerzielen und ebenso begrenzten, rückprüfbaren Änderungen ergänzen.
5. Vorher-/Nachher-Logs und Ausgangs-/Nachflug-Konfigurationen im praktischen Betrieb auswerten.
