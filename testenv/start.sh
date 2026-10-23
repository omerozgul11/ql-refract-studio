#!/usr/bin/env bash
# Starts (or updates) the Refraction Studio local test environment and prints the addresses.
#   testenv/start.sh [--rebuild-app]   --rebuild-app republishes the Linux build of the application first
# Everything runs in Docker under the project name "rfs-testenv". Files the app sees are in
# testenv/workspace/Files (git-ignored); the test data is refreshed there on every start.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
cd "$here"

if [[ "${1:-}" == "--rebuild-app" || ! -x "$root/artifacts/publish/linux-x64/RefractionStudio" ]]; then
  echo "== Publishing the Linux build"
  source "$root/.tools/env.sh"
  rm -rf "$root/artifacts/publish/linux-x64"
  dotnet publish "$root/src/RefractionStudio.App" -c Release -r linux-x64 --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -o "$root/artifacts/publish/linux-x64" | tail -1
fi

echo "== Preparing test data"
mkdir -p workspace/Files workspace/appdata workspace/filebrowser
data="workspace/Files/Test data"
if [[ ! -d "$root/artifacts/testenv-data/work/P" ]]; then
  source "$root/.tools/env.sh"
  rm -rf "$root/artifacts/testenv-data"
  dotnet run --project "$root/tools/RefractionStudio.Cli" -c Release -v q -- sample \
    --out "$root/artifacts/testenv-data/sample.rfsx" --work "$root/artifacts/testenv-data/work" | tail -1
fi
rm -rf "$data"
mkdir -p "$data/P-wave SEG-2 records" "$data/SH-wave SEG-2 records" "workspace/Files/My projects" "workspace/Files/Reports and exports" "workspace/Files/Uploads"
cp "$root/artifacts/testenv-data/work/P/"*.sg2 "$data/P-wave SEG-2 records/"
cp "$root/artifacts/testenv-data/work/SH/"*.sg2 "$data/SH-wave SEG-2 records/"
cp "$root/samples/Refraction-Studio-Sample.rfsx" "$data/"

echo "== Building the image"
build="$root/artifacts/testenv-build"
rm -rf "$build" && mkdir -p "$build/testenv"
cp -r "$root/artifacts/publish/linux-x64" "$build/app"
cp -r "$here/app" "$build/testenv/app"

if [[ ! -f .env ]]; then
  umask 077
  printf 'VNC_PASSWORD=%s\n' "$(openssl rand -hex 4)" > .env
fi
docker compose build -q app
docker compose up -d --force-recreate
pw="$(grep '^VNC_PASSWORD=' .env | cut -d= -f2)"
for _ in $(seq 60); do curl -fs -o /dev/null http://127.0.0.1:50020/vnc.html && break; sleep 1; done
cat <<MSG

Refraction Studio test environment is running.
  App in the browser:  http://127.0.0.1:50020/vnc.html?autoconnect=1&resize=remote&password=$pw
  Files (up/download): http://127.0.0.1:50021/
From another computer, open an SSH tunnel first:
  ssh -L 50020:127.0.0.1:50020 -L 50021:127.0.0.1:50021 $(whoami)@$(hostname -I | awk '{print $1}')
Stop with testenv/stop.sh.
MSG
