#!/usr/bin/env bash
# Builds the Debian package of Wino Mail for Linux from a Native AOT publish, a staged tree or an existing beta .deb.
# Run on Ubuntu or in WSL2: bash scripts/release/linux/build-deb.sh [--publish-dir <dir> | --root <dir> | --from-deb <deb>]
# See docs/releases-linux.md.
set -euo pipefail
umask 022

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SCRIPT_DIR="$REPO_ROOT/scripts/release/linux"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"
# shellcheck source=lib/stage.sh
source "$SCRIPT_DIR/lib/stage.sh"

PUBLISH_DIR=""
ROOT_DIR=""
FROM_DEB=""
BUILD_NUMBER=""
OUTPUT_DIR=""
CHANNEL="beta"
SKIP_LINTIAN=0

usage() {
  cat <<'EOF'
Usage: build-deb.sh [options]

  --publish-dir <dir>    Package this Native AOT publish. Default: publish the app first.
  --root <dir>           Package this tree, staged from a publish by build-linux-release.sh.
  --from-deb <deb>       Repackage the tree of this beta .deb; its version and build are kept.
  --build-number <n>     Build number of the package. Default: the UTC time as yyDDDHHMM, which always increases.
  --output <dir>         Folder for the package, its .sha256 and Symbols/. Default:
                         $WINO_RELEASES_ROOT (or ~/Wino Releases)/<a.b.c.0>/Linux/<build>.
  --channel beta         Release channel written into the package (beta is the only channel now).
  --skip-lintian         Do not run lintian even when it is installed.
  -h, --help             Show this help.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --publish-dir) [ $# -ge 2 ] || fail "--publish-dir needs a folder."; PUBLISH_DIR="$2"; shift 2 ;;
    --publish-dir=*) PUBLISH_DIR="${1#*=}"; shift ;;
    --root) [ $# -ge 2 ] || fail "--root needs a folder."; ROOT_DIR="$2"; shift 2 ;;
    --root=*) ROOT_DIR="${1#*=}"; shift ;;
    --from-deb) [ $# -ge 2 ] || fail "--from-deb needs a package."; FROM_DEB="$2"; shift 2 ;;
    --from-deb=*) FROM_DEB="${1#*=}"; shift ;;
    --build-number) [ $# -ge 2 ] || fail "--build-number needs a value."; BUILD_NUMBER="$2"; shift 2 ;;
    --build-number=*) BUILD_NUMBER="${1#*=}"; shift ;;
    --output) [ $# -ge 2 ] || fail "--output needs a folder."; OUTPUT_DIR="$2"; shift 2 ;;
    --output=*) OUTPUT_DIR="${1#*=}"; shift ;;
    --channel) [ $# -ge 2 ] || fail "--channel needs a value."; CHANNEL="$2"; shift 2 ;;
    --channel=*) CHANNEL="${1#*=}"; shift ;;
    --skip-lintian) SKIP_LINTIAN=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[ "$CHANNEL" = "beta" ] || fail "Only the beta channel exists (--channel beta)."

# ---------------------------------------------------------------- preflight
step "Preflight"
[ "$(uname -s)" = "Linux" ] || fail "This script runs on Linux (or WSL2) only."
[ "$(dpkg --print-architecture 2>/dev/null || true)" = "$DEB_ARCH" ] || fail "This script builds $DEB_ARCH packages on an $DEB_ARCH host only."
require_tools dpkg-deb dpkg-shlibdeps fakeroot desktop-file-validate appstreamcli gzip md5sum sha256sum install strip jq
SELF_PUBLISH=0
if [ -z "$PUBLISH_DIR$ROOT_DIR$FROM_DEB" ]; then
  SELF_PUBLISH=1
  require_tools clang
  find_dotnet
  info ".NET SDK $(cd "$REPO_ROOT" && "$DOTNET" --version) ($DOTNET)"
fi
resolve_package_input
DEB_VERSION="$(debian_version "$CHANNEL")"
DEB_NAME="$(deb_file_name "$CHANNEL")"
info "Version: $VERSION (folder $FULL_VERSION), build $BUILD_NUMBER, package $DEB_VERSION"
info "Commit: ${COMMIT:-unknown}$([ "$DIRTY" = "true" ] && printf ' (with uncommitted changes)')"

[ -n "$OUTPUT_DIR" ] || OUTPUT_DIR="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}/$FULL_VERSION/Linux/$BUILD_NUMBER"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
[ ! -e "$OUTPUT_DIR/$DEB_NAME" ] || fail "$OUTPUT_DIR/$DEB_NAME exists. Choose another build number or output folder."
STAGING="$OUTPUT_DIR/.staging-deb"
rm -rf "$STAGING"
mkdir -p "$STAGING"

# ---------------------------------------------------------------- publish
if [ "$SELF_PUBLISH" -eq 1 ]; then
  step "Publishing"
  PUBLISH_DIR="$STAGING/publish"
  publish_aot "$PUBLISH_DIR" "$OUTPUT_DIR/publish.log"
  check_publish_dir "$PUBLISH_DIR"
fi
[ -z "$PUBLISH_DIR" ] || info "Publish: $PUBLISH_DIR ($(du -sh "$PUBLISH_DIR" | cut -f1))"

# ---------------------------------------------------------------- stage
step "Staging the package tree"
ROOT="$STAGING/root"
APP_DIR="$ROOT/$INSTALL_DIR"
stage_package_input "$ROOT" deb
install -d -m 0755 "$ROOT/DEBIAN" "$ROOT/usr/share/doc/wino-mail" "$ROOT/usr/share/lintian/overrides"
sed "s|@MAINTAINER@|$MAINTAINER|g" "$SCRIPT_DIR/deb/copyright" >"$ROOT/usr/share/doc/wino-mail/copyright"
{
  printf 'wino-mail (%s) unstable; urgency=medium\n\n' "$DEB_VERSION"
  printf '  * Wino Mail %s, %s build %s' "$VERSION" "$CHANNEL" "$BUILD_NUMBER"
  [ -n "$COMMIT" ] && printf ' from commit %s' "${COMMIT:0:12}"
  printf '.\n\n -- %s  %s\n' "$MAINTAINER" "$(LC_ALL=C date -u -R)"
} | gzip -9n >"$ROOT/usr/share/doc/wino-mail/changelog.gz"
install -m 0644 "$SCRIPT_DIR/deb/lintian-overrides" "$ROOT/usr/share/lintian/overrides/wino-mail"
chmod 0644 "$ROOT/usr/share/doc/wino-mail/copyright" "$ROOT/usr/share/doc/wino-mail/changelog.gz"
METAINFO="$ROOT/usr/share/metainfo/$APP_ID.metainfo.xml"

# ---------------------------------------------------------------- control
step "Writing the control file"
# dpkg-shlibdeps reads debian/control of a source tree; a minimal one in the staging folder is enough.
SHLIBS_DIR="$STAGING/shlibs"
mkdir -p "$SHLIBS_DIR/debian"
printf 'Source: wino-mail\n\nPackage: wino-mail\nArchitecture: %s\n' "$DEB_ARCH" >"$SHLIBS_DIR/debian/control"
ELF_FILES=("$APP_DIR/wino-mail")
for library in "${NATIVE_LIBRARIES[@]}"; do ELF_FILES+=("$APP_DIR/$library"); done
SHLIBS="$(cd "$SHLIBS_DIR" && dpkg-shlibdeps -O -l"$APP_DIR" "${ELF_FILES[@]}" 2>"$STAGING/shlibdeps.log")" ||
  { cat "$STAGING/shlibdeps.log" >&2; fail "dpkg-shlibdeps failed."; }
SHLIBS="${SHLIBS#shlibs:Depends=}"
[ -n "$SHLIBS" ] || fail "dpkg-shlibdeps found no dependencies."
info "Libraries: $SHLIBS"

# The extended description is the metainfo description: its paragraph, then its list.
DESCRIPTION="$(metainfo_text "$METAINFO" p | fold -s -w 78 | sed 's/ *$//; s/^/ /')"
[ -n "$DESCRIPTION" ] || fail "The metainfo has no description paragraph."
DESCRIPTION="$DESCRIPTION"$'\n'" ."
while IFS= read -r item; do DESCRIPTION="$DESCRIPTION"$'\n'"  * $item"; done < <(metainfo_text "$METAINFO" li)
INSTALLED_SIZE="$(du -sk --exclude=DEBIAN "$ROOT" | cut -f1)"

sed -e "s#@VERSION@#$DEB_VERSION#" -e "s#@ARCH@#$DEB_ARCH#" -e "s#@MAINTAINER@#$MAINTAINER#" \
    -e "s#@INSTALLED_SIZE@#$INSTALLED_SIZE#" -e "s#@SHLIBS@#$SHLIBS#" "$SCRIPT_DIR/deb/control.in" |
  DESCRIPTION="$DESCRIPTION" awk '$0 == "@DESCRIPTION@" { print ENVIRON["DESCRIPTION"]; next } { print }' >"$ROOT/DEBIAN/control"
chmod 0644 "$ROOT/DEBIAN/control"
(cd "$ROOT" && find usr -type f -print0 | sort -z | xargs -0 md5sum) >"$ROOT/DEBIAN/md5sums"
chmod 0644 "$ROOT/DEBIAN/md5sums"

# ---------------------------------------------------------------- build
step "Building $DEB_NAME"
DEB="$OUTPUT_DIR/$DEB_NAME"
fakeroot dpkg-deb --build -Zxz --root-owner-group "$ROOT" "$DEB" >/dev/null || fail "dpkg-deb failed."
[ "$(dpkg-deb -f "$DEB" Version)" = "$DEB_VERSION" ] || fail "The built package does not carry version $DEB_VERSION."
info "$DEB ($(( $(size_of "$DEB") / 1048576 )) MB)"

if [ "$SKIP_LINTIAN" -eq 1 ]; then
  info "lintian skipped (--skip-lintian)"
elif command -v lintian >/dev/null 2>&1; then
  step "lintian"
  lintian --fail-on error,warning "$DEB" || fail "lintian reported errors or warnings."
  info "lintian passed"
else
  info "lintian is not installed; the package was not linted. Install it with: sudo apt install lintian"
fi

(cd "$OUTPUT_DIR" && sha256sum "$DEB_NAME" >"$DEB_NAME.sha256")

[ -z "$PUBLISH_DIR" ] || copy_symbols "$PUBLISH_DIR" "$OUTPUT_DIR"
rm -rf "$STAGING"

step "Done"
info "$DEB"
info "$DEB.sha256"
[ -z "$PUBLISH_DIR" ] || info "$OUTPUT_DIR/Symbols/$DEB_ARCH"
