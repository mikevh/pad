#!/usr/bin/env bash
# Read-only: list DNS host records for a domain via the Namecheap API.
# Usage: scripts/nc-hosts.sh zorgen.io
set -euo pipefail
DOMAIN="${1:?domain required}"
SLD="${DOMAIN%%.*}"; TLD="${DOMAIN#*.}"
KEY="$(cat ~/.config/namecheap/api-key)"
IP="$(curl -s -m 10 https://api.ipify.org)"
curl -s -m 30 -G "https://api.namecheap.com/xml.response" \
  --data-urlencode "ApiUser=${NC_USER:-mikevh}" \
  --data-urlencode "ApiKey=$KEY" \
  --data-urlencode "UserName=${NC_USER:-mikevh}" \
  --data-urlencode "ClientIp=$IP" \
  --data-urlencode "Command=namecheap.domains.dns.getHosts" \
  --data-urlencode "SLD=$SLD" --data-urlencode "TLD=$TLD"
