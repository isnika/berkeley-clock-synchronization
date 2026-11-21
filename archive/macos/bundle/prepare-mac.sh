#!/bin/bash
# Local developer signing of this lab build; no Gatekeeper/quarantine changes.
set -euo pipefail
if [[ "$(uname -s)" != "Darwin" ]]; then echo "Run this script on macOS." >&2; exit 1; fi
package_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
bundle="$package_dir/BerkeleyTimeSync.app"
host="$bundle/Contents/MacOS/BerkeleyTimeSync"
[[ -f "$host" && -f "$package_dir/entitlements.plist" ]] || { echo "Incomplete Mac package." >&2; exit 1; }
chmod u+x "$host"
while IFS= read -r -d '' native_file; do
  /usr/bin/codesign --force --sign - "$native_file"
done < <(find "$bundle/Contents/MacOS" -type f -name '*.dylib' -print0)
/usr/bin/codesign --force --sign - --entitlements "$package_dir/entitlements.plist" "$bundle"
/usr/bin/codesign --verify --deep --strict "$bundle"
echo "Local lab bundle prepared: $bundle"
echo "This is ad-hoc developer signing, not Developer ID signing or notarization."
