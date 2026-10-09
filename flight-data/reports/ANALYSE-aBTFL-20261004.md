# Blackbox-Auswertung: aBTFL_BLACKBOX_LOG_20261004_160250_BETAFPVG473_V3.BBL

## Ergebnis

Decoder: `bbl_parser` 1.1.1. Firmware: Betaflight 2026.6.2, Board BEFH BETAFPVG473_V3, Loopzeit 125 µs. Die Datei enthält drei interne Logs. Der Pilot bestätigt die Schalterreihenfolge Acro → Angle → Horizon; die drei Abschnitte stimmen damit überein.

| Log | Frames | aktive Zeit (`rcCommand[3] >= 1100`) | vom Decoder gedruckter Flagtext | Modus nach firmwaregerechter Zuordnung |
|---|---:|---:|---|---|
| 1 | 68.379 | 63,10 s | `ANGLE_MODE` | Acro/Manuell |
| 2 | 65.330 | 61,80 s | `ANGLE_MODE|HORIZON_MODE` | Angle |
| 3 | 66.597 | 64,84 s | `ANGLE_MODE|MAG` | Horizon |

## Warum die Flagtexte irreführend sind

Betaflight 2026.6.2 schreibt `rcModeActivationMask` in das Blackbox-Feld `flightModeFlags`. Der Decoder 1.1.1 benennt diese Bits noch nach der älteren `flightModeFlags_e`-Zuordnung. Bei dieser Firmware entspricht daher:

- Decoderbit 0 / `ANGLE_MODE` dem ARM-Flag; es bedeutet nicht automatisch Angle.
- Decoderbit 1 / `HORIZON_MODE` dem BOXANGLE-Bit.
- Decoderbit 2 / `MAG` dem BOXHORIZON-Bit.

Die Zuordnung der drei vorliegenden Sessions ist durch die vom Piloten bestätigte Reihenfolge Acro → Angle → Horizon gestützt. Es gibt keinen Moduswechsel innerhalb eines einzelnen aktiven Logs: Jeder der drei Modi ist in einem separaten Arm-/Logabschnitt aufgezeichnet. Die kurzen abweichenden Flagzeilen am jeweiligen Disarm-Ende sind Grenzzeilen und werden nicht als zusätzlicher Flugmodus gewertet.

## Einordnung

Diese Datei ist eine brauchbare Referenz für die firmware-spezifische Moduszuordnung. Für den App-Code sollte Betaflight 2026.6.2 als `rcModeActivationMask` dekodiert werden; die alten Decodertexte dürfen nicht direkt als Modusnamen angezeigt werden. Die aktiven Zeitanteile beruhen auf dem Projektfilter von 1100 und sind keine exakte Flugzeitmessung.
