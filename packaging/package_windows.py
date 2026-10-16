#!/usr/bin/env python3
"""Builds the Windows distribution of Refraction Studio.

Steps:
  1. dotnet publish of the desktop application (self-contained, win-x64) into artifacts/publish/win-x64;
  2. a check that every bundled third-party package is listed in THIRD-PARTY-NOTICES.txt;
  3. compilation of the Inno Setup installer (ISCC.exe; on Linux through Wine) into artifacts/installer;
  4. SHA-256 of the installer, and optionally a signed update manifest (tools/RefractionStudio.Cli).

Usage:
  python3 packaging/package_windows.py --version 1.0.0 [--iscc PATH] [--wine PATH] [--skip-installer]
         [--manifest-key PATH --download-url URL --notes TEXT]

On Windows, ISCC.exe is found in the default Inno Setup folder. On Linux, pass --wine and --iscc, or
set up the private toolchain with packaging/windows-toolchain.sh (it writes .tools/windows-build).
Code signing is configured on the build machine (see docs/DEPLOYMENT.md); unsigned installers are for
testing only.

Release secrets are read from the repository's .env (gitignored; template: .env.example):
  RS_RELEASE_SIGNING_KEY  path of the release-signing private key (default for --manifest-key)
  RS_DOWNLOAD_BASE_URL    base URL of the release downloads (default for --download-url)
  RS_SIGNTOOL_COMMAND     SignTool command line for the "rssign" sign tool (used with --sign)
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "RefractionStudio.App" / "RefractionStudio.App.csproj"
CLI = ROOT / "tools" / "RefractionStudio.Cli" / "RefractionStudio.Cli.csproj"
PUBLISH = ROOT / "artifacts" / "publish" / "win-x64"
INSTALLER_DIR = ROOT / "artifacts" / "installer"
ISS = ROOT / "packaging" / "windows" / "RefractionStudio.iss"
NOTICES = ROOT / "packaging" / "windows" / "THIRD-PARTY-NOTICES.txt"
TOOLS = ROOT / ".tools" / "windows-build"


def load_dotenv(path: Path) -> None:
    """Reads KEY=VALUE lines from the repository's .env (never committed) into the environment.
    Values already set in the environment win. Quotes around values are removed."""
    if not path.exists():
        return
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key, value = key.strip(), value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        os.environ.setdefault(key, value)


def env_path(name: str) -> Path | None:
    value = os.environ.get(name, "").strip()
    if not value:
        return None
    path = Path(value)
    return path if path.is_absolute() else ROOT / path


def run(cmd: list[str], **kw) -> None:
    print("+", " ".join(str(c) for c in cmd), flush=True)
    subprocess.run([str(c) for c in cmd], check=True, **kw)


def dotnet() -> str:
    local = ROOT / ".tools" / "dotnet" / "dotnet"
    return str(local) if local.exists() else "dotnet"


def publish(version: str) -> None:
    if PUBLISH.exists():
        shutil.rmtree(PUBLISH)
    run([dotnet(), "publish", APP, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
         f"-p:Version={version}", "-p:DebugType=none", "-p:DebugSymbols=false",
         "-p:PublishReadyToRun=false", "-o", PUBLISH])
    exe = PUBLISH / "RefractionStudio.exe"
    if not exe.exists():
        raise SystemExit(f"Publish did not produce {exe}")


def check_notices() -> None:
    """Every NuGet package in the published dependency graph must be named in the notices file."""
    deps = json.loads((PUBLISH / "RefractionStudio.deps.json").read_text(encoding="utf-8"))
    packages = sorted({name.split("/")[0] for name, lib in deps["libraries"].items() if lib.get("type") == "package"})
    text = NOTICES.read_text(encoding="utf-8")
    missing = [p for p in packages if p not in text and not p.startswith("runtimepack.")]
    if missing:
        raise SystemExit("THIRD-PARTY-NOTICES.txt does not cover: " + ", ".join(missing))
    print(f"Third-party notices cover all {len(packages)} packages.")


def winpath(path: Path) -> str:
    return "Z:" + str(path.resolve()).replace("/", "\\")


def find_toolchain(args) -> tuple[list[str], dict[str, str] | None, bool]:
    """Returns the command prefix to run ISCC, the environment, and whether paths must be Windows paths."""
    if platform.system() == "Windows":
        iscc = args.iscc or Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Inno Setup 6" / "ISCC.exe"
        return [str(iscc)], None, False
    wine = args.wine or TOOLS / "root" / "usr" / "lib" / "x86_64-linux-gnu" / "wine" / "wine"
    iscc = args.iscc or TOOLS / "inno" / "ISCC.exe"
    if not Path(wine).exists() or not Path(iscc).exists():
        raise SystemExit("Inno Setup is not available: run packaging/windows-toolchain.sh or pass --wine and --iscc.")
    wine_lib = Path(wine).parent
    env = dict(os.environ)
    env.update(WINEPREFIX=str(TOOLS / "prefix"), WINESERVER=str(wine_lib / "wineserver"), WINEDLLPATH=str(wine_lib),
               WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree,mshtml,winemenubuilder.exe=d",
               LD_LIBRARY_PATH=os.pathsep.join([str(TOOLS / "root" / "usr" / "lib" / "x86_64-linux-gnu"), str(wine_lib / "x86_64-unix")]),
               XDG_CONFIG_HOME=str(TOOLS / "xdg-config"), XDG_DATA_HOME=str(TOOLS / "xdg-data"), XDG_CACHE_HOME=str(TOOLS / "xdg-cache"))
    return [str(wine), winpath(Path(iscc))], env, True


def build_installer(version: str, args) -> Path:
    INSTALLER_DIR.mkdir(parents=True, exist_ok=True)
    prefix, env, windows_paths = find_toolchain(args)
    p = winpath if windows_paths else (lambda x: str(x))
    cmd = prefix + ["/Qp", f"/DAppVersion={version}", f"/DSourceDir={p(PUBLISH)}", f"/DOutputDir={p(INSTALLER_DIR)}"]
    if args.sign:
        cmd.append("/DSignTool=1")
        # The sign tool command (certificate selection, timestamp server) comes from .env so that it never
        # appears in the repository; ISCC replaces $f with each file to sign.
        if command := os.environ.get("RS_SIGNTOOL_COMMAND", "").strip():
            cmd.append(f"/Srssign={command}")
    cmd.append(p(ISS))
    run(cmd, env=env, cwd=ISS.parent)
    out = INSTALLER_DIR / f"RefractionStudio-{version}-win-x64-setup.exe"
    if not out.exists():
        raise SystemExit(f"The installer was not produced: {out}")
    return out


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", required=True)
    ap.add_argument("--iscc", type=Path)
    ap.add_argument("--wine", type=Path)
    ap.add_argument("--sign", action="store_true", help="Sign with the 'rssign' tool configured in Inno Setup")
    ap.add_argument("--skip-publish", action="store_true")
    ap.add_argument("--skip-installer", action="store_true")
    ap.add_argument("--manifest-key", type=Path, help="Release signing private key (PEM) for the update manifest")
    ap.add_argument("--download-url", help="Public URL of the installer (for the update manifest)")
    ap.add_argument("--channel", default="stable")
    ap.add_argument("--notes", default="")
    load_dotenv(ROOT / ".env")
    args = ap.parse_args()
    args.manifest_key = args.manifest_key or env_path("RS_RELEASE_SIGNING_KEY")
    args.download_url = args.download_url or (
        f"{os.environ['RS_DOWNLOAD_BASE_URL'].rstrip('/')}/v{args.version}/RefractionStudio-{args.version}-win-x64-setup.exe"
        if os.environ.get("RS_DOWNLOAD_BASE_URL") else None)

    if not args.skip_publish:
        publish(args.version)
    check_notices()
    if args.skip_installer:
        return 0
    installer = build_installer(args.version, args)
    digest = sha256(installer)
    (installer.parent / (installer.name + ".sha256")).write_text(f"{digest}  {installer.name}\n", encoding="ascii")
    print(f"Installer: {installer}\nSHA-256:   {digest}")
    if args.manifest_key:
        if not args.download_url:
            raise SystemExit("--download-url is required with --manifest-key")
        from datetime import datetime, timezone
        unsigned = installer.parent / f"release-{args.channel}.unsigned.json"
        unsigned.write_text(json.dumps({
            "product": "refraction-studio", "version": args.version, "channel": args.channel,
            "releasedAt": datetime.now(timezone.utc).isoformat(), "downloadUrl": args.download_url,
            "sha256": digest, "size": installer.stat().st_size, "releaseNotes": args.notes,
            "major": False, "minimumVersion": None}, indent=2), encoding="utf-8")
        manifest = installer.parent / f"release-{args.channel}.json"
        run([dotnet(), "run", "--project", CLI, "--", "sign-manifest", unsigned, "--key", args.manifest_key, "--out", manifest])
        print(f"Signed manifest: {manifest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
