#!/usr/bin/env bash
set -euo pipefail

root=$(realpath "$(dirname "$0")/../..")
stage=/tmp/dose-glide-unreal-95
mkdir -p "$stage"
cp "$root/artifacts/glide-bridge/win95-complete.img" "$stage/win95-complete.img"
cp -a "$root/artifacts/glide-bridge/unreal-cd-extracted" "$stage/unreal-cd-extracted"
bash "$root/scripts/glide-bridge/copy_unreal_assets.sh" "$stage/win95-complete.img" "$stage/unreal-cd-extracted"
cp "$stage/win95-complete.img" "$root/artifacts/glide-bridge/win95-complete-with-game.img"
