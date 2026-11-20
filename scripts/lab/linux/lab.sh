#!/usr/bin/env bash
# Lease and release lab accounts on Linux, where lab.ps1 needs PowerShell. Needs curl and jq.
#   lab.sh lease [server ...]   leases accounts (default: stalwart) and prints the lease file path
#   lab.sh release <lease id>   releases the lease and removes its file
#   lab.sh list                 lists the local lease files
# A lease file has the shape lab.ps1 writes. The password and the control token are never printed.
set -euo pipefail

state="${WINO_LAB_STATE:-$HOME/.wino-lab}"
manifest="$state/fixtures.json"
leases="$state/leases"

if [[ ! -f "$manifest" ]]; then
    echo "The lab is not set up on this machine ($manifest is missing). The owner sets it up with lab.ps1 manifest." >&2
    exit 1
fi

url="$(jq -r .control.url "$manifest")"
api() { curl -sS --fail-with-body -H @<(printf 'Authorization: Bearer %s' "$(jq -r .control.token "$manifest")") "$@"; }

case "${1:-}" in
lease)
    shift
    servers="$(printf '%s\n' "${@:-stalwart}" | jq -R . | jq -sc .)"
    holder="$(hostname -s)/$(basename "$(git -C "$(dirname "$0")" rev-parse --show-toplevel)")"
    answer="$(api -X POST "$url/leases" -H 'Content-Type: application/json' \
        -d "$(jq -nc --arg holder "$holder" --argjson servers "$servers" '{holder: $holder, pop3: false, ttlHours: 24, servers: $servers}')")"
    id="$(jq -r .id <<<"$answer")"
    mkdir -p "$leases"
    (umask 077; jq --slurpfile manifest "$manifest" --argjson servers "$servers" '{
        schema: 2, kind: "lease", id: .id, holder: .holder, createdAt: .createdAt, expiresAt: .expiresAt, servers: $servers,
        anchorDate: $manifest[0].anchorDate, labHost: $manifest[0].labHost,
        password: (.password // $manifest[0].password), accounts: (.accounts // []), pop3Accounts: (.pop3Accounts // [])
    }' <<<"$answer" > "$leases/$id.json")
    jq -r '.accounts[] | "\(.key)\t\(.address)"' "$leases/$id.json" >&2
    echo "$leases/$id.json"
    ;;
release)
    id="${2:?lease id}"
    api -X DELETE "$url/leases/$id" > /dev/null
    rm -f "$leases/$id.json"
    echo "Released $id."
    ;;
list)
    for file in "$leases"/*.json; do
        [[ -e "$file" ]] || continue
        jq -r '"\(.id)\t\(.expiresAt)\t\([.accounts[].address] | join(", "))"' "$file"
    done
    ;;
*)
    sed -n '2,6p' "$0" >&2
    exit 2
    ;;
esac
