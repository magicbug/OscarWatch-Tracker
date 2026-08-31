#!/usr/bin/env bash
# Wrap a self-contained OscarWatch publish folder as OscarWatch.app and zip it.
# Usage: package-app.sh <publish-dir> <version> <rid> <icon-png> <output-zip>
# Example: package-app.sh ./publish 1.0.3 osx-arm64 ./OscarWatch-Icon.png ./out.app.zip
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "Usage: $0 <publish-dir> <version> <rid> <icon-png> <output-zip>" >&2
  exit 1
fi

PUBLISH_DIR="$1"
VERSION="$2"
RID="$3"
ICON_PNG="$4"
OUTPUT_ZIP="$5"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ ! -d "$PUBLISH_DIR" ]]; then
  echo "Publish directory not found: $PUBLISH_DIR" >&2
  exit 1
fi
if [[ ! -f "$PUBLISH_DIR/OscarWatch" ]]; then
  echo "Expected executable not found: $PUBLISH_DIR/OscarWatch" >&2
  exit 1
fi
if [[ ! -f "$ICON_PNG" ]]; then
  echo "Icon PNG not found: $ICON_PNG" >&2
  exit 1
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/oscarwatch-app.XXXXXX")"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

STAGE="$WORK/stage"
APP="$STAGE/OscarWatch.app"
MACOS="$APP/Contents/MacOS"
RESOURCES="$APP/Contents/Resources"
mkdir -p "$MACOS" "$RESOURCES"

# Copy publish tree next to the executable (AppContext.BaseDirectory).
cp -a "$PUBLISH_DIR"/. "$MACOS/"
chmod +x "$MACOS/OscarWatch"

# Build .icns from the source PNG (sips + iconutil on macOS runners).
ICONSET="$WORK/OscarWatch.iconset"
mkdir -p "$ICONSET"
sips -z 16 16     "$ICON_PNG" --out "$ICONSET/icon_16x16.png" >/dev/null
sips -z 32 32     "$ICON_PNG" --out "$ICONSET/icon_16x16@2x.png" >/dev/null
sips -z 32 32     "$ICON_PNG" --out "$ICONSET/icon_32x32.png" >/dev/null
sips -z 64 64     "$ICON_PNG" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
sips -z 128 128   "$ICON_PNG" --out "$ICONSET/icon_128x128.png" >/dev/null
sips -z 256 256   "$ICON_PNG" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
sips -z 256 256   "$ICON_PNG" --out "$ICONSET/icon_256x256.png" >/dev/null
sips -z 512 512   "$ICON_PNG" --out "$ICONSET/icon_256x256@2x.png" >/dev/null
sips -z 512 512   "$ICON_PNG" --out "$ICONSET/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$ICON_PNG" --out "$ICONSET/icon_512x512@2x.png" >/dev/null
iconutil -c icns "$ICONSET" -o "$RESOURCES/OscarWatch.icns"

# Info.plist (microphone usage for pass recording TCC prompt).
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple Computer//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en-GB</string>
  <key>CFBundleExecutable</key>
  <string>OscarWatch</string>
  <key>CFBundleIconFile</key>
  <string>OscarWatch</string>
  <key>CFBundleIdentifier</key>
  <string>org.oscarwatch.OscarWatch</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>OscarWatch</string>
  <key>CFBundleDisplayName</key>
  <string>OscarWatch</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSMicrophoneUsageDescription</key>
  <string>OscarWatch records pass audio from your radio when you enable pass recording.</string>
</dict>
</plist>
EOF

# Re-sign nested Mach-O (copy may invalidate prior seals), then the bundle.
bash "$SCRIPT_DIR/adhoc-sign.sh" "$MACOS"
codesign --force --deep --sign - --timestamp=none "$APP"

# Quarantine helper next to the .app (operators who prefer not to use Terminal).
cat > "$STAGE/Remove Quarantine.command" <<'EOF'
#!/bin/bash
# Clears Gatekeeper quarantine on OscarWatch.app in this folder.
set -euo pipefail
cd "$(dirname "$0")"
if [[ ! -d "OscarWatch.app" ]]; then
  echo "OscarWatch.app not found next to this script." >&2
  read -r -p "Press Enter to close…"
  exit 1
fi
xattr -cr "OscarWatch.app"
echo "Quarantine cleared on OscarWatch.app."
echo "You can now drag OscarWatch.app to Applications and open it."
read -r -p "Press Enter to close…"
EOF
chmod +x "$STAGE/Remove Quarantine.command"

mkdir -p "$(dirname "$OUTPUT_ZIP")"
rm -f "$OUTPUT_ZIP"
(
  cd "$STAGE"
  zip -qry "$OUTPUT_ZIP" "OscarWatch.app" "Remove Quarantine.command"
)

# Sanity checks
unzip -l "$OUTPUT_ZIP" | grep -q 'OscarWatch.app/Contents/MacOS/OscarWatch'
unzip -l "$OUTPUT_ZIP" | grep -q 'Remove Quarantine.command'
codesign -dv "$APP" 2>&1 | grep -q 'Signature='

echo "Packaged $OUTPUT_ZIP (version=$VERSION rid=$RID)"
