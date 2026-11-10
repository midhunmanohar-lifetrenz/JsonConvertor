#!/usr/bin/env bash
# Builds self-contained single-file releases.
#   ./publish.sh            -> macOS Apple Silicon (.app + .dmg) and Windows x64 (.exe)
#   ./publish.sh mac        -> macOS only
#   ./publish.sh win        -> Windows only
# Output goes to ./dist
set -euo pipefail
cd "$(dirname "$0")"
TARGET="${1:-all}"
PROJECT="JSONConverterPro.App/JSONConverterPro.App.csproj"
VERSION="$(grep -m1 '<Version>' "$PROJECT" | sed -E 's/.*<Version>(.*)<\/Version>.*/\1/')"
COMMON=(-c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false)

publish_win() {
  echo "==> Windows x64"
  rm -rf dist/win-x64
  dotnet publish "$PROJECT" "${COMMON[@]}" -r win-x64 -o dist/win-x64
  echo "    dist/win-x64/JSONConverterPro.exe"
}

publish_mac() {
  echo "==> macOS arm64 (Apple Silicon)"
  rm -rf dist/osx-arm64 "dist/JSON Converter Pro.app"
  dotnet publish "$PROJECT" "${COMMON[@]}" -r osx-arm64 -o dist/osx-arm64
  APP="dist/JSON Converter Pro.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp dist/osx-arm64/JSONConverterPro "$APP/Contents/MacOS/JSONConverterPro"
  chmod +x "$APP/Contents/MacOS/JSONConverterPro"
  # On macOS the native libraries (Avalonia, Skia, HarfBuzz) are not embedded in the single file; ship them beside it.
  cp dist/osx-arm64/*.dylib "$APP/Contents/MacOS/"
  cp JSONConverterPro.App/Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
  cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>JSON Converter Pro</string>
  <key>CFBundleDisplayName</key><string>JSON Converter Pro</string>
  <key>CFBundleIdentifier</key><string>com.lifetrenz.jsonconverterpro</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>JSONConverterPro</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleDocumentTypes</key><array><dict>
    <key>CFBundleTypeName</key><string>JSON</string><key>CFBundleTypeRole</key><string>Viewer</string>
    <key>LSItemContentTypes</key><array><string>public.json</string><string>public.plain-text</string></array>
  </dict></array>
</dict></plist>
PLIST
  # Ad-hoc signature: required for arm64 binaries to run. Replace with a Developer ID identity to distribute widely.
  SIGN_ID="${CODESIGN_IDENTITY:--}"
  codesign --force --deep --sign "$SIGN_ID" "$APP"
  codesign --verify --deep --strict "$APP" && echo "    signature OK (identity: $SIGN_ID)"
  rm -f "dist/JSONConverterPro-${VERSION}-macos-arm64.dmg"
  STAGE="$(mktemp -d)"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  hdiutil create -quiet -volname "JSON Converter Pro" -srcfolder "$STAGE" -ov -format UDZO "dist/JSONConverterPro-${VERSION}-macos-arm64.dmg"
  rm -rf "$STAGE"
  echo "    $APP"
  echo "    dist/JSONConverterPro-${VERSION}-macos-arm64.dmg"
}

case "$TARGET" in
  win) publish_win ;;
  mac) publish_mac ;;
  all) publish_mac; publish_win ;;
  *) echo "usage: $0 [mac|win|all]"; exit 1 ;;
esac
