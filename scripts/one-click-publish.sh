#!/usr/bin/env bash
set -euo pipefail

TARGET="${1:-all}"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${VERSION:-1.0.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH="$ROOT/publish"
ARTIFACTS="$ROOT/artifacts"
mkdir -p "$PUBLISH" "$ARTIFACTS"

zip_dir() {
  local src="$1" out="$2"
  rm -f "$out"
  (cd "$src" && zip -qr "$out" .)
  echo "Artifact: $out"
}

publish_desktop() {
  local project="$ROOT/src/Hello1Drive.Desktop/Hello1Drive.Desktop.csproj"
  for rid in win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64; do
    echo "=== Desktop: $rid ==="
    local target="$PUBLISH/desktop/$rid"
    rm -rf "$target"; mkdir -p "$target"
    local output="$target"
    if [[ "$rid" == osx-* ]]; then output="$target/raw"; mkdir -p "$output"; fi
    dotnet publish "$project" -c "$CONFIGURATION" -r "$rid" --self-contained true -o "$output" \
      /p:Version="$VERSION" /p:PublishSingleFile=false

    if [[ "$rid" == osx-* ]]; then
      local app="$target/Hello1Drive.app"
      mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
      cp -R "$output"/* "$app/Contents/MacOS/"
      cp "$ROOT/src/Hello1Drive.Core/Assets/app-icon.icns" "$app/Contents/Resources/app-icon.icns"
      chmod +x "$app/Contents/MacOS/Hello1Drive" 2>/dev/null || true
      cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>Hello1Drive</string>
<key>CFBundleDisplayName</key><string>Hello1Drive</string>
<key>CFBundleIdentifier</key><string>com.xiaowei.hello1drive</string>
<key>CFBundleExecutable</key><string>Hello1Drive</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>app-icon.icns</string>
<key>CFBundleShortVersionString</key><string>$VERSION</string>
<key>CFBundleVersion</key><string>$VERSION</string>
</dict></plist>
EOF
      rm -rf "$output"
    fi
    zip_dir "$target" "$ARTIFACTS/Hello1Drive-Desktop-$rid-$VERSION.zip"
  done
}

publish_browser() {
  dotnet workload install wasm-tools
  dotnet publish "$ROOT/src/Hello1Drive.Browser/Hello1Drive.Browser.csproj" -c "$CONFIGURATION" /p:Version="$VERSION"
  local src="$ROOT/src/Hello1Drive.Browser/bin/$CONFIGURATION/net10.0-browser/publish/wwwroot"
  local target="$PUBLISH/browser"
  rm -rf "$target"; mkdir -p "$target"; cp -R "$src"/* "$target"/
  zip_dir "$target" "$ARTIFACTS/Hello1Drive-Browser-$VERSION.zip"
}

publish_android() {
  dotnet workload install android
  dotnet publish "$ROOT/src/Hello1Drive.Android/Hello1Drive.Android.csproj" -c "$CONFIGURATION" -f net10.0-android36.0 /p:Version="$VERSION"
  local src="$ROOT/src/Hello1Drive.Android/bin/$CONFIGURATION/net10.0-android36.0/publish"
  local target="$PUBLISH/android"
  rm -rf "$target"; mkdir -p "$target"
  find "$src" -maxdepth 1 -type f \( -name '*.apk' -o -name '*.aab' \) -exec cp {} "$target"/ \;
  zip_dir "$target" "$ARTIFACTS/Hello1Drive-Android-$VERSION.zip"
}

publish_ios() {
  if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "iOS requires macOS + Xcode; skipped."
    return
  fi
  dotnet workload install ios
  dotnet publish "$ROOT/src/Hello1Drive.iOS/Hello1Drive.iOS.csproj" -c "$CONFIGURATION" -f net10.0-ios26.0 -r iossimulator-arm64 \
    /p:CodesignKey= /p:CodesignProvision= /p:Version="$VERSION"
  local src="$ROOT/src/Hello1Drive.iOS/bin/$CONFIGURATION/net10.0-ios26.0/iossimulator-arm64/publish"
  local target="$PUBLISH/ios-simulator-arm64"
  rm -rf "$target"; mkdir -p "$target"; cp -R "$src"/* "$target"/
  zip_dir "$target" "$ARTIFACTS/Hello1Drive-iOS-Simulator-arm64-$VERSION.zip"
}

case "$TARGET" in
  desktop) publish_desktop ;;
  browser) publish_browser ;;
  android) publish_android ;;
  ios) publish_ios ;;
  all) publish_desktop; publish_browser; publish_android; publish_ios ;;
  *) echo "Usage: $0 [all|desktop|browser|android|ios]"; exit 2 ;;
esac
