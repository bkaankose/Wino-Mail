#!/bin/sh
# Creates the lab CA, its CRL, and the TLS certificate of the Stalwart OAuth server in /tls (tools/local-lab/tls).
# Existing CA files are kept, so the CA trusted on Windows survives `up` and `reset`.
# The CA is name-constrained to *.test, localhost and 127.0.0.1: trusting it cannot vouch for real sites.
# Windows checks revocation online (MailKit asks for it), so the leaf names a CRL and the CA certificate on
# the lab-pki service (http://127.0.0.1:8808/, served from /tls/pub). An IP keeps the URL independent of the hosts file.
set -eu
umask 077
cd /tls
HOST="${LAB_TLS_HOST:-mail.stalwartoauth.test}"
DOMAIN="${LAB_TLS_DOMAIN:-stalwartoauth.test}"
PKI_URL="${LAB_PKI_URL:-http://127.0.0.1:8808}"

if [ ! -s ca.key ] || [ ! -s ca.crt ]; then
    openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 3650 \
        -keyout ca.key -out ca.crt -subj '/CN=Wino Local Lab CA/O=Wino local lab' \
        -addext 'basicConstraints=critical,CA:TRUE,pathlen:0' \
        -addext 'keyUsage=critical,keyCertSign,cRLSign' \
        -addext 'subjectKeyIdentifier=hash' \
        -addext 'nameConstraints=critical,permitted;DNS:test,permitted;DNS:localhost,permitted;IP:127.0.0.0/255.0.0.0' \
        2>/dev/null
    rm -rf server.crt server.key ca-db
    echo "created lab CA tls/ca.crt"
fi

# Minimal `openssl ca` state, used only to sign the CRL. No certificate is ever revoked in the lab.
mkdir -p ca-db pub
[ -f ca-db/index.txt ] || : > ca-db/index.txt
[ -f ca-db/crlnumber ] || echo 1000 > ca-db/crlnumber
cat > ca-db/openssl.cnf <<EOF
[ ca ]
default_ca = lab
[ lab ]
database = /tls/ca-db/index.txt
crlnumber = /tls/ca-db/crlnumber
certificate = /tls/ca.crt
private_key = /tls/ca.key
default_md = sha256
default_crl_days = 3650
crl_extensions = crl_ext
[ crl_ext ]
authorityKeyIdentifier = keyid:always
EOF

if [ ! -s pub/lab-ca.crl ] || ! openssl crl -in pub/lab-ca.crl -inform DER -noout -verify -CAfile ca.crt >/dev/null 2>&1 \
    || [ "$(date -u -d "$(openssl crl -in pub/lab-ca.crl -inform DER -noout -nextupdate | cut -d= -f2)" +%s)" -lt "$(( $(date -u +%s) + 31536000 ))" ]; then
    openssl ca -config ca-db/openssl.cnf -gencrl -out ca-db/lab-ca.crl.pem 2>/dev/null
    openssl crl -in ca-db/lab-ca.crl.pem -outform DER -out pub/lab-ca.crl
    echo "published empty CRL tls/pub/lab-ca.crl"
fi
openssl x509 -in ca.crt -outform DER -out pub/lab-ca.crt

# Reissue the leaf when it is missing, expires within 30 days, or predates the CRL distribution point.
if [ ! -s server.key ] || [ ! -s server.crt ] || ! openssl x509 -in server.crt -noout -checkend 2592000 >/dev/null 2>&1 \
    || ! openssl x509 -in server.crt -noout -ext crlDistributionPoints 2>/dev/null | grep -q "${PKI_URL}/lab-ca.crl"; then
    cat > server.ext <<EOF
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectKeyIdentifier=hash
authorityKeyIdentifier=keyid
subjectAltName=DNS:${HOST},DNS:${DOMAIN},DNS:localhost,IP:127.0.0.1
crlDistributionPoints=URI:${PKI_URL}/lab-ca.crl
authorityInfoAccess=caIssuers;URI:${PKI_URL}/lab-ca.crt
EOF
    openssl req -newkey rsa:2048 -nodes -sha256 -keyout server.key -out server.csr -subj "/CN=${HOST}" 2>/dev/null
    openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial -sha256 -days 825 \
        -extfile server.ext -out server.crt 2>/dev/null
    rm -f server.csr server.ext ca.srl
    echo "issued tls/server.crt for ${HOST}"
fi
chmod 755 pub
chmod 644 ca.crt server.crt pub/lab-ca.crl pub/lab-ca.crt
openssl verify -crl_check -CRLfile ca-db/lab-ca.crl.pem -CAfile ca.crt server.crt 2>/dev/null \
    || openssl verify -CAfile ca.crt server.crt
