#!/usr/bin/env bash
# Sets up a private toolchain to compile the Windows installer on Linux:
#   .tools/windows-build/root    Wine, extracted from the distribution packages (no root rights needed)
#   .tools/windows-build/prefix  Wine prefix (headless: Wine's "null" graphics driver, no X server)
#   .tools/windows-build/inno    Inno Setup (ISCC.exe), installer verified by SHA-256
# Nothing is installed system-wide. Delete .tools/windows-build to undo.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS="$ROOT/.tools/windows-build"
INNO_VERSION="6.7.3"
INNO_URL="https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe"
INNO_SHA256="9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732"
PACKAGES=(libwine wine64 wine-common libz-mingw-w64 libmspack0t64 cabextract libtalloc2)

mkdir -p "$TOOLS/debs" "$TOOLS/root"
cd "$TOOLS/debs"
for p in "${PACKAGES[@]}"; do
  if ! ls "${p}"_*.deb >/dev/null 2>&1; then
    apt-get download "$p"
  fi
done
for deb in *.deb; do
  dpkg-deb -x "$deb" "$TOOLS/root"
done

WINE_LIB="$TOOLS/root/usr/lib/x86_64-linux-gnu/wine"
# The relocated Wine looks for its data in <lib>/../../../share/wine/wine.
[ -e "$TOOLS/root/usr/share/wine/wine" ] || ln -s . "$TOOLS/root/usr/share/wine/wine"
# Debian's Wine finds the MinGW zlib through system paths; place it next to the Wine DLLs instead.
cp -f "$TOOLS/root/usr/x86_64-w64-mingw32/lib/zlib1.dll" "$WINE_LIB/x86_64-windows/zlib1.dll"
cp -f "$TOOLS/root/usr/i686-w64-mingw32/lib/zlib1.dll" "$WINE_LIB/i386-windows/zlib1.dll"

export WINEPREFIX="$TOOLS/prefix" WINESERVER="$WINE_LIB/wineserver" WINEDLLPATH="$WINE_LIB" WINEDEBUG=-all \
       WINEDLLOVERRIDES="mscoree,mshtml,winemenubuilder.exe=d" \
       LD_LIBRARY_PATH="$TOOLS/root/usr/lib/x86_64-linux-gnu:$WINE_LIB/x86_64-unix" \
       XDG_CONFIG_HOME="$TOOLS/xdg-config" XDG_DATA_HOME="$TOOLS/xdg-data" XDG_CACHE_HOME="$TOOLS/xdg-cache"
unset DISPLAY WAYLAND_DISPLAY
WINE="$WINE_LIB/wine"

if [ ! -f "$TOOLS/innosetup-$INNO_VERSION.exe" ]; then
  curl -fsSL -o "$TOOLS/innosetup-$INNO_VERSION.exe" "$INNO_URL"
fi
echo "$INNO_SHA256  $TOOLS/innosetup-$INNO_VERSION.exe" | sha256sum -c -

if [ ! -f "$TOOLS/inno/ISCC.exe" ]; then
  "$WINE" wineboot -i >/dev/null 2>&1 || true
  "$WINESERVER" -w || true
  # Wine's built-in DLLs import zlib1.dll; provide it inside the prefix as well.
  cp -f "$TOOLS/root/usr/x86_64-w64-mingw32/lib/zlib1.dll" "$WINEPREFIX/drive_c/windows/system32/zlib1.dll"
  cp -f "$TOOLS/root/usr/i686-w64-mingw32/lib/zlib1.dll" "$WINEPREFIX/drive_c/windows/syswow64/zlib1.dll"
  # Headless: windows are created by the "null" driver (the Inno Setup installer creates windows even
  # when silent).
  "$WINE" reg add 'HKCU\Software\Wine\Drivers' /v Graphics /d null /f >/dev/null
  # Restart the prefix so that the desktop process also uses the null driver.
  "$WINESERVER" -k || true
  "$WINESERVER" -w || true
  "$WINE" "$TOOLS/innosetup-$INNO_VERSION.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /NOICONS \
    "/DIR=Z:${TOOLS//\//\\}\\inno" "/LOG=Z:${TOOLS//\//\\}\\inno-install.log"
  "$WINESERVER" -w || true
fi
test -f "$TOOLS/inno/ISCC.exe"
echo "Inno Setup $INNO_VERSION ready: $TOOLS/inno/ISCC.exe"
