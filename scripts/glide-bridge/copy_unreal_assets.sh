#!/usr/bin/env bash
set -euo pipefail

# Run under WSL with mtools. The image is an isolated, materialized Win95 copy.
root=$(realpath "$(dirname "$0")/../..")
image="${1:-$root/artifacts/glide-bridge/win95-complete.img}@@32256"
source="${2:-$root/artifacts/glide-bridge/unreal-cd-extracted}"

test -f "${image%@@32256}"
test -d "$source/System"
mdir -i "$image" ::/UNREAL >/dev/null

for folder in System Maps Textures Music Sounds Help; do
    if ! mdir -i "$image" "::/UNREAL/$folder" >/dev/null 2>&1; then
        mmd -i "$image" "::/UNREAL/$folder"
    fi
    mcopy -s -o -i "$image" "$source/$folder/"* "::/UNREAL/$folder/"
    printf '%s copied\n' "$folder"
done

mdir -i "$image" ::/UNREAL/SYSTEM/UNREAL.EXE
mdir -i "$image" ::/UNREAL/MAPS/VORTEX2.UNR
