#!/usr/bin/env bash
# Publishes a built macOS DMG and its Sparkle appcast to https://download.winomail.app/macos.
# Run from Terminal: bash scripts/release/macos/publish-macos-release.sh [--version 2.2.7.0] [--build <n>] [--dry-run]
# See docs/releases-macos.md ("Publishing") and docs/local-script-environment.md.
set -euo pipefail

BUCKET="wino-downloads"
PREFIX="macos"
DOWNLOAD_BASE_URL="https://download.winomail.app/$PREFIX"
# The website links here, so it always serves the latest universal DMG.
ALIAS_NAME="WinoMail.dmg"

OUTPUT_ROOT="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}"
VERSION_ARG=""
BUILD_ARG=""
NON_INTERACTIVE=0
DRY_RUN=0
ALLOW_OLDER=0

usage() {
  cat <<'EOF'
Usage: publish-macos-release.sh [options]

  --version <x.y.z.0>   Release folder to publish. Default: ask, or the newest with --non-interactive.
  --build <n>           Build of that version. Default: its newest build.
  --output-root <path>  Release root. Default: $WINO_RELEASES_ROOT or ~/Wino Releases.
  --dry-run             Check everything and show the uploads without changing the bucket.
  --allow-older         Publish even when the live appcast has a newer build.
  --non-interactive     Do not prompt; publish without confirmation.
  -h, --help            Show this help.

Requires WINO_R2_ACCOUNT_ID, WINO_R2_ACCESS_KEY_ID and WINO_R2_SECRET_ACCESS_KEY (except with --dry-run).
EOF
}

step() { printf '\n==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
fail() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || fail "--version needs a value such as 2.2.7.0."; VERSION_ARG="$2"; shift 2 ;;
    --version=*) VERSION_ARG="${1#*=}"; shift ;;
    --build) [ $# -ge 2 ] || fail "--build needs a build number."; BUILD_ARG="$2"; shift 2 ;;
    --build=*) BUILD_ARG="${1#*=}"; shift ;;
    --output-root) [ $# -ge 2 ] || fail "--output-root needs a path."; OUTPUT_ROOT="$2"; shift 2 ;;
    --output-root=*) OUTPUT_ROOT="${1#*=}"; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --allow-older) ALLOW_OLDER=1; shift ;;
    --non-interactive) NON_INTERACTIVE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

if [ "$NON_INTERACTIVE" -eq 0 ] && [ ! -t 0 ]; then
  fail "No terminal for prompts. Run from Terminal or pass --non-interactive."
fi

# Prints -1, 0 or 1. Missing components count as zero, so 2.2.7 equals 2.2.7.0.
compare_versions() {
  awk -v a="$1" -v b="$2" 'BEGIN {
    n = split(a, x, "."); m = split(b, y, "."); k = n > m ? n : m
    for (i = 1; i <= k; i++) { p = (i <= n ? x[i] : 0) + 0; q = (i <= m ? y[i] : 0) + 0
      if (p < q) { print -1; exit } if (p > q) { print 1; exit } }
    print 0 }'
}

appcast_value() { xmllint --xpath "string($1)" "$2" 2>/dev/null || true; }
appcast_item() { appcast_value "//*[local-name()='item'][1]/$1" "$2"; }
release_value() { sed -n "s/^  \"$1\": \"\{0,1\}\([^\",]*\)\"\{0,1\},\{0,1\}$/\1/p" "$2"; }

# ---------------------------------------------------------------- preflight
step "Preflight"
for tool in curl xmllint shasum; do
  command -v "$tool" >/dev/null || fail "$tool is not installed."
done
curl --help all 2>/dev/null | grep -q -- '--aws-sigv4' || fail "This curl has no --aws-sigv4 support (curl 7.86 or later is needed)."
[ -d "$OUTPUT_ROOT" ] || fail "The release folder does not exist: $OUTPUT_ROOT"

if [ "$DRY_RUN" -eq 0 ]; then
  for name in WINO_R2_ACCOUNT_ID WINO_R2_ACCESS_KEY_ID WINO_R2_SECRET_ACCESS_KEY; do
    [ -n "${!name:-}" ] || fail "Set $name before publishing. See docs/local-script-environment.md."
  done
  R2_ENDPOINT="https://$WINO_R2_ACCOUNT_ID.r2.cloudflarestorage.com"
fi

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

LIVE_APPCAST="$TEMP_DIR/live-appcast.xml"
LIVE_BUILD=""
if curl -fsS -H 'Cache-Control: no-cache' -o "$LIVE_APPCAST" "$DOWNLOAD_BASE_URL/appcast.xml" 2>/dev/null; then
  LIVE_BUILD="$(appcast_item "*[local-name()='version']" "$LIVE_APPCAST")"
  info "Live appcast: $(appcast_item "*[local-name()='shortVersionString']" "$LIVE_APPCAST") ($LIVE_BUILD)"
else
  info "Live appcast: none"
fi

# ---------------------------------------------------------------- select
# Builds are <root>/<x.y.z.0>/macOS/<build>/ (build-macos-release.sh); each line is "<version> <build>", oldest first.
step "Selecting the release"
RELEASES=()
while IFS= read -r line; do
  RELEASES+=("$line")
done < <(find "$OUTPUT_ROOT" -mindepth 4 -maxdepth 4 -path '*/macOS/*/appcast.xml' 2>/dev/null |
  while IFS= read -r appcast; do
    build_dir="$(dirname "$appcast")"
    printf '%s %s\n' "$(basename "$(dirname "$(dirname "$build_dir")")")" "$(basename "$build_dir")"
  done |
  sort -t' ' -k1,1 -k2,2n | sort -s -t. -k1,1n -k2,2n -k3,3n -k4,4n)
[ ${#RELEASES[@]} -gt 0 ] || fail "No macOS build with an appcast.xml under $OUTPUT_ROOT. Run build-macos-release.sh first."

newest_build_of() {
  local wanted="$1" line result=""
  for line in "${RELEASES[@]}"; do [ "${line% *}" = "$wanted" ] && result="${line#* }"; done
  printf '%s' "$result"
}

NEWEST_VERSION="${RELEASES[${#RELEASES[@]}-1]% *}"
if [ -n "$VERSION_ARG" ]; then
  FULL_VERSION="$VERSION_ARG"
elif [ "$NON_INTERACTIVE" -eq 1 ]; then
  FULL_VERSION="$NEWEST_VERSION"
else
  for line in "${RELEASES[@]}"; do info "${line% *}  build ${line#* }"; done
  read -r -p "    Version to publish [$NEWEST_VERSION]: " FULL_VERSION
  FULL_VERSION="${FULL_VERSION:-$NEWEST_VERSION}"
fi

NEWEST_BUILD="$(newest_build_of "$FULL_VERSION")"
[ -n "$NEWEST_BUILD" ] || fail "No build of $FULL_VERSION under $OUTPUT_ROOT/$FULL_VERSION/macOS."
if [ -n "$BUILD_ARG" ]; then
  BUILD="$BUILD_ARG"
elif [ "$NON_INTERACTIVE" -eq 1 ] || [ "$(grep -c "^$FULL_VERSION " < <(printf '%s\n' "${RELEASES[@]}"))" -eq 1 ]; then
  BUILD="$NEWEST_BUILD"
else
  read -r -p "    Build of $FULL_VERSION to publish [$NEWEST_BUILD]: " BUILD
  BUILD="${BUILD:-$NEWEST_BUILD}"
fi

FOLDER="$OUTPUT_ROOT/$FULL_VERSION/macOS/$BUILD"
APPCAST="$FOLDER/appcast.xml"
[ -f "$APPCAST" ] || fail "No appcast.xml in $FOLDER."

# ---------------------------------------------------------------- validate
step "Checking $FULL_VERSION, build $BUILD"
xmllint --noout "$APPCAST" || fail "$APPCAST is not valid XML."
SPARKLE_VERSION="$(appcast_item "*[local-name()='version']" "$APPCAST")"
SHORT_VERSION="$(appcast_item "*[local-name()='shortVersionString']" "$APPCAST")"
ENCLOSURE_URL="$(appcast_item "*[local-name()='enclosure']/@url" "$APPCAST")"
ENCLOSURE_LENGTH="$(appcast_item "*[local-name()='enclosure']/@length" "$APPCAST")"
SIGNATURE="$(appcast_item "*[local-name()='enclosure']/@*[local-name()='edSignature']" "$APPCAST")"
HARDWARE="$(appcast_item "*[local-name()='hardwareRequirements']" "$APPCAST")"

[ "$SPARKLE_VERSION" = "$BUILD" ] || fail "The appcast's sparkle:version '$SPARKLE_VERSION' is not the folder's build $BUILD."
[ -n "$SIGNATURE" ] || fail "The appcast enclosure has no Sparkle EdDSA signature."
[ "$(compare_versions "$SHORT_VERSION" "${FULL_VERSION%.*}")" -eq 0 ] ||
  fail "The appcast's version $SHORT_VERSION does not belong to folder $FULL_VERSION."
[[ "$ENCLOSURE_URL" == "$DOWNLOAD_BASE_URL/"* ]] || fail "The appcast points at $ENCLOSURE_URL, outside $DOWNLOAD_BASE_URL."
DMG_NAME="${ENCLOSURE_URL#"$DOWNLOAD_BASE_URL/"}"
[[ "$DMG_NAME" != */* && "$DMG_NAME" == *.dmg ]] || fail "Unexpected DMG name in the appcast: $DMG_NAME"
# An arm64 appcast carries a hardware requirement, so Intel Macs would never see an update again.
[ -z "$HARDWARE" ] || fail "$DMG_NAME is an $HARDWARE-only build. Publish the universal build's appcast."

DMG="$FOLDER/$DMG_NAME"
[ -f "$DMG" ] || fail "The appcast references $DMG_NAME, which is missing from $FOLDER."
DMG_SIZE="$(wc -c <"$DMG" | tr -d ' ')"
[ "$DMG_SIZE" = "$ENCLOSURE_LENGTH" ] ||
  fail "$DMG_NAME is $DMG_SIZE bytes but the appcast says $ENCLOSURE_LENGTH. The DMG changed after Sparkle signed it."
[ -f "$DMG.sha256" ] || fail "$DMG_NAME.sha256 is missing."
(cd "$FOLDER" && shasum -a 256 -c "$DMG_NAME.sha256" >/dev/null) || fail "$DMG_NAME does not match $DMG_NAME.sha256."
info "$DMG_NAME ($((DMG_SIZE / 1048576)) MB), version $SHORT_VERSION, build $BUILD"

RECORD="$FOLDER/release.json"
if [ -f "$RECORD" ]; then
  [ "$(release_value build "$RECORD")" = "$BUILD" ] && [ "$(release_value dmg "$RECORD")" = "$DMG_NAME" ] ||
    fail "$RECORD does not describe $DMG_NAME, build $BUILD."
  COMMIT="$(release_value commit "$RECORD")"
  if [ -z "$COMMIT" ] || [ "$COMMIT" = "null" ]; then
    info "Built from an unknown commit; tag the release with an explicit commit."
  else
    info "Built from $COMMIT$([ "$(release_value dirty "$RECORD")" = "true" ] && printf ' with uncommitted changes')"
  fi
else
  info "No release.json beside the DMG (built before the build record existed)."
fi

if [ -n "$LIVE_BUILD" ]; then
  order="$(compare_versions "$BUILD" "$LIVE_BUILD")"
  if [ "$order" -lt 0 ]; then
    [ "$ALLOW_OLDER" -eq 1 ] || fail "The live appcast serves build $LIVE_BUILD, newer than $BUILD. Pass --allow-older to replace it anyway."
    info "Replacing the newer live build $LIVE_BUILD (--allow-older)."
  elif [ "$order" -eq 0 ]; then
    info "The live appcast already serves build $BUILD. Installed copies of it will not update to this upload."
  fi
fi

# ---------------------------------------------------------------- confirm
step "Uploads"
info "$PREFIX/$DMG_NAME"
info "$PREFIX/$DMG_NAME.sha256"
info "$PREFIX/$ALIAS_NAME (copy of $DMG_NAME)"
info "$PREFIX/appcast.xml (last)"

if [ "$DRY_RUN" -eq 1 ]; then
  printf '\nDry run: nothing was uploaded.\n'
  exit 0
fi

if [ "$NON_INTERACTIVE" -eq 0 ]; then
  read -r -p "    Publish $SHORT_VERSION ($BUILD) to $DOWNLOAD_BASE_URL? [y/N] " answer
  [[ "$answer" =~ ^[Yy]$ ]] || fail "Cancelled."
fi

# ---------------------------------------------------------------- upload
# R2's S3 API through curl's SigV4 signer. The credentials go through a config file on stdin, never the command line,
# and the payload hash is sent explicitly so curl signs the real body.
r2_put() {
  local key="$1" file="$2" content_type="$3" hash
  hash="$(shasum -a 256 "$file" | cut -d' ' -f1)"
  printf 'user = "%s:%s"\n' "$WINO_R2_ACCESS_KEY_ID" "$WINO_R2_SECRET_ACCESS_KEY" |
    curl --config - --fail-with-body -sS --retry 3 --retry-all-errors --retry-delay 5 \
      --aws-sigv4 "aws:amz:auto:s3" \
      -H "x-amz-content-sha256: $hash" \
      -H "Content-Type: $content_type" \
      -H "Cache-Control: no-cache" \
      -T "$file" -o "$TEMP_DIR/r2-response.txt" \
      "$R2_ENDPOINT/$BUCKET/$key" ||
    { cat "$TEMP_DIR/r2-response.txt" >&2 2>/dev/null || true; fail "Uploading $key failed."; }
}

verify_public() {
  local name="$1" size="$2" headers status length
  headers="$(curl -sSI -H 'Cache-Control: no-cache' "$DOWNLOAD_BASE_URL/$name")" || fail "$DOWNLOAD_BASE_URL/$name is not reachable."
  status="$(awk 'NR == 1 { print $2 }' <<<"$headers")"
  length="$(awk 'tolower($1) == "content-length:" { gsub("\r", "", $2); print $2 }' <<<"$headers" | tail -1)"
  [ "$status" = "200" ] || fail "$DOWNLOAD_BASE_URL/$name returned HTTP $status."
  [ "$length" = "$size" ] || fail "$DOWNLOAD_BASE_URL/$name serves $length bytes, expected $size."
}

step "Uploading"
info "$PREFIX/$DMG_NAME"
r2_put "$PREFIX/$DMG_NAME" "$DMG" "application/x-apple-diskimage"
verify_public "$DMG_NAME" "$DMG_SIZE"
r2_put "$PREFIX/$DMG_NAME.sha256" "$DMG.sha256" "text/plain; charset=utf-8"
info "$PREFIX/$ALIAS_NAME"
r2_put "$PREFIX/$ALIAS_NAME" "$DMG" "application/x-apple-diskimage"
verify_public "$ALIAS_NAME" "$DMG_SIZE"
info "$PREFIX/appcast.xml"
r2_put "$PREFIX/appcast.xml" "$APPCAST" "application/xml"

step "Verifying"
curl -fsS -H 'Cache-Control: no-cache' -o "$LIVE_APPCAST" "$DOWNLOAD_BASE_URL/appcast.xml" || fail "The appcast cannot be downloaded."
[ "$(appcast_item "*[local-name()='version']" "$LIVE_APPCAST")" = "$BUILD" ] || fail "The live appcast does not serve build $BUILD yet."
info "$DOWNLOAD_BASE_URL/appcast.xml serves $SHORT_VERSION ($BUILD)"
info "$DOWNLOAD_BASE_URL/$ALIAS_NAME"

printf '\nPublished %s (%s).\n' "$SHORT_VERSION" "$BUILD"
exit 0
