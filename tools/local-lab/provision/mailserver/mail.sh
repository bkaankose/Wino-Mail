#!/bin/bash
# Creates the mailbox for one docker-mailserver instance and installs the user patch.
# Runs in a throwaway container with the instance's config volume mounted.
set -euo pipefail
: "${LAB_DOMAIN:?LAB_DOMAIN is required}"
: "${LAB_USER:?LAB_USER is required}"
cp /lab/user-patches.sh /tmp/docker-mailserver/user-patches.sh
if ! grep -q "^${LAB_USER}@${LAB_DOMAIN}|" /tmp/docker-mailserver/postfix-accounts.cf 2>/dev/null; then
  setup email add "${LAB_USER}@${LAB_DOMAIN}" '1'
fi
echo "Mailbox ${LAB_USER}@${LAB_DOMAIN} ready"
