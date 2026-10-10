#!/usr/bin/env bash
# Builds a signed, notarized and stapled DMG of Wino Mail for macOS.
# Run from Terminal on a Mac: bash scripts/release/build-macos-release.sh [--arch arm64|universal] [--non-interactive]
# See docs/releases.md ("macOS DMG") and docs/local-script-environment.md.
set -euo pipefail

IDENTITY="Developer ID Application: Burak Kaan Kose (4VB7YWRAQ9)"
TEAM_ID="4VB7YWRAQ9"
BUNDLE_ID="com.winomail.macos"
APP_NAME="Wino Mail.app"
VOLUME_NAME="Wino Mail"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj"
MANIFEST="$REPO_ROOT/src/Wino.Mail.WinUI/Package.appxmanifest"
ENTITLEMENTS="$REPO_ROOT/src/Wino.Mail.MacOS/Entitlements.plist"
SPARKLE_PLIST="$REPO_ROOT/src/Wino.Mail.MacOS/Sparkle.plist"
SPARKLE_VERSION_FILE="$REPO_ROOT/src/Wino.Core.MacOS.Bindings/Sparkle/Native/Sparkle.version"
# Sparkle signing key in the login keychain (generate_keys --account).
SPARKLE_ACCOUNT="com.winomail.macos"
# The DMG and appcast.xml are uploaded here; Sparkle.plist's SUFeedURL points at the appcast.
DOWNLOAD_BASE_URL="https://download.winomail.app/macos"

ARCH=""
NON_INTERACTIVE=0
OUTPUT_ROOT="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}"

usage() {
  cat <<'EOF'
Usage: build-macos-release.sh [options]

  --arch arm64|universal   Apple silicon only, or Apple silicon + Intel (default: universal).
  --output-root <path>     Release root. Default: $WINO_RELEASES_ROOT or ~/Wino Releases.
  --non-interactive        Do not prompt. Missing choices use their defaults.
  -h, --help               Show this help.

Requires WINO_NOTARY_KEY_PATH, WINO_NOTARY_KEY_ID and WINO_NOTARY_ISSUER_ID.
EOF
}

step() { printf '\n==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
fail() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --arch) [ $# -ge 2 ] || fail "--arch needs a value: arm64 or universal."; ARCH="$2"; shift 2 ;;
    --arch=*) ARCH="${1#*=}"; shift ;;
    --output-root) [ $# -ge 2 ] || fail "--output-root needs a path."; OUTPUT_ROOT="$2"; shift 2 ;;
    --output-root=*) OUTPUT_ROOT="${1#*=}"; shift ;;
    --non-interactive) NON_INTERACTIVE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

if [ "$NON_INTERACTIVE" -eq 0 ] && [ ! -t 0 ]; then
  fail "No terminal for prompts. Run from Terminal or pass --non-interactive."
fi

# ---------------------------------------------------------------- preflight
step "Preflight"
[ "$(uname -s)" = "Darwin" ] || fail "This script runs on macOS only."

DOTNET=""
if [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET="$HOME/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then DOTNET="$(command -v dotnet)"
else fail "The .NET SDK was not found in ~/.dotnet or on PATH. Install the SDK selected by global.json and the macos workload."
fi
"$DOTNET" --version >/dev/null 2>&1 || fail "'$DOTNET --version' failed. Check that the SDK selected by global.json is installed."
"$DOTNET" workload list 2>/dev/null | grep -q '^macos' || fail "The .NET macos workload is not installed. Run: $DOTNET workload install macos"
info ".NET SDK $("$DOTNET" --version) ($DOTNET)"

xcode-select -p >/dev/null 2>&1 || fail "Xcode command line tools are missing. Run: xcode-select --install"
for tool in notarytool stapler; do
  xcrun --find "$tool" >/dev/null 2>&1 || fail "'xcrun $tool' is unavailable. Install Xcode or update the command line tools."
done
for tool in codesign hdiutil spctl ditto lipo plutil shasum security curl tar xmllint; do
  command -v "$tool" >/dev/null 2>&1 || fail "'$tool' was not found."
done
info "Xcode tools: $(xcode-select -p)"

security find-identity -v -p codesigning | grep -qF "\"$IDENTITY\"" ||
  fail "Signing identity \"$IDENTITY\" is not in the keychain. Import the Developer ID Application certificate and its private key."
info "Signing identity: $IDENTITY"

[ -n "${WINO_NOTARY_KEY_PATH:-}" ] || fail "WINO_NOTARY_KEY_PATH is not set (App Store Connect API key .p8 file)."
[ -r "$WINO_NOTARY_KEY_PATH" ] || fail "WINO_NOTARY_KEY_PATH does not point to a readable file."
[ -n "${WINO_NOTARY_KEY_ID:-}" ] || fail "WINO_NOTARY_KEY_ID is not set."
[ -n "${WINO_NOTARY_ISSUER_ID:-}" ] || fail "WINO_NOTARY_ISSUER_ID is not set."
info "Notarization key: $WINO_NOTARY_KEY_ID"

[ -f "$PROJECT" ] || fail "Project not found: $PROJECT"
[ -f "$SPARKLE_PLIST" ] || fail "Sparkle settings not found: $SPARKLE_PLIST"
[ -f "$SPARKLE_VERSION_FILE" ] || fail "Sparkle version file not found: $SPARKLE_VERSION_FILE"

# Sparkle's sign_update and generate_keys come from the official release the app embeds
# (Sparkle.version), cached outside the repository and checked against the recorded hash.
SPARKLE_VERSION="$(sed -n 's/^version=//p' "$SPARKLE_VERSION_FILE")"
SPARKLE_URL="$(sed -n 's/^url=//p' "$SPARKLE_VERSION_FILE")"
SPARKLE_SHA256="$(sed -n 's/^sha256=//p' "$SPARKLE_VERSION_FILE")"
SPARKLE_TOOLS="$HOME/Library/Caches/WinoMail/sparkle-$SPARKLE_VERSION"
if [ ! -x "$SPARKLE_TOOLS/bin/sign_update" ]; then
  info "Downloading Sparkle $SPARKLE_VERSION tools"
  SPARKLE_DOWNLOAD="$(mktemp -d)"
  curl -fsSL -o "$SPARKLE_DOWNLOAD/sparkle.tar.xz" "$SPARKLE_URL" || fail "Downloading $SPARKLE_URL failed."
  [ "$(shasum -a 256 "$SPARKLE_DOWNLOAD/sparkle.tar.xz" | cut -d' ' -f1)" = "$SPARKLE_SHA256" ] ||
    fail "The Sparkle download does not match the hash in $SPARKLE_VERSION_FILE."
  mkdir -p "$SPARKLE_DOWNLOAD/release"
  tar -xJf "$SPARKLE_DOWNLOAD/sparkle.tar.xz" -C "$SPARKLE_DOWNLOAD/release" ./bin
  rm -rf "$SPARKLE_TOOLS"; mkdir -p "$SPARKLE_TOOLS"
  mv "$SPARKLE_DOWNLOAD/release/bin" "$SPARKLE_TOOLS/bin"
  rm -rf "$SPARKLE_DOWNLOAD"
fi
SPARKLE_PUBLIC_KEY="$(plutil -extract SUPublicEDKey raw "$SPARKLE_PLIST")"
KEYCHAIN_PUBLIC_KEY="$("$SPARKLE_TOOLS/bin/generate_keys" --account "$SPARKLE_ACCOUNT" -p 2>/dev/null || true)"
[ -n "$KEYCHAIN_PUBLIC_KEY" ] ||
  fail "No Sparkle signing key for account $SPARKLE_ACCOUNT in the keychain. Import the backup with: $SPARKLE_TOOLS/bin/generate_keys --account $SPARKLE_ACCOUNT -f <file>"
[ "$KEYCHAIN_PUBLIC_KEY" = "$SPARKLE_PUBLIC_KEY" ] ||
  fail "The keychain's Sparkle key does not match SUPublicEDKey in Sparkle.plist."
info "Sparkle $SPARKLE_VERSION, update key $SPARKLE_PUBLIC_KEY"
[ -f "$MANIFEST" ] || fail "Manifest not found: $MANIFEST"

# ---------------------------------------------------------------- version
FULL_VERSION="$(awk '/<Identity/{f=1} f && match($0, /Version="[^"]*"/){print substr($0, RSTART+9, RLENGTH-10); exit}' "$MANIFEST")"
[[ "$FULL_VERSION" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] ||
  fail "Package.appxmanifest Identity Version '$FULL_VERSION' must have four numeric components."
[ "${BASH_REMATCH[1]}" -ne 0 ] || fail "The major version must be nonzero ($FULL_VERSION)."
[ "${BASH_REMATCH[4]}" -eq 0 ] || fail "The revision must be zero ($FULL_VERSION)."
VERSION="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
info "Version: $VERSION (manifest $FULL_VERSION)"

# ---------------------------------------------------------------- choices
if [ -z "$ARCH" ]; then
  if [ "$NON_INTERACTIVE" -eq 1 ]; then
    ARCH="universal"
  else
    printf '\nArchitecture:\n  1) Apple silicon only (arm64)\n  2) Universal (Apple silicon + Intel)\nSelect [2]: '
    read -r choice
    case "${choice:-2}" in
      1) ARCH="arm64" ;;
      2) ARCH="universal" ;;
      *) fail "Select 1 or 2." ;;
    esac
  fi
fi
case "$ARCH" in
  arm64) RID_ARGS=(-r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64); EXPECTED_ARCHS="arm64" ;;
  universal) RID_ARGS=('-p:RuntimeIdentifiers="osx-arm64;osx-x64"'); EXPECTED_ARCHS="x86_64 arm64" ;;
  *) fail "--arch must be arm64 or universal." ;;
esac

DEST="$OUTPUT_ROOT/$FULL_VERSION/macOS"
DMG_NAME="WinoMail_${VERSION}_${ARCH}.dmg"
[ ! -e "$DEST/$DMG_NAME" ] || fail "$DEST/$DMG_NAME already exists. Move it or change the manifest version."

info "Architecture: $ARCH"
info "Output: $DEST/$DMG_NAME"
if [ "$NON_INTERACTIVE" -eq 0 ]; then
  printf '\nBuild, sign and notarize now? [Y/n]: '
  read -r answer
  case "${answer:-y}" in [Yy]*) ;; *) echo "Cancelled."; exit 0 ;; esac
fi

# ---------------------------------------------------------------- staging
mkdir -p "$OUTPUT_ROOT"
LOCK="$OUTPUT_ROOT/.macos-release.lock"
mkdir "$LOCK" 2>/dev/null || fail "Another macOS release run holds $LOCK. Remove it if no run is active."
STAGE="$OUTPUT_ROOT/.staging/macos-$(date +%Y%m%d-%H%M%S)-$$"
MOUNT="$STAGE/mount"
mkdir -p "$STAGE"
SUCCESS=0
cleanup() {
  if [ -d "$MOUNT" ] && mount | grep -qF "$MOUNT"; then hdiutil detach -quiet "$MOUNT" || true; fi
  rmdir "$LOCK" 2>/dev/null || true
  if [ "$SUCCESS" -eq 1 ]; then
    rm -rf "$STAGE"; rmdir "$OUTPUT_ROOT/.staging" 2>/dev/null || true
  else
    printf '\nFailed. Logs and intermediate files remain in %s\n' "$STAGE" >&2
  fi
}
trap cleanup EXIT

# ---------------------------------------------------------------- build
step "Publishing Release ($ARCH)"
PROJECT_DIR="$(dirname "$PROJECT")"
# Release outputs only; Debug outputs stay untouched.
rm -rf "$PROJECT_DIR/bin/Release" "$PROJECT_DIR/obj/Release"
BUILD_LOG="$STAGE/publish.log"
# Info.plist holds development values; this partial manifest overrides them (see the csproj).
VERSION_PLIST="$STAGE/Version.plist"
plutil -create xml1 "$VERSION_PLIST"
plutil -insert CFBundleShortVersionString -string "$VERSION" "$VERSION_PLIST"
plutil -insert CFBundleVersion -string "$VERSION" "$VERSION_PLIST"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
if ! "$DOTNET" publish "$PROJECT" -c Release -nologo \
    -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true \
    "${RID_ARGS[@]}" \
    -p:WinoVersionPlist="$VERSION_PLIST" \
    -p:ApplicationDisplayVersion="$VERSION" -p:ApplicationVersion="$VERSION" \
    -p:EnableCodeSigning=true -p:CodesignKey="$IDENTITY" -p:CodesignProvision= \
    -p:CodesignEntitlements="$ENTITLEMENTS" -p:UseHardenedRuntime=true \
    -p:CreatePackage=false >"$BUILD_LOG" 2>&1; then
  grep -E ' error |error [A-Z]+[0-9]+' "$BUILD_LOG" | sort -u | head -40 >&2 || tail -40 "$BUILD_LOG" >&2
  fail "dotnet publish failed. Full log: $BUILD_LOG"
fi
info "Log: $BUILD_LOG"

BIN="$PROJECT_DIR/bin/Release/net10.0-macos"
if [ "$ARCH" = "arm64" ]; then APP_SRC="$BIN/osx-arm64/$APP_NAME"; else APP_SRC="$BIN/$APP_NAME"; fi
[ -d "$APP_SRC" ] || APP_SRC="$(find "$BIN" -maxdepth 2 -type d -name "$APP_NAME" | head -1)"
[ -n "$APP_SRC" ] && [ -d "$APP_SRC" ] || fail "The publish did not produce $APP_NAME under $BIN."
info "App: $APP_SRC"

# ---------------------------------------------------------------- verify app
step "Verifying the app signature"
APP_DIR="$STAGE/dmg/$APP_NAME"
mkdir -p "$STAGE/dmg"
ditto "$APP_SRC" "$APP_DIR"
codesign --verify --deep --strict --verbose=2 "$APP_DIR" >"$STAGE/codesign-verify.txt" 2>&1 ||
  { cat "$STAGE/codesign-verify.txt" >&2; fail "codesign verification failed."; }
SIG="$(codesign -dvv "$APP_DIR" 2>&1)"
grep -qF "Authority=$IDENTITY" <<<"$SIG" || fail "The app is not signed with $IDENTITY."
grep -qF "TeamIdentifier=$TEAM_ID" <<<"$SIG" || fail "The app team identifier is not $TEAM_ID."
grep -qE 'flags=0x[0-9a-f]*\(runtime\)' <<<"$SIG" || fail "The app is not signed with the hardened runtime."
grep -q 'Timestamp=' <<<"$SIG" || fail "The app signature has no secure timestamp."
ENTS="$(codesign -d --entitlements :- "$APP_DIR" 2>/dev/null)"
! grep -q 'get-task-allow' <<<"$ENTS" || fail "The app entitlements contain get-task-allow."
grep -q 'com.apple.security.app-sandbox' <<<"$ENTS" || fail "The app entitlements lack the app sandbox."
grep -q 'com.winomail.macos-spki' <<<"$ENTS" || fail "The app entitlements lack Sparkle's installer exception."
PLIST="$APP_DIR/Contents/Info.plist"
[ "$(plutil -extract CFBundleIdentifier raw "$PLIST")" = "$BUNDLE_ID" ] || fail "CFBundleIdentifier is not $BUNDLE_ID."
[ "$(plutil -extract CFBundleShortVersionString raw "$PLIST")" = "$VERSION" ] || fail "CFBundleShortVersionString is not $VERSION."
[ "$(plutil -extract CFBundleVersion raw "$PLIST")" = "$VERSION" ] || fail "CFBundleVersion is not $VERSION."
EXE="$APP_DIR/Contents/MacOS/$(plutil -extract CFBundleExecutable raw "$PLIST")"
ARCHS="$(lipo -archs "$EXE")"
for a in $EXPECTED_ARCHS; do [[ " $ARCHS " == *" $a "* ]] || fail "The executable lacks $a (has: $ARCHS)."; done
SPARKLE_FRAMEWORK="$APP_DIR/Contents/Frameworks/Sparkle.framework"
[ -d "$SPARKLE_FRAMEWORK" ] || fail "The app does not contain Sparkle.framework."
for nested in "" /Versions/B/Autoupdate /Versions/B/Updater.app /Versions/B/XPCServices/Installer.xpc; do
  NESTED_SIG="$(codesign -dvv "$SPARKLE_FRAMEWORK$nested" 2>&1)"
  grep -qF "Authority=$IDENTITY" <<<"$NESTED_SIG" || fail "Sparkle.framework$nested is not signed with $IDENTITY."
  grep -qE 'flags=0x[0-9a-f]*\(runtime\)' <<<"$NESTED_SIG" || fail "Sparkle.framework$nested lacks the hardened runtime."
done
[ "$(plutil -extract SUPublicEDKey raw "$PLIST")" = "$SPARKLE_PUBLIC_KEY" ] || fail "The app's SUPublicEDKey does not match Sparkle.plist."
[ "$(plutil -extract SUFeedURL raw "$PLIST")" = "$DOWNLOAD_BASE_URL/appcast.xml" ] || fail "The app's SUFeedURL is not $DOWNLOAD_BASE_URL/appcast.xml."
info "Signed by $IDENTITY, hardened runtime, no get-task-allow"
info "Sparkle.framework and its helpers signed; feed $DOWNLOAD_BASE_URL/appcast.xml"
info "$BUNDLE_ID $VERSION, executable: $ARCHS"

# ---------------------------------------------------------------- symbols
SYMBOLS="$STAGE/Symbols/$ARCH"
mkdir -p "$SYMBOLS"
while IFS= read -r -d '' d; do ditto "$d" "$SYMBOLS/$(basename "$d")"; done < <(find "$BIN" -maxdepth 3 -type d -name '*.dSYM' -print0)
# Release bundles omit PDBs; they stay beside each RID's intermediate assemblies.
for rid_dir in "$BIN"/osx-*; do
  [ -d "$rid_dir" ] || continue
  for p in "$rid_dir"/*.pdb; do
    [ -f "$p" ] || continue
    mkdir -p "$SYMBOLS/$(basename "$rid_dir")"; cp "$p" "$SYMBOLS/$(basename "$rid_dir")/"
  done
done
[ -n "$(ls -A "$SYMBOLS")" ] || rmdir "$SYMBOLS" "$STAGE/Symbols"

# ---------------------------------------------------------------- DMG
step "Creating the DMG"
ln -s /Applications "$STAGE/dmg/Applications"
DMG="$STAGE/$DMG_NAME"
hdiutil create -quiet -volname "$VOLUME_NAME" -srcfolder "$STAGE/dmg" -fs HFS+ -format UDZO -ov "$DMG" ||
  fail "hdiutil create failed."
codesign --sign "$IDENTITY" --timestamp "$DMG" || fail "Signing the DMG failed."
codesign --verify --strict --verbose=2 "$DMG" >/dev/null 2>&1 || fail "DMG signature verification failed."
info "$DMG_NAME ($(du -h "$DMG" | cut -f1 | tr -d ' '))"

# ---------------------------------------------------------------- notarize
step "Notarizing (this usually takes a few minutes)"
NOTARY=(--key "$WINO_NOTARY_KEY_PATH" --key-id "$WINO_NOTARY_KEY_ID" --issuer "$WINO_NOTARY_ISSUER_ID")
SUBMIT_JSON="$STAGE/notary-submit.json"
NOTARY_LOG="$STAGE/${DMG_NAME%.dmg}.notarization.json"
set +e
xcrun notarytool submit "$DMG" "${NOTARY[@]}" --wait --timeout 1h --output-format json >"$SUBMIT_JSON" 2>"$STAGE/notary-submit.err"
submit_rc=$?
set -e
SUBMISSION_ID="$(plutil -extract id raw "$SUBMIT_JSON" 2>/dev/null || true)"
STATUS="$(plutil -extract status raw "$SUBMIT_JSON" 2>/dev/null || true)"
[ -n "$SUBMISSION_ID" ] || { cat "$STAGE/notary-submit.err" "$SUBMIT_JSON" >&2; fail "notarytool submit failed (exit $submit_rc)."; }
info "Submission $SUBMISSION_ID: ${STATUS:-unknown}"
xcrun notarytool log "$SUBMISSION_ID" "${NOTARY[@]}" "$NOTARY_LOG" >/dev/null 2>&1 || info "Could not download the notarization log."
if [ "$STATUS" != "Accepted" ]; then
  if [ -f "$NOTARY_LOG" ]; then
    echo "Notarization issues:" >&2
    plutil -extract issues json -o - "$NOTARY_LOG" 2>/dev/null >&2 || cat "$NOTARY_LOG" >&2
  fi
  fail "Notarization status is '${STATUS:-unknown}'. See $NOTARY_LOG"
fi

# ---------------------------------------------------------------- staple and assess
step "Stapling and assessing"
xcrun stapler staple -q "$DMG" || fail "stapler staple failed."
xcrun stapler validate -q "$DMG" || fail "stapler validate failed."
DMG_ASSESS="$(spctl --assess --type open --context context:primary-signature -vv "$DMG" 2>&1)" ||
  { echo "$DMG_ASSESS" >&2; fail "Gatekeeper rejected the DMG."; }
mkdir -p "$MOUNT"
hdiutil attach -quiet -nobrowse -readonly -noautoopen -mountpoint "$MOUNT" "$DMG" || fail "Mounting the DMG failed."
APP_ASSESS="$(spctl --assess --type execute -vv "$MOUNT/$APP_NAME" 2>&1)" ||
  { echo "$APP_ASSESS" >&2; fail "Gatekeeper rejected the app inside the DMG."; }
hdiutil detach -quiet "$MOUNT"
for out in "$DMG_ASSESS" "$APP_ASSESS"; do
  grep -q 'accepted' <<<"$out" && grep -q 'source=Notarized Developer ID' <<<"$out" ||
    { echo "$out" >&2; fail "Gatekeeper did not report a notarized Developer ID."; }
done
info "DMG: $(head -2 <<<"$DMG_ASSESS" | tr '\n' ' ')"
info "App: $(head -2 <<<"$APP_ASSESS" | tr '\n' ' ')"

# ---------------------------------------------------------------- appcast
# Sparkle signs the final (stapled) DMG; any later change to the file invalidates the signature.
step "Writing the Sparkle appcast"
SPARKLE_SIGNATURE="$("$SPARKLE_TOOLS/bin/sign_update" --account "$SPARKLE_ACCOUNT" "$DMG")" || fail "sign_update failed."
[[ "$SPARKLE_SIGNATURE" =~ ^sparkle:edSignature=\"[^\"]+\"\ length=\"[0-9]+\"$ ]] || fail "Unexpected sign_update output."
HARDWARE=""
[ "$ARCH" = "arm64" ] && HARDWARE="
      <sparkle:hardwareRequirements>arm64</sparkle:hardwareRequirements>"
APPCAST="$STAGE/appcast.xml"
cat >"$APPCAST" <<APPCAST_XML
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Wino Mail</title>
    <link>$DOWNLOAD_BASE_URL/appcast.xml</link>
    <item>
      <title>Wino Mail $VERSION</title>
      <pubDate>$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')</pubDate>
      <sparkle:version>$VERSION</sparkle:version>
      <sparkle:shortVersionString>$VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>$(plutil -extract LSMinimumSystemVersion raw "$PLIST")</sparkle:minimumSystemVersion>$HARDWARE
      <enclosure url="$DOWNLOAD_BASE_URL/$DMG_NAME" $SPARKLE_SIGNATURE type="application/octet-stream"/>
    </item>
  </channel>
</rss>
APPCAST_XML
xmllint --noout "$APPCAST" || fail "appcast.xml is not valid XML."
info "appcast.xml for $DMG_NAME"

# ---------------------------------------------------------------- finalize
step "Finalizing"
(cd "$STAGE" && shasum -a 256 "$DMG_NAME" >"$DMG_NAME.sha256")
mkdir -p "$DEST"
[ ! -e "$DEST/$DMG_NAME" ] || fail "$DEST/$DMG_NAME appeared during the run."
mv "$NOTARY_LOG" "$DEST/"
mv "$DMG.sha256" "$DEST/"
mv "$APPCAST" "$DEST/appcast.xml"
if [ -d "$STAGE/Symbols/$ARCH" ]; then
  rm -rf "$DEST/Symbols/$ARCH"; mkdir -p "$DEST/Symbols"; mv "$STAGE/Symbols/$ARCH" "$DEST/Symbols/$ARCH"
fi
mv "$DMG" "$DEST/"
SUCCESS=1

printf '\nDone.\n'
info "$DEST/$DMG_NAME"
info "$DEST/$DMG_NAME.sha256"
info "$DEST/appcast.xml"
info "$DEST/$(basename "$NOTARY_LOG")"
[ -d "$DEST/Symbols/$ARCH" ] && info "$DEST/Symbols/$ARCH"
printf '\nTo publish, upload %s first and then appcast.xml to %s/.\n' "$DMG_NAME" "$DOWNLOAD_BASE_URL"
exit 0
