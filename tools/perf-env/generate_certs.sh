#!/usr/bin/env bash
# Creates a local certificate authority and a server certificate for the RabbitMQ container
# (AMQPS on 5671, management HTTPS on 15671). Output: docker/rabbitmq/certs (git-ignored).
# The explorer validates the server against ca.pem (Clusters[].CaCertificatePath); nothing is disabled.
set -euo pipefail

dir="$(cd "$(dirname "$0")/../.." && pwd)/docker/rabbitmq/certs"
mkdir -p "$dir"
cd "$dir"

if [[ -f server.pem && "${1:-}" != "--force" ]]; then
  echo "Certificates already exist in $dir (use --force to regenerate)."
  exit 0
fi

openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -sha256 \
  -keyout ca.key -out ca.pem -subj "/CN=DynaQueueExplorer Local CA" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null

openssl req -newkey rsa:2048 -nodes -sha256 -keyout server.key -out server.csr -subj "/CN=localhost" 2>/dev/null
cat > server.ext <<'EXT'
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:localhost,DNS:rabbitmq,DNS:rabbit-1,IP:127.0.0.1
EXT
openssl x509 -req -in server.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 825 -sha256 \
  -extfile server.ext -out server.pem 2>/dev/null
rm -f server.csr server.ext ca.srl

# Readable by the rabbitmq user inside the container (local development only).
chmod 644 ca.pem server.pem server.key
chmod 600 ca.key
echo "Created CA and server certificate in $dir"
openssl x509 -in server.pem -noout -subject -issuer -ext subjectAltName
