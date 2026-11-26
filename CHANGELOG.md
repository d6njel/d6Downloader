# Changelog

## d6Downloader 0.3.1 (in Arbeit, noch nicht veröffentlicht)

### Geändert

- Einstellungen → Handy: „Entfernen“ und „Alle entfernen“ fragen vorher nach.
- Dazu die App „d6 Remote“ 0.1.1: Nachfrage vor „Verlauf löschen“, „Erneut senden“ auch im Teilen-Dialog, viel kleinere APK.
- Noch ungeprüft: die Rückfrage auf einem echten Bildschirm und die neue App auf einem echten Handy.

### Behoben

- App „d6 Remote“: Kam eine Sendung trotz gemeldetem Fehler an und wurde erneut gesendet, blieb sie auf „wartet auf PC“. Die erste Rückmeldung wird jetzt gefunden.

## d6Downloader 0.3.0 (in Arbeit, noch nicht veröffentlicht)

### Neu

- Links vom Handy annehmen (App „d6 Remote“ für Android): über einen ntfy-Server (Standard `https://ntfy.sh`), Ende-zu-Ende verschlüsselt, ab Werk aus. Koppeln per QR-Code unter Einstellungen → Handy, Geräte lassen sich dort entfernen.
- Links vom Handy landen im Hinzufügen-Dialog. Auf Wunsch („Links vom Handy sofort starten“, ab Werk aus) starten unbedenkliche Links direkt; Unsicheres bleibt im Dialog, Ziele im eigenen Netz werden abgelehnt.
- Noch ungeprüft: alles mit dem echten ntfy-Server und einem echten Handy (nur automatische Tests gegen Attrappen).

## d6Downloader 0.2.0 (in Arbeit, noch nicht veröffentlicht)

### Geändert

- Neues eigenes App-Symbol im Hacker-/Matrix-Stil („d6“ mit Download-Pfeil) statt des blauen „Y“: Fenster, Tray, Startmenü (jetzt in 7 Größen) und Browser-Erweiterung. Die Erweiterung im Browser einmal neu laden, damit sie das neue Symbol zeigt.

### Neu

- Unterhäkchen „Stattdessen endgültig löschen (nicht rückgängig zu machen)“ unter „Archive nach dem Entpacken in den Papierkorb verschieben“ (ab Werk aus, braucht kein `gio`): Die Archivteile werden nur gelöscht, wenn das Entpacken vollständig geklappt hat und alle Teile unverändert sind, sonst bleiben alle liegen. Andere Dateien werden nie gelöscht.
- Noch ungeprüft: das endgültige Löschen mit echten Paketen in der laufenden Oberfläche (nur automatische Tests).

### Behoben

- Einfache ZIP-Archive mit beschädigtem Inhalt galten als entpackt. Jetzt werden Länge und Prüfsumme (CRC32) jeder Datei geprüft, auch Ordnereinträge mit Daten gelten als Schaden. Dann heißt es „beschädigt“, es wird nichts übernommen und nichts entfernt.
- `bsdtar` kann ein als beschädigt erkanntes ZIP, RAR oder 7z nicht mehr als entpackt durchwinken. Hat `bsdtar` ein ZIP oder RAR entpackt, bleiben die Archive liegen, weil es Schäden nicht sicher meldet.
- Ein RAR, das genau zwischen zwei Dateien abgeschnitten ist, wurde als vollständig entpackt behandelt und hätte entfernt werden können. RAR-Archive werden jetzt nur noch entfernt, wenn ihre Vollständigkeit belegt ist (Blockkette mit gültigen Kopf-Prüfsummen bis zum Ende-Block, bei Sätzen bis zum letzten Teil). Sonst wird entpackt, die Archive bleiben, und das Protokoll sagt es. Das betrifft auch alte RAR ohne Ende-Block und RAR mit verschlüsselten Dateinamen.

## d6Downloader 0.1.1 (2026-11-26)

### Behoben

- Der Fortschrittsbalken der Download-Liste zeigte bei mehreren Verbindungen Teilstücke mit Lücken. Er füllt sich jetzt lückenlos von links nach dem Gesamtfortschritt, auch bei pausierten Downloads. Die Detailansicht zeigt die Verbindungen weiterhin einzeln.

## d6Downloader 0.1.0 (in Arbeit, noch nicht veröffentlicht)

Vorabfassung, siehe [README](README.md) und [DOKUMENTATION](DOKUMENTATION.md).

### Neu

- Einstellung „Archive nach dem Entpacken in den Papierkorb verschieben“ (ab Werk aus): nur nach vollständig geglücktem Entpacken, alle Teile des Archivs, nie endgültiges Löschen.
- Ein ZIP-Eintrag, der kürzer ist als angegeben, gilt jetzt als beschädigtes Archiv.
- Click'n'Load funktioniert mit mehr Webseiten: `/jdcheck.js` und `/crossdomain.xml` werden beantwortet, Links auch per GET, als `multipart/form-data` und über `/flash/addcnl` angenommen, und es wird zusätzlich auf `::1` gelauscht. Noch nicht mit einer echten Webseite ausprobiert.
- RAR wird mit `unar`/`lsar` entpackt, auch mehrteilig. Vor dem Entpacken werden Namen, Verknüpfungen, Vollständigkeit und Platz geprüft.
- Neuer Zustand „RAR: unar nötig“, wenn kein passendes Programm da ist.
- Einstellung „Passwort an unar übergeben“ (ab Werk aus): ohne sie bleiben geschützte RAR ungeöffnet, mit ihr ist das Passwort während des Entpackens in der Prozessliste sichtbar.
- Hinweis zum Zurückwechseln: Ältere Fassungen können eine Download-Liste mit den neuen Zuständen nicht lesen (sie wird als `.bad` beiseitegelegt).
- Noch ungeprüft: verschlüsseltes RAR mit richtigem Passwort, komprimiertes mehrteiliges RAR 4 bis zum Ergebnis, große echte Pakete, ein 7-Zip mit RAR (nur Attrappe).
- Befehlszeile: `--version`, `--help`, `--add <url…>`, `--headless-download <url> --out <ordner>`. Unbekannte Argumente werden abgewiesen, statt die App zu starten.
- `tools/publish.sh` baut eine einzelne Programmdatei (self-contained, linux-x64) mit `SHA256SUMS`, `tools/install.sh` und `tools/uninstall.sh` richten sie ohne root nach `~/.local` ein, mit Startmenü-Eintrag und Symbol.
- `DOKUMENTATION.md`: Anleitung auf Deutsch.
- Oberfläche für Linux mit Avalonia 12: Hauptfenster mit nativem Rahmen, Hinzufügen-Dialog, alle bisherigen Dialoge, Tray-Symbol, Benachrichtigungen, Zwischenablage-Überwachung, Click'n'Load, hell und dunkel, Skalierung.
- Rapidgator-Dialog: Anmelden mit 2FA, Premium-Laufzeit, Traffic, Abmelden, fehlgeschlagene Downloads nach der Anmeldung neu starten.
- Symbole aus den Fluent System Icons (MIT) statt der Windows-Schrift. Die Fensterknöpfe im macOS-Stil entfallen.
- Rapidgator mit eigenem Premium-Konto (API v2): Anmelden mit 2FA, angemeldet bleiben bis zum Abmelden, automatischer Neu-Login mit Schutz vor Wiederholschleifen, Traffic-Prüfung, Online-Check, Vorrang vor AllDebrid. Nur gegen eine Attrappe getestet, nie gegen das echte Rapidgator.
- Downloads merken sich den Dienst (`viaService`). Ältere Listen mit `debrid: true` gelten als AllDebrid.
- Ein Rapidgator-Dateilink, der direkt geladen würde, scheitert, statt die Webseite als Datei zu speichern.

### Geändert

- Abspaltung von YDownloader 1.0.1 und Umbenennung in d6Downloader: Programmname `d6downloader`, neue Kennungen der Browser-Erweiterung, neuer Name des Native-Messaging-Hosts. Daten des Originals werden nicht übernommen.
- Die Programmlogik liegt UI-frei in `D6Downloader.Core` (.NET 10), die Linux-Dienste in `D6Downloader.Platform.Linux`.
- Geheimnisse werden mit AES-256-GCM statt mit der Windows-Datenschutz-API verschlüsselt. Der Schlüssel liegt in `~/.config/d6downloader/master.key` (nur für den eigenen Benutzer lesbar).
- Zum Entpacken werden `7zz`, `7z`, `7za` und `bsdtar` aus dem PATH gesucht.
- Die Registrierung beim Browser schreibt Manifest-Dateien statt Registry-Einträge. Angebunden sind Chrome, Chromium, Brave, Vivaldi, Edge und Firefox, jeweils nur bei vorhandenem Profilordner.
- Speicherorte nach XDG, der Downloads-Ordner kommt aus `user-dirs.dirs`.
- Einzelinstanz per Sperrdatei, Nachrichten zwischen den Prozessen über einen Unix-Socket.
- Wachhalten über `systemd-inhibit`, Herunterfahren über KDE (sonst `systemctl`), Standby und Ruhezustand über `systemctl`.
- Autostart als `.desktop`-Datei, Öffnen und „im Ordner zeigen“ über `xdg-open` und den Dateimanager.
- Das Programm selbst beantwortet `--native-host`.
- Die Sprachtexte nennen kein Windows, `tar.exe` oder `7z.exe` mehr.
- Die Tests laufen unter Linux.
- Die Windows-Oberfläche, ihre Bildschirmfotos, die PowerShell-Skripte und `icon.ico` sind entfernt.
- Die Aktion „Nach Abschluss“ läuft im Hintergrund: Die Oberfläche bleibt ansprechbar, auch während das System herunterfährt.

### Behoben (Linux)

- Mehrteilige RAR wurden als „beschädigt“ gemeldet: Das 7-Zip von Fedora kann kein RAR und wurde trotzdem gestartet. Es wird für RAR nur noch benutzt, wenn es RAR nachweislich liest. Betroffene Pakete mit „Entpacken“ erneut versuchen.
- Zwei Downloads auf dasselbe Ziel schrieben gleichzeitig in dieselbe Teildatei, auch aus zwei Prozessen.
- Dateinamen behielten Steuerzeichen und Zeichen wie `:` oder `?`.
- Lange Namen mit Umlauten oder Sonderzeichen konnten die Grenze von 255 Byte überschreiten.
- Dateifehler (Ziel weg, kein Zugriff, Pfad zu lang) werden anhand der Linux-Fehlernummern erkannt.
- Ein Zielordner, der als Verknüpfung ins Leere zeigt, wurde als „gibt es schon“ gemeldet.
- Ohne vorhandenes `~/.config` konnte der Datenordner relativ zum Arbeitsordner entstehen.
- Archive mit Ordnern ohne Zugriffsrechte ließen den vorübergehenden Entpackordner liegen.

---

# Historie des Originals (YDownloader)

## 1.0.1

### Added

- ZIP files that use a compression method .NET cannot read (for example Zstandard) are now extracted with 7-Zip or Windows `tar.exe` as a fallback. The order is .NET, 7-Zip, `tar.exe`.
- Protection against data loss for `downloads.json` and `settings.json`: a file that is locked is retried a few times and then left untouched for the session (nothing is saved over it); a corrupt file is copied to a `.bad` file before the app starts with defaults. The user is told in a notice.
- Settings are written through a single write chain (the newest state wins and is flushed on exit). A write error is shown in the Settings sheet, which stays open.
- Clearer error messages for file problems: missing folder or drive (for example an unplugged disk), path too long, and no access.

### Changed

- More precise messages when extraction fails: unsupported compression method (with the method name), damaged or incomplete archive, password needed, and 7-Zip not responding in time.
- Better file names for direct downloads: `Content-Disposition` first, then the URL path and query (including the final address after redirects), then the `Content-Type` for a missing extension. A guessed name is improved by one extra request when the download starts.
- Notices about data files are shown one after another and never on top of an open sheet.

### Fixed

- A download whose server returns a web page instead of the file is rejected when the target name is an archive, media file or program (zip, rar, 7z, iso, video, audio, images, exe, msi and similar), and for any non-page name when AllDebrid is enabled. If the AllDebrid host list was unavailable, the message says so.
- A locked `settings.json` no longer crashes the start.
- Quitting before the download list has finished loading no longer writes an empty list.
- Moving data between portable mode and `%AppData%` is refused while the list or settings could not be loaded safely.

## 1.0.0

Initial release.
