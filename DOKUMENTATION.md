# d6Downloader: Anleitung

d6Downloader ist ein Download-Manager für Linux (entwickelt unter Nobara/Fedora 44 mit KDE). Diese Anleitung setzt kein
Vorwissen voraus. Befehle gibt man im Programm **Konsole** ein, eine Zeile nach der anderen, jeweils mit Enter.

Nie `sudo` davorsetzen: Alles wird nur für dein eigenes Benutzerkonto eingerichtet.

## 1. Installieren

Zum Holen des Quelltexts braucht man `git`, zum Bauen einmalig das .NET-10-Entwicklerpaket (nur dieser eine Befehl
braucht Administratorrechte, falls etwas davon noch fehlt). Zum Entpacken von Archiven werden `7zip`, `bsdtar` und für
RAR `unar` gebraucht, zum Verschieben in den Papierkorb `gio` (Paket `glib2`).

```
sudo dnf install git dotnet-sdk-10.0 7zip bsdtar unar glib2
```

Danach den Quelltext holen, bauen und installieren.

```
git clone https://github.com/d6njel/d6Downloader.git ~/d6Downloader
cd ~/d6Downloader
./tools/publish.sh
./tools/install.sh
```

`publish.sh` erzeugt die Programmdatei (rund 100 MB, läuft ohne installiertes .NET). Beim ersten Mal lädt .NET dafür
Pakete von nuget.org, dafür ist Internet nötig. `install.sh` kopiert alles in deinen Benutzerordner und legt den Eintrag
im Startmenü an. Es prüft vorher die Prüfsummen und überschreibt nichts, was nicht von d6Downloader stammt.

**Aktualisieren:** d6Downloader beenden, im Projektordner mit `git pull` den neuen Stand holen, dann `publish.sh` und
`install.sh` noch einmal ausführen. Einstellungen und Downloads bleiben.

## 2. Starten

- Im KDE-Startmenü nach **d6Downloader** suchen, oder
- in der Konsole: `~/.local/bin/d6downloader`

Es läuft immer nur ein d6Downloader. Ein zweiter Start holt das vorhandene Fenster nach vorn (Linux kann das verhindern,
dann auf das Symbol in der Leiste klicken).

Links fügt man mit dem Plus-Knopf oder mit Strg+N hinzu. Geladen wird erst, nachdem du im Dialog bestätigt hast.

**Fenster schließen:** In den Einstellungen unter „Fenster schließen“ wählt man, ob das Programm endet oder im Tray
(Symbole neben der Uhr) weiterläuft. Beenden kann man es dort über „Beenden“.

**Autostart:** Einstellungen, „Beim Anmelden starten, still im Tray“.

### Befehle für die Konsole

| Befehl | Wirkung |
|---|---|
| `d6downloader` | Programm starten |
| `d6downloader --version` | Version anzeigen |
| `d6downloader --add <Link> [<Link> ...]` | Links an das Programm geben, startet es bei Bedarf |
| `d6downloader --headless-download <Link> --out <Ordner>` | eine Datei ohne Fenster laden |
| `d6downloader --help` | Hilfe |

`--headless-download` benutzt keine Konten und keine Einstellungen und überschreibt nie etwas: Ist der Name belegt,
bekommt die Datei eine Nummer. Ein abgebrochener Download lässt eine Datei mit der Endung `.part` zurück, die man
löschen kann. Links gehören in Anführungszeichen, wenn sie Zeichen wie `&` enthalten.

## 3. Browser verbinden

Danach schickt ein Rechtsklick auf einen Link („Mit d6Downloader laden“) den Link an das Programm. Das geht mit Firefox,
Chrome, Chromium, Brave, Vivaldi und Edge, **nicht** mit Browsern aus Flatpak oder Snap.

Der Browser muss mindestens einmal gestartet worden sein.

**Schritt 1: in d6Downloader.** Einstellungen öffnen, **„Browser verbinden …“**, dann bei deinem Browser auf
**„Verbinden“**. „Chrome / Chromium“ gilt auch für Brave, Vivaldi und Edge.

**Schritt 2: Erweiterung laden.** Der Ordner der Erweiterung ist `~/.local/lib/d6downloader/Extension`. Im Dialog gibt es
„Pfad kopieren“. Der Ordner `.local` ist versteckt: Im Dateiauswahlfenster hilft Strg+H, oder man fügt den Pfad ein.

Chrome, Chromium und Verwandte:

1. In die Adresszeile `chrome://extensions` eingeben (Brave: `brave://extensions`, Edge: `edge://extensions`).
2. Oben rechts den **Entwicklermodus** einschalten.
3. **„Entpackte Erweiterung laden“** und den Ordner `Extension` wählen.

Firefox:

1. In die Adresszeile `about:debugging` eingeben.
2. **„Dieser Firefox“**, dann **„Temporäres Add-on laden …“**.
3. Die Datei `Extension/firefox/manifest.json` wählen (nicht die `manifest.json` direkt in `Extension`).

Firefox vergisst temporäre Add-ons beim Beenden. Nach jedem Neustart von Firefox muss man Schritt 2 wiederholen. Eine
dauerhafte Installation bräuchte eine von Mozilla signierte Erweiterung, die es nicht gibt.

**Schritt 3: testen.** Das d6Downloader-Symbol in der Werkzeugleiste anklicken (eventuell erst über das Puzzle-Symbol
anheften). „OK“ heißt, die Verbindung steht. Ein „!“ heißt, sie fehlt: Hinweistext lesen und Schritt 1 prüfen. Dann einen
Link rechts anklicken. Lief d6Downloader nicht, wird es gestartet und bleibt offen, auch wenn man den Browser schließt.

**Trennen:** im selben Dialog „Trennen“, und die Erweiterung im Browser entfernen.

## 4. Rapidgator einrichten und abmelden

Voraussetzung ist ein **Premium-Konto**. Konten ohne Premium und Ordner-Links gehen vorerst nicht.

**Die Anbindung wurde nie mit dem echten Rapidgator ausprobiert**, nur mit einer Nachbildung. Beim ersten Mal also mit
einer kleinen Datei testen.

Anmelden:

1. Oben im Fenster auf **Rapidgator** klicken (oder Einstellungen, „Rapidgator anmelden …“).
2. E-Mail und Passwort eingeben, **„Anmelden“**.
3. Ist die Zwei-Faktor-Anmeldung (2FA) eingeschaltet, den Code aus der Authentifizierungs-App eingeben. Er wird nie
   gespeichert.

Du bleibst angemeldet, auch nach einem Neustart. Der Dialog zeigt, wie lange Premium läuft und wie viel Traffic übrig
ist. Rapidgator-Links lädt man danach wie andere Links.

**Abmelden:** denselben Dialog öffnen, **„Abmelden“**. Passwort und Anmeldeschlüssel werden gelöscht. Die E-Mail-Adresse
steht unverschlüsselt in den Einstellungen.

Wurde die Anmeldung abgelehnt (falsches Passwort, falscher Code, Captcha), versucht es das Programm nicht von selbst
erneut: Zu viele Fehlversuche führen bei Rapidgator zur Sperre deiner Internetadresse. Mit 2FA kann sich das Programm
nach Ablauf der Anmeldung nicht allein neu anmelden, dann wird wieder ein Code gebraucht.

## 5. Archive entpacken

Fertige Pakete und einzelne Archive entpackt d6Downloader von selbst (abschaltbar in den Einstellungen) oder über
Rechtsklick, **„Entpacken“**. Vorhandene Dateien werden nie überschrieben. Die Archive bleiben liegen, außer du schaltest
die Option darunter ein.

**Archive nach dem Entpacken in den Papierkorb verschieben** (Einstellungen, ab Werk aus):

- Gilt beim automatischen und beim manuellen Entpacken, und nur, wenn das Entpacken vollständig geklappt hat und
  mindestens eine Datei herauskam.
- In den Papierkorb kommen alle Teile des Archivs aus der Download-Liste, andere Dateien (etwa `.nfo`) nie.
- Eine Datei, die seit Beginn des Entpackens verändert wurde oder eine Verknüpfung ist, bleibt liegen. Ebenso, wenn der
  Papierkorb nicht geht (etwa auf manchen Netzlaufwerken). Endgültig gelöscht wird nie. Das Protokoll des Downloads nennt,
  wie viele Dateien verschoben oder behalten wurden.
- Der Platz wird erst frei, wenn du den Papierkorb leerst. Gebraucht wird dafür `gio` (Teil von `glib2`).

| Archiv | Programm |
|---|---|
| einfache ZIP | eingebaut |
| 7z, mehrteilige oder geschützte ZIP | `7z`/`7zz` (Paket `7zip`), einteilige 7z notfalls `bsdtar` |
| RAR, auch mehrteilig | `unar` und `lsar` (Paket `unar`) |

Das 7-Zip von Fedora und Nobara kann **kein RAR**. Für RAR nimmt d6Downloader deshalb `unar`. Ein 7-Zip wird für RAR
nur benutzt, wenn es selbst angibt, RAR zu können; `bsdtar` nur als letzter Versuch bei einteiligen, ungeschützten RAR.

**Passwortgeschützte RAR** gehen ab Werk **nicht**. Der Grund: `unar` nimmt ein Passwort nur auf der Befehlszeile an.
Dort können andere Programme und andere Benutzer des Rechners es während des Entpackens in der Prozessliste lesen. Wer
das in Kauf nimmt, schaltet in den Einstellungen **„Passwort an unar übergeben (für geschützte RAR)“** ein und wählt
danach erneut „Entpacken“ oder „Passwort eingeben …“. Es werden dann auch die Passwörter aus der Liste nacheinander so
probiert. Bei ZIP und 7z bleibt das Passwort wie bisher unsichtbar (Übergabe an 7-Zip ohne Befehlszeile).

Meldungen:

- **„RAR: unar nötig“:** Paket installieren (`sudo dnf install unar`), dann „Entpacken“.
- **„Teile fehlen“:** Nicht alle Teile eines mehrteiligen Archivs sind da oder einer ist unvollständig.
- **„Archiv beschädigt oder unvollständig“:** Datei neu laden. Bei verschlüsselten RAR lässt sich ein falsches Passwort
  nicht immer von einem Schaden unterscheiden; dann heißt es „Passwort nötig“.
- **„Verknüpfungen im Archiv“** oder **„unsichere Pfade“:** Das Archiv wird aus Sicherheitsgründen nicht entpackt, auch
  nicht teilweise. Das trifft auch harmlose Archive mit Verknüpfungen.

**Früher als „beschädigt“ gemeldete RAR-Pakete** nach der Aktualisierung mit Rechtsklick, „Entpacken“ erneut
versuchen. Neu geladen werden muss nichts.

**Zurück zu einer älteren Fassung:** Eine Fassung ohne `unar`-Unterstützung kann die Download-Liste nicht lesen, wenn
darin der Zustand „unar nötig“ oder der neue Passwort-Grund steht. Sie legt die Liste dann als
`downloads.json.bad` beiseite und beginnt leer. Vorher `~/.config/d6downloader` sichern.

## 6. Deinstallieren

d6Downloader beenden, dann:

```
~/.local/lib/d6downloader/uninstall.sh
```

Das entfernt Programm, Startmenü-Eintrag, Symbol und, soweit sie auf diese Installation zeigen, den Autostart und die
Browser-Verbindung. Danach fragt das Skript, ob auch Einstellungen, Download-Liste und Anmeldedaten gelöscht werden
sollen. **Nur die Antwort `j` löscht sie.** Heruntergeladene Dateien werden nie gelöscht.

Die Erweiterung entfernt man im Browser selbst.

## 7. Wo die Daten liegen

| Was | Ort |
|---|---|
| Heruntergeladene Dateien | dein Downloads-Ordner, änderbar in den Einstellungen |
| Unfertige Downloads | daneben, Endungen `.part` und `.part.json` |
| Einstellungen (auch verschlüsselte Passwörter) | `~/.config/d6downloader/settings.json` |
| Download-Liste | `~/.config/d6downloader/downloads.json` |
| Schlüssel zur Verschlüsselung | `~/.config/d6downloader/master.key` |
| Beschädigte Dateien, zur Rettung beiseitegelegt | gleicher Ordner, Endung `.bad` |
| Programm und Erweiterung | `~/.local/lib/d6downloader/` |
| Startbefehl (Verknüpfung) | `~/.local/bin/d6downloader` |
| Startmenü-Eintrag und Symbol | `~/.local/share/applications/d6downloader.desktop`, `~/.local/share/icons/hicolor/256x256/apps/d6downloader.png` |
| Autostart | `~/.config/autostart/d6downloader.desktop` |
| Browser-Verbindung | `d6downloader.host.json` in `~/.config/<Browser>/NativeMessagingHosts/` bzw. `~/.mozilla/native-messaging-hosts/` |
| Zwischenspeicher des Programms | `~/.net/d6downloader/` |
| Sperre und Socket, solange es läuft | `$XDG_RUNTIME_DIR/d6downloader/` (meist `/run/user/<Nummer>/d6downloader/`) |

Mit dem Schalter „Portable: Daten im Programmordner“ liegen Einstellungen und Liste stattdessen in
`~/.local/lib/d6downloader/Data/`. Der Schlüssel `master.key` bleibt auch dann in `~/.config/d6downloader`.

Zum Sichern den Ordner `~/.config/d6downloader` bei beendetem Programm kopieren. Eine solche Kopie enthält Schlüssel
**und** verschlüsselte Passwörter, also wie ein Passwort behandeln.

## 8. Sicherheitsgrenzen

- **Passwörter** (Rapidgator-Passwort und Anmeldeschlüssel, AllDebrid-Key, Archiv-Passwörter) werden verschlüsselt
  gespeichert (AES-256-GCM). Der Schlüssel liegt in `master.key`, nur für dein Benutzerkonto lesbar. KWallet wird nicht
  benutzt. Das verhindert, dass Passwörter lesbar in einer Datei stehen. Es schützt **nicht** vor Programmen, die unter
  deinem Benutzerkonto laufen, vor Administratoren oder vor jemandem, der eine Kopie beider Dateien hat.
- **Unverschlüsselter Rückfall bei Rapidgator:** Anmeldung, Passwort und Token gehen nur verschlüsselt (HTTPS) über das
  Netz. Für die Datei selbst liefert Rapidgator eine Adresse (`download_url`), die mit `http://` beginnen kann. Das
  Programm versucht sie zuerst über HTTPS. Klappt das nicht, lädt es **ohne Verschlüsselung** über die gelieferte
  Adresse. Dann kann jemand im selben Netz (zum Beispiel in einem offenen WLAN) mitlesen, was du lädst, die Adresse samt
  ihrer Sitzungskennung sehen und die Datei verändern. Eine Prüfsumme, falls vorhanden, deckt Veränderungen auf.
- **Heruntergeladene Dateien** werden nicht auf Schadsoftware geprüft. Nur laden, was du kennst und darfst.
- **RAR mit Passwort:** Ist „Passwort an unar übergeben“ eingeschaltet (ab Werk aus), steht jedes probierte Passwort
  während des Aufrufs lesbar in der Prozessliste.
- **Click'n'Load** ist ab Werk aus (Einstellungen, **„Click'n'Load: Links von Webseiten annehmen“**). Eingeschaltet
  lauscht es nur auf dem eigenen Rechner (`127.0.0.1` und, wenn möglich, `::1`, Port 9666). Webseiten und
  Programme auf dem Rechner können dann Links schicken. Diese öffnen nur den Hinzufügen-Dialog. Es funktioniert nur,
  solange d6Downloader läuft (dafür Autostart einschalten). Fragt der Browser nach Zugriff auf lokale Geräte oder Apps,
  erlauben.
- **Browser-Erweiterung:** Sie darf nur ein Rechtsklick-Menü anzeigen und mit d6Downloader sprechen. Sie liest keine
  Seiteninhalte.
- Über `--add` und die Verbindung zwischen den Programmteilen kann jedes Programm unter deinem Benutzerkonto Links in den
  Hinzufügen-Dialog legen.

## 9. Was noch nicht in Wirklichkeit ausprobiert ist

Automatisch geprüft sind Programmlogik und Fenster (ohne Bildschirm, gegen Attrappen). Die fertige Programmdatei wurde
in einer unsichtbaren KDE-Testsitzung gestartet: Browser-Aufruf bei geschlossenem Programm, Weiterlaufen nach dessen
Ende, `--add`, zweiter Start, Beenden.

**Ungeprüft:**

- Bedienung auf deinem echten Desktop (Maus, Tastatur, Hell/Dunkel, Skalierung), Eintrag und Symbol im Startmenü
- Tray-Symbol, Benachrichtigungen, Zwischenablage-Überwachung, Drag & Drop aus dem Browser, Dateidialoge,
  „Im Ordner zeigen“, Click'n'Load
- Wachhalten, Herunterfahren, Standby, Ruhezustand, Autostart nach dem Anmelden
- die Erweiterung in einem echten Firefox oder Chrome (geprüft ist nur das Programm, so aufgerufen, wie der Browser es tut)
- Rapidgator mit dem echten Dienst. Was die Schnittstellenbeschreibung nicht sagt (abgelaufene Anmeldung, aufgebrauchter
  Traffic, Captcha), beruht auf Annahmen.
- AllDebrid mit dem echten Dienst
- Archive nach dem Entpacken in den Papierkorb verschieben (ab Werk aus, braucht `gio`): mit einem echten Papierkorb
  ungeprüft

**Entpacken von RAR:** Automatisch geprüft mit dem echten `unar` 1.10.7 an Testdateien aus libarchive und an kleinen
selbst erzeugten Archiven: einteilig (RAR 4 und 5), mehrteilig RAR 5, mehrteilig RAR 4 (nur selbst erzeugt, ohne
Kompression, beide Namensschemata), fehlende Teile, Beschädigung, Verknüpfungen, falsches Passwort.

**Beim Entpacken ungeprüft:**

- ein verschlüsseltes RAR mit dem **richtigen** Passwort (nur gegen eine Attrappe)
- komprimiertes mehrteiliges RAR 4 bis zum fertigen Ergebnis (die Testdatei dafür enthält eine Verknüpfung und wird
  deshalb abgelehnt)
- große echte Pakete, wie man sie herunterlädt, und der Ablauf in der laufenden Oberfläche
- ein 7-Zip, das RAR kann (nur gegen eine Attrappe)
- andere `unar`-Versionen als 1.10.7

## 10. Wenn etwas nicht geht

- **Nichts passiert beim Start:** In der Konsole `~/.local/bin/d6downloader` ausführen und die Meldung lesen. Läuft schon
  eine Instanz (Tray-Symbol?), kommt keine neue.
- **Browser meldet „nicht verbunden“:** In d6Downloader unter „Browser verbinden …“ den Stand prüfen. Steht dort
  „Aktualisieren nötig“, auf „Aktualisieren“ klicken.
- **Entpacken geht nicht:** `sudo dnf install 7zip bsdtar unar glib2`, Meldungen siehe Abschnitt 5.
