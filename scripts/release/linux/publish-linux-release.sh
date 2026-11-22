#!/usr/bin/env bash
# Publishes a built Linux beta (the .deb, Arch and RPM packages in its feed, and the feed) to
# https://download.winomail.app/linux/<channel>, then updates the AUR package files in the repository.
# Run on Linux or in WSL2: bash scripts/release/linux/publish-linux-release.sh [--version 2.2.15.0] [--build <n>] [--dry-run]
# See docs/releases-linux.md ("Publishing") and docs/local-script-environment.md.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SCRIPT_DIR="$REPO_ROOT/scripts/release/linux"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"

BUCKET="wino-downloads"
CHANNEL="beta"
PREFIX="linux/$CHANNEL"
DOWNLOAD_BASE_URL="$DOWNLOAD_HOST/$PREFIX"

OUTPUT_ROOT="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}"
VERSION_ARG=""
BUILD_ARG=""
NON_INTERACTIVE=0
DRY_RUN=0
ALLOW_OLDER=0
SKIP_AUR=0

usage() {
  cat <<'EOF'
Usage: publish-linux-release.sh [options]

  --version <x.y.z.0>   Release folder to publish. Default: ask, or the newest with --non-interactive.
  --build <n>           Build of that version. Default: its newest build.
  --output-root <path>  Release root. Default: $WINO_RELEASES_ROOT or ~/Wino Releases.
  --dry-run             Check everything and show the uploads without changing the bucket or the repository.
  --allow-older         Publish even when the live feed has a newer build.
  --skip-aur            Do not update the AUR package files after the upload.
  --non-interactive     Do not prompt; publish without confirmation.
  -h, --help            Show this help.

Requires WINO_R2_ACCOUNT_ID, WINO_R2_ACCESS_KEY_ID and WINO_R2_SECRET_ACCESS_KEY (except with --dry-run).
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || fail "--version needs a value such as 2.2.15.0."; VERSION_ARG="$2"; shift 2 ;;
    --version=*) VERSION_ARG="${1#*=}"; shift ;;
    --build) [ $# -ge 2 ] || fail "--build needs a build number."; BUILD_ARG="$2"; shift 2 ;;
    --build=*) BUILD_ARG="${1#*=}"; shift ;;
    --output-root) [ $# -ge 2 ] || fail "--output-root needs a path."; OUTPUT_ROOT="$2"; shift 2 ;;
    --output-root=*) OUTPUT_ROOT="${1#*=}"; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    --allow-older) ALLOW_OLDER=1; shift ;;
    --skip-aur) SKIP_AUR=1; shift ;;
    --non-interactive) NON_INTERACTIVE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

if [ "$NON_INTERACTIVE" -eq 0 ] && [ ! -t 0 ]; then
  fail "No terminal for prompts. Run from a terminal or pass --non-interactive."
fi

# ---------------------------------------------------------------- preflight
step "Preflight"
require_tools curl jq sha256sum
curl --help all 2>/dev/null | grep -q -- '--aws-sigv4' || fail "This curl has no --aws-sigv4 support (curl 7.86 or later is needed)."
[ -d "$OUTPUT_ROOT" ] || fail "The release folder does not exist: $OUTPUT_ROOT"

if [ "$DRY_RUN" -eq 0 ]; then
  for name in WINO_R2_ACCOUNT_ID WINO_R2_ACCESS_KEY_ID WINO_R2_SECRET_ACCESS_KEY; do
    [ -n "${!name:-}" ] || fail "Set $name before publishing. See docs/local-script-environment.md (in WSL2, pass it through WSLENV)."
  done
  R2_ENDPOINT="https://$WINO_R2_ACCOUNT_ID.r2.cloudflarestorage.com"
fi

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

LIVE_FEED="$TEMP_DIR/live-release.json"
LIVE_BUILD=""
if curl -fsS -H 'Cache-Control: no-cache' -o "$LIVE_FEED" "$DOWNLOAD_BASE_URL/release.json" 2>/dev/null &&
   LIVE_BUILD="$(jq -er '.build' "$LIVE_FEED" 2>/dev/null)"; then
  info "Live feed: $(jq -r '.version' "$LIVE_FEED") ($LIVE_BUILD), packages: $(jq -r '.packages | keys | join(", ")' "$LIVE_FEED")"
else
  LIVE_BUILD=""
  info "Live feed: none"
fi

# ---------------------------------------------------------------- select
# Builds are <root>/<x.y.z.0>/Linux/<build>/ (build-linux-release.sh); each line is "<version> <build>", oldest first.
step "Selecting the release"
RELEASES=()
while IFS= read -r line; do
  RELEASES+=("$line")
done < <(find "$OUTPUT_ROOT" -mindepth 4 -maxdepth 4 -path '*/Linux/*/release.json' 2>/dev/null |
  while IFS= read -r feed; do
    build_dir="$(dirname "$feed")"
    printf '%s %s\n' "$(basename "$(dirname "$(dirname "$build_dir")")")" "$(basename "$build_dir")"
  done |
  sort -t' ' -k2,2n | sort -s -t. -k1,1n -k2,2n -k3,3n -k4,4n)
[ ${#RELEASES[@]} -gt 0 ] || fail "No Linux build with a release.json under $OUTPUT_ROOT. Run build-linux-release.sh first."

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
[ -n "$NEWEST_BUILD" ] || fail "No build of $FULL_VERSION under $OUTPUT_ROOT/$FULL_VERSION/Linux."
if [ -n "$BUILD_ARG" ]; then
  BUILD="$BUILD_ARG"
elif [ "$NON_INTERACTIVE" -eq 1 ] || [ "$(grep -c "^$FULL_VERSION " < <(printf '%s\n' "${RELEASES[@]}"))" -eq 1 ]; then
  BUILD="$NEWEST_BUILD"
else
  read -r -p "    Build of $FULL_VERSION to publish [$NEWEST_BUILD]: " BUILD
  BUILD="${BUILD:-$NEWEST_BUILD}"
fi

FOLDER="$OUTPUT_ROOT/$FULL_VERSION/Linux/$BUILD"
FEED="$FOLDER/release.json"
[ -f "$FEED" ] || fail "No release.json in $FOLDER."

# ---------------------------------------------------------------- validate
step "Checking $FULL_VERSION, build $BUILD"
jq -e . "$FEED" >/dev/null 2>&1 || fail "$FEED is not valid JSON."
feed_value() { jq -er "$1" "$FEED" 2>/dev/null || fail "release.json has no $1."; }
[ "$(feed_value '.schema')" = "1" ] || fail "release.json has an unknown schema."
[ "$(feed_value '.platform')" = "linux" ] || fail "release.json is not a Linux feed."
[ "$(feed_value '.channel')" = "$CHANNEL" ] || fail "release.json is not a $CHANNEL feed."
FEED_VERSION="$(feed_value '.version')"
FEED_BUILD="$(feed_value '.build')"
[ "$FEED_BUILD" = "$BUILD" ] || fail "The feed's build $FEED_BUILD is not the folder's build $BUILD."
[ "$FEED_VERSION.0" = "$FULL_VERSION" ] || fail "The feed's version $FEED_VERSION does not belong to folder $FULL_VERSION."
for url in "$(jq -r '.notesUrl // empty' "$FEED")" "$(jq -r '.downloadPageUrl // empty' "$FEED")"; do
  [ -z "$url" ] || [[ "$url" =~ ^https://(www\.)?winomail\.app(/|$) ]] || fail "The feed links to $url, which the app does not open."
done
VERSION="$FEED_VERSION"
BUILD_NUMBER="$BUILD"

while IFS= read -r kind; do
  [[ " ${PACKAGE_KINDS[*]} " == *" $kind "* ]] || fail "release.json lists an unknown package kind '$kind'."
done < <(jq -r '.packages | keys[]' "$FEED")
KINDS=()
for kind in "${PACKAGE_KINDS[@]}"; do
  jq -e --arg k "$kind" '.packages | has($k)' "$FEED" >/dev/null && KINDS+=("$kind")
done
[ ${#KINDS[@]} -gt 0 ] || fail "release.json lists no package."
for kind in "${KINDS[@]}"; do
  case "$kind" in
    deb) require_tools dpkg-deb ;;
    arch) require_tools bsdtar ;;
    rpm) require_tools rpm ;;
  esac
done
[[ " ${KINDS[*]} " == *" deb "* ]] || info "The feed has no .deb: installed .deb copies will see no update."

# Each kind: its file, size and content type, and the alias the website links to (as macos/WinoMail.dmg).
declare -A FILE_OF SIZE_OF ALIAS_OF TYPE_OF
ALIAS_OF[deb]="wino-mail_${DEB_ARCH}.deb"
ALIAS_OF[arch]="wino-mail-${PKG_ARCH}.pkg.tar.zst"
ALIAS_OF[rpm]="wino-mail.${PKG_ARCH}.rpm"
TYPE_OF[deb]="application/vnd.debian.binary-package"
TYPE_OF[arch]="application/zstd"
TYPE_OF[rpm]="application/x-rpm"

for kind in "${KINDS[@]}"; do
  arch="$(package_arch_of "$kind")"
  name="$(package_file_name "$kind" "$CHANNEL")"
  package_url="$(feed_value ".packages.${kind}[\"$arch\"].url")"
  package_sha256="$(feed_value ".packages.${kind}[\"$arch\"].sha256")"
  package_size="$(feed_value ".packages.${kind}[\"$arch\"].size")"
  [ "$(jq -r ".packages.$kind | keys | length" "$FEED")" = "1" ] || fail "release.json lists more than the $arch $kind package."
  [ "$package_url" = "$DOWNLOAD_BASE_URL/$name" ] || fail "The feed's $kind address is $package_url, not $DOWNLOAD_BASE_URL/$name."
  file="$FOLDER/$name"
  [ -f "$file" ] || fail "The feed references $name, which is missing from $FOLDER."
  size="$(size_of "$file")"
  [ "$size" = "$package_size" ] || fail "$name is $size bytes but the feed says $package_size."
  [ "$(sha256_of "$file")" = "$package_sha256" ] || fail "$name does not match the SHA-256 in the feed."
  [ -f "$file.sha256" ] || fail "$name.sha256 is missing."
  (cd "$FOLDER" && sha256sum -c --quiet "$name.sha256") || fail "$name does not match $name.sha256."
  case "$kind" in
    deb)
      [ "$(dpkg-deb -f "$file" Version)" = "$(debian_version "$CHANNEL")" ] || fail "$name does not carry the feed's version and build."
      [ "$(dpkg-deb -f "$file" Architecture)" = "$DEB_ARCH" ] || fail "$name is not an $DEB_ARCH package."
      ;;
    arch)
      pkginfo="$(bsdtar -xOf "$file" .PKGINFO)" || fail "$name has no .PKGINFO."
      [ "$(sed -n 's/^pkgname = //p' <<<"$pkginfo")" = "wino-mail" ] || fail "$name is not the wino-mail package."
      [ "$(sed -n 's/^pkgver = //p' <<<"$pkginfo")" = "$(arch_version "$CHANNEL")-1" ] || fail "$name does not carry the feed's version and build."
      [ "$(sed -n 's/^arch = //p' <<<"$pkginfo")" = "$PKG_ARCH" ] || fail "$name is not an $PKG_ARCH package."
      ;;
    rpm)
      [ "$(rpm -qp --qf '%{NAME} %{VERSION}-%{RELEASE} %{ARCH}' "$file" 2>/dev/null)" = "wino-mail $(rpm_version "$CHANNEL")-1 $PKG_ARCH" ] ||
        fail "$name does not carry the feed's version and build."
      ;;
  esac
  FILE_OF[$kind]="$file"
  SIZE_OF[$kind]="$size"
  info "$name ($((size / 1048576)) MB), version $FEED_VERSION, build $BUILD"
done

RECORD="$FOLDER/build.json"
if [ -f "$RECORD" ]; then
  [ "$(jq -r '.build' "$RECORD")" = "$BUILD" ] || fail "$RECORD does not describe build $BUILD."
  COMMIT="$(jq -r '.commit // empty' "$RECORD")"
  if [ -z "$COMMIT" ]; then
    info "Built from an unknown commit."
  else
    info "Built from $COMMIT$([ "$(jq -r '.dirty' "$RECORD")" = "true" ] && printf ' with uncommitted changes')"
  fi
  for kind in "${KINDS[@]}"; do
    info "$kind lint: $(jq -r --arg k "$kind" '.packages[$k].lint // (if $k == "deb" then .lintian else null end) // "unknown"' "$RECORD")"
  done
else
  info "No build.json beside the packages."
fi

if [ -n "$LIVE_BUILD" ]; then
  if [ "$BUILD" -lt "$LIVE_BUILD" ]; then
    [ "$ALLOW_OLDER" -eq 1 ] || fail "The live feed serves build $LIVE_BUILD, newer than $BUILD. Pass --allow-older to replace it anyway."
    info "Replacing the newer live build $LIVE_BUILD (--allow-older)."
  elif [ "$BUILD" -eq "$LIVE_BUILD" ]; then
    info "The live feed already serves build $BUILD. Installed copies of it will not be offered this upload."
  fi
fi

# ---------------------------------------------------------------- confirm
step "Uploads"
for kind in "${KINDS[@]}"; do
  name="$(basename "${FILE_OF[$kind]}")"
  info "$PREFIX/$name"
  info "$PREFIX/$name.sha256"
  info "$PREFIX/${ALIAS_OF[$kind]} (copy of $name)"
done
info "$PREFIX/release.json (last)"
HAS_ARCH=0
[[ " ${KINDS[*]} " != *" arch "* ]] || HAS_ARCH=1
if [ "$HAS_ARCH" -eq 1 ] && [ "$SKIP_AUR" -eq 0 ]; then
  info "Then: update-aur.sh --version $FULL_VERSION --build $BUILD (scripts/release/linux/aur/wino-mail-bin)"
fi

if [ "$DRY_RUN" -eq 1 ]; then
  printf '\nDry run: nothing was uploaded and the AUR files were not changed.\n'
  exit 0
fi

if [ "$NON_INTERACTIVE" -eq 0 ]; then
  read -r -p "    Publish $FEED_VERSION ($BUILD) to $DOWNLOAD_BASE_URL? [y/N] " answer
  [[ "$answer" =~ ^[Yy]$ ]] || fail "Cancelled."
fi

# ---------------------------------------------------------------- upload
# R2's S3 API through curl's SigV4 signer, as publish-macos-release.sh. The credentials go through a config file on
# stdin, never the command line, and the payload hash is sent explicitly so curl signs the real body.
r2_put() {
  local key="$1" file="$2" content_type="$3" hash
  hash="$(sha256sum "$file" | cut -d' ' -f1)"
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
for kind in "${KINDS[@]}"; do
  file="${FILE_OF[$kind]}"
  name="$(basename "$file")"
  info "$PREFIX/$name"
  r2_put "$PREFIX/$name" "$file" "${TYPE_OF[$kind]}"
  verify_public "$name" "${SIZE_OF[$kind]}"
  r2_put "$PREFIX/$name.sha256" "$file.sha256" "text/plain; charset=utf-8"
  info "$PREFIX/${ALIAS_OF[$kind]}"
  r2_put "$PREFIX/${ALIAS_OF[$kind]}" "$file" "${TYPE_OF[$kind]}"
  verify_public "${ALIAS_OF[$kind]}" "${SIZE_OF[$kind]}"
done
info "$PREFIX/release.json"
r2_put "$PREFIX/release.json" "$FEED" "application/json"

step "Verifying"
curl -fsS -H 'Cache-Control: no-cache' -o "$LIVE_FEED" "$DOWNLOAD_BASE_URL/release.json" || fail "The feed cannot be downloaded."
[ "$(jq -r '.build' "$LIVE_FEED")" = "$BUILD" ] || fail "The live feed does not serve build $BUILD yet."
info "$DOWNLOAD_BASE_URL/release.json serves $FEED_VERSION ($BUILD)"
for kind in "${KINDS[@]}"; do info "$DOWNLOAD_BASE_URL/${ALIAS_OF[$kind]}"; done

printf '\nPublished %s (%s).\n' "$FEED_VERSION" "$BUILD"

if [ "$HAS_ARCH" -eq 1 ] && [ "$SKIP_AUR" -eq 0 ]; then
  step "AUR"
  bash "$SCRIPT_DIR/update-aur.sh" --version "$FULL_VERSION" --build "$BUILD" --output-root "$OUTPUT_ROOT"
fi
exit 0
