#!/bin/bash
set -euo pipefail
# Local-only authenticated plaintext endpoints, including submission.
printf '\nssl = no\nauth_allow_cleartext = yes\n' >> /etc/dovecot/conf.d/10-ssl.conf
postconf -P 'submission/inet/smtpd_tls_security_level=none'
postconf -P 'submission/inet/smtpd_tls_auth_only=no'
postconf -e 'smtpd_tls_auth_only=no'
# Never deliver seeded invitation messages as server scheduling traffic.
# External delivery is intentionally disabled; local wino.test mail still works.
postconf -e 'default_transport=error:Local Wino lab does not deliver external mail'
postconf -e 'relay_transport=error:Local Wino lab does not relay external mail'
