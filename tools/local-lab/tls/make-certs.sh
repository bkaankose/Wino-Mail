#!/usr/bin/env bash
# Issues the lab CA, the four TLS test leaves (valid, wronghost, expired, revoked) and the CRL into
# /tls/generated. Idempotent: an existing CA and leaves are kept, so certificates trusted in Wino stay
# the same across runs; only the CRL is re-signed. Runs inside the docker-mailserver image (OpenSSL).
set -euo pipefail

root=/tls/generated
ca=$root/ca
certs=$root/certs
crl=$root/crl
crl_url=http://127.0.0.1:8807/lab-ca.crl

mkdir -p "$ca/newcerts" "$certs" "$crl"
chmod 700 "$ca"
[ -f "$ca/index.txt" ] || : > "$ca/index.txt"
[ -f "$ca/index.txt.attr" ] || echo 'unique_subject = no' > "$ca/index.txt.attr"
[ -f "$ca/serial" ] || echo 1000 > "$ca/serial"
[ -f "$ca/crlnumber" ] || echo 1000 > "$ca/crlnumber"

cat > "$ca/openssl.cnf" <<EOF
[ ca ]
default_ca = lab_ca

[ lab_ca ]
dir = $ca
database = \$dir/index.txt
new_certs_dir = \$dir/newcerts
certificate = \$dir/ca.crt
private_key = \$dir/ca.key
serial = \$dir/serial
crlnumber = \$dir/crlnumber
default_md = sha256
default_days = 3650
default_crl_days = 3650
policy = lab_policy
copy_extensions = none
unique_subject = no

[ lab_policy ]
commonName = supplied
organizationName = optional

[ req ]
distinguished_name = req_dn
prompt = no

[ req_dn ]
CN = Wino Lab CA

[ ca_ext ]
basicConstraints = critical, CA:TRUE
keyUsage = critical, keyCertSign, cRLSign
subjectKeyIdentifier = hash
EOF

if [ ! -f "$ca/ca.crt" ]; then
  echo "Creating the lab CA"
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$ca/ca.key" -out "$ca/ca.crt" \
    -days 3650 -config "$ca/openssl.cnf" -extensions ca_ext 2>/dev/null
  chmod 600 "$ca/ca.key"
fi

issue() {
  local name=$1 sans=$2 dates=$3
  if [ -f "$certs/$name.crt" ]; then
    echo "Keeping $name"
    return
  fi

  echo "Issuing $name"
  cat > "$certs/$name.ext" <<EOF
basicConstraints = CA:FALSE
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = $sans
crlDistributionPoints = URI:$crl_url
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid
EOF
  openssl req -new -newkey rsa:2048 -nodes -keyout "$certs/$name.key" -out "$certs/$name.csr" \
    -subj "/O=Wino Lab/CN=wino-lab-$name" 2>/dev/null
  openssl ca -batch -notext -config "$ca/openssl.cnf" -extfile "$certs/$name.ext" \
    -in "$certs/$name.csr" -out "$certs/$name.crt" $dates 2>/dev/null
  chmod 600 "$certs/$name.key"
  rm -f "$certs/$name.csr" "$certs/$name.ext"
}

issue valid 'DNS:localhost, IP:127.0.0.1' '-days 825'
issue wronghost 'DNS:wrong-host.wino.lab' '-days 825'
issue expired 'DNS:localhost, IP:127.0.0.1' '-startdate 20240101000000Z -enddate 20250101000000Z'
issue revoked 'DNS:localhost, IP:127.0.0.1' '-days 825'

revoked_serial=$(openssl x509 -in "$certs/revoked.crt" -noout -serial | cut -d= -f2)
if ! grep -q "^R.*[[:space:]]$revoked_serial[[:space:]]" "$ca/index.txt"; then
  echo "Revoking revoked ($revoked_serial)"
  openssl ca -batch -config "$ca/openssl.cnf" -revoke "$certs/revoked.crt" -crl_reason keyCompromise 2>/dev/null
fi

openssl ca -batch -config "$ca/openssl.cnf" -gencrl -out "$crl/lab-ca.crl.pem" 2>/dev/null
openssl crl -in "$crl/lab-ca.crl.pem" -outform DER -out "$crl/lab-ca.crl"
rm -f "$crl/lab-ca.crl.pem"

for name in valid wronghost expired revoked; do
  cat "$certs/$name.crt" "$ca/ca.crt" > "$certs/$name.fullchain.pem"
done
cp "$ca/ca.crt" "$root/wino-lab-ca.crt"

echo "Lab CA: $root/wino-lab-ca.crt"
for name in valid wronghost expired revoked; do
  printf '%-10s %s\n' "$name" "$(openssl x509 -in "$certs/$name.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
done
