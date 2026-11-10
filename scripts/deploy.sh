#!/usr/bin/env bash
# Build, test and deploy pad to Azure App Service from this machine.
# Needs: az (signed in: `az login`), dotnet, node/npm, zip, curl.
# Usage: scripts/deploy.sh [--skip-tests]
set -euo pipefail

RESOURCE_GROUP="mikevh_group"
APP_NAME="pad-mikevh"
HEALTH_URL="https://p.zorgen.io/api/health"

cd "$(dirname "$0")/.."
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

az account show >/dev/null 2>&1 || { echo "Not signed in to Azure. Run: az login" >&2; exit 1; }

echo "==> Building UI"
npm --prefix web ci --silent
npm --prefix web run build --silent

if [[ "${1:-}" != "--skip-tests" ]]; then
  echo "==> Running tests"
  dotnet test --configuration Release --nologo -v q
fi

echo "==> Publishing"
dotnet publish src/Pad.Api --configuration Release --output "$WORK/publish" -v q --nologo
rm -f "$WORK/publish/appsettings.Development.json"   # local-only settings
(cd "$WORK/publish" && zip -qr "$WORK/pad.zip" .)

echo "==> Deploying to $APP_NAME"
az webapp deploy --resource-group "$RESOURCE_GROUP" --name "$APP_NAME" --src-path "$WORK/pad.zip" --type zip --only-show-errors >/dev/null

echo "==> Health check ($HEALTH_URL)"
for i in $(seq 1 12); do
  if curl -fsS --max-time 20 "$HEALTH_URL"; then echo; echo "Deployed."; exit 0; fi
  echo "waiting for app ($i/12)..."; sleep 10
done
echo "Health check failed after deploy." >&2
exit 1
