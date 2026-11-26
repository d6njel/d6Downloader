# Installation auf Fedora mit KDE, Schritt für Schritt

Diese Anleitung ist für Fedora mit KDE Plasma (Computer mit Intel- oder AMD-Prozessor, „x86_64“). Entwickelt und
geprüft wird d6Downloader unter Nobara/Fedora 44. **Andere Fedora-Versionen sind nicht ausprobiert.**

d6Downloader ist eine Vorabfassung. Was noch nie in Wirklichkeit ausprobiert wurde, steht in
[DOKUMENTATION.md](DOKUMENTATION.md), Abschnitt 9.

> **Nur für Linux.** Dieses Projekt ist eine Abspaltung von YDownloader (Windows). Downloads des Originals und
> ältere Versionsnummern wie `v1.0.0` oder `v1.0.1` sind die **Windows-Fassung** und laufen hier nicht. Bitte nicht
> „Code > Download ZIP“ nehmen, sondern so vorgehen wie unten (sonst klappt das Aktualisieren nicht).

Es gibt nur den **Quellcode**: Die Releases auf GitHub enthalten keine fertige Programmdatei, man baut sie wie unten.

## So gibst du Befehle ein

1. Das Programm **Konsole** öffnen (im Startmenü nach „Konsole“ suchen).
2. Einen Befehl aus dem grauen Kasten kopieren, in die Konsole einfügen (Strg+Umschalt+V) und Enter drücken.
3. Warten, bis wieder eine Eingabezeile erscheint, dann der nächste Befehl.

`sudo` nur dort, wo es in dieser Anleitung steht (Schritt 1). Danach fragt es nach **deinem** Passwort. Beim Tippen
sieht man nichts, das ist normal.

## Schritt 1: Werkzeuge installieren (einmalig)

```
sudo dnf install git dotnet-sdk-10.0 7zip bsdtar unar glib2
```

Mit `j` (oder `y`) und Enter bestätigen.

- `git` holt das Programm von GitHub, `dotnet-sdk-10.0` baut es.
- `7zip`, `bsdtar` und `unar` werden zum Entpacken gebraucht (RAR nur mit `unar`), `glib2` für den Papierkorb.

Meldet `dnf`, dass `dotnet-sdk-10.0` nicht gefunden wird, ist dein Fedora zu alt für diese Anleitung (nicht
ausprobiert).

## Schritt 2: Programm herunterladen

```
git clone https://github.com/d6njel/d6Downloader.git ~/d6Downloader
cd ~/d6Downloader
```

Das legt den Ordner `d6Downloader` in deinem persönlichen Ordner an.

Wer eine bestimmte Version bauen will, gibt danach zum Beispiel `git checkout v0.3.1` ein. Vor dem späteren
Aktualisieren dann zuerst `git switch main`.

## Schritt 3: Programm bauen

```
./tools/publish.sh
```

- Dauert einige Minuten und braucht Internet (beim ersten Mal lädt .NET Pakete von nuget.org).
- Am Ende steht `Fertig: …/dist/d6downloader-…-linux-x64` und die Größe der Programmdatei (rund 100 MB).

## Schritt 4: Installieren (ohne sudo)

```
./tools/install.sh
```

- Am Ende steht `d6Downloader ist installiert.` und darunter, wo alles liegt.
- Das Programm kommt nach `~/.local/lib/d6downloader`, der Startbefehl nach `~/.local/bin/d6downloader`, dazu ein
  Eintrag im Startmenü.
- Ein Hinweis, dass `~/.local/bin` nicht im PATH steht, ist harmlos: Das Startmenü funktioniert trotzdem.

## Schritt 5: Starten

- Im KDE-Startmenü nach **d6Downloader** suchen, oder
- in der Konsole: `~/.local/bin/d6downloader`

Erste Schritte im Programm:

- Links hinzufügen: Plus-Knopf oder Strg+N. Geladen wird erst nach deiner Bestätigung.
- Einstellungen: Zahnrad oben oder Strg+, (Komma).
- Unter „Fenster schließen“ wählen, ob das Programm beim Schließen endet oder im Tray (Symbole neben der Uhr)
  weiterläuft.
- Autostart: Einstellungen, „Beim Anmelden starten, still im Tray“.

## Schritt 6: Browser verbinden (freiwillig)

Danach schickt ein Rechtsklick auf einen Link, „Mit d6Downloader laden“, den Link an d6Downloader.

**Geht mit:** Firefox, Chrome, Chromium, Brave, Vivaldi, Edge, wenn sie als normales Paket installiert sind (bei
Fedora ist Firefox das von Haus aus).

**Geht nicht** mit Browsern aus **Flatpak oder Snap**. Ob ein Browser als Flatpak installiert ist, zeigt in der Konsole
`flatpak list`. Steht er dort, geht die Verbindung nicht. Vorsicht: Discover bietet Browser oft als Flatpak an.

Der Browser muss vorher mindestens einmal gestartet worden sein.

1. **In d6Downloader:** Einstellungen, **„Browser verbinden …“**, bei deinem Browser **„Verbinden“**.
   „Chrome / Chromium“ gilt auch für Brave, Vivaldi und Edge.
2. **Erweiterung laden.** Der Ordner ist `~/.local/lib/d6downloader/Extension`. Im Dialog gibt es „Pfad kopieren“.
   Der Ordner `.local` ist versteckt: Im Auswahlfenster zeigt Strg+H versteckte Ordner, oder du fügst den Pfad ein.
   - **Chrome und Verwandte:**
     1. `chrome://extensions` in die Adresszeile eingeben (Brave: `brave://extensions`, Edge: `edge://extensions`).
     2. Oben rechts **Entwicklermodus** einschalten.
     3. **„Entpackte Erweiterung laden“** wählen und den Ordner `Extension` angeben.
   - **Firefox:**
     1. `about:debugging` in die Adresszeile eingeben.
     2. **„Dieser Firefox“**, dann **„Temporäres Add-on laden …“**.
     3. Die Datei `Extension/firefox/manifest.json` wählen (nicht die `manifest.json` direkt in `Extension`).
     4. **Firefox vergisst das Add-on beim Beenden.** Nach jedem Firefox-Neustart diesen Schritt wiederholen.
3. **Testen:** Das d6Downloader-Symbol in der Werkzeugleiste anklicken (eventuell erst über das Puzzle-Symbol
   anheften). „OK“ heißt, die Verbindung steht. „!“ heißt, sie fehlt: den Hinweistext lesen und Schritt 1 prüfen.

Die Erweiterung wurde noch nicht in einem echten Firefox oder Chrome ausprobiert.

## Schritt 7: Click'n'Load (freiwillig, ungetestet)

Manche Download-Seiten haben einen Click'n'Load-Knopf, der die Links direkt an ein Download-Programm schickt.

1. In den Einstellungen **„Click'n'Load: Links von Webseiten annehmen“** einschalten (ab Werk aus).
2. Es funktioniert nur, solange d6Downloader läuft (dafür am besten den Autostart einschalten).
3. Fragt der Browser, ob die Seite auf lokale Geräte oder Apps zugreifen darf: erlauben.

Die empfangenen Links erscheinen im Hinzufügen-Dialog und starten nie von selbst.

- Läuft gleichzeitig **JDownloader**, ist der Port 9666 belegt, und Click'n'Load bleibt aus. d6Downloader meldet das.
- Mit einer echten Webseite noch nicht ausprobiert.

## Schritt 8: Handy-App (freiwillig)

Mit der Android-App [d6 Remote](https://github.com/d6njel/d6Remote) schickt man Links vom Handy an den PC. Installation
und Hinweise stehen dort, das Koppeln in [DOKUMENTATION.md](DOKUMENTATION.md), Abschnitt 11.

## Aktualisieren

Zuerst d6Downloader beenden (Tray-Symbol, „Beenden“). Dann:

```
cd ~/d6Downloader
git pull
./tools/publish.sh
./tools/install.sh
```

Einstellungen, Download-Liste und die Browser-Verbindung bleiben erhalten. Die Firefox-Erweiterung muss wie immer nach
einem Firefox-Neustart neu geladen werden.

## Deinstallieren

d6Downloader beenden, dann:

```
~/.local/lib/d6downloader/uninstall.sh
```

1. Das entfernt Programm, Startmenü-Eintrag, Symbol, Autostart und Browser-Verbindung.
2. Danach kommt die Frage, ob auch Einstellungen, Download-Liste und Anmeldedaten gelöscht werden sollen. **Nur `j`
   löscht sie.**
3. Heruntergeladene Dateien bleiben immer.

Danach selbst erledigen:

- Die Erweiterung im Browser entfernen.
- Wer den Quelltext nicht mehr braucht, löscht zusätzlich den Ordner `~/d6Downloader`. Darin liegen keine Downloads,
  außer du hast selbst welche dort gespeichert.

## Typische Fehler

| Meldung | Lösung |
|---|---|
| `Keine Berechtigung` / `Permission denied` bei `./tools/…` | stattdessen `bash tools/publish.sh` bzw. `bash tools/install.sh` |
| `dotnet: Befehl nicht gefunden` / `command not found` | Schritt 1 wiederholen |
| `install.sh: Bitte ohne sudo ausführen.` | den Befehl ohne `sudo` eingeben |
| `Keine fertige Programmdatei gefunden. Erst tools/publish.sh ausführen.` | Schritt 3 ausführen und warten, bis `Fertig:` erscheint |
| `Prüfsummen stimmen nicht, nichts installiert.` | Schritt 3 noch einmal, dann Schritt 4 |
| `… gibt es schon und gehört nicht zu dieser Installation.` | Unter `~/.local/bin/d6downloader` liegt schon etwas anderes, das das Skript nicht überschreibt. Prüfen, was es ist. |
| `fatal: destination path … already exists` bei `git clone` | Der Ordner ist schon da: `cd ~/d6Downloader` und weiter wie unter „Aktualisieren“ |
| `publish.sh` bricht mit Netzwerkfehler ab | Internet prüfen und noch einmal starten |
| Beim Start passiert nichts | In der Konsole `~/.local/bin/d6downloader` ausführen und die Meldung lesen. Läuft es schon im Tray, kommt kein zweites Fenster. |
| Browser: „nicht verbunden“ | In „Browser verbinden …“ nachsehen. Steht dort „Aktualisieren nötig“, auf „Aktualisieren“ klicken. Flatpak/Snap-Browser gehen nicht. |
| „RAR: unar nötig“ | `sudo dnf install unar`, dann Rechtsklick, „Entpacken“ |

Mehr Einzelheiten (Rapidgator, Entpacken, wo die Daten liegen, Sicherheit) stehen in
[DOKUMENTATION.md](DOKUMENTATION.md).
