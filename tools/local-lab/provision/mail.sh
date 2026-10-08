#!/bin/bash
set -euo pipefail
cp /lab/user-patches.sh /tmp/docker-mailserver/user-patches.sh
for user in alice bob empty; do
  if ! grep -q "^${user}@wino.test|" /tmp/docker-mailserver/postfix-accounts.cf 2>/dev/null; then
    setup email add "${user}@wino.test" 'WinoLab123!'
  fi
done
