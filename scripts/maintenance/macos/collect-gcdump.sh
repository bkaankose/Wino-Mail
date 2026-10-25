#!/bin/sh
# Collects a .NET GC heap graph (.gcdump) from the running Debug Mac app.
#
# The sandboxed app creates its diagnostics socket in its container tmp folder, where the full
# path is too long and the runtime truncates the name to "...-sock". dotnet-gcdump looks for
# "...-socket" in TMPDIR, so this script proxies the real socket under the expected name.
# dotnet-dump cannot be used: the app cannot spawn createdump inside the sandbox.
#
# Usage: scripts/maintenance/macos/collect-gcdump.sh [output.gcdump]
# Requires: dotnet tool install -g dotnet-gcdump
set -e
OUT=${1:-/tmp/wino.gcdump}
PID=$(pgrep -x Wino.Mail.MacOS) || { echo "Wino.Mail.MacOS is not running"; exit 1; }
CONTAINER_TMP="$HOME/Library/Containers/com.winomail.macos/Data/tmp"
REAL=$(ls "$CONTAINER_TMP" | grep "dotnet-diagnostic-$PID-") || { echo "no diagnostics socket for pid $PID"; exit 1; }
PROXY_DIR=/tmp/wino-diag
mkdir -p "$PROXY_DIR"; rm -f "$PROXY_DIR"/dotnet-diagnostic-*
PROXY_SOCKET="$PROXY_DIR/${REAL%-sock}-socket"
python3 -I - "$PROXY_SOCKET" "$CONTAINER_TMP/$REAL" <<'PY' &
import socket, sys, threading
listen, target = sys.argv[1], sys.argv[2]
def pump(a, b):
    try:
        while True:
            d = a.recv(65536)
            if not d: break
            b.sendall(d)
    except Exception: pass
    finally:
        try: b.shutdown(socket.SHUT_WR)
        except Exception: pass
srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
srv.bind(listen); srv.listen(8)
while True:
    c, _ = srv.accept()
    t = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); t.connect(target)
    threading.Thread(target=pump, args=(c, t), daemon=True).start()
    threading.Thread(target=pump, args=(t, c), daemon=True).start()
PY
PROXY=$!
trap 'kill $PROXY 2>/dev/null' EXIT
sleep 1
export PATH="$HOME/.dotnet/tools:$HOME/.dotnet:$PATH"
TMPDIR="$PROXY_DIR" dotnet-gcdump collect -p "$PID" -t 240 -o "$OUT"
echo "Type histogram: dotnet-gcdump report $OUT"
