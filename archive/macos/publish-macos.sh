#!/bin/bash
set -euo pipefail
export AVALONIA_TELEMETRY_OPTOUT=1
[[ "$(uname -s)" == "Darwin" ]] || { echo "Run this script on a Mac." >&2; exit 1; }
repo="$(cd -- "$(dirname -- "$0")/.." && pwd)"
if [[ "$(uname -m)" == "arm64" ]]; then default_rid=osx-arm64; else default_rid=osx-x64; fi
rid="${1:-$default_rid}"
[[ "$rid" == "osx-arm64" || "$rid" == "osx-x64" ]] || { echo "Expected osx-arm64 or osx-x64." >&2; exit 1; }
package="$repo/artifacts/crossplatform/BerkeleyTimeSync-$rid-$(date +%Y%m%d-%H%M%S)-$RANDOM"
bundle="$package/BerkeleyTimeSync.app/Contents"
mkdir -p "$bundle/MacOS"
dotnet publish "$repo/src/Berkeley.CrossPlatform/Berkeley.CrossPlatform.csproj" -c Release -r "$rid" --self-contained true -p:UseAppHost=true -o "$bundle/MacOS"
cp "$repo/scripts/macos/Info.plist" "$bundle/Info.plist"
cp "$repo/scripts/macos/prepare-mac.sh" "$repo/scripts/macos/entitlements.plist" "$repo/docs/huong-dan-macos.md" "$package/"
bash "$package/prepare-mac.sh"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$package" "$package.zip"
echo "$package.zip"
