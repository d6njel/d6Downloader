#!/usr/bin/env bash
# Erzeugt alle PNG-Symbole aus den beiden SVGs in assets/. Nur nach Änderungen am Symbol nötig:
# Die PNGs liegen im Repo, Bauen und Veröffentlichen brauchen kein Inkscape.
set -euo pipefail

root=$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)
large="$root/assets/d6downloader.svg"
small="$root/assets/d6downloader-small.svg"
inkscape=/usr/bin/inkscape
[ -x "$inkscape" ] || { echo "render-icons.sh: $inkscape fehlt." >&2; exit 1; }

# render <svg> <Kantenlänge> <Ziel>
render() {
    mkdir -p "$(dirname "$3")"
    "$inkscape" "$1" --export-type=png -w "$2" -h "$2" --export-filename="$3" >/dev/null
    # Metadaten entfernen, falls ImageMagick da ist.
    if command -v magick >/dev/null; then magick "$3" -strip "PNG32:$3"; fi
}

# Bis 48 Pixel die kleine Fassung (Pixelraster), darüber die große.
for size in 16 24 32 48 64 128 256; do
    if [ "$size" -le 48 ]; then svg=$small; else svg=$large; fi
    render "$svg" "$size" "$root/assets/icons/hicolor/${size}x${size}/apps/d6downloader.png"
done

hicolor="$root/assets/icons/hicolor"
cp -- "$hicolor/256x256/apps/d6downloader.png" "$root/assets/d6downloader-256.png"
cp -- "$hicolor/256x256/apps/d6downloader.png" "$root/src/D6Downloader/Assets/icon.png"
render "$small" 64 "$root/src/D6Downloader/Assets/tray.png"

for size in 16 32 48; do
    cp -- "$hicolor/${size}x${size}/apps/d6downloader.png" "$root/extension/icons/$size.png"
done
cp -- "$hicolor/128x128/apps/d6downloader.png" "$root/extension/icons/128.png"

echo "Symbole erzeugt."
