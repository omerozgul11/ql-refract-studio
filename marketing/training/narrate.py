"""Narration for the training video with the offline Kokoro voice (Apache-2.0 model, CPU).

Run in the media toolbox: marketing/toolbox/run.sh python marketing/training/narrate.py
Writes <OUT>/audio/<segment>.wav (24 kHz mono) and <OUT>/audio/manifest.json with the durations.
"""
import hashlib, json, sys
from pathlib import Path
import soundfile as sf
from kokoro_onnx import Kokoro

sys.path.insert(0, str(Path(__file__).parent))
from common import MODELS, OUT, STORY

audio = OUT / 'audio'; audio.mkdir(exist_ok=True)
voice = Kokoro(str(MODELS / 'kokoro-v1.0.onnx'), str(MODELS / 'voices-v1.0.bin'))
previous = {m['id']: m for m in json.loads((audio / 'manifest.json').read_text())} if (audio / 'manifest.json').exists() else {}
manifest = []
for seg in STORY['segments']:
    path = audio / f"{seg['id']}.wav"
    key = hashlib.sha256(f"{STORY['voice']}|{STORY['voice_speed']}|{seg['say']}".encode()).hexdigest()
    if previous.get(seg['id'], {}).get('key') == key and path.exists():
        manifest.append(previous[seg['id']]); print(f"{seg['id']:12s} {manifest[-1]['seconds']:6.1f} s (unchanged)"); continue
    samples, rate = voice.create(seg['say'], voice=STORY['voice'], speed=STORY['voice_speed'], lang=STORY['voice_language'])
    sf.write(path, samples, rate)
    manifest.append({'id': seg['id'], 'file': path.name, 'seconds': round(len(samples) / rate, 3), 'key': key})
    print(f"{seg['id']:12s} {manifest[-1]['seconds']:6.1f} s")
(audio / 'manifest.json').write_text(json.dumps(manifest, indent=1))
print(f"total {sum(m['seconds'] for m in manifest):.1f} s")
