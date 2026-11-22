#!/usr/bin/env bash
# Builds the RPM of Wino Mail for Fedora with rpmbuild, from a Native AOT publish, a staged tree or an existing beta
# .deb. Run on Ubuntu (apt install rpm rpmlint) or on Fedora:
# bash scripts/release/linux/build-rpm.sh [--publish-dir <dir> | --root <dir> | --from-deb <deb>]
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
SKIP_RPMLINT=0

usage() {
  cat <<'EOF'
Usage: build-rpm.sh (--publish-dir <dir> | --root <dir> | --from-deb <deb>) [options]

  --publish-dir <dir>    Package this Native AOT publish.
  --root <dir>           Package this tree, staged from a publish by build-linux-release.sh.
  --from-deb <deb>       Package the tree of this beta .deb; its version and build are kept.
  --build-number <n>     Build number of the package. Default: the UTC time as yyDDDHHMM (from the .deb with --from-deb).
  --output <dir>         Folder for the package and its .sha256. Default:
                         $WINO_RELEASES_ROOT (or ~/Wino Releases)/<a.b.c.0>/Linux/<build>.
  --channel beta         Release channel written into the package (beta is the only channel now).
  --skip-rpmlint         Do not run rpmlint even when it is installed.
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
    --skip-rpmlint) SKIP_RPMLINT=1; shift ;;
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
require_tools rpmbuild rpm bsdtar sha256sum install jq
[ -z "$PUBLISH_DIR" ] || require_tools desktop-file-validate appstreamcli strip gzip
[ -z "$FROM_DEB" ] || require_tools dpkg-deb
resolve_package_input
PKG_VERSION="$(rpm_version "$CHANNEL")"
PKG_NAME="$(rpm_file_name "$CHANNEL")"
info "Version: $VERSION (folder $FULL_VERSION), build $BUILD_NUMBER, package $PKG_VERSION-1"

[ -n "$OUTPUT_DIR" ] || OUTPUT_DIR="${WINO_RELEASES_ROOT:-$HOME/Wino Releases}/$FULL_VERSION/Linux/$BUILD_NUMBER"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
[ ! -e "$OUTPUT_DIR/$PKG_NAME" ] || fail "$OUTPUT_DIR/$PKG_NAME exists. Choose another build number or output folder."
# rpmbuild splits the build root on spaces, and the default release root is "Wino Releases".
STAGING="$(mktemp -d "${TMPDIR:-/tmp}/wino-rpm.XXXXXX")"

# ---------------------------------------------------------------- stage
step "Staging the package tree"
ROOT="$STAGING/root"
stage_package_input "$ROOT" rpm
stage_license "$ROOT"
EXECUTABLE_SHA256="$(sha256_of "$ROOT/$INSTALL_DIR/wino-mail")"

# ---------------------------------------------------------------- spec
step "Writing the spec"
METAINFO="$ROOT/usr/share/metainfo/$APP_ID.metainfo.xml"
DESCRIPTION="$(metainfo_text "$METAINFO" p | fold -s -w 78 | sed 's/ *$//')"
[ -n "$DESCRIPTION" ] || fail "The metainfo has no description paragraph."
DESCRIPTION="$DESCRIPTION"$'\n'
while IFS= read -r item; do DESCRIPTION="$DESCRIPTION"$'\n'"- $item"; done < <(metainfo_text "$METAINFO" li)
EPOCH="${SOURCE_DATE_EPOCH:-$(date +%s)}"
CHANGELOG="* $(LC_ALL=C date -u -d "@$EPOCH" '+%a %b %d %Y') $MAINTAINER - $PKG_VERSION-1"$'\n'
CHANGELOG="$CHANGELOG- Wino Mail $VERSION, $CHANNEL build $BUILD_NUMBER${COMMIT:+ from commit ${COMMIT:0:12}}."
SPEC="$STAGING/wino-mail.spec"
sed -e "s#@VERSION@#$PKG_VERSION#" -e "s#@MAINTAINER@#$MAINTAINER#" \
    -e "s#@SUMMARY@#Mail, calendar, contacts and To Do in one app ($CHANNEL)#" "$SCRIPT_DIR/rpm/wino-mail.spec.in" |
  DESCRIPTION="${DESCRIPTION//%/%%}" CHANGELOG="${CHANGELOG//%/%%}" awk '
    $0 == "@DESCRIPTION@" { print ENVIRON["DESCRIPTION"]; next }
    $0 == "@CHANGELOG@" { print ENVIRON["CHANGELOG"]; next }
    { print }' >"$SPEC"

# ---------------------------------------------------------------- build
step "Building $PKG_NAME"
# rpm 6 writes v6 packages by default; _rpmformat 4 keeps them installable by the rpm 4 releases Fedora ships.
SOURCE_DATE_EPOCH="$EPOCH" rpmbuild -bb --target "$PKG_ARCH" --nodeps \
  --define "_topdir $STAGING/rpmbuild" \
  --define "wino_root $ROOT" \
  --define "_rpmformat 4" \
  --define "_binary_payload w19.zstdio" \
  --define "_buildhost wino-release" \
  --define "use_source_date_epoch_as_buildtime 1" \
  --define "clamp_mtime_to_source_date_epoch 1" \
  "$SPEC" >"$STAGING/rpmbuild.log" 2>&1 || { tail -n 40 "$STAGING/rpmbuild.log" >&2; fail "rpmbuild failed."; }
BUILT="$STAGING/rpmbuild/RPMS/$PKG_ARCH/$PKG_NAME"
[ -f "$BUILT" ] || { find "$STAGING/rpmbuild/RPMS" -type f >&2; fail "rpmbuild did not produce $PKG_NAME."; }
PKG="$OUTPUT_DIR/$PKG_NAME"
mv "$BUILT" "$PKG"

# ---------------------------------------------------------------- check
[ "$(rpm -qp --qf '%{NAME} %{VERSION}-%{RELEASE} %{ARCH}' "$PKG" 2>/dev/null)" = "wino-mail $PKG_VERSION-1 $PKG_ARCH" ] ||
  fail "$PKG_NAME does not carry wino-mail $PKG_VERSION-1 for $PKG_ARCH."
[ "$(bsdtar -xOf "$PKG" "./$INSTALL_DIR/wino-mail" | sha256sum | cut -d' ' -f1)" = "$EXECUTABLE_SHA256" ] ||
  fail "rpmbuild changed the executable in $PKG_NAME."
[ "$(bsdtar -xOf "$PKG" "./$INSTALL_DIR/distribution" | sed -n 's/^package=//p')" = "rpm" ] ||
  fail "The distribution file in $PKG_NAME does not name package rpm."
info "$PKG ($(( $(size_of "$PKG") / 1048576 )) MB), executable unchanged"

if [ "$SKIP_RPMLINT" -eq 1 ]; then
  info "rpmlint skipped (--skip-rpmlint)"
elif command -v rpmlint >/dev/null 2>&1; then
  step "rpmlint"
  rpmlint "$PKG" >"$STAGING/rpmlint.log" 2>&1 || true
  grep -E '^wino-mail[^ ]*: [EW]: ' "$STAGING/rpmlint.log" | sed 's/^/    /' || true
  grep -q 'packages and [0-9]* specfiles checked' "$STAGING/rpmlint.log" || { cat "$STAGING/rpmlint.log" >&2; fail "rpmlint did not run."; }
  UNEXPECTED="$(sed -n 's/^wino-mail[^ ]*: [EW]: \([^ ]*\).*/\1/p' "$STAGING/rpmlint.log" | sort -u |
    grep -vxF -f <(sed '/^#/d; /^$/d' "$SCRIPT_DIR/rpm/rpmlint-accepted") | paste -sd, - || true)"
  [ -z "$UNEXPECTED" ] || fail "rpmlint reported findings that rpm/rpmlint-accepted does not list: $UNEXPECTED"
  info "rpmlint passed (only the findings in rpm/rpmlint-accepted)"
else
  info "rpmlint is not installed; the package was not linted. $(install_hint rpmlint)"
fi

(cd "$OUTPUT_DIR" && sha256sum "$PKG_NAME" >"$PKG_NAME.sha256")
rm -rf "$STAGING"

step "Done"
info "$PKG"
info "$PKG.sha256"
