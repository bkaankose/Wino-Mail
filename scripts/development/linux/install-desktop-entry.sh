#!/usr/bin/env bash
# Registers a built Wino Mail with the desktop for the current user: the .desktop file named after the application
# id (GNOME shows notifications and the window icon under it, and its desktop actions: New Mail, New Event, Calendar,
# Contacts, To Do), the app icon in the hicolor theme, the AppStream metainfo and the wino:// scheme handler. The entry
# also lists mailto:, message/rfc822 (.eml), text/calendar (.ics) and text/vcard (.vcf), so they can open with Wino; the
# script does not make Wino their default application unless --default-mail is given, which makes it the mailto handler.
#   install-desktop-entry.sh [--default-mail] <path to the wino-mail executable>
#   install-desktop-entry.sh --remove
# Files go to $XDG_DATA_HOME (~/.local/share: applications, icons/hicolor, metainfo) and scheme defaults to
# $XDG_CONFIG_HOME/mimeapps.list; nothing needs root. --remove deletes the files only: gio cannot unset a default, and
# an entry that names a missing .desktop file is ignored.
set -euo pipefail

id="app.winomail.Mail"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
applications="$data/applications"
icons="$data/icons/hicolor"
metainfo="$data/metainfo"
entry="$applications/$id.desktop"
resources="$(cd "$(dirname "$0")/../../.." && pwd)/src/Wino.Mail.Linux/Resources"

# Only a cache that already exists is refreshed: creating one in the user's folder would hide icons that other apps
# add later without updating it.
refresh_icons() {
    if command -v gtk-update-icon-cache > /dev/null && [[ -f "$icons/icon-theme.cache" ]]; then
        gtk-update-icon-cache -f -t -q "$icons" || true
    fi
}

if [[ "${1:-}" == "--remove" ]]; then
    rm -f "$entry" "$metainfo/$id.metainfo.xml"
    find "$icons" -path "*/apps/$id.png" -delete 2>/dev/null || true
    update-desktop-database "$applications" 2>/dev/null || true
    refresh_icons
    echo "Removed $entry, its icons and metainfo"
    exit 0
fi

default_mail=false
if [[ "${1:-}" == "--default-mail" ]]; then
    default_mail=true
    shift
fi

executable="$(realpath "${1:?path to the wino-mail executable}")"
[[ -x "$executable" ]] || { echo "$executable is not an executable file." >&2; exit 1; }

mkdir -p "$applications" "$metainfo"
# Exec values quote the path; a literal quote, backslash, dollar or backtick in it would need escaping.
case "$executable" in *[\"\\\$\`]*) echo "The path contains a character the Exec key cannot carry." >&2; exit 1 ;; esac
sed "s|@WINO_EXEC@|\"$executable\"|" "$resources/$id.desktop" > "$entry"
cp "$resources/$id.metainfo.xml" "$metainfo/$id.metainfo.xml"

built_icons="$(dirname "$executable")/icons/hicolor"
if [[ -d "$built_icons" ]]; then
    (cd "$built_icons" && find . -name "$id.png" -print0 | while IFS= read -r -d '' icon; do
        install -D -m 0644 "$icon" "$icons/$icon"
    done)
    refresh_icons
else
    echo "No icons next to the executable ($built_icons); build the app first." >&2
fi

desktop-file-validate "$entry"
update-desktop-database "$applications"
gio mime x-scheme-handler/wino "$id.desktop" > /dev/null
if $default_mail; then gio mime x-scheme-handler/mailto "$id.desktop" > /dev/null; fi
echo "Installed $entry"
echo "wino:// opens with: $(gio mime x-scheme-handler/wino | head -1)"
echo "mailto: opens with: $(gio mime x-scheme-handler/mailto 2>/dev/null | head -1)"
for type in message/rfc822 text/calendar text/vcard; do
    registered="$(gio mime "$type" 2>/dev/null | grep -c "$id.desktop" || true)"
    echo "$type can open with Wino: $([[ "${registered:-0}" -gt 0 ]] && echo yes || echo no)"
done
