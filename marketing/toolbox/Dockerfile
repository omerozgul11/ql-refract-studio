# Media toolbox for the Refraction Studio documents and training video: Chromium (HTML -> PDF),
# the offline Kokoro voice (narration) and ffmpeg. Build and use through marketing/toolbox/run.sh.
FROM python:3.12-slim-bookworm
ENV DEBIAN_FRONTEND=noninteractive PIP_NO_CACHE_DIR=1 PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core poppler-utils \
    && rm -rf /var/lib/apt/lists/*
RUN pip install kokoro-onnx==0.6.1 soundfile==0.12.1 numpy pillow==11.1.0 playwright==1.49.1 markdown==3.7 qrcode==8.0 \
    && playwright install --with-deps chromium && rm -rf /var/lib/apt/lists/*
