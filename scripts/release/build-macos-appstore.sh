#!/usr/bin/env bash
# Builds the Mac App Store package of Wino Mail: an app signed with Apple Distribution and the
# Mac App Store profile, wrapped in a .pkg signed with the installer certificate. Optionally
# validates and uploads it to App Store Connect.
# Run from Terminal on a Mac: bash scripts/release/build-macos-appstore.sh [--upload] [--build-number N]
# See docs/releases.md ("Mac App Store package") and docs/local-script-environment.md.
set -euo pipefail

TEAM_ID="4VB7YWRAQ9"
BUNDLE_ID="com.winomail.macos"
APP_NAME="Wino Mail.app"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj"
MANIFEST="$REPO_ROOT/src/Wino.Mail.WinUI/Package.appxmanifest"
# Entitlements.plist without the Sparkle exception that DMG builds need.
ENTITLEMENTS="$REPO_ROOT/src/Wino.Mail.MacOS/Entitlements.AppStore.plist"

ARCH=""
BUILD_NUMBER=""
UPLOAD=0
VALIDATE=1
NON_INTERACTIVE=0
OUTPUT_ROOT="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}"

usage() {
  cat <<'EOF'
Usage: build-macos-appstore.sh [options]

  --arch arm64|universal   Apple silicon only, or Apple silicon + Intel (default: universal).
  --build-number <n>       CFBundleVersion. Must be higher than every earlier upload.
                           Default: the UTC time as yyDDDHHMM, which always increases.
  --upload                 Upload the package to App Store Connect after validation.
  --skip-validation        Do not run App Store Connect validation (no API key needed).
  --output-root <path>     Release root. Default: $WINO_RELEASES_ROOT or ~/Wino Releases.
  --non-interactive        Do not prompt. Missing choices use their defaults.
  -h, --help               Show this help.

Signing needs the Apple Distribution and Mac Installer Distribution certificates of team
4VB7YWRAQ9 and an installed Mac App Store provisioning profile for com.winomail.macos.
Validation and upload need WINO_NOTARY_KEY_PATH, WINO_NOTARY_KEY_ID and WINO_NOTARY_ISSUER_ID.
EOF
}

step() { printf '\n==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
fail() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --arch) [ $# -ge 2 ] || fail "--arch needs a value: arm64 or universal."; ARCH="$2"; shift 2 ;;
    --arch=*) ARCH="${1#*=}"; shift ;;
    --build-number) [ $# -ge 2 ] || fail "--build-number needs a value."; BUILD_NUMBER="$2"; shift 2 ;;
    --build-number=*) BUILD_NUMBER="${1#*=}"; shift ;;
    --upload) UPLOAD=1; shift ;;
    --skip-validation) VALIDATE=0; shift ;;
    --output-root) [ $# -ge 2 ] || fail "--output-root needs a path."; OUTPUT_ROOT="$2"; shift 2 ;;
    --output-root=*) OUTPUT_ROOT="${1#*=}"; shift ;;
    --non-interactive) NON_INTERACTIVE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[ "$UPLOAD" -eq 0 ] || [ "$VALIDATE" -eq 1 ] || fail "--upload requires validation; remove --skip-validation."
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
xcrun --find altool >/dev/null 2>&1 || fail "'xcrun altool' is unavailable. Install Xcode."
for tool in codesign productbuild pkgutil ditto lipo plutil shasum security; do
  command -v "$tool" >/dev/null 2>&1 || fail "'$tool' was not found."
done
info "Xcode tools: $(xcode-select -p)"

# Certificate names vary by creation date ("3rd Party Mac Developer ..." is the older form).
find_identity() {
  local policy="$1"; shift
  local identities
  if [ -n "$policy" ]; then identities="$(security find-identity -v -p "$policy")"; else identities="$(security find-identity -v)"; fi
  local prefix
  for prefix in "$@"; do
    local match
    match="$(grep -oE "\"$prefix: [^\"]*\($TEAM_ID\)\"" <<<"$identities" | head -1 | tr -d '"')" || true
    if [ -n "$match" ]; then echo "$match"; return 0; fi
  done
  return 1
}
APP_IDENTITY="$(find_identity codesigning "Apple Distribution" "3rd Party Mac Developer Application")" ||
  fail "No Apple Distribution certificate of team $TEAM_ID is in the keychain. Create one in Xcode → Settings → Accounts → Manage Certificates."
INSTALLER_IDENTITY="$(find_identity "" "3rd Party Mac Developer Installer" "Mac Installer Distribution")" ||
  fail "No Mac Installer Distribution certificate of team $TEAM_ID is in the keychain. Create one in Xcode → Settings → Accounts → Manage Certificates."
info "App signing: $APP_IDENTITY"
info "Package signing: $INSTALLER_IDENTITY"

# The newest unexpired Mac App Store profile for the bundle ID. Store profiles list no devices.
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
    ! plutil -extract ProvisionedDevices raw "$PROFILE_PLIST" >/dev/null 2>&1 || continue
    [ "$(plutil -extract Entitlements.com\\.apple\\.security\\.get-task-allow raw "$PROFILE_PLIST" 2>/dev/null)" != "true" ] || continue
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
  fail "No unexpired Mac App Store provisioning profile for $BUNDLE_ID is installed. Create a \"Mac App Store Connect\" profile in the developer portal and double-click it."
info "Profile: $PROFILE_NAME ($PROFILE_UUID), expires $PROFILE_EXPIRY"

if [ "$VALIDATE" -eq 1 ]; then
  [ -n "${WINO_NOTARY_KEY_PATH:-}" ] || fail "WINO_NOTARY_KEY_PATH is not set (App Store Connect API key .p8 file). Pass --skip-validation to build only."
  [ -r "$WINO_NOTARY_KEY_PATH" ] || fail "WINO_NOTARY_KEY_PATH does not point to a readable file."
  [ -n "${WINO_NOTARY_KEY_ID:-}" ] || fail "WINO_NOTARY_KEY_ID is not set."
  [ -n "${WINO_NOTARY_ISSUER_ID:-}" ] || fail "WINO_NOTARY_ISSUER_ID is not set."
  info "App Store Connect key: $WINO_NOTARY_KEY_ID"
fi

[ -f "$PROJECT" ] || fail "Project not found: $PROJECT"
[ -f "$MANIFEST" ] || fail "Manifest not found: $MANIFEST"

# ---------------------------------------------------------------- version
FULL_VERSION="$(awk '/<Identity/{f=1} f && match($0, /Version="[^"]*"/){print substr($0, RSTART+9, RLENGTH-10); exit}' "$MANIFEST")"
[[ "$FULL_VERSION" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] ||
  fail "Package.appxmanifest Identity Version '$FULL_VERSION' must have four numeric components."
[ "${BASH_REMATCH[1]}" -ne 0 ] || fail "The major version must be nonzero ($FULL_VERSION)."
[ "${BASH_REMATCH[4]}" -eq 0 ] || fail "The revision must be zero ($FULL_VERSION)."
VERSION="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
# App Store Connect rejects a build number it has seen for the app, so the default increases
# with time instead of following the version.
[ -n "$BUILD_NUMBER" ] || BUILD_NUMBER="$(date -u +%y%j%H%M | sed 's/^0*//')"
[[ "$BUILD_NUMBER" =~ ^[1-9][0-9]*$ ]] || fail "--build-number must be a positive integer ($BUILD_NUMBER)."
info "Version: $VERSION (manifest $FULL_VERSION), build $BUILD_NUMBER"

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

DEST="$OUTPUT_ROOT/$FULL_VERSION/macOS-AppStore"
PKG_NAME="WinoMail_${VERSION}_${BUILD_NUMBER}_${ARCH}.pkg"
[ ! -e "$DEST/$PKG_NAME" ] || fail "$DEST/$PKG_NAME already exists. Pass another --build-number."

info "Architecture: $ARCH"
info "Output: $DEST/$PKG_NAME"
[ "$UPLOAD" -eq 1 ] && info "Upload: yes" || info "Upload: no"
if [ "$NON_INTERACTIVE" -eq 0 ]; then
  printf '\nBuild and sign now? [Y/n]: '
  read -r answer
  case "${answer:-y}" in [Yy]*) ;; *) echo "Cancelled."; exit 0 ;; esac
fi

# ---------------------------------------------------------------- staging
mkdir -p "$OUTPUT_ROOT"
LOCK="$OUTPUT_ROOT/.macos-release.lock"
mkdir "$LOCK" 2>/dev/null || fail "Another macOS release run holds $LOCK. Remove it if no run is active."
STAGE="$OUTPUT_ROOT/.staging/macos-appstore-$(date +%Y%m%d-%H%M%S)-$$"
mkdir -p "$STAGE"
SUCCESS=0
cleanup() {
  rmdir "$LOCK" 2>/dev/null || true
  if [ "$SUCCESS" -eq 1 ]; then
    rm -rf "$STAGE"; rmdir "$OUTPUT_ROOT/.staging" 2>/dev/null || true
  else
    printf '\nFailed. Logs and intermediate files remain in %s\n' "$STAGE" >&2
  fi
}
trap cleanup EXIT

# ---------------------------------------------------------------- build
step "Publishing Release for the App Store ($ARCH)"
PROJECT_DIR="$(dirname "$PROJECT")"
# Release outputs only; Debug outputs stay untouched.
rm -rf "$PROJECT_DIR/bin/Release" "$PROJECT_DIR/obj/Release"
BUILD_LOG="$STAGE/publish.log"
# Info.plist holds development values; this partial manifest overrides them (see the csproj).
VERSION_PLIST="$STAGE/Version.plist"
plutil -create xml1 "$VERSION_PLIST"
plutil -insert CFBundleShortVersionString -string "$VERSION" "$VERSION_PLIST"
plutil -insert CFBundleVersion -string "$BUILD_NUMBER" "$VERSION_PLIST"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# WinoMacDistribution=AppStore defines WINO_APPSTORE (see the csproj).
if ! "$DOTNET" publish "$PROJECT" -c Release -nologo \
    -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true \
    -p:WinoMacDistribution=AppStore \
    "${RID_ARGS[@]}" \
    -p:WinoVersionPlist="$VERSION_PLIST" \
    -p:ApplicationDisplayVersion="$VERSION" -p:ApplicationVersion="$BUILD_NUMBER" \
    -p:EnableCodeSigning=true -p:CodesignKey="$APP_IDENTITY" -p:CodesignProvision="$PROFILE_UUID" \
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
APP_DIR="$STAGE/$APP_NAME"
ditto "$APP_SRC" "$APP_DIR"
codesign --verify --deep --strict --verbose=2 "$APP_DIR" >"$STAGE/codesign-verify.txt" 2>&1 ||
  { cat "$STAGE/codesign-verify.txt" >&2; fail "codesign verification failed."; }
SIG="$(codesign -dvv "$APP_DIR" 2>&1)"
grep -qF "Authority=$APP_IDENTITY" <<<"$SIG" || fail "The app is not signed with $APP_IDENTITY."
grep -qF "TeamIdentifier=$TEAM_ID" <<<"$SIG" || fail "The app team identifier is not $TEAM_ID."
ENTS_PLIST="$STAGE/entitlements.plist"
codesign -d --entitlements :- "$APP_DIR" >"$ENTS_PLIST" 2>/dev/null
[ "$(plutil -extract com\\.apple\\.application-identifier raw "$ENTS_PLIST" 2>/dev/null)" = "$TEAM_ID.$BUNDLE_ID" ] ||
  fail "The app entitlements lack com.apple.application-identifier $TEAM_ID.$BUNDLE_ID."
[ "$(plutil -extract com\\.apple\\.developer\\.team-identifier raw "$ENTS_PLIST" 2>/dev/null)" = "$TEAM_ID" ] ||
  fail "The app entitlements lack com.apple.developer.team-identifier $TEAM_ID."
[ "$(plutil -extract com\\.apple\\.security\\.app-sandbox raw "$ENTS_PLIST" 2>/dev/null)" = "true" ] ||
  fail "The app entitlements lack the app sandbox."
! grep -q 'get-task-allow' "$ENTS_PLIST" || fail "The app entitlements contain get-task-allow."
! grep -q 'temporary-exception' "$ENTS_PLIST" || fail "The app entitlements contain a temporary exception (Sparkle's belongs to DMG builds only)."
EMBEDDED="$APP_DIR/Contents/embedded.provisionprofile"
[ -f "$EMBEDDED" ] || fail "The app has no embedded.provisionprofile."
[ "$(security cms -D -i "$EMBEDDED" 2>/dev/null | plutil -extract UUID raw -)" = "$PROFILE_UUID" ] ||
  fail "The embedded provisioning profile is not $PROFILE_UUID."
while IFS= read -r -d '' framework; do
  FRAMEWORK_SIG="$(codesign -dvv "$framework" 2>&1)"
  grep -qF "Authority=$APP_IDENTITY" <<<"$FRAMEWORK_SIG" || fail "$(basename "$framework") is not signed with $APP_IDENTITY."
done < <(find "$APP_DIR/Contents/Frameworks" -maxdepth 1 -name '*.framework' -print0 2>/dev/null)
PLIST="$APP_DIR/Contents/Info.plist"
[ "$(plutil -extract CFBundleIdentifier raw "$PLIST")" = "$BUNDLE_ID" ] || fail "CFBundleIdentifier is not $BUNDLE_ID."
[ "$(plutil -extract CFBundleShortVersionString raw "$PLIST")" = "$VERSION" ] || fail "CFBundleShortVersionString is not $VERSION."
[ "$(plutil -extract CFBundleVersion raw "$PLIST")" = "$BUILD_NUMBER" ] || fail "CFBundleVersion is not $BUILD_NUMBER."
plutil -extract LSApplicationCategoryType raw "$PLIST" >/dev/null 2>&1 || fail "Info.plist lacks LSApplicationCategoryType."
# App Store apps update only through the App Store (App Review Guideline 2.4.5).
[ ! -e "$APP_DIR/Contents/Frameworks/Sparkle.framework" ] || fail "The App Store build contains Sparkle.framework."
! plutil -extract SUFeedURL raw "$PLIST" >/dev/null 2>&1 || fail "The App Store build's Info.plist has Sparkle settings."
EXE="$APP_DIR/Contents/MacOS/$(plutil -extract CFBundleExecutable raw "$PLIST")"
ARCHS="$(lipo -archs "$EXE")"
for a in $EXPECTED_ARCHS; do [[ " $ARCHS " == *" $a "* ]] || fail "The executable lacks $a (has: $ARCHS)."; done
info "Signed by $APP_IDENTITY with profile $PROFILE_NAME, sandboxed, no get-task-allow"
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

# ---------------------------------------------------------------- package
step "Creating the installer package"
PKG="$STAGE/$PKG_NAME"
productbuild --component "$APP_DIR" /Applications --sign "$INSTALLER_IDENTITY" "$PKG" >"$STAGE/productbuild.log" 2>&1 ||
  { cat "$STAGE/productbuild.log" >&2; fail "productbuild failed."; }
pkgutil --check-signature "$PKG" >"$STAGE/pkg-signature.txt" 2>&1 ||
  { cat "$STAGE/pkg-signature.txt" >&2; fail "The package signature is invalid."; }
grep -qF "$TEAM_ID" "$STAGE/pkg-signature.txt" || fail "The package is not signed by team $TEAM_ID."
info "$PKG_NAME ($(du -h "$PKG" | cut -f1 | tr -d ' '))"

# ---------------------------------------------------------------- App Store Connect
ALTOOL_AUTH=(--apiKey "${WINO_NOTARY_KEY_ID:-}" --apiIssuer "${WINO_NOTARY_ISSUER_ID:-}" --p8-file-path "${WINO_NOTARY_KEY_PATH:-}")
if [ "$VALIDATE" -eq 1 ]; then
  step "Validating with App Store Connect"
  xcrun altool --validate-app -f "$PKG" -t macos "${ALTOOL_AUTH[@]}" >"$STAGE/validate.log" 2>&1 ||
    { cat "$STAGE/validate.log" >&2; fail "App Store Connect validation failed."; }
  info "Validation passed"
fi
if [ "$UPLOAD" -eq 1 ]; then
  step "Uploading to App Store Connect"
  xcrun altool --upload-app -f "$PKG" -t macos "${ALTOOL_AUTH[@]}" >"$STAGE/upload.log" 2>&1 ||
    { cat "$STAGE/upload.log" >&2; fail "The upload failed."; }
  info "Uploaded. The build appears in App Store Connect after processing."
fi

# ---------------------------------------------------------------- finalize
step "Finalizing"
(cd "$STAGE" && shasum -a 256 "$PKG_NAME" >"$PKG_NAME.sha256")
mkdir -p "$DEST"
[ ! -e "$DEST/$PKG_NAME" ] || fail "$DEST/$PKG_NAME appeared during the run."
mv "$PKG.sha256" "$DEST/"
for log in validate.log upload.log; do
  [ -f "$STAGE/$log" ] && mv "$STAGE/$log" "$DEST/${PKG_NAME%.pkg}.$log"
done
if [ -d "$STAGE/Symbols/$ARCH" ]; then
  rm -rf "$DEST/Symbols/$BUILD_NUMBER-$ARCH"; mkdir -p "$DEST/Symbols"; mv "$STAGE/Symbols/$ARCH" "$DEST/Symbols/$BUILD_NUMBER-$ARCH"
fi
mv "$PKG" "$DEST/"
SUCCESS=1

printf '\nDone.\n'
info "$DEST/$PKG_NAME"
info "$DEST/$PKG_NAME.sha256"
[ -d "$DEST/Symbols/$BUILD_NUMBER-$ARCH" ] && info "$DEST/Symbols/$BUILD_NUMBER-$ARCH"
exit 0
