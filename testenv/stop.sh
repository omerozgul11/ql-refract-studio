#!/usr/bin/env bash
# Stops the Refraction Studio test environment. Files in testenv/workspace are kept.
set -euo pipefail
cd "$(dirname "$0")"
docker compose --profile public down
