#!/usr/bin/env bash
# Builds the Arch Linux package of Wino Mail (also for Omarchy) with makepkg, from a Native AOT publish, a staged tree
# or an existing beta .deb. Run as a normal user on Ubuntu (apt install makepkg) or on Arch:
# bash scripts/release/linux/build-arch.sh [--publish-dir <dir> | --root <dir> | --from-deb <deb>]
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

usage() {
  cat <<'EOF'
Usage: build-arch.sh (--publish-dir <dir> | --root <dir> | --from-deb <deb>) [options]

  --publish-dir <dir>    Package this Native AOT publish.
  --root <dir>           Package this tree, staged from a publish by build-linux-release.sh.
  --from-deb <deb>       Package the tree of this beta .deb; its version and build are kept.
  --build-number <n>     Build number of the package. Default: the UTC time as yyDDDHHMM (from the .deb with --from-deb).
  --output <dir>         Folder for the package and its .sha256. Default:
                         $WINO_RELEASES_ROOT (or ~/Wino Releases)/<a.b.c.0>/Linux/<build>.
  --channel beta         Release channel written into the package (beta is the only channel now).
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
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[ "$CHANNEL" = "beta" ] || fail "Only the beta channel exists (--channel beta)."
[ -n "$PUBLISH_DIR$ROOT_DIR$FROM_DEB" ] || { usage >&2; fail "Pass --publish-dir, --root or --from-deb."; }

# ---------------------------------------------------------------- preflight
step "Preflight"
[ "$(uname -s)" = "Linux" ] || fail "This script runs on Linux (or WSL2) only."
[ "$(uname -m)" = "$PKG_ARCH" ] || fail "This script builds $PKG_ARCH packages on an $PKG_ARCH host only."
[ "$(id -u)" -ne 0 ] || fail "makepkg refuses to run as root. Run this script as a normal user."
require_tools makepkg fakeroot bsdtar zstd sha256sum install jq
[ -z "$PUBLISH_DIR" ] || require_tools desktop-file-validate appstreamcli strip gzip
[ -z "$FROM_DEB" ] || require_tools dpkg-deb
resolve_package_input
PKG_VERSION="$(arch_version "$CHANNEL")"
PKG_NAME="$(arch_file_name "$CHANNEL")"
info "Version: $VERSION (folder $FULL_VERSION), build $BUILD_NUMBER, package $PKG_VERSION-1"

[ -n "$OUTPUT_DIR" ] || OUTPUT_DIR="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}/$FULL_VERSION/Linux/$BUILD_NUMBER"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
[ ! -e "$OUTPUT_DIR/$PKG_NAME" ] || fail "$OUTPUT_DIR/$PKG_NAME exists. Choose another build number or output folder."
STAGING="$OUTPUT_DIR/.staging-arch"
rm -rf "$STAGING"
mkdir -p "$STAGING"

# ---------------------------------------------------------------- stage
step "Staging the package tree"
ROOT="$STAGING/root"
stage_package_input "$ROOT" arch
stage_license "$ROOT"
EXECUTABLE_SHA256="$(sha256_of "$ROOT/$INSTALL_DIR/wino-mail")"

# ---------------------------------------------------------------- build
step "Building $PKG_NAME"
BUILD_DIR="$STAGING/build"
mkdir -p "$BUILD_DIR" "$STAGING/makepkg" "$STAGING/out"
PKGDESC="Mail, calendar, contacts and To Do in one app ($CHANNEL)"
sed -e "s#@MAINTAINER@#$MAINTAINER#" -e "s#@PKGVER@#$PKG_VERSION#" -e "s#@PKGDESC@#$PKGDESC#" \
  "$SCRIPT_DIR/arch/PKGBUILD.in" >"$BUILD_DIR/PKGBUILD"
(
  cd "$BUILD_DIR"
  WINO_ROOT="$ROOT" PKGDEST="$STAGING/out" BUILDDIR="$STAGING/makepkg" SRCDEST="$STAGING/makepkg" \
    PACKAGER="$MAINTAINER" SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(date +%s)}" \
    makepkg --config "$SCRIPT_DIR/arch/makepkg.conf" --force --nodeps --skipinteg --noconfirm --noprogressbar
) >"$STAGING/makepkg.log" 2>&1 || { tail -n 40 "$STAGING/makepkg.log" >&2; fail "makepkg failed."; }
[ -f "$STAGING/out/$PKG_NAME" ] || { ls "$STAGING/out" >&2; fail "makepkg did not produce $PKG_NAME."; }
PKG="$OUTPUT_DIR/$PKG_NAME"
mv "$STAGING/out/$PKG_NAME" "$PKG"

# ---------------------------------------------------------------- check
PKGINFO="$(bsdtar -xOf "$PKG" .PKGINFO)" || fail "$PKG_NAME has no .PKGINFO."
pkginfo_value() { awk -v key="$1" '$1 == key && $2 == "=" { sub(/^[^=]*= /, ""); print; exit }' <<<"$PKGINFO"; }
[ "$(pkginfo_value pkgname)" = "wino-mail" ] || fail "$PKG_NAME is not the wino-mail package."
[ "$(pkginfo_value pkgver)" = "$PKG_VERSION-1" ] || fail "$PKG_NAME does not carry version $PKG_VERSION-1."
[ "$(pkginfo_value arch)" = "$PKG_ARCH" ] || fail "$PKG_NAME is not an $PKG_ARCH package."
[ "$(bsdtar -xOf "$PKG" "$INSTALL_DIR/wino-mail" | sha256sum | cut -d' ' -f1)" = "$EXECUTABLE_SHA256" ] ||
  fail "makepkg changed the executable in $PKG_NAME."
[ "$(bsdtar -xOf "$PKG" "$INSTALL_DIR/distribution" | sed -n 's/^package=//p')" = "arch" ] ||
  fail "The distribution file in $PKG_NAME does not name package arch."
info "$PKG ($(( $(size_of "$PKG") / 1048576 )) MB), executable unchanged"

if command -v namcap >/dev/null 2>&1; then
  step "namcap"
  namcap "$PKG" | tee "$STAGING/namcap.log"
  ! grep -q ' E: ' "$STAGING/namcap.log" || fail "namcap reported errors."
  info "namcap passed"
else
  info "namcap is not installed (it exists on Arch only); the package was not linted."
fi

(cd "$OUTPUT_DIR" && sha256sum "$PKG_NAME" >"$PKG_NAME.sha256")
rm -rf "$STAGING"

step "Done"
info "$PKG"
info "$PKG.sha256"
