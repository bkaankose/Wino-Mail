#!/usr/bin/env bash
# Registers a built Wino Mail with the desktop for the current user: the .desktop file named after the application
# id (GNOME shows notifications and the window icon under it) and the wino:// scheme handler.
#   install-desktop-entry.sh <path to the wino-mail executable>
#   install-desktop-entry.sh --remove
# The file goes to $XDG_DATA_HOME/applications (~/.local/share/applications) and the scheme default to
# $XDG_CONFIG_HOME/mimeapps.list; nothing needs root. --remove deletes the file only: gio cannot unset a default, and
# an entry that names a missing .desktop file is ignored.
set -euo pipefail

id="app.winomail.Mail"
applications="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
entry="$applications/$id.desktop"
template="$(cd "$(dirname "$0")/../../.." && pwd)/src/Wino.Mail.Linux/Resources/$id.desktop"

if [[ "${1:-}" == "--remove" ]]; then
    rm -f "$entry"
    update-desktop-database "$applications" 2>/dev/null || true
    echo "Removed $entry"
    exit 0
fi

executable="$(realpath "${1:?path to the wino-mail executable}")"
[[ -x "$executable" ]] || { echo "$executable is not an executable file." >&2; exit 1; }

mkdir -p "$applications"
# Exec values quote the path; a literal quote, backslash, dollar or backtick in it would need escaping.
case "$executable" in *[\"\\\$\`]*) echo "The path contains a character the Exec key cannot carry." >&2; exit 1 ;; esac
sed "s|@WINO_EXEC@|\"$executable\"|" "$template" > "$entry"

desktop-file-validate "$entry"
update-desktop-database "$applications"
gio mime x-scheme-handler/wino "$id.desktop" > /dev/null
echo "Installed $entry"
echo "wino:// opens with: $(gio mime x-scheme-handler/wino | head -1)"
