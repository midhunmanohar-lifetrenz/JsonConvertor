#!/usr/bin/env bash
# Builds self-contained single-file releases.
#   ./publish.sh            -> macOS Apple Silicon and Intel (.app + .dmg) and Windows x64 (.exe)
#   ./publish.sh mac        -> macOS only (both); mac-arm64 / mac-x64 for one
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
  mkdir -p dist/release
  rm -f "dist/release/JSONConverterPro-${VERSION}-win-x64-portable.zip"
  STAGE="$(mktemp -d)"
  cp dist/win-x64/JSONConverterPro.exe THIRD-PARTY-NOTICES.txt docs/USER_GUIDE.md "$STAGE/"
  (cd "$STAGE" && zip -q -X "$OLDPWD/dist/release/JSONConverterPro-${VERSION}-win-x64-portable.zip" ./*)
  rm -rf "$STAGE"
  echo "    dist/win-x64/JSONConverterPro.exe"
  echo "    dist/release/JSONConverterPro-${VERSION}-win-x64-portable.zip"
  echo "    (Windows installer: run ./publish.ps1 -Installer on Windows, or the release workflow)"
}

checksums() {
  (cd dist/release && rm -f SHA256SUMS.txt && shasum -a 256 *.zip *.dmg *.exe 2>/dev/null > SHA256SUMS.txt; cat SHA256SUMS.txt)
}

# publish_mac arm64|x64   (arm64 = Apple Silicon, x64 = Intel)
publish_mac() {
  local ARCH="$1"
  echo "==> macOS ${ARCH}"
  # The Apple Silicon app keeps its usual place; the Intel one is built in its own folder.
  local APP="dist/JSON Converter Pro.app"
  [ "$ARCH" = "x64" ] && APP="dist/intel/JSON Converter Pro.app"
  rm -rf "dist/osx-${ARCH}" "$APP"
  dotnet publish "$PROJECT" "${COMMON[@]}" -r "osx-${ARCH}" -o "dist/osx-${ARCH}"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp dist/osx-${ARCH}/JSONConverterPro "$APP/Contents/MacOS/JSONConverterPro"
  chmod +x "$APP/Contents/MacOS/JSONConverterPro"
  # On macOS the native libraries (Avalonia, Skia, HarfBuzz) are not embedded in the single file; ship them beside it.
  cp dist/osx-${ARCH}/*.dylib "$APP/Contents/MacOS/"
  cp JSONConverterPro.App/Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
  cp THIRD-PARTY-NOTICES.txt "$APP/Contents/Resources/"
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
  # Ad-hoc signature: required for Apple Silicon binaries to run. Replace with a Developer ID identity to distribute widely.
  SIGN_ID="${CODESIGN_IDENTITY:--}"
  codesign --force --deep --sign "$SIGN_ID" "$APP"
  codesign --verify --deep --strict "$APP" && echo "    signature OK (identity: $SIGN_ID)"
  mkdir -p dist/release
  rm -f "dist/release/JSONConverterPro-${VERSION}-macos-${ARCH}.dmg" "dist/JSONConverterPro-${VERSION}-macos-${ARCH}.dmg"
  STAGE="$(mktemp -d)"
  cp -R "$APP" "$STAGE/"
  cp docs/USER_GUIDE.md THIRD-PARTY-NOTICES.txt "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  hdiutil create -quiet -volname "JSON Converter Pro" -srcfolder "$STAGE" -ov -format UDZO "dist/release/JSONConverterPro-${VERSION}-macos-${ARCH}.dmg"
  rm -rf "$STAGE"
  echo "    $APP"
  echo "    dist/release/JSONConverterPro-${VERSION}-macos-${ARCH}.dmg"
}

case "$TARGET" in
  win) publish_win ;;
  mac) publish_mac arm64; publish_mac x64 ;;
  mac-arm64) publish_mac arm64 ;;
  mac-x64) publish_mac x64 ;;
  all) publish_mac arm64; publish_mac x64; publish_win; checksums ;;
  *) echo "usage: $0 [mac|mac-arm64|mac-x64|win|all]"; exit 1 ;;
esac
