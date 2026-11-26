#!/usr/bin/env bash
# Baut die fertige Programmdatei (self-contained, eine Datei, linux-x64) nach dist/.
set -euo pipefail

root=$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)
project="$root/src/D6Downloader/D6Downloader.csproj"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project" | head -n 1)
[ -n "$version" ] || { echo "publish.sh: Version nicht gefunden." >&2; exit 1; }
out="$root/dist/d6downloader-$version-linux-x64"

# Wurde das Programm von dort im Portable-Modus benutzt, liegen darin Nutzerdaten.
if [ -e "$out/Data" ] || [ -e "$out/.portable" ]; then
    echo "publish.sh: In $out liegen Daten (Portable-Modus). Bitte erst wegsichern." >&2
    exit 1
fi
rm -rf -- "$out"

dotnet publish "$project" -c Release -r linux-x64 --self-contained --nologo \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -p:DebugSymbols=false -o "$out"

mkdir -p "$out/Licenses"
install -m 644 "$root/LICENSE" "$out/Licenses/LICENSE"
install -m 644 "$root/NOTICE.md" "$out/Licenses/NOTICE.md"
install -m 644 "$root/DOKUMENTATION.md" "$out/DOKUMENTATION.md"
install -m 644 "$root/assets/d6downloader-256.png" "$out/d6downloader.png"
install -m 755 "$root/tools/install.sh" "$out/install.sh"
install -m 755 "$root/tools/uninstall.sh" "$out/uninstall.sh"

for needed in d6downloader Extension/manifest.json Extension/firefox/manifest.json Licenses/LICENSE-FluentSystemIcons.txt; do
    [ -f "$out/$needed" ] || { echo "publish.sh: $needed fehlt im Ergebnis." >&2; exit 1; }
done

(cd "$out" && find . -type f ! -name SHA256SUMS -printf '%P\0' | LC_ALL=C sort -z | xargs -0 sha256sum -- > SHA256SUMS)

echo
echo "Fertig: $out"
du -h "$out/d6downloader" | cut -f 1 | sed 's/^/Programmdatei: /'
echo "Installieren (ohne sudo): $root/tools/install.sh"
