#!/usr/bin/env bash
# Installiert d6Downloader für den eigenen Benutzer nach ~/.local. Kein root, kein sudo.
# Aufruf: install.sh [Ordner mit dem Ergebnis von publish.sh]
set -euo pipefail

die() { echo "install.sh: $*" >&2; exit 1; }

[ "$(id -u)" -ne 0 ] || die "Bitte ohne sudo ausführen."
case "${HOME:-}" in /*) ;; *) die "HOME ist nicht gesetzt." ;; esac
case "$HOME" in *$'\n'*) die "HOME enthält Zeilenumbrüche." ;; esac

self=$(dirname "$(readlink -f "$0")")
if [ $# -ge 1 ]; then
    source_dir=$1
elif [ -f "$self/d6downloader" ]; then
    source_dir=$self
else
    source_dir=$(find "$self/../dist" -maxdepth 1 -type d -name 'd6downloader-*-linux-x64' 2>/dev/null | sort -V | tail -n 1)
fi
[ -n "$source_dir" ] && [ -f "$source_dir/d6downloader" ] && [ -f "$source_dir/SHA256SUMS" ] \
    || die "Keine fertige Programmdatei gefunden. Erst tools/publish.sh ausführen."
source_dir=$(cd "$source_dir" && pwd)
(cd "$source_dir" && sha256sum --quiet -c SHA256SUMS) || die "Prüfsummen stimmen nicht, nichts installiert."

case "${XDG_DATA_HOME:-}" in /*) data_home=$XDG_DATA_HOME ;; *) data_home=$HOME/.local/share ;; esac
app_dir=$HOME/.local/lib/d6downloader
bin=$HOME/.local/bin/d6downloader
desktop=$data_home/applications/d6downloader.desktop
hicolor=$data_home/icons/hicolor
list=$app_dir/installed-files.txt

[ "$source_dir" != "$app_dir" ] || die "Das ist schon der installierte Ordner."
if [ -e "$bin" ] || [ -L "$bin" ]; then
    [ -L "$bin" ] && [ "$(readlink "$bin")" = "$app_dir/d6downloader" ] \
        || die "$bin gibt es schon und gehört nicht zu dieser Installation."
fi
if [ -d "$app_dir" ] && [ ! -f "$list" ] && [ -n "$(ls -A "$app_dir")" ]; then
    # Nach dem Deinstallieren dürfen Daten aus dem Portable-Modus liegen bleiben.
    [ -z "$(find "$app_dir" -mindepth 1 -maxdepth 1 ! -name Data ! -name .portable)" ] \
        || die "$app_dir ist nicht leer und stammt nicht von diesem Skript."
fi

# Dateien einer früheren Version entfernen, aber nur die damals installierten.
if [ -f "$list" ]; then
    while IFS= read -r file; do
        case "$file" in ""|/*|*..*) continue ;; esac
        rm -f -- "$app_dir/$file"
    done < "$list"
fi

mkdir -p "$app_dir" "$(dirname "$bin")" "$(dirname "$desktop")"
files=$( { sed -n 's/^[0-9a-f]\{64\}  //p' "$source_dir/SHA256SUMS"; echo SHA256SUMS; } )
printf '%s\ninstalled-files.txt\n' "$files" > "$list"
while IFS= read -r file; do
    case "$file" in d6downloader|*.sh) mode=755 ;; *) mode=644 ;; esac
    install -D -m "$mode" -- "$source_dir/$file" "$app_dir/$file"
done <<< "$files"

ln -sfn -- "$app_dir/d6downloader" "$bin"
if [ -d "$source_dir/icons/hicolor" ]; then
    for size in 16 24 32 48 64 128 256; do
        install -D -m 644 -- "$source_dir/icons/hicolor/${size}x${size}/apps/d6downloader.png" \
            "$hicolor/${size}x${size}/apps/d6downloader.png"
    done
else
    # Ältere Ergebnisse von publish.sh haben nur ein Symbol.
    install -D -m 644 -- "$source_dir/d6downloader.png" "$hicolor/256x256/apps/d6downloader.png"
fi
# Der Oberfläche sagen, dass sich Symbole geändert haben. Einen Symbol-Cache nur erneuern, wenn es schon einen gibt.
touch -- "$hicolor"
if [ -f "$hicolor/icon-theme.cache" ] && command -v gtk-update-icon-cache >/dev/null; then
    gtk-update-icon-cache -f -t "$hicolor" >/dev/null 2>&1 || true
fi

# Ein Argument für Exec nach der Desktop-Entry-Spezifikation.
exec_path=$(printf '%s' "$bin" | sed -e 's/[\\"`$]/\\&/g' -e 's/%/%%/g' -e 's/\\/\\\\/g')
cat > "$desktop" <<ENTRY
[Desktop Entry]
Type=Application
Name=d6Downloader
GenericName=Download Manager
GenericName[de]=Download-Manager
Comment=Download files and extract archives
Comment[de]=Dateien herunterladen und entpacken
Exec="$exec_path"
Icon=d6downloader
Terminal=false
Categories=Network;FileTransfer;
StartupWMClass=d6downloader
ENTRY
chmod 644 "$desktop"
if command -v kbuildsycoca6 >/dev/null; then kbuildsycoca6 >/dev/null 2>&1 || true; fi

echo "d6Downloader ist installiert."
echo "  Programm:        $bin"
echo "  Programmordner:  $app_dir"
echo "  Erweiterung:     $app_dir/Extension"
echo "  Startmenü:       $desktop"
echo "  Deinstallieren:  $app_dir/uninstall.sh"
case ":${PATH:-}:" in
    *":$HOME/.local/bin:"*) ;;
    *) echo "Hinweis: $HOME/.local/bin steht nicht im PATH. Das Startmenü funktioniert trotzdem." ;;
esac
