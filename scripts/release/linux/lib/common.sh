# Sourced by the Linux release scripts. Expects REPO_ROOT to be set by the caller.
# shellcheck shell=bash

PROJECT="$REPO_ROOT/src/Wino.Mail.Linux/Wino.Mail.Linux.csproj"
MANIFEST="$REPO_ROOT/src/Wino.Mail.WinUI/Package.appxmanifest"
DOWNLOAD_HOST="https://download.winomail.app"
# The feed's "Learn more" and download page links. No Linux page exists yet, so both are the website the apps' About
# links open (AppUrls.Website); the app accepts any https address on winomail.app or www.winomail.app.
NOTES_URL="https://www.winomail.app"
DOWNLOAD_PAGE_URL="https://www.winomail.app"
MAINTAINER="Burak Kaan Köse <bkaankose@outlook.com>"
APP_ID="app.winomail.Mail"
INSTALL_DIR="usr/lib/wino-mail"
DEB_ARCH="amd64"
PKG_ARCH="x86_64"
DOTNET_RID="linux-x64"
PACKAGE_KINDS=(deb arch rpm)
# The native libraries an AOT publish must contain beside the executable.
NATIVE_LIBRARIES=(libSkiaSharp.so libe_sqlite3.so libonnxruntime.so libonnxruntime_providers_shared.so)
# The data folders the app reads from AppContext.BaseDirectory.
DATA_FOLDERS=(Fonts Models Themes Assets/WhatsNew icons Resources)

step() { printf '\n==> %s\n' "$*"; }
info() { printf '    %s\n' "$*"; }
fail() { printf '\nERROR: %s\n' "$*" >&2; exit 1; }

require_tools() {
  local tool
  for tool in "$@"; do
    command -v "$tool" >/dev/null 2>&1 || fail "'$tool' was not found. $(install_hint "$tool")"
  done
}

# apt is the release host's package manager; Arch and Fedora names are given for running a script there.
install_hint() {
  local apt pacman dnf
  case "$1" in
    dpkg-deb) apt=dpkg pacman=dpkg dnf=dpkg ;;
    dpkg-shlibdeps) apt=dpkg-dev pacman=dpkg dnf=dpkg-dev ;;
    desktop-file-validate) apt=desktop-file-utils pacman=desktop-file-utils dnf=desktop-file-utils ;;
    appstreamcli) apt=appstream pacman=appstream dnf=appstream ;;
    sha256sum|md5sum|install) apt=coreutils pacman=coreutils dnf=coreutils ;;
    strip|objdump) apt=binutils pacman=binutils dnf=binutils ;;
    makepkg) apt=makepkg pacman=pacman dnf=- ;;
    vercmp) apt=pacman-package-manager pacman=pacman dnf=- ;;
    rpmbuild) apt=rpm pacman=rpm-tools dnf=rpm-build ;;
    rpm) apt=rpm pacman=rpm-tools dnf=rpm ;;
    rpmlint) apt=rpmlint pacman=rpmlint dnf=rpmlint ;;
    bsdtar) apt=libarchive-tools pacman=libarchive dnf=bsdtar ;;
    zstd) apt=zstd pacman=zstd dnf=zstd ;;
    *) apt="$1" pacman="$1" dnf="$1" ;;
  esac
  printf 'Install it with: sudo apt install %s (Arch: sudo pacman -S %s; Fedora: sudo dnf install %s)' "$apt" "$pacman" "$dnf"
}

find_dotnet() {
  if [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET="$HOME/.dotnet/dotnet"
  elif command -v dotnet >/dev/null 2>&1; then DOTNET="$(command -v dotnet)"
  else fail "The .NET SDK was not found in ~/.dotnet or on PATH. Install the SDK selected by global.json."
  fi
  (cd "$REPO_ROOT" && "$DOTNET" --version >/dev/null 2>&1) ||
    fail "'$DOTNET --version' failed in the repository. Install the SDK selected by global.json."
  export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$DOTNET")")}"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
}

# Sets FULL_VERSION (a.b.c.0 from the Windows manifest, the release folder name) and VERSION (a.b.c).
read_manifest_version() {
  [ -f "$MANIFEST" ] || fail "Manifest not found: $MANIFEST"
  FULL_VERSION="$(awk '/<Identity/{f=1} f && match($0, /Version="[^"]*"/){print substr($0, RSTART+9, RLENGTH-10); exit}' "$MANIFEST")"
  [[ "$FULL_VERSION" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] ||
    fail "Package.appxmanifest Identity Version '$FULL_VERSION' must have four numeric components."
  [ "${BASH_REMATCH[1]}" -ne 0 ] || fail "The major version must be nonzero ($FULL_VERSION)."
  [ "${BASH_REMATCH[4]}" -eq 0 ] || fail "The revision must be zero ($FULL_VERSION)."
  VERSION="${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
}

# The build number grows with every build, also when the manifest version stays the same (the macOS scheme).
resolve_build_number() {
  [ -n "${BUILD_NUMBER:-}" ] || BUILD_NUMBER="$(date -u +%y%j%H%M | sed 's/^0*//')"
  [[ "$BUILD_NUMBER" =~ ^[1-9][0-9]*$ ]] || fail "--build-number must be a positive integer ($BUILD_NUMBER)."
}

# "~" sorts before the final version, so a later stable a.b.c-<n> upgrades every beta of a.b.c.
debian_version() { printf '%s~%s%s' "$VERSION" "$1" "$BUILD_NUMBER"; }

deb_file_name() { printf 'wino-mail_%s_%s.deb' "$(debian_version "$1")" "$DEB_ARCH"; }

# pacman allows no "~" or "-" in pkgver. A trailing letter segment sorts before the release, so vercmp orders
# 2.2.15beta<n> before 2.2.15, and a later beta build after an earlier one.
arch_version() { printf '%s%s%s' "$VERSION" "$1" "$BUILD_NUMBER"; }

arch_file_name() { printf 'wino-mail-%s-1-%s.pkg.tar.zst' "$(arch_version "$1")" "$PKG_ARCH"; }

# RPM sorts "~" before the release as Debian does. Release 1 has no %{?dist}: one package serves every Fedora.
rpm_version() { debian_version "$1"; }

rpm_file_name() { printf 'wino-mail-%s-1.%s.rpm' "$(rpm_version "$1")" "$PKG_ARCH"; }

package_file_name() {
  case "$1" in
    deb) deb_file_name "$2" ;;
    arch) arch_file_name "$2" ;;
    rpm) rpm_file_name "$2" ;;
    *) fail "Unknown package kind: $1" ;;
  esac
}

# The architecture key of each kind in release.json, the name the distribution uses.
package_arch_of() { if [ "$1" = "deb" ]; then printf '%s' "$DEB_ARCH"; else printf '%s' "$PKG_ARCH"; fi; }

# Sets VERSION, BUILD_NUMBER and CHANNEL from a beta .deb built by build-deb.sh.
read_deb_identity() {
  local deb="$1" deb_version
  deb_version="$(dpkg-deb -f "$deb" Version)" || fail "$deb is not a Debian package."
  [[ "$deb_version" =~ ^([0-9]+\.[0-9]+\.[0-9]+)~([a-z]+)([1-9][0-9]*)$ ]] ||
    fail "$deb has version '$deb_version', not <a.b.c>~<channel><build>."
  VERSION="${BASH_REMATCH[1]}"
  CHANNEL="${BASH_REMATCH[2]}"
  BUILD_NUMBER="${BASH_REMATCH[3]}"
  FULL_VERSION="$VERSION.0"
  [ "$(dpkg-deb -f "$deb" Package)" = "wino-mail" ] || fail "$deb is not the wino-mail package."
  [ "$(dpkg-deb -f "$deb" Architecture)" = "$DEB_ARCH" ] || fail "$deb is not an $DEB_ARCH package."
}

# Sets COMMIT (or empty) and DIRTY (true, false or null) for the build record.
read_commit() {
  COMMIT="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || true)"
  DIRTY="null"
  if [ -n "$COMMIT" ]; then
    if [ -n "$(git -C "$REPO_ROOT" status --porcelain --untracked-files=no 2>/dev/null)" ]; then DIRTY="true"; else DIRTY="false"; fi
  fi
}

# Sets COMMIT and DIRTY from the build record of an existing build, when there is one.
read_record_commit() {
  COMMIT=""
  DIRTY="null"
  [ -f "$1" ] || return 0
  COMMIT="$(jq -r '.commit // empty' "$1")"
  DIRTY="$(jq -r 'if .dirty == true or .dirty == false then .dirty else null end' "$1")"
}

check_publish_dir() {
  local dir="$1" name
  [ -x "$dir/wino-mail" ] || fail "$dir has no wino-mail executable."
  for name in "${NATIVE_LIBRARIES[@]}"; do
    [ -f "$dir/$name" ] || fail "$dir is missing $name."
  done
  for name in "${DATA_FOLDERS[@]}"; do
    [ -d "$dir/$name" ] || fail "$dir is missing the folder $name."
  done
  [ -n "$(find "$dir/Assets/WhatsNew" -name '*.json' -print -quit)" ] || fail "$dir/Assets/WhatsNew has no release notes."
  [ -f "$dir/icons/hicolor/256x256/apps/app.winomail.Mail.png" ] || fail "$dir/icons has no application icon."
}

# One Native AOT publish of the Linux app. Never -p:PublishAot: it would reach the netstandard2.0 source generators.
publish_aot() {
  local dir="$1" log="$2"
  rm -rf "$dir"
  mkdir -p "$dir"
  if pgrep -f 'dotnet.*(publish|-c Release)' >/dev/null 2>&1; then
    info "Another Release build or publish is running; this publish may run out of memory."
  fi
  info "Publishing (Native AOT, $DOTNET_RID); log: $log"
  if ! (cd "$REPO_ROOT" && "$DOTNET" publish "$PROJECT" -c Release -r "$DOTNET_RID" -p:WinoLinuxPublish=Aot \
      -p:Version="$VERSION" -o "$dir") >"$log" 2>&1; then
    tail -n 40 "$log" >&2
    fail "The publish failed. See $log."
  fi
}

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }
size_of() { stat -c %s "$1"; }
