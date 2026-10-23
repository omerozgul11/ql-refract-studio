#!/usr/bin/env bash
# Starts (or updates) the Refraction Studio local test environment and prints the addresses.
#   testenv/start.sh [--rebuild-app] [--public]
#     --rebuild-app  republishes the Linux build of the application first
#     --public       also starts two Cloudflare quick tunnels (HTTPS addresses usable from any network)
# Everything runs in Docker under the project name "rfs-testenv". Files the app sees are in
# testenv/workspace/Files (git-ignored); the test data is refreshed there on every start.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
cd "$here"

rebuild=0; public=0
for a in "$@"; do
  case "$a" in --rebuild-app) rebuild=1 ;; --public) public=1 ;; *) echo "unknown option $a" >&2; exit 2 ;; esac
done
if [[ $rebuild == 1 || ! -x "$root/artifacts/publish/linux-x64/RefractionStudio" ]]; then
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

umask 077
touch .env
grep -q '^VNC_PASSWORD=' .env || printf 'VNC_PASSWORD=%s\n' "$(openssl rand -hex 4)" >> .env
grep -q '^FILES_PASSWORD=' .env || printf 'FILES_PASSWORD=%s\n' "$(openssl rand -hex 8)" >> .env
umask 022
pw="$(grep '^VNC_PASSWORD=' .env | cut -d= -f2)"
fpw="$(grep '^FILES_PASSWORD=' .env | cut -d= -f2)"

# File browser with a login (user "tester"): created once in workspace/filebrowser.
if [[ ! -f workspace/filebrowser/.login ]]; then
  docker compose rm -sf files >/dev/null 2>&1 || true
  rm -f workspace/filebrowser/filebrowser.db
  fb=(docker run --rm -u 1000:1000 -v "$here/workspace/filebrowser:/database" filebrowser/filebrowser:v2.32.0)
  "${fb[@]}" config init -d /database/filebrowser.db >/dev/null
  "${fb[@]}" users add tester "$fpw" --perm.admin -d /database/filebrowser.db >/dev/null
  touch workspace/filebrowser/.login
fi

docker compose build -q app
profile=(); [[ $public == 1 ]] && profile=(--profile public)
docker compose "${profile[@]}" up -d --force-recreate
for _ in $(seq 60); do curl -fs -o /dev/null http://127.0.0.1:50020/vnc.html && break; sleep 1; done
query="vnc.html?autoconnect=1&resize=remote&password=$pw"
cat <<MSG

Refraction Studio test environment is running.
  App in the browser:  http://127.0.0.1:50020/$query
  Files (up/download): http://127.0.0.1:50021/   (user tester, password $fpw)
From another computer on any network, either open an SSH tunnel to this server first:
  ssh -L 50020:127.0.0.1:50020 -L 50021:127.0.0.1:50021 <your ssh login for this server>
or start with --public for HTTPS addresses that work without SSH.
MSG
if [[ $public == 1 ]]; then
  for svc in tunnel-app tunnel-files; do
    url=""
    for _ in $(seq 60); do
      url="$(docker compose "${profile[@]}" logs "$svc" 2>/dev/null | grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' | tail -1 || true)"
      [[ -n "$url" ]] && break; sleep 1
    done
    [[ $svc == tunnel-app ]] && echo "  Public app:          $url/$query" || echo "  Public files:        $url/   (user tester, password $fpw)"
  done
fi
echo "Stop with testenv/stop.sh."
