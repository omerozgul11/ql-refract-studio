#!/usr/bin/env bash
# run.sh <command...>: runs a command in the media toolbox with the repository mounted at /repo.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
docker image inspect rfs-media:local >/dev/null 2>&1 || docker build -q -t rfs-media:local "$root/marketing/toolbox" >/dev/null
exec docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -v "$root:/repo" -w /repo rfs-media:local "$@"
