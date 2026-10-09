# Blackbox-Auswertung: 3BTFL_BLACKBOX_LOG_20260929_232201_BETAFPVG473_V3.BBL

## Korrektur zum Flugmodus (30.09.2026)

Der Pilot bestätigt, dass der Flugmodusschalter während der Flüge unverändert auf Manuell blieb. Die zuvor aus den CSV-Namen abgeleitete Aussage „Session 8: Manuell/Acro, danach Angle“ ist zurückzunehmen. `bbl_parser` 1.1.1 benennt die Modusbits als klassische `flightModeFlags`; Betaflight 2026.6.2 schreibt in dieses Blackbox-Feld jedoch die untere 32-Bit-Hälfte von `rcModeActivationMask`. Dadurch können die ausgegebenen Namen wie `ANGLE_MODE` und `MAG` falsche Modi suggerieren. Bis zur firmwaregerechten Dekodierung sind weder Angle-/Horizon-Anteile noch Moduswechsel aus diesen benannten CSV-Flags belastbar.

## Log-Metadaten und Throttle

- Decoder: `bbl_parser` 1.1.1; 11 interne Logs, Betaflight 2026.6.2, BEFH BETAFPVG473_V3, 125-us-Loopzeit.
- Bei Flugmodusanteilen wurde wie im Projekt nur `rcCommand[3] >= 1100` als aktiver Flugabschnitt gezählt.
- Die vom Decoder erzeugten Flag-Namen werden hier nicht als Flugmodus-Auswertung verwendet.
- Der Decoder fand 11 interne Logs. Die Sessions 1–10 enthalten zusammen rund 302 s mit `rcCommand[3] >= 1100`; Session 11 hat keine solche aktive Throttle-Zeit. Das ist ein Throttle-Filterwert und keine präzise Aussage über tatsächliche Flugzeit.

## Aktive Zeit nach Session

| Session | aktive Zeit (Throttle >= 1100) | Modusbeobachtung |
|---|---:|---|
| 1 | 52,23 s | Angle |
| 2 | 42,99 s | Angle |
| 3 | 10,78 s | Angle |
| 4 | 81,61 s | Angle |
| 5 | 40,66 s | Angle |
| 6 | 6,28 s | Angle |
| 7 | 57,83 s | Angle |
| 8 | 8,87 s | ca. 0,99 s Manuell/Acro, danach ca. 7,88 s Angle |
| 9 | 0,62 s | sehr kurz; nicht als Tuningvergleich verwenden |
| 10 | 0,26 s | sehr kurz; nicht als Tuningvergleich verwenden |
| 11 | 0 s | keine auswertbare aktive Throttle-Zeit |

Die aktiven Zeiten summieren sich auf rund 302 s über alle Sessions. Session 8 enthält außerdem eine lange Phase mit niedrigerem Throttle und ohne Modusflags; diese wird nicht zur aktiven Flugzeit gezählt.

## Einordnung für Tuning

Der Pilot bestätigt, dass der Modusschalter während der Flüge unverändert auf Manuell blieb. Er schätzt ungefähr zehn Abstürze durch Hindernisse und dazwischen manuelles Disarmen und erneutes Armen. Das passt grundsätzlich zu den 11 internen Logs, ordnet aber nicht jeden einzelnen Log sicher einem Absturz zu. Die niedrigen Throttle-Werte und die ruhigen Kellerflüge begrenzen die Aussagekraft für PID-/Filtertuning: Aus diesem Log allein keine PID- oder Filteränderung ableiten. Sessions 9–11 sind zu kurz bzw. ohne aktive Throttle-Zeit.

Nächster technischer Schritt: den Firmware-Stand bzw. das BBL-Feld firmwaregerecht als `rcModeActivationMask` dekodieren oder dessen rohe Werte erhalten, bevor die App Modusnamen oder Wechsel behauptet.

## Dateien

Original-BBL und CSV-Exporte bleiben lokal und sind nicht Teil des GitHub-Quellstands.

