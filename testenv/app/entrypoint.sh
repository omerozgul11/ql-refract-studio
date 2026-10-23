#!/usr/bin/env bash
# Virtual display + window manager + VNC + noVNC, then Refraction Studio (restarted whenever it is closed).
set -euo pipefail
export DISPLAY=:1
export REFRACTIONSTUDIO_DATA_DIR="$HOME/.refraction-studio"
mkdir -p "$HOME/.vnc" "$REFRACTIONSTUDIO_DATA_DIR"
Xvfb :1 -screen 0 "${SCREEN:-1680x1050}x24" -nolisten tcp &
for _ in $(seq 50); do [ -e /tmp/.X11-unix/X1 ] && break; sleep 0.1; done
openbox &
x11vnc -storepasswd "$VNC_PASSWORD" "$HOME/.vnc/passwd" >/dev/null
x11vnc -display :1 -rfbauth "$HOME/.vnc/passwd" -forever -shared -noxdamage -quiet -rfbport 5900 &
websockify --web /usr/share/novnc 6080 localhost:5900 &
# Keep the main window filling the screen (Openbox applies its rules only when a window first appears).
(
  geometry="${SCREEN:-1680x1050}"
  while sleep 1; do
    for w in $(xdotool search --onlyvisible --classname RefractionStudio 2>/dev/null); do
      [[ "$(xdotool getwindowname "$w" 2>/dev/null)" == *"Refraction Studio" ]] || continue
      g="$(xdotool getwindowgeometry "$w" 2>/dev/null | tr -d '\n')"
      [[ "$g" == *"Position: 0,0"*"Geometry: $geometry"* ]] || xdotool windowmove "$w" 0 0 windowsize "$w" "${geometry%x*}" "${geometry#*x}"
    done
  done
) &
while true; do
  /opt/refraction-studio/RefractionStudio >>"$HOME/Files/app-console.log" 2>&1 || true
  echo "$(date -Is) Refraction Studio exited; restarting in 2 s" >>"$HOME/Files/app-console.log"
  sleep 2
done
