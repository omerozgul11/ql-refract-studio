"""Shared paths and settings for the Refraction Studio training video (see README.md)."""
from pathlib import Path
import json

ROOT = Path(__file__).resolve().parents[2]
STORY = json.loads((Path(__file__).parent / 'storyboard.json').read_text('utf-8'))
OUT = ROOT / 'artifacts' / f"training-video-{STORY['version']}"
MODELS = ROOT / '.tools' / 'media' / 'models'
FONTS = ROOT / 'src' / 'RefractionStudio.App' / 'Assets' / 'Fonts'
BRAND = ROOT / 'src' / 'RefractionStudio.App' / 'Assets' / 'Brand'
OUT.mkdir(parents=True, exist_ok=True)


def sentences(text):
    """Splits caption text into sentences (keeps "e.g." style abbreviations together)."""
    out, cur = [], ''
    for word in text.split():
        cur = (cur + ' ' + word).strip()
        if word.endswith(('.', '?', '!', ':')) and not word.endswith(('e.g.', 'i.e.')):
            out.append(cur); cur = ''
    return out + ([cur] if cur else [])
