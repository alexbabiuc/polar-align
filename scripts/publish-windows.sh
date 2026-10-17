#!/usr/bin/env bash
# Publishes the Windows x64 build: the application self-contained, each device
# plugin into plugins/<Name>/ beside it. The layout dist/ has always had, and
# the one the installer packages.
#
# Usage: scripts/publish-windows.sh <output directory>
#
# Bash rather than PowerShell so it runs unchanged on the macOS development
# machine and on the Windows runner, whose Git Bash provides it.
set -euo pipefail

if [ $# -ne 1 ]; then
    echo "Usage: $0 <output directory>" >&2
    exit 2
fi

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$1"

if [ -e "$out" ] && [ -n "$(ls -A "$out")" ]; then
    # Publishing into a folder that already holds a build leaves its files
    # behind: a plugin since removed, or one built against another host, would
    # ship with this one (D24).
    echo "'$out' is not empty. Give an empty or new directory." >&2
    exit 2
fi

dotnet publish "$root/src/FreePolarAlign.App" -c Release -r win-x64 --self-contained -o "$out"

# Plugins are framework-dependent: they run inside the application's runtime,
# and a runtime of their own beside them is what D24 forbids.
for plugin in Ascom Zwo ToupTek; do
    dotnet publish "$root/src/FreePolarAlign.Devices.$plugin" -c Release -r win-x64 --self-contained false \
        -o "$out/plugins/$plugin"
done

if [ ! -f "$out/plugins/ToupTek/toupcam.dll" ]; then
    echo "Note: no toupcam.dll in resources/touptek/camera/, so ToupTek cameras need the user's own copy (D25)." >&2
fi
