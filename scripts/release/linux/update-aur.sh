#!/usr/bin/env bash
# Points the AUR package wino-mail-bin (scripts/release/linux/aur/wino-mail-bin) at a published Arch package: rewrites
# pkgver and sha256sums in its PKGBUILD and regenerates .SRCINFO. publish-linux-release.sh runs it after an upload.
# Run as a normal user where makepkg exists (Ubuntu: apt install makepkg; Arch):
# bash scripts/release/linux/update-aur.sh --version 2.2.15.0 --build <n>
# See docs/releases-linux.md ("The AUR package").
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SCRIPT_DIR="$REPO_ROOT/scripts/release/linux"
# shellcheck source=lib/common.sh
source "$SCRIPT_DIR/lib/common.sh"

AUR_DIR="$SCRIPT_DIR/aur/wino-mail-bin"
OUTPUT_ROOT="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}"
CHANNEL="beta"
FULL_VERSION=""
BUILD_NUMBER=""

usage() {
  cat <<'EOF'
Usage: update-aur.sh --version <x.y.z.0> --build <n> [options]

  --version <x.y.z.0>   Release folder of the published build.
  --build <n>           The published build.
  --output-root <path>  Release root. Default: $WINO_RELEASES_ROOT or ~/Wino Releases.
  -h, --help            Show this help.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || fail "--version needs a value such as 2.2.15.0."; FULL_VERSION="$2"; shift 2 ;;
    --version=*) FULL_VERSION="${1#*=}"; shift ;;
    --build) [ $# -ge 2 ] || fail "--build needs a build number."; BUILD_NUMBER="$2"; shift 2 ;;
    --build=*) BUILD_NUMBER="${1#*=}"; shift ;;
    --output-root) [ $# -ge 2 ] || fail "--output-root needs a path."; OUTPUT_ROOT="$2"; shift 2 ;;
    --output-root=*) OUTPUT_ROOT="${1#*=}"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; fail "Unknown argument: $1" ;;
  esac
done

[[ "$FULL_VERSION" =~ ^([1-9][0-9]*\.[0-9]+\.[0-9]+)\.0$ ]] || fail "--version must be a.b.c.0 ($FULL_VERSION)."
VERSION="${BASH_REMATCH[1]}"
resolve_build_number
[ "$(id -u)" -ne 0 ] || fail "makepkg refuses to run as root. Run this script as a normal user."
require_tools makepkg sha256sum

PKG_NAME="$(arch_file_name "$CHANNEL")"
PKG="$OUTPUT_ROOT/$FULL_VERSION/Linux/$BUILD_NUMBER/$PKG_NAME"
[ -f "$PKG" ] || fail "The Arch package of build $BUILD_NUMBER is not in $(dirname "$PKG")."
PKG_VERSION="$(arch_version "$CHANNEL")"
PKG_SHA256="$(sha256_of "$PKG")"

sed -i -e "s/^pkgver=.*/pkgver=$PKG_VERSION/" -e "s/^pkgrel=.*/pkgrel=1/" \
  -e "s/^sha256sums=.*/sha256sums=('$PKG_SHA256')/" "$AUR_DIR/PKGBUILD"
if ! grep -q "^pkgver=$PKG_VERSION$" "$AUR_DIR/PKGBUILD" || ! grep -q "^sha256sums=('$PKG_SHA256')$" "$AUR_DIR/PKGBUILD"; then
  fail "$AUR_DIR/PKGBUILD was not rewritten."
fi
(cd "$AUR_DIR" && makepkg --printsrcinfo >.SRCINFO) || fail "makepkg --printsrcinfo failed."
grep -q "pkgver = $PKG_VERSION$" "$AUR_DIR/.SRCINFO" || fail ".SRCINFO does not name $PKG_VERSION."

step "AUR package files"
info "$AUR_DIR/PKGBUILD: pkgver $PKG_VERSION, sha256 $PKG_SHA256"
info "$AUR_DIR/.SRCINFO regenerated"
cat <<EOF

Commit the two files here, then publish them to the AUR (the owner's AUR SSH key is needed):

  git clone ssh://aur@aur.archlinux.org/wino-mail-bin.git ~/aur-wino-mail-bin   # once; later: git -C ~/aur-wino-mail-bin pull
  cp "$AUR_DIR/PKGBUILD" "$AUR_DIR/.SRCINFO" ~/aur-wino-mail-bin/
  git -C ~/aur-wino-mail-bin add PKGBUILD .SRCINFO
  git -C ~/aur-wino-mail-bin commit -m "Update to $PKG_VERSION"
  git -C ~/aur-wino-mail-bin push
EOF
