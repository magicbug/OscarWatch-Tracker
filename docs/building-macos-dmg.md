# Building a macOS DMG

Instructions for creating a distributable `.dmg` disk image for OscarWatch on macOS.

## Prerequisites

- .NET 10 SDK installed (`dotnet --version` should show 10.x)
- macOS with `hdiutil` (included with every macOS installation)
- For Apple Silicon: build on an ARM64 Mac (or cross-compile with `-r osx-arm64`)
- For Intel: use `-r osx-x64`

## Quick build (one-liner)

```bash
# Apple Silicon
./build-dmg.sh osx-arm64

# Intel
./build-dmg.sh osx-x64
```

If the script doesn't exist yet, follow the manual steps below.

## Manual steps

### 1. Publish the application

```bash
# Clean previous output
rm -rf publish/osx-arm64 publish/OscarWatch.app

# Publish self-contained for macOS ARM64
dotnet publish OscarWatch/OscarWatch.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained true \
  -o publish/osx-arm64
```

For Intel Macs, replace `osx-arm64` with `osx-x64` throughout.

### 2. Create the .app bundle structure

```bash
mkdir -p publish/OscarWatch.app/Contents/MacOS
mkdir -p publish/OscarWatch.app/Contents/Resources
```

### 3. Create Info.plist

```bash
cat > publish/OscarWatch.app/Contents/Info.plist << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>OscarWatch</string>
    <key>CFBundleDisplayName</key>
    <string>OscarWatch</string>
    <key>CFBundleIdentifier</key>
    <string>org.oscarwatch.tracker</string>
    <key>CFBundleVersion</key>
    <string>0.9.6</string>
    <key>CFBundleShortVersionString</key>
    <string>0.9.6</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>OscarWatch</string>
    <key>CFBundleIconFile</key>
    <string>OscarWatch.icns</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSMicrophoneUsageDescription</key>
    <string>OscarWatch uses the microphone for pass audio recording.</string>
</dict>
</plist>
EOF
```

Update `CFBundleVersion` and `CFBundleShortVersionString` to match the release version.

### 4. Copy published files into the bundle

```bash
cp -R publish/osx-arm64/* publish/OscarWatch.app/Contents/MacOS/
chmod +x publish/OscarWatch.app/Contents/MacOS/OscarWatch
```

### 5. (Optional) Add an app icon

If you have an `.icns` file:

```bash
cp path/to/OscarWatch.icns publish/OscarWatch.app/Contents/Resources/
```

### 6. (Optional) Code sign

For distribution outside the App Store, ad-hoc signing removes the "damaged" warning for some users:

```bash
codesign --force --deep --sign - publish/OscarWatch.app
```

For proper distribution, sign with a Developer ID certificate:

```bash
codesign --force --deep --sign "Developer ID Application: Your Name (TEAMID)" \
  --options runtime \
  publish/OscarWatch.app
```

### 7. Create the DMG

```bash
hdiutil create \
  -volname "OscarWatch" \
  -srcfolder publish/OscarWatch.app \
  -ov \
  -format UDZO \
  publish/OscarWatch-0.9.6-osx-arm64.dmg
```

The `-format UDZO` flag compresses the image (typically ~66 MB for OscarWatch).

### 8. (Optional) Notarise

Required for users to open the app without Gatekeeper warnings on macOS 10.15+:

```bash
xcrun notarytool submit publish/OscarWatch-0.9.6-osx-arm64.dmg \
  --apple-id "your@email.com" \
  --team-id "TEAMID" \
  --password "@keychain:AC_PASSWORD" \
  --wait

# After notarisation succeeds, staple the ticket
xcrun stapler staple publish/OscarWatch-0.9.6-osx-arm64.dmg
```

## Output

The final DMG will be at:

```
publish/OscarWatch-{version}-{rid}.dmg
```

## Notes

- The app is **not code-signed or notarised** by default. Users will see a Gatekeeper warning on first launch. They can bypass it with right-click → Open, or System Settings → Privacy & Security → Open Anyway.
- The `.app` bundle includes the full .NET runtime (~66 MB compressed) since it's self-contained.
- The `publish/` directory is in `.gitignore` and should not be committed.
- For CI/CD, the GitHub Actions workflow in `.github/workflows/publish.yml` produces `tar.gz` archives for macOS. The DMG creation is a local-only step for now.
