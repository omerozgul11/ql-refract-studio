#!/usr/bin/env python3
"""Publishes a built installer as a GitHub release.

Usage:
  python3 packaging/publish_release.py --version 1.0.0 [--notes-file NOTES.md] [--draft]

Reads GITHUB_TOKEN and GITHUB_REPOSITORY from the repository's .env (gitignored; template:
.env.example). The token needs Contents: Read and write on that repository only; it is never printed.

Creates (or reuses) the release "v<version>" on the default branch and uploads from artifacts/installer:
  RefractionStudio-<version>-win-x64-setup.exe   versioned installer (referenced by the update manifest)
  RefractionStudio-Setup-win-x64.exe             same file under a stable name (README download link:
                                                 releases/latest/download/RefractionStudio-Setup-win-x64.exe)
  RefractionStudio-<version>-win-x64-setup.exe.sha256
  release-stable.json                            signed update manifest, if present
Existing assets with the same names are replaced.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
INSTALLERS = ROOT / "artifacts" / "installer"
API = "https://api.github.com"
UPLOADS = "https://uploads.github.com"
STABLE_NAME = "RefractionStudio-Setup-win-x64.exe"


def load_dotenv(path: Path) -> None:
    if not path.exists():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        os.environ.setdefault(key.strip(), value)


def request(method: str, url: str, token: str, body: bytes | None = None, content_type: str = "application/json"):
    req = urllib.request.Request(url, data=body, method=method, headers={
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "refraction-studio-release",
        "Content-Type": content_type,
    })
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            data = r.read()
            return r.status, json.loads(data) if data else None
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        return e.code, json.loads(detail) if detail.startswith("{") else {"message": detail}


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", required=True)
    ap.add_argument("--notes-file", type=Path)
    ap.add_argument("--draft", action="store_true")
    args = ap.parse_args()

    load_dotenv(ROOT / ".env")
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    repo = os.environ.get("GITHUB_REPOSITORY", "").strip()
    if not token:
        raise SystemExit("GITHUB_TOKEN is not set: add it to .env (see .env.example).")
    if not repo or "/" not in repo:
        raise SystemExit("GITHUB_REPOSITORY must be owner/name (in .env).")

    installer = INSTALLERS / f"RefractionStudio-{args.version}-win-x64-setup.exe"
    if not installer.exists():
        raise SystemExit(f"{installer} not found: build it with packaging/package_windows.py --version {args.version}")
    digest = sha256(installer)
    sha_file = installer.with_name(installer.name + ".sha256")
    sha_file.write_text(f"{digest}  {installer.name}\n{digest}  {STABLE_NAME}\n", encoding="ascii")

    tag = f"v{args.version}"
    notes = args.notes_file.read_text(encoding="utf-8") if args.notes_file else (
        f"Refraction Studio {args.version} for Windows 10/11 (64-bit).\n\n"
        f"Download **{STABLE_NAME}** and run it.\n\nSHA-256: `{digest}`")

    status, release = request("GET", f"{API}/repos/{repo}/releases/tags/{urllib.parse.quote(tag)}", token)
    if status == 404:
        status, release = request("POST", f"{API}/repos/{repo}/releases", token, json.dumps({
            "tag_name": tag, "name": f"Refraction Studio {args.version}", "body": notes,
            "draft": args.draft, "prerelease": "-" in args.version}).encode())
        if status not in (200, 201):
            raise SystemExit(f"Could not create the release ({status}): {release.get('message')}")
        print(f"Created release {tag}")
    elif status == 200:
        print(f"Updating release {tag}")
    else:
        raise SystemExit(f"GitHub API error ({status}): {release.get('message')}")

    assets = [(installer, installer.name), (installer, STABLE_NAME), (sha_file, sha_file.name)]
    manifest = INSTALLERS / "release-stable.json"
    if manifest.exists():
        assets.append((manifest, manifest.name))
    existing = {a["name"]: a["id"] for a in release.get("assets", [])}
    for path, name in assets:
        if name in existing:
            request("DELETE", f"{API}/repos/{repo}/releases/assets/{existing[name]}", token)
        content_type = "application/vnd.microsoft.portable-executable" if name.endswith(".exe") else "text/plain"
        url = f"{UPLOADS}/repos/{repo}/releases/{release['id']}/assets?name={urllib.parse.quote(name)}"
        status, asset = request("POST", url, token, path.read_bytes(), content_type)
        if status not in (200, 201):
            raise SystemExit(f"Upload of {name} failed ({status}): {asset.get('message')}")
        print(f"Uploaded {name} ({path.stat().st_size / 1e6:.1f} MB)")
    print(f"Release page: {release['html_url']}")
    print(f"Download link: https://github.com/{repo}/releases/latest/download/{STABLE_NAME}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
