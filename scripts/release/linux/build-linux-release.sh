#!/usr/bin/env bash
# Builds the Linux beta of Wino Mail: one Native AOT publish, the Debian, Arch and RPM packages of it, the update feed
# and the build record.
# Run on Ubuntu 26.04 or in WSL2: bash scripts/release/linux/build-linux-release.sh [--non-interactive]
# Needs no credentials. Publish the result with publish-linux-release.sh. See docs/releases-linux.md.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SCRIPT_DIR="$REPO_ROOT/scripts/release/linux"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=lib/stage.sh
source "$SCRIPT_DIR/lib/stage.sh"
# shellcheck source=../macos/release-translation.sh
source "$REPO_ROOT/scripts/release/macos/release-translation.sh"

CHANNEL="beta"
BUILD_NUMBER=""
NON_INTERACTIVE=0
TRANSLATE=""
SKIP_LINTIAN=0
SKIP_RPMLINT=0
OUTPUT_ROOT="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}"
PUBLISH_DIR=""
ROOT_DIR=""
FROM_DEB=""
ONLY=""
SKIP=""

usage() {
  cat <<'EOF'
Usage: build-linux-release.sh [options]

  --channel beta           Release channel (beta is the only channel now).
  --build-number <n>       Build number. Default: the UTC time as yyDDDHHMM, which always increases.
  --output-root <path>     Release root. Default: $WINO_RELEASES_ROOT or ~/Wino Releases.
  --only <kinds>           Build only these packages, comma-separated from deb,arch,rpm. Default: all three.
  --skip <kinds>           Build all packages except these.
  --publish-dir <dir>      Package this Native AOT publish instead of publishing.
  --from-deb <deb>         Repackage an existing beta .deb: keep its version and build, add the Arch and RPM packages,
                           and write the feed, record and checksums again. The output is the build's folder under the
                           release root; it may be the folder that holds the .deb.
  --translate              Run translate_resources.py --apply before building, without asking.
  --skip-translation       Build without translating and without asking.
  --skip-lintian           Do not run lintian even when it is installed.
  --skip-rpmlint           Do not run rpmlint even when it is installed.
  --non-interactive        Do not prompt. Missing choices use their defaults (no translation).
  -h, --help               Show this help.

Output: <root>/<a.b.c.0>/Linux/<build>/ with each package and its .sha256, release.json (the update feed), build.json,
SHA256SUMS and Symbols/amd64.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --channel) [ $# -ge 2 ] || fail "--channel needs a value."; CHANNEL="$2"; shift 2 ;;
    --channel=*) CHANNEL="${1#*=}"; shift ;;
    --build-number) [ $# -ge 2 ] || fail "--build-number needs a value."; BUILD_NUMBER="$2"; shift 2 ;;
    --build-number=*) BUILD_NUMBER="${1#*=}"; shift ;;
    --output-root) [ $# -ge 2 ] || fail "--output-root needs a path."; OUTPUT_ROOT="$2"; shift 2 ;;
    --output-root=*) OUTPUT_ROOT="${1#*=}"; shift ;;
    --only) [ $# -ge 2 ] || fail "--only needs a list."; ONLY="$2"; shift 2 ;;
    --only=*) ONLY="${1#*=}"; shift ;;
    --skip) [ $# -ge 2 ] || fail "--skip needs a list."; SKIP="$2"; shift 2 ;;
    --skip=*) SKIP="${1#*=}"; shift ;;
    --publish-dir) [ $# -ge 2 ] || fail "--publish-dir needs a folder."; PUBLISH_DIR="$2"; shift 2 ;;
    --publish-dir=*) PUBLISH_DIR="${1#*=}"; shift ;;
    --from-deb) [ $# -ge 2 ] || fail "--from-deb needs a package."; FROM_DEB="$2"; shift 2 ;;
    --from-deb=*) FROM_DEB="${1#*=}"; shift ;;
    --translate) set_release_translation yes; shift ;;
    --skip-translation) set_release_translation no; shift ;;
    --skip-lintian) SKIP_LINTIAN=1; shift ;;
    --skip-rpmlint) SKIP_RPMLINT=1; shift ;;
    --non-interactive) NON_INTERACTIVE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

if [ "$NON_INTERACTIVE" -eq 0 ] && [ ! -t 0 ]; then
  fail "No terminal for prompts. Run from a terminal or pass --non-interactive."
fi
[ "$CHANNEL" = "beta" ] || fail "Only the beta channel exists (--channel beta)."
[ -z "$ONLY" ] || [ -z "$SKIP" ] || fail "Pass --only or --skip, not both."

KINDS=()
for kind in "${PACKAGE_KINDS[@]}"; do
  if [ -n "$ONLY" ]; then [[ ",$ONLY," == *",$kind,"* ]] || continue; fi
  if [ -n "$SKIP" ]; then [[ ",$SKIP," != *",$kind,"* ]] || continue; fi
  KINDS+=("$kind")
done
for kind in ${ONLY//,/ } ${SKIP//,/ }; do
  [[ " ${PACKAGE_KINDS[*]} " == *" $kind "* ]] || fail "Unknown package kind '$kind'; the kinds are ${PACKAGE_KINDS[*]}."
done
[ ${#KINDS[@]} -gt 0 ] || fail "--only and --skip leave no package to build."
wants() { [[ " ${KINDS[*]} " == *" $1 "* ]]; }

# ---------------------------------------------------------------- preflight
step "Preflight"
[ "$(uname -s)" = "Linux" ] || fail "This script runs on Linux (or WSL2) only."
[ "$(dpkg --print-architecture 2>/dev/null || true)" = "$DEB_ARCH" ] || fail "This script builds $DEB_ARCH packages on an $DEB_ARCH host only."
require_tools jq sha256sum dpkg-deb
SELF_PUBLISH=0
if [ -n "$FROM_DEB" ]; then
  [ -z "$PUBLISH_DIR" ] || fail "Pass --publish-dir or --from-deb, not both."
  wants deb || fail "--from-deb keeps its .deb; do not skip deb."
elif [ -n "$PUBLISH_DIR" ]; then
  require_tools desktop-file-validate appstreamcli strip gzip git
else
  SELF_PUBLISH=1
  require_tools clang desktop-file-validate appstreamcli strip gzip git
  find_dotnet
  info ".NET SDK $(cd "$REPO_ROOT" && "$DOTNET" --version) ($DOTNET)"
fi
if wants deb && [ -z "$FROM_DEB" ]; then
  require_tools dpkg-shlibdeps fakeroot md5sum
  if [ "$SKIP_LINTIAN" -eq 1 ]; then info "lintian: skipped"
  elif command -v lintian >/dev/null 2>&1; then info "lintian $(lintian --version 2>/dev/null | awk '{print $2}')"
  else info "lintian is not installed; the .deb will not be linted. Install it with: sudo apt install lintian"
  fi
fi
if wants arch; then
  require_tools makepkg fakeroot bsdtar zstd
  [ "$(id -u)" -ne 0 ] || fail "makepkg refuses to run as root. Run this script as a normal user, or pass --skip arch."
fi
if wants rpm; then
  require_tools rpmbuild rpm bsdtar
  if [ "$SKIP_RPMLINT" -eq 0 ] && ! command -v rpmlint >/dev/null 2>&1; then
    info "rpmlint is not installed; the RPM will not be linted. $(install_hint rpmlint)"
  fi
fi
wants deb || info "No .deb in this build: installed .deb copies will see no update in its feed."

if [ -n "$FROM_DEB" ]; then
  [ -f "$FROM_DEB" ] || fail "The package does not exist: $FROM_DEB"
  FROM_DEB="$(cd "$(dirname "$FROM_DEB")" && pwd)/$(basename "$FROM_DEB")"
  WANTED_BUILD="$BUILD_NUMBER"
  read_deb_identity "$FROM_DEB"
  [ -z "$WANTED_BUILD" ] || [ "$WANTED_BUILD" = "$BUILD_NUMBER" ] || fail "$FROM_DEB is build $BUILD_NUMBER, not $WANTED_BUILD."
  [ "$CHANNEL" = "beta" ] || fail "$FROM_DEB is not a beta package."
  RECORD_IN="$(dirname "$FROM_DEB")/build.json"
  read_record_commit "$RECORD_IN"
else
  read_manifest_version
  resolve_build_number
  read_commit
fi
info "Version: $VERSION (folder $FULL_VERSION), build $BUILD_NUMBER, packages: ${KINDS[*]}"
info "Commit: ${COMMIT:-unknown}$([ "$DIRTY" = "true" ] && printf ' (with uncommitted changes)')"

DEST="$OUTPUT_ROOT/$FULL_VERSION/Linux/$BUILD_NUMBER"
IN_PLACE=0
if [ -n "$FROM_DEB" ] && [ -d "$DEST" ] && [ "$(cd "$DEST" && pwd)" = "$(dirname "$FROM_DEB")" ]; then
  IN_PLACE=1
  for kind in "${KINDS[@]}"; do
    [ "$kind" = "deb" ] || [ ! -e "$DEST/$(package_file_name "$kind" "$CHANNEL")" ] ||
      fail "$DEST already has the $kind package."
  done
  info "Adding packages to the folder of $(basename "$FROM_DEB")"
else
  [ ! -e "$DEST" ] || fail "$DEST exists. Choose another --build-number or remove the folder."
fi
mkdir -p "$DEST"
DEST="$(cd "$DEST" && pwd)"
SUCCESS=0
on_exit() {
  [ "$SUCCESS" -eq 0 ] || return 0
  if [ "$IN_PLACE" -eq 1 ]; then
    local kind name
    for kind in "${KINDS[@]}"; do
      [ "$kind" = "deb" ] && continue
      name="$(package_file_name "$kind" "$CHANNEL")"
      rm -f "$DEST/$name" "$DEST/$name.sha256" "$DEST/package-$kind.log"
      rm -rf "$DEST/.staging-$kind"
    done
    rm -rf "$DEST/.stage"
    if [ -d "$DEST/.previous" ]; then
      cp -p "$DEST"/.previous/* "$DEST/" 2>/dev/null || true
      rm -rf "$DEST/.previous"
    fi
    printf '\nThe build stopped; %s is back to its feed, record and packages from before.\n' "$DEST" >&2
  else
    printf '\nThe build stopped; the partial output is in %s.\n' "$DEST" >&2
  fi
}
trap on_exit EXIT
if [ "$IN_PLACE" -eq 1 ]; then
  rm -rf "$DEST/.previous"
  mkdir "$DEST/.previous"
  for name in release.json build.json SHA256SUMS; do [ ! -f "$DEST/$name" ] || cp -p "$DEST/$name" "$DEST/.previous/"; done
fi
PUBLISHED_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
export SOURCE_DATE_EPOCH
SOURCE_DATE_EPOCH="$(date -u -d "$PUBLISHED_UTC" +%s)"

# ---------------------------------------------------------------- translations
[ "$SELF_PUBLISH" -eq 0 ] || run_release_translation

# ---------------------------------------------------------------- publish and stage
STAGE="$DEST/.stage"
rm -rf "$STAGE"
if [ "$SELF_PUBLISH" -eq 1 ]; then
  step "Publishing"
  PUBLISH_DIR="$DEST/.publish"
  publish_aot "$PUBLISH_DIR" "$DEST/publish.log"
  check_publish_dir "$PUBLISH_DIR"
  info "Published $(du -sh "$PUBLISH_DIR" | cut -f1)"
elif [ -n "$PUBLISH_DIR" ]; then
  [ -d "$PUBLISH_DIR" ] || fail "The publish folder does not exist: $PUBLISH_DIR"
  PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd)"
  check_publish_dir "$PUBLISH_DIR"
fi

step "Staging the package tree"
if [ -n "$FROM_DEB" ]; then stage_from_deb "$FROM_DEB" "$STAGE" deb
else stage_from_publish "$PUBLISH_DIR" "$STAGE" deb
fi
check_staged_root "$STAGE" deb
write_stage_info "$STAGE"

# ---------------------------------------------------------------- packages
declare -A LINT=([deb]="not run" [arch]="not run" [rpm]="not run")
for kind in "${KINDS[@]}"; do
  step "Packaging $kind"
  ARGS=(--root "$STAGE" --build-number "$BUILD_NUMBER" --output "$DEST" --channel "$CHANNEL")
  case "$kind" in
    deb)
      if [ -n "$FROM_DEB" ]; then
        DEB_NAME="$(deb_file_name "$CHANNEL")"
        [ "$IN_PLACE" -eq 1 ] || cp "$FROM_DEB" "$DEST/$DEB_NAME"
        (cd "$DEST" && sha256sum "$DEB_NAME" >"$DEB_NAME.sha256")
        LINT[deb]="$(jq -r '.lintian // "unknown"' "$RECORD_IN" 2>/dev/null || printf 'unknown')"
        info "Kept $DEB_NAME (lintian: ${LINT[deb]})"
        continue
      fi
      [ "$SKIP_LINTIAN" -eq 1 ] && ARGS+=(--skip-lintian)
      bash "$SCRIPT_DIR/build-deb.sh" "${ARGS[@]}" | tee "$DEST/package.log"
      if [ "$SKIP_LINTIAN" -eq 1 ]; then LINT[deb]="skipped"; elif grep -q 'lintian passed' "$DEST/package.log"; then LINT[deb]="passed"; fi
      ;;
    arch)
      bash "$SCRIPT_DIR/build-arch.sh" "${ARGS[@]}" | tee "$DEST/package-arch.log"
      if grep -q 'namcap passed' "$DEST/package-arch.log"; then LINT[arch]="passed"; fi
      ;;
    rpm)
      [ "$SKIP_RPMLINT" -eq 1 ] && ARGS+=(--skip-rpmlint)
      bash "$SCRIPT_DIR/build-rpm.sh" "${ARGS[@]}" | tee "$DEST/package-rpm.log"
      if [ "$SKIP_RPMLINT" -eq 1 ]; then LINT[rpm]="skipped"; elif grep -q 'rpmlint passed' "$DEST/package-rpm.log"; then LINT[rpm]="passed"; fi
      ;;
  esac
  [ -f "$DEST/$(package_file_name "$kind" "$CHANNEL")" ] || fail "The $kind package was not produced."
done
[ -z "$PUBLISH_DIR" ] || copy_symbols "$PUBLISH_DIR" "$DEST"
rm -rf "$STAGE"
[ "$SELF_PUBLISH" -eq 0 ] || rm -rf "$PUBLISH_DIR"

# ---------------------------------------------------------------- feed and record
step "Writing the update feed and the build record"
PACKAGES_FEED='{}'
PACKAGES_RECORD='{}'
CHECKSUMMED=()
for kind in "${KINDS[@]}"; do
  name="$(package_file_name "$kind" "$CHANNEL")"
  PACKAGES_FEED="$(jq -c --arg kind "$kind" --arg arch "$(package_arch_of "$kind")" \
    --arg url "$DOWNLOAD_HOST/linux/$CHANNEL/$name" --arg sha256 "$(sha256_of "$DEST/$name")" --argjson size "$(size_of "$DEST/$name")" \
    '.[$kind] = {($arch): {url: $url, sha256: $sha256, size: $size}}' <<<"$PACKAGES_FEED")"
  case "$kind" in
    deb) version="$(debian_version "$CHANNEL")" ;;
    arch) version="$(arch_version "$CHANNEL")-1" ;;
    rpm) version="$(rpm_version "$CHANNEL")-1" ;;
  esac
  PACKAGES_RECORD="$(jq -c --arg kind "$kind" --arg arch "$(package_arch_of "$kind")" --arg file "$name" \
    --arg version "$version" --arg lint "${LINT[$kind]}" \
    '.[$kind] = {arch: $arch, file: $file, version: $version, lint: $lint}' <<<"$PACKAGES_RECORD")"
  CHECKSUMMED+=("$name")
done
jq -n --arg channel "$CHANNEL" --arg version "$VERSION" --argjson build "$BUILD_NUMBER" --arg published "$PUBLISHED_UTC" \
  --arg notes "$NOTES_URL" --arg page "$DOWNLOAD_PAGE_URL" --argjson packages "$PACKAGES_FEED" \
  '{schema: 1, platform: "linux", channel: $channel, version: $version, build: $build, publishedUtc: $published,
    notesUrl: $notes, downloadPageUrl: $page, packages: $packages}' >"$DEST/release.json"
DEB_NAME=""
DEB_VERSION=""
if wants deb; then DEB_NAME="$(deb_file_name "$CHANNEL")"; DEB_VERSION="$(debian_version "$CHANNEL")"; fi
jq -n --arg version "$VERSION" --arg manifest "$FULL_VERSION" --argjson build "$BUILD_NUMBER" --arg channel "$CHANNEL" \
  --arg arch "$DEB_ARCH" --arg deb "$DEB_NAME" --arg debVersion "$DEB_VERSION" --arg commit "$COMMIT" --argjson dirty "$DIRTY" \
  --arg lintian "${LINT[deb]}" --arg built "$PUBLISHED_UTC" --argjson packages "$PACKAGES_RECORD" \
  --arg source "$([ -n "$FROM_DEB" ] && printf 'deb' || printf 'publish')" \
  '{platform: "linux", version: $version, manifestVersion: $manifest, build: $build, channel: $channel, arch: $arch,
    deb: (if $deb == "" then null else $deb end), debVersion: (if $debVersion == "" then null else $debVersion end),
    commit: (if $commit == "" then null else $commit end), dirty: $dirty,
    lintian: $lintian, builtUtc: $built, packagedFrom: $source, packages: $packages}' >"$DEST/build.json"
(cd "$DEST" && sha256sum "${CHECKSUMMED[@]}" release.json build.json >SHA256SUMS)

# ---------------------------------------------------------------- self-check
step "Checking the output"
(cd "$DEST" && sha256sum -c --quiet SHA256SUMS) || fail "A checksum in SHA256SUMS does not match."
jq -e --arg v "$VERSION" --argjson b "$BUILD_NUMBER" '.schema == 1 and .platform == "linux" and .version == $v and .build == $b' \
  "$DEST/release.json" >/dev/null || fail "release.json does not describe version $VERSION, build $BUILD_NUMBER."
for kind in "${KINDS[@]}"; do
  name="$(package_file_name "$kind" "$CHANNEL")"
  file="$DEST/$name"
  (cd "$DEST" && sha256sum -c --quiet "$name.sha256") || fail "$name does not match $name.sha256."
  jq -e --arg k "$kind" --arg a "$(package_arch_of "$kind")" --arg s "$(sha256_of "$file")" --argjson z "$(size_of "$file")" \
    --arg u "$DOWNLOAD_HOST/linux/$CHANNEL/$name" \
    '.packages[$k][$a].sha256 == $s and .packages[$k][$a].size == $z and .packages[$k][$a].url == $u' \
    "$DEST/release.json" >/dev/null || fail "release.json does not describe $name."
  case "$kind" in
    deb)
      [ "$(dpkg-deb -f "$file" Version)" = "$(debian_version "$CHANNEL")" ] || fail "$name does not carry its version."
      distribution="$(dpkg-deb --fsys-tarfile "$file" | tar -xOf - "./$INSTALL_DIR/distribution")"
      ;;
    arch)
      [ "$(bsdtar -xOf "$file" .PKGINFO | sed -n 's/^pkgver = //p')" = "$(arch_version "$CHANNEL")-1" ] || fail "$name does not carry its version."
      distribution="$(bsdtar -xOf "$file" "$INSTALL_DIR/distribution")"
      ;;
    rpm)
      [ "$(rpm -qp --qf '%{VERSION}-%{RELEASE}' "$file" 2>/dev/null)" = "$(rpm_version "$CHANNEL")-1" ] || fail "$name does not carry its version."
      distribution="$(bsdtar -xOf "$file" "./$INSTALL_DIR/distribution")"
      ;;
  esac
  [ "$(sed -n 's/^build=//p' <<<"$distribution")" = "$BUILD_NUMBER" ] || fail "The distribution file in $name does not name build $BUILD_NUMBER."
  [ "$(sed -n 's/^package=//p' <<<"$distribution")" = "$kind" ] || fail "The distribution file in $name does not name package $kind."
  [ "$(sed -n 's/^channel=//p' <<<"$distribution")" = "$CHANNEL" ] || fail "The distribution file in $name does not name channel $CHANNEL."
  info "$name: checksums, feed, version and distribution agree; lint: ${LINT[$kind]}"
done
rm -rf "$DEST/.previous"
SUCCESS=1

step "Done"
for kind in "${KINDS[@]}"; do
  info "$DEST/$(package_file_name "$kind" "$CHANNEL")"
done
info "$DEST/release.json"
info "$DEST/build.json"
info "$DEST/SHA256SUMS"
[ ! -d "$DEST/Symbols/$DEB_ARCH" ] || info "$DEST/Symbols/$DEB_ARCH"
printf '\nTo publish: bash scripts/release/linux/publish-linux-release.sh --version %s --build %s\n' "$FULL_VERSION" "$BUILD_NUMBER"
