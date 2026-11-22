# Sourced by the Linux packaging scripts after common.sh. Builds the file tree every package installs under /usr, so
# the .deb, the Arch package and the RPM carry the same files; only the `distribution` file names its own kind.
# shellcheck shell=bash

ICON_SIZES=(16x16 32x32 64x64 128x128 256x256 512x512)

# Writes the marker that turns a copy into a packaged one (LinuxDistribution in Wino.Platform.Linux).
set_distribution() {
  local root="$1" kind="$2"
  printf 'channel=%s\npackage=%s\nbuild=%s\n' "$CHANNEL" "$kind" "$BUILD_NUMBER" >"$root/$INSTALL_DIR/distribution"
  chmod 0644 "$root/$INSTALL_DIR/distribution"
}

distribution_value() { sed -n "s/^$2=//p" "$1/$INSTALL_DIR/distribution" 2>/dev/null; }

# Stages a Native AOT publish: the app folder, the /usr/bin link, icons, desktop file, metainfo and man page.
stage_from_publish() {
  local publish="$1" root="$2" kind="$3" app_dir library folder size desktop metainfo
  app_dir="$root/$INSTALL_DIR"
  install -d -m 0755 "$app_dir" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/metainfo" "$root/usr/share/man/man1"

  # The executable is already stripped by the AOT publish (its symbols are in wino-mail.dbg).
  install -m 0755 "$publish/wino-mail" "$app_dir/wino-mail"
  # Two of the NuGet libraries ship with their symbol tables.
  for library in "${NATIVE_LIBRARIES[@]}"; do
    install -m 0644 "$publish/$library" "$app_dir/$library"
    strip --strip-unneeded --remove-section=.comment --remove-section=.note "$app_dir/$library"
  done
  # Resources/ holds the desktop file and metainfo, which go to their system folders below. The license files of the
  # model are in the package's copyright file.
  for folder in Fonts Models Themes Assets icons; do
    (cd "$publish" && find -L "$folder" -type f ! -name LICENSE ! -name '*.pdb' -print0) |
      while IFS= read -r -d '' file; do install -D -m 0644 "$publish/$file" "$app_dir/$file"; done
  done
  set_distribution "$root" "$kind"
  ln -s "../lib/wino-mail/wino-mail" "$root/usr/bin/wino-mail"

  for size in "${ICON_SIZES[@]}"; do
    install -D -m 0644 "$publish/icons/hicolor/$size/apps/$APP_ID.png" "$root/usr/share/icons/hicolor/$size/apps/$APP_ID.png"
  done

  desktop="$root/usr/share/applications/$APP_ID.desktop"
  sed 's|@WINO_EXEC@|wino-mail|g' "$publish/Resources/$APP_ID.desktop" >"$desktop"
  chmod 0644 "$desktop"
  ! grep -q '@WINO_EXEC@' "$desktop" || fail "The desktop file still has an @WINO_EXEC@ placeholder."
  desktop-file-validate "$desktop" || fail "desktop-file-validate rejected $APP_ID.desktop."
  info "$APP_ID.desktop is valid"

  metainfo="$root/usr/share/metainfo/$APP_ID.metainfo.xml"
  awk -v version="$(debian_version "$CHANNEL")" -v date="$(date -u +%Y-%m-%d)" '
    /<\/component>/ { printf "  <releases>\n    <release version=\"%s\" date=\"%s\" type=\"development\"/>\n  </releases>\n", version, date }
    { print }' "$publish/Resources/$APP_ID.metainfo.xml" >"$metainfo"
  chmod 0644 "$metainfo"
  appstreamcli validate --no-net "$metainfo" || fail "appstreamcli rejected $APP_ID.metainfo.xml."
  info "$APP_ID.metainfo.xml is valid"

  gzip -9n -c "$SCRIPT_DIR/deb/wino-mail.1" >"$root/usr/share/man/man1/wino-mail.1.gz"
  chmod 0644 "$root/usr/share/man/man1/wino-mail.1.gz"
  find "$root" -type d -exec chmod 0755 {} +
}

# Stages the tree of a beta .deb, so the other packages can be made without a new publish. The Debian-only files
# (changelog, copyright in /usr/share/doc, lintian overrides) are left out.
stage_from_deb() {
  local deb="$1" root="$2" kind="$3"
  mkdir -p "$root"
  dpkg-deb -x "$deb" "$root" || fail "dpkg-deb could not extract $deb."
  rm -rf "$root/usr/share/doc" "$root/usr/share/lintian"
  [ "$(distribution_value "$root" build)" = "$BUILD_NUMBER" ] || fail "$deb's distribution file does not name build $BUILD_NUMBER."
  set_distribution "$root" "$kind"
  find "$root" -type d -exec chmod 0755 {} +
}

# A tree handed to the packagers with --root records what it was staged from, because a tree staged from an older .deb
# must not take the version of the current manifest.
write_stage_info() {
  printf 'version=%s\nchannel=%s\nbuild=%s\ncommit=%s\ndirty=%s\n' "$VERSION" "$CHANNEL" "$BUILD_NUMBER" "$COMMIT" "$DIRTY" \
    >"$1/.wino-stage"
}

read_stage_info() {
  local info="$1/.wino-stage" wanted_build="$BUILD_NUMBER" wanted_channel="$CHANNEL"
  [ -f "$info" ] || fail "$1 is not a staged tree (no .wino-stage). Stage it with build-linux-release.sh."
  VERSION="$(sed -n 's/^version=//p' "$info")"
  CHANNEL="$(sed -n 's/^channel=//p' "$info")"
  BUILD_NUMBER="$(sed -n 's/^build=//p' "$info")"
  COMMIT="$(sed -n 's/^commit=//p' "$info")"
  DIRTY="$(sed -n 's/^dirty=//p' "$info")"
  [[ "$VERSION" =~ ^[1-9][0-9]*\.[0-9]+\.[0-9]+$ ]] || fail "$info names no version."
  FULL_VERSION="$VERSION.0"
  [ -z "$wanted_build" ] || [ "$wanted_build" = "$BUILD_NUMBER" ] || fail "$1 is staged for build $BUILD_NUMBER, not $wanted_build."
  [ "$wanted_channel" = "$CHANNEL" ] || fail "$1 is staged for the $CHANNEL channel, not $wanted_channel."
  [ "$(distribution_value "$1" build)" = "$BUILD_NUMBER" ] || fail "$1's distribution file does not name build $BUILD_NUMBER."
}

# Copies a staged tree into a packager's own folder, so each package rewrites its distribution file in its copy.
stage_copy() {
  local source="$1" root="$2" kind="$3"
  mkdir -p "$root"
  cp -a --no-preserve=ownership "$source/usr" "$root/"
  set_distribution "$root" "$kind"
}

# Arch and Fedora keep license texts in /usr/share/licenses/<package>; Debian keeps them in the copyright file. namcap
# wants a file for each license without a common text on Arch, so MIT (Expat) and BSD-3-Clause are also split out.
stage_license() {
  local dir="$1/usr/share/licenses/wino-mail" name file
  install -d -m 0755 "$dir"
  sed "s|@MAINTAINER@|$MAINTAINER|g" "$SCRIPT_DIR/deb/copyright" >"$dir/LICENSE"
  for name in Expat:MIT BSD-3-Clause:BSD-3-Clause; do
    file="$dir/${name#*:}"
    awk -v want="License: ${name%%:*}" '
      $0 == want && previous == "" { inside = 1; next }
      inside && /^ / { line = substr($0, 2); print (line == "." ? "" : line); next }
      inside { exit }
      { previous = $0 }' "$dir/LICENSE" >"$file"
    [ -s "$file" ] || fail "The copyright file has no ${name%%:*} license text."
  done
  chmod 0644 "$dir"/*
}

check_staged_root() {
  local root="$1" kind="$2"
  [ -x "$root/$INSTALL_DIR/wino-mail" ] || fail "$root has no $INSTALL_DIR/wino-mail."
  [ -L "$root/usr/bin/wino-mail" ] || fail "$root has no /usr/bin/wino-mail link."
  [ -f "$root/usr/share/applications/$APP_ID.desktop" ] || fail "$root has no desktop file."
  [ -f "$root/usr/share/metainfo/$APP_ID.metainfo.xml" ] || fail "$root has no metainfo."
  [ "$(distribution_value "$root" package)" = "$kind" ] || fail "$root's distribution file does not name package $kind."
  [ "$(distribution_value "$root" channel)" = "$CHANNEL" ] || fail "$root's distribution file does not name channel $CHANNEL."
}

# The metainfo description: "p" prints each paragraph on one line, "li" each list item.
metainfo_text() {
  awk -v want="$2" '
    /<description>/ { inside = 1; next }
    /<\/description>/ { inside = 0 }
    inside {
      line = $0; gsub(/^[ \t]+|[ \t]+$/, "", line)
      if (want == "p" && line ~ /^<p>$/) { para = 1; next }
      if (want == "p" && line ~ /^<\/p>$/) { para = 0; print text; text = ""; next }
      if (want == "p" && para && line != "") text = (text == "" ? line : text " " line)
      if (want == "li" && line ~ /^<li>/) { gsub(/<\/?li>/, "", line); print line }
    }' "$1"
}

copy_symbols() {
  local publish="$1" symbols="$2/Symbols/$DEB_ARCH"
  mkdir -p "$symbols"
  [ -f "$publish/wino-mail.dbg" ] && cp "$publish/wino-mail.dbg" "$symbols/"
  find "$publish" -maxdepth 1 -name '*.pdb' -exec cp {} "$symbols/" \;
}

# The input of a packaging script: --publish-dir, --root (a tree staged by stage_from_publish) or --from-deb. Sets
# VERSION, FULL_VERSION, BUILD_NUMBER and CHANNEL; with --from-deb they come from the package.
resolve_package_input() {
  local count=0
  [ -n "$PUBLISH_DIR" ] && count=$((count + 1))
  [ -n "$ROOT_DIR" ] && count=$((count + 1))
  [ -n "$FROM_DEB" ] && count=$((count + 1))
  [ "$count" -le 1 ] || fail "Pass only one of --publish-dir, --root and --from-deb."
  if [ -n "$FROM_DEB" ]; then
    [ -f "$FROM_DEB" ] || fail "The package does not exist: $FROM_DEB"
    FROM_DEB="$(cd "$(dirname "$FROM_DEB")" && pwd)/$(basename "$FROM_DEB")"
    local wanted_build="$BUILD_NUMBER" wanted_channel="$CHANNEL"
    read_deb_identity "$FROM_DEB"
    [ -z "$wanted_build" ] || [ "$wanted_build" = "$BUILD_NUMBER" ] || fail "$FROM_DEB is build $BUILD_NUMBER, not $wanted_build."
    [ "$wanted_channel" = "$CHANNEL" ] || fail "$FROM_DEB is a $CHANNEL package, not $wanted_channel."
    read_record_commit "$(dirname "$FROM_DEB")/build.json"
  elif [ -n "$ROOT_DIR" ]; then
    [ -d "$ROOT_DIR" ] || fail "The staged tree does not exist: $ROOT_DIR"
    ROOT_DIR="$(cd "$ROOT_DIR" && pwd)"
    read_stage_info "$ROOT_DIR"
  else
    read_manifest_version
    resolve_build_number
    read_commit
  fi
  if [ -n "$PUBLISH_DIR" ]; then
    [ -d "$PUBLISH_DIR" ] || fail "The publish folder does not exist: $PUBLISH_DIR"
    PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd)"
    check_publish_dir "$PUBLISH_DIR"
  fi
}

# Stages the package tree of kind into root from whichever input resolve_package_input accepted.
stage_package_input() {
  local root="$1" kind="$2"
  if [ -n "$FROM_DEB" ]; then stage_from_deb "$FROM_DEB" "$root" "$kind"
  elif [ -n "$ROOT_DIR" ]; then stage_copy "$ROOT_DIR" "$root" "$kind"
  else stage_from_publish "$PUBLISH_DIR" "$root" "$kind"
  fi
  check_staged_root "$root" "$kind"
}
