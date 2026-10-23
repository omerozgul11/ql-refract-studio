# Refraction Studio (Linux build) on a virtual display, served to a web browser with noVNC.
# imagemagick and ffmpeg take screenshots and screen recordings for the automated walkthrough.
# Built by testenv/start.sh from artifacts/publish/linux-x64. For local testing only.
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
        xvfb x11vnc novnc websockify openbox xdotool \
        libgtk-3-0t64 libice6 libsm6 libfontconfig1 libicu74 ca-certificates \
        fonts-dejavu-core fonts-liberation dbus-x11 xdg-utils \
        imagemagick ffmpeg \
    && rm -rf /var/lib/apt/lists/*
# Same uid as the host user, so files written by the app can be inspected and removed from the host.
RUN userdel -r ubuntu 2>/dev/null || true; useradd -m -u 1000 -s /bin/bash tester \
    && printf '5f0c2a9e7d1b4c3e8a6f9d2b1e7c4a90\n' > /etc/machine-id
COPY --chown=tester:tester app/ /opt/refraction-studio/
COPY testenv/app/openbox-rc.xml /etc/xdg/openbox/rc.xml
COPY testenv/app/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /opt/refraction-studio/RefractionStudio \
    && ln -sf /usr/share/novnc/vnc.html /usr/share/novnc/index.html
USER tester
WORKDIR /home/tester
EXPOSE 6080
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
