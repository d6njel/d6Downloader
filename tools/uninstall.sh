#!/usr/bin/env bash
# Entfernt d6Downloader aus ~/.local. Heruntergeladene Dateien werden nie angefasst. Einstellungen und Anmeldedaten werden
# nur gelöscht, wenn die Frage danach ausdrücklich mit Ja beantwortet wird.
set -euo pipefail

die() { echo "uninstall.sh: $*" >&2; exit 1; }

[ "$(id -u)" -ne 0 ] || die "Bitte ohne sudo ausführen."
case "${HOME:-}" in /*) ;; *) die "HOME ist nicht gesetzt." ;; esac

case "${XDG_DATA_HOME:-}" in /*) data_home=$XDG_DATA_HOME ;; *) data_home=$HOME/.local/share ;; esac
case "${XDG_CONFIG_HOME:-}" in /*) config_home=$XDG_CONFIG_HOME ;; *) config_home=$HOME/.config ;; esac
app_dir=$HOME/.local/lib/d6downloader
bin=$HOME/.local/bin/d6downloader
list=$app_dir/installed-files.txt
config=$config_home/d6downloader

echo "Läuft d6Downloader noch, bitte zuerst über das Tray-Symbol oder durch Schließen beenden."

# Verweise auf das Programm: nur, wenn sie auf diese Installation zeigen.
points_here() { grep -qF -e "$app_dir/d6downloader" -e "$bin" -- "$1"; }

autostart=$config_home/autostart/d6downloader.desktop
if [ -f "$autostart" ] && points_here "$autostart"; then rm -f -- "$autostart"; fi

for dir in "$config_home/google-chrome/NativeMessagingHosts" "$config_home/chromium/NativeMessagingHosts" \
    "$config_home/BraveSoftware/Brave-Browser/NativeMessagingHosts" "$config_home/vivaldi/NativeMessagingHosts" \
    "$config_home/microsoft-edge/NativeMessagingHosts" "$HOME/.mozilla/native-messaging-hosts"; do
    manifest=$dir/d6downloader.host.json
    [ -f "$manifest" ] || continue
    if points_here "$manifest"; then rm -f -- "$manifest"
    else echo "Nicht entfernt (zeigt auf ein anderes Programm): $manifest"; fi
done

if [ -L "$bin" ] && [ "$(readlink "$bin")" = "$app_dir/d6downloader" ]; then rm -f -- "$bin"; fi
rm -f -- "$data_home/applications/d6downloader.desktop" "$data_home/icons/hicolor/256x256/apps/d6downloader.png"

# Nur die installierten Dateien, nie den Ordner als Ganzes.
if [ -f "$list" ]; then
    mapfile -t files < "$list"
    for file in "${files[@]}"; do
        case "$file" in ""|/*|*..*) continue ;; esac
        rm -f -- "$app_dir/$file"
    done
fi
# Zwischenspeicher der Programmdatei (von .NET ausgepackte Bibliotheken).
rm -rf -- "$HOME/.net/d6downloader"

# Die festen Dateinamen, die das Programm selbst anlegt. Alles andere bleibt liegen.
remove_data() {
    local dir=$1
    rm -f -- "$dir"/settings.json "$dir"/settings.json.tmp "$dir"/settings.json.bad \
        "$dir"/downloads.json "$dir"/downloads.json.tmp "$dir"/downloads.json.bad
}

portable_data=$app_dir/Data
if [ -d "$config" ] || [ -d "$portable_data" ]; then
    echo
    echo "Einstellungen, Download-Liste und gespeicherte Anmeldedaten liegen noch in:"
    [ -d "$config" ] && echo "  $config"
    [ -d "$portable_data" ] && echo "  $portable_data"
    answer=
    printf 'Diese Daten ebenfalls löschen? Heruntergeladene Dateien bleiben in jedem Fall. [j/N] '
    read -r answer || answer=
    echo
    case "$answer" in
        j|J|ja|Ja|y|Y|yes)
            if [ -d "$portable_data" ]; then
                remove_data "$portable_data"
                rm -f -- "$app_dir/.portable"
                rmdir -- "$portable_data" 2>/dev/null || echo "Andere Dateien bleiben in $portable_data."
            fi
            if [ -d "$config" ]; then
                remove_data "$config"
                rm -f -- "$config"/master.key "$config"/master.key.lock "$config"/master.key.*.tmp \
                    "$config"/run/ipc.sock "$config"/run/instance.lock
                rmdir -- "$config/run" 2>/dev/null || true
                rmdir -- "$config" 2>/dev/null || echo "Andere Dateien bleiben in $config."
            fi
            echo "Daten gelöscht." ;;
        *) echo "Daten behalten." ;;
    esac
fi

# Leere Ordner aufräumen. Was noch Dateien enthält, bleibt.
if [ -d "$app_dir" ]; then find "$app_dir" -depth -type d -empty -delete; fi
echo "d6Downloader ist entfernt. Die Browser-Erweiterung bitte im Browser selbst entfernen."
