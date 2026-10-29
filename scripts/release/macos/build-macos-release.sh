#!/usr/bin/env bash
# Builds a signed, notarized and stapled DMG of Wino Mail for macOS.
# Run from Terminal on a Mac: bash scripts/release/macos/build-macos-release.sh [--arch arm64|universal] [--non-interactive]
# See docs/releases.md ("macOS DMG") and docs/local-script-environment.md.
set -euo pipefail

IDENTITY="Developer ID Application: Burak Kaan Kose (4VB7YWRAQ9)"
TEAM_ID="4VB7YWRAQ9"
BUNDLE_ID="com.winomail.macos"
APP_NAME="Wino Mail.app"
VOLUME_NAME="Wino Mail"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
PROJECT="$REPO_ROOT/src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj"
MANIFEST="$REPO_ROOT/src/Wino.Mail.WinUI/Package.appxmanifest"
ENTITLEMENTS="$REPO_ROOT/src/Wino.Mail.MacOS/Entitlements.plist"
SPARKLE_PLIST="$REPO_ROOT/src/Wino.Mail.MacOS/Sparkle.plist"
SPARKLE_VERSION_FILE="$REPO_ROOT/src/Wino.Core.Apple.Bindings/Sparkle/Native/Sparkle.version"
# Sparkle signing key in the login keychain (generate_keys --account).
SPARKLE_ACCOUNT="com.winomail.macos"
# The DMG and appcast.xml are uploaded here; Sparkle.plist's SUFeedURL points at the appcast.
DOWNLOAD_BASE_URL="https://download.winomail.app/macos"
# DMG window layout. The artwork in dmg/background.html is drawn around these icon centres; move them together.
DMG_BACKGROUND="$REPO_ROOT/scripts/release/macos/dmg/background.tiff"
DMG_WINDOW_WIDTH=660
DMG_WINDOW_HEIGHT=350
# Finder window bounds include the title bar (32 points on macOS 26).
DMG_TITLE_BAR_HEIGHT=32
DMG_ICON_SIZE=128
DMG_APP_POSITION="170, 165"
DMG_APPLICATIONS_POSITION="490, 165"

ARCH=""
BUILD_NUMBER=""
NON_INTERACTIVE=0
TRANSLATE=""
OUTPUT_ROOT="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}"

usage() {
  cat <<'EOF'
Usage: build-macos-release.sh [options]

  --arch arm64|universal   Apple silicon only, or Apple silicon + Intel (default: universal).
  --build-number <n>       CFBundleVersion and Sparkle version of this build.
                           Default: the UTC time as yyDDDHHMM, which always increases.
  --output-root <path>     Release root. Default: $WINO_RELEASES_ROOT or ~/Wino Releases.
  --translate              Run translate_resources.py --apply before building, without asking.
  --skip-translation       Build without translating and without asking.
  --non-interactive        Do not prompt. Missing choices use their defaults (no translation).
  -h, --help               Show this help.

Requires WINO_NOTARY_KEY_PATH, WINO_NOTARY_KEY_ID and WINO_NOTARY_ISSUER_ID.
EOF
}

step() { printf '\n==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
fail() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }
# shellcheck source=release-translation.sh
source "$(dirname "${BASH_SOURCE[0]}")/release-translation.sh"

while [ $# -gt 0 ]; do
  case "$1" in
    --arch) [ $# -ge 2 ] || fail "--arch needs a value: arm64 or universal."; ARCH="$2"; shift 2 ;;
    --arch=*) ARCH="${1#*=}"; shift ;;
    --build-number) [ $# -ge 2 ] || fail "--build-number needs a value."; BUILD_NUMBER="$2"; shift 2 ;;
    --build-number=*) BUILD_NUMBER="${1#*=}"; shift ;;
    --output-root) [ $# -ge 2 ] || fail "--output-root needs a path."; OUTPUT_ROOT="$2"; shift 2 ;;
    --output-root=*) OUTPUT_ROOT="${1#*=}"; shift ;;
    --translate) set_release_translation yes; shift ;;
    --skip-translation) set_release_translation no; shift ;;
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
for tool in codesign hdiutil osascript spctl ditto lipo plutil shasum security curl tar xmllint; do
  command -v "$tool" >/dev/null 2>&1 || fail "'$tool' was not found."
done
info "Xcode tools: $(xcode-select -p)"

security find-identity -v -p codesigning | grep -qF "\"$IDENTITY\"" ||
  fail "Signing identity \"$IDENTITY\" is not in the keychain. Import the Developer ID Application certificate and its private key."
info "Signing identity: $IDENTITY"

# The newest unexpired Developer ID profile for the bundle ID ("Wino Mail macOS Developer ID"). It carries Push
# Notifications (aps-environment production) for live notifications; macOS refuses to launch the app with that
# entitlement and no profile. Developer ID profiles are the ones with ProvisionsAllDevices.
PROFILE_UUID=""
PROFILE_NAME=""
PROFILE_EXPIRY=""
NOW="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
PROFILE_PLIST="$(mktemp)"
for dir in "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles" "$HOME/Library/MobileDevice/Provisioning Profiles"; do
  [ -d "$dir" ] || continue
  for file in "$dir"/*.provisionprofile; do
    [ -f "$file" ] || continue
    security cms -D -i "$file" >"$PROFILE_PLIST" 2>/dev/null || continue
    [ "$(plutil -extract Entitlements.com\\.apple\\.application-identifier raw "$PROFILE_PLIST" 2>/dev/null)" = "$TEAM_ID.$BUNDLE_ID" ] || continue
    [ "$(plutil -extract ProvisionsAllDevices raw "$PROFILE_PLIST" 2>/dev/null)" = "true" ] || continue
    [ "$(plutil -extract Entitlements.com\\.apple\\.developer\\.aps-environment raw "$PROFILE_PLIST" 2>/dev/null)" = "production" ] || continue
    expiry="$(plutil -extract ExpirationDate raw "$PROFILE_PLIST")"
    [[ "$expiry" > "$NOW" ]] || continue
    if [ -z "$PROFILE_EXPIRY" ] || [[ "$expiry" > "$PROFILE_EXPIRY" ]]; then
      PROFILE_UUID="$(plutil -extract UUID raw "$PROFILE_PLIST")"
      PROFILE_NAME="$(plutil -extract Name raw "$PROFILE_PLIST")"
      PROFILE_EXPIRY="$expiry"
    fi
  done
done
rm -f "$PROFILE_PLIST"
[ -n "$PROFILE_UUID" ] ||
  fail "No unexpired Developer ID provisioning profile with Push Notifications for $BUNDLE_ID is installed. Download \"Wino Mail macOS Developer ID\" from the developer portal and double-click it."
info "Profile: $PROFILE_NAME ($PROFILE_UUID), expires $PROFILE_EXPIRY"

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
[ -f "$DMG_BACKGROUND" ] || fail "DMG background not found: $DMG_BACKGROUND"
[ -f "$MANIFEST" ] || fail "Manifest not found: $MANIFEST"

# ---------------------------------------------------------------- version
FULL_VERSION="$(awk '/<Identity/{f=1} f && match($0, /Version="[^"]*"/){print substr($0, RSTART+9, RLENGTH-10); exit}' "$MANIFEST")"
[[ "$FULL_VERSION" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] ||
  fail "Package.appxmanifest Identity Version '$FULL_VERSION' must have four numeric components."
[ "${BASH_REMATCH[1]}" -ne 0 ] || fail "The major version must be nonzero ($FULL_VERSION)."
[ "${BASH_REMATCH[4]}" -eq 0 ] || fail "The revision must be zero ($FULL_VERSION)."
VERSION="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
# Sparkle and macOS compare CFBundleVersion, which must grow with every published build, also when the manifest
# version stays the same. The App Store script uses the same scheme, so both channels count alike.
[ -n "$BUILD_NUMBER" ] || BUILD_NUMBER="$(date -u +%y%j%H%M | sed 's/^0*//')"
[[ "$BUILD_NUMBER" =~ ^[1-9][0-9]*$ ]] || fail "--build-number must be a positive integer ($BUILD_NUMBER)."
info "Version: $VERSION (manifest $FULL_VERSION), build $BUILD_NUMBER"

# ---------------------------------------------------------------- translations
run_release_translation

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

DEST="$OUTPUT_ROOT/$FULL_VERSION/macOS/$BUILD_NUMBER"
DMG_NAME="WinoMail_${VERSION}_${BUILD_NUMBER}_${ARCH}.dmg"
[ ! -e "$DEST/$DMG_NAME" ] || fail "$DEST/$DMG_NAME already exists. Wait a minute or pass another --build-number."

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
# release.json records the commit for the platform tag. Checkouts copied with git archive have no repository.
COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)"
DIRTY="null"
if [ -n "$COMMIT" ]; then
  if [ -n "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]; then DIRTY="true"; else DIRTY="false"; fi
  info "Commit: $COMMIT$([ "$DIRTY" = "true" ] && printf ' (with uncommitted changes)')"
else
  info "Commit: unknown (not a git checkout)"
fi
# Info.plist holds development values; this partial manifest overrides them (see the csproj).
VERSION_PLIST="$STAGE/Version.plist"
plutil -create xml1 "$VERSION_PLIST"
plutil -insert CFBundleShortVersionString -string "$VERSION" "$VERSION_PLIST"
plutil -insert CFBundleVersion -string "$BUILD_NUMBER" "$VERSION_PLIST"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
if ! "$DOTNET" publish "$PROJECT" -c Release -nologo \
    -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true \
    "${RID_ARGS[@]}" \
    -p:WinoVersionPlist="$VERSION_PLIST" \
    -p:ApplicationDisplayVersion="$VERSION" -p:ApplicationVersion="$BUILD_NUMBER" \
    -p:EnableCodeSigning=true -p:CodesignKey="$IDENTITY" -p:CodesignProvision="$PROFILE_UUID" \
    -p:WinoApsEnvironment=production -p:WinoAppleSignIn=false \
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
[ "$(plutil -extract com\\.apple\\.developer\\.aps-environment raw - <<<"$ENTS" 2>/dev/null)" = "production" ] ||
  fail "The app entitlements lack com.apple.developer.aps-environment = production (live notifications)."
# The keychain store uses the data protection keychain, which needs the profile's application identifier.
[ "$(plutil -extract com\\.apple\\.application-identifier raw - <<<"$ENTS" 2>/dev/null)" = "$TEAM_ID.$BUNDLE_ID" ] ||
  fail "The app entitlements lack com.apple.application-identifier $TEAM_ID.$BUNDLE_ID (the keychain needs it)."
EMBEDDED="$APP_DIR/Contents/embedded.provisionprofile"
[ -f "$EMBEDDED" ] || fail "The app has no embedded.provisionprofile."
[ "$(security cms -D -i "$EMBEDDED" 2>/dev/null | plutil -extract UUID raw -)" = "$PROFILE_UUID" ] ||
  fail "The embedded provisioning profile is not $PROFILE_UUID."
PLIST="$APP_DIR/Contents/Info.plist"
[ "$(plutil -extract CFBundleIdentifier raw "$PLIST")" = "$BUNDLE_ID" ] || fail "CFBundleIdentifier is not $BUNDLE_ID."
[ "$(plutil -extract CFBundleShortVersionString raw "$PLIST")" = "$VERSION" ] || fail "CFBundleShortVersionString is not $VERSION."
[ "$(plutil -extract CFBundleVersion raw "$PLIST")" = "$BUILD_NUMBER" ] || fail "CFBundleVersion is not $BUILD_NUMBER."
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
info "$BUNDLE_ID $VERSION ($BUILD_NUMBER), executable: $ARCHS"

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

# ---------------------------------------------------------------- notarize the app
NOTARY=(--key "$WINO_NOTARY_KEY_PATH" --key-id "$WINO_NOTARY_KEY_ID" --issuer "$WINO_NOTARY_ISSUER_ID")

# Submits a file, waits, and keeps Apple's log. Fails with the log's issues unless the submission is Accepted.
notarize() {
  local file="$1" log="$2" name submit_json submit_err submit_rc submission_id status
  name="$(basename "$file")"
  submit_json="$STAGE/$name.notary-submit.json"
  submit_err="$STAGE/$name.notary-submit.err"
  set +e
  xcrun notarytool submit "$file" "${NOTARY[@]}" --wait --timeout 1h --output-format json >"$submit_json" 2>"$submit_err"
  submit_rc=$?
  set -e
  submission_id="$(plutil -extract id raw "$submit_json" 2>/dev/null || true)"
  status="$(plutil -extract status raw "$submit_json" 2>/dev/null || true)"
  [ -n "$submission_id" ] || { cat "$submit_err" "$submit_json" >&2; fail "notarytool submit of $name failed (exit $submit_rc)."; }
  info "$name: submission $submission_id, ${status:-unknown}"
  xcrun notarytool log "$submission_id" "${NOTARY[@]}" "$log" >/dev/null 2>&1 || info "Could not download the notarization log of $name."
  if [ "$status" != "Accepted" ]; then
    if [ -f "$log" ]; then
      echo "Notarization issues:" >&2
      plutil -extract issues json -o - "$log" 2>/dev/null >&2 || cat "$log" >&2
    fi
    fail "Notarization of $name is '${status:-unknown}'. See $log"
  fi
}

# The app gets its own ticket before it goes into the DMG. The DMG's ticket only travels with the DMG, so without
# this an app copied to Applications must reach Apple on its first launch, and Gatekeeper blocks it offline.
step "Notarizing the app (this usually takes a few minutes)"
APP_ZIP="$STAGE/$(basename "$APP_NAME" .app).zip"
APP_NOTARY_LOG="$STAGE/${DMG_NAME%.dmg}.app-notarization.json"
ditto -c -k --keepParent "$APP_DIR" "$APP_ZIP" || fail "Zipping the app for notarization failed."
notarize "$APP_ZIP" "$APP_NOTARY_LOG"
rm -f "$APP_ZIP"
xcrun stapler staple -q "$APP_DIR" || fail "stapler staple failed for the app."
xcrun stapler validate -q "$APP_DIR" || fail "stapler validate failed for the app."
codesign --verify --deep --strict "$APP_DIR" >/dev/null 2>&1 || fail "The app signature is invalid after stapling."
info "Ticket stapled to $APP_NAME"

# ---------------------------------------------------------------- DMG
step "Creating the DMG"
ln -s /Applications "$STAGE/dmg/Applications"
mkdir "$STAGE/dmg/.background"
cp "$DMG_BACKGROUND" "$STAGE/dmg/.background/background.tiff"
LAYOUT_DMG="$STAGE/layout.dmg"
hdiutil create -quiet -volname "$VOLUME_NAME" -srcfolder "$STAGE/dmg" -fs HFS+ -format UDRW -ov "$LAYOUT_DMG" ||
  fail "hdiutil create failed."
mkdir -p "$MOUNT"
hdiutil attach -quiet -readwrite -noverify -noautoopen -mountpoint "$MOUNT" "$LAYOUT_DMG" || fail "Mounting the DMG for layout failed."
# Finder writes the window layout into the volume's .DS_Store. Layout files written by other tools (dmgbuild) keep
# the icon positions but lose the background on macOS 26, so Finder lays it out itself. The window is opened twice
# because Finder draws the background only after a reopen.
osascript >"$STAGE/dmg-layout.log" 2>&1 <<APPLESCRIPT || { cat "$STAGE/dmg-layout.log" >&2; fail "Finder could not lay out the DMG window. If Terminal may not control Finder, allow it in System Settings > Privacy & Security > Automation."; }
tell application "Finder"
  set volumeFolder to (POSIX file "$MOUNT" as alias)
  open volumeFolder
  set dmgWindow to container window of volumeFolder
  set current view of dmgWindow to icon view
  set toolbar visible of dmgWindow to false
  set statusbar visible of dmgWindow to false
  set bounds of dmgWindow to {200, 120, 200 + $DMG_WINDOW_WIDTH, 120 + $DMG_WINDOW_HEIGHT + $DMG_TITLE_BAR_HEIGHT}
  set viewOptions to icon view options of dmgWindow
  set arrangement of viewOptions to not arranged
  set icon size of viewOptions to $DMG_ICON_SIZE
  set text size of viewOptions to 12
  set label position of viewOptions to bottom
  set shows item info of viewOptions to false
  set shows icon preview of viewOptions to false
  set background picture of viewOptions to file ((volumeFolder as text) & ".background:background.tiff")
  set position of item "$APP_NAME" of volumeFolder to {$DMG_APP_POSITION}
  set position of item "Applications" of volumeFolder to {$DMG_APPLICATIONS_POSITION}
  close dmgWindow
  open volumeFolder
  update volumeFolder without registering applications
  delay 2
  close container window of volumeFolder
end tell
APPLESCRIPT
for _ in 1 2 3 4 5 6 7 8 9 10; do [ -f "$MOUNT/.DS_Store" ] && break; sleep 1; done
[ -f "$MOUNT/.DS_Store" ] || fail "Finder did not save the DMG window layout."
rm -rf "$MOUNT/.fseventsd"
sync
hdiutil detach -quiet "$MOUNT" || hdiutil detach -quiet -force "$MOUNT" || fail "Detaching the layout DMG failed."
DMG="$STAGE/$DMG_NAME"
hdiutil convert -quiet "$LAYOUT_DMG" -format UDZO -o "$DMG" || fail "Compressing the DMG failed."
rm -f "$LAYOUT_DMG"
codesign --sign "$IDENTITY" --timestamp "$DMG" || fail "Signing the DMG failed."
codesign --verify --strict --verbose=2 "$DMG" >/dev/null 2>&1 || fail "DMG signature verification failed."
info "$DMG_NAME ($(du -h "$DMG" | cut -f1 | tr -d ' '))"

# ---------------------------------------------------------------- notarize the DMG
step "Notarizing the DMG (this usually takes a few minutes)"
NOTARY_LOG="$STAGE/${DMG_NAME%.dmg}.notarization.json"
notarize "$DMG" "$NOTARY_LOG"

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
xcrun stapler validate -q "$MOUNT/$APP_NAME" || fail "The app inside the DMG has no stapled ticket."
hdiutil detach -quiet "$MOUNT"
for out in "$DMG_ASSESS" "$APP_ASSESS"; do
  grep -q 'accepted' <<<"$out" && grep -q 'source=Notarized Developer ID' <<<"$out" ||
    { echo "$out" >&2; fail "Gatekeeper did not report a notarized Developer ID."; }
done
info "DMG: $(head -2 <<<"$DMG_ASSESS" | tr '\n' ' ')"
info "App: $(head -2 <<<"$APP_ASSESS" | tr '\n' ' ')(ticket stapled)"

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
      <sparkle:version>$BUILD_NUMBER</sparkle:version>
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
mv "$NOTARY_LOG" "$APP_NOTARY_LOG" "$DEST/"
mv "$DMG.sha256" "$DEST/"
mv "$APPCAST" "$DEST/appcast.xml"
if [ -d "$STAGE/Symbols/$ARCH" ]; then
  rm -rf "$DEST/Symbols/$ARCH"; mkdir -p "$DEST/Symbols"; mv "$STAGE/Symbols/$ARCH" "$DEST/Symbols/$ARCH"
fi
mv "$DMG" "$DEST/"
cat >"$DEST/release.json" <<RELEASE_JSON
{
  "platform": "macos",
  "version": "$VERSION",
  "manifestVersion": "$FULL_VERSION",
  "build": "$BUILD_NUMBER",
  "arch": "$ARCH",
  "dmg": "$DMG_NAME",
  "commit": $([ -n "$COMMIT" ] && printf '"%s"' "$COMMIT" || printf 'null'),
  "dirty": $DIRTY,
  "builtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
RELEASE_JSON
SUCCESS=1

printf '\nDone.\n'
info "$DEST/$DMG_NAME"
info "$DEST/$DMG_NAME.sha256"
info "$DEST/appcast.xml"
info "$DEST/release.json"
info "$DEST/$(basename "$NOTARY_LOG")"
info "$DEST/$(basename "$APP_NOTARY_LOG")"
[ -d "$DEST/Symbols/$ARCH" ] && info "$DEST/Symbols/$ARCH"
printf '\nTo publish: bash scripts/release/macos/publish-macos-release.sh --version %s\n' "$FULL_VERSION"
exit 0
