#!/bin/zsh
# Starts the Docker lab on macOS (colima + the docker compose plugin) with the same steps as local-lab.ps1 up:
# mailboxes, the Baikal user, the TLS certificates, the eleven services, then seeding and delivery probes.
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
colima status >/dev/null 2>&1 || colima start --cpu 4 --memory 6 --disk 40
C() { docker compose --project-name wino-local-lab --file "$ROOT/tools/local-lab/compose.yaml" "$@"; }
mkdir -p "$ROOT/tools/local-lab/.state"
C run --rm --no-deps --entrypoint bash dovecot /lab/mail.sh
C run --rm --no-deps --entrypoint bash condstore /lab/mail.sh
C run --rm --no-deps --entrypoint php baikal /lab/dav.php
C run --rm --no-deps certs
C up -d --wait --wait-timeout 300 dovecot condstore stalwart greenmail james baikal radicale-condstore radicale-greenmail radicale-james tls crl
C run --rm --no-deps seed all
