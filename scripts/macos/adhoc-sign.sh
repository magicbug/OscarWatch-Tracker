#!/usr/bin/env bash
# Ad-hoc codesign every Mach-O under a publish directory (Apple Silicon needs this).
# Usage: adhoc-sign.sh <publish-dir>
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <publish-dir>" >&2
  exit 1
fi

PUBLISH_DIR="$1"
if [[ ! -d "$PUBLISH_DIR" ]]; then
  echo "Publish directory not found: $PUBLISH_DIR" >&2
  exit 1
fi

# Sign each Mach-O. Order does not matter for flat publish trees; the .app
# bundle is sealed separately with codesign --deep after packaging.
while IFS= read -r -d '' path; do
  if file -b "$path" | grep -q 'Mach-O'; then
    codesign --force --sign - --timestamp=none "$path"
  fi
done < <(find "$PUBLISH_DIR" -type f -print0)

echo "Ad-hoc signed Mach-O files under $PUBLISH_DIR"
