"""Render the narrated Refraction Studio training video (1920 x 1080, H.264/AAC).

Run in the media toolbox after narrate.py and capture.py:
    marketing/toolbox/run.sh python marketing/training/render.py
Output in <OUT>: Refraction-Studio-Training-<version>.mp4, .srt captions, -chapters.txt and -poster.jpg.
"""
import json, shutil, subprocess, sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).parent))
from common import BRAND, FONTS, OUT, STORY, sentences

W, H = 1920, 1080
UI_W, UI_H = STORY['screen']
BAND = H - UI_H
FPS, LEAD_IN, CHAPTER_CARD = 30, 0.3, 2.4
BLACK, INK, WHITE, MUTED, ORANGE = (21, 21, 23), (35, 31, 32), (255, 255, 255), (163, 164, 167), (237, 106, 35)
REN = OUT / 'render'
VERSION = STORY['version']
NAME = f'Refraction-Studio-Training-{VERSION}'
ENCODE = ['-c:v', 'libx264', '-preset', 'slow', '-crf', '27', '-tune', 'stillimage', '-pix_fmt', 'yuv420p', '-r', str(FPS),
          '-c:a', 'aac', '-b:a', '96k', '-ar', '48000', '-ac', '1']


def barlow(size, weight='Regular'):
    return ImageFont.truetype(str(FONTS / f'Barlow-{weight}.ttf'), size)


def oswald(size, weight=600):
    f = ImageFont.truetype(str(FONTS / 'Oswald-VF.ttf'), size)
    f.set_variation_by_axes([weight])
    return f


def tracked(draw, xy, text, font, fill, spacing):
    """Uppercase label with letter spacing (Oswald category lines)."""
    x, y = xy
    for ch in text:
        draw.text((x, y), ch, font=font, fill=fill)
        x += draw.textlength(ch, font=font) + spacing
    return x


def tracked_width(draw, text, font, spacing):
    return sum(draw.textlength(c, font=font) + spacing for c in text) - spacing


def logo(height, white=True):
    img = Image.open(BRAND / ('quakelogic-logo-white.png' if white else 'quakelogic-logo.png')).convert('RGBA')
    return img.resize((round(img.width * height / img.height), height), Image.LANCZOS)


def wrap(draw, text, font, width):
    rows, row = [], ''
    for word in text.split():
        candidate = (row + ' ' + word).strip()
        if row and draw.textlength(candidate, font=font) > width:
            rows.append(row); row = word
        else:
            row = candidate
    return rows + ([row] if row else [])


def card(kind, seg=None):
    """Title, chapter and closing cards, near-black with the white logo (UI area only; captions go below)."""
    img = Image.new('RGB', (UI_W, UI_H), BLACK); d = ImageDraw.Draw(img)
    lg = logo(34); img.paste(lg, (96, 80), lg)
    if kind == 'intro':
        d.text((96, 330), 'Refraction Studio', font=oswald(132, 600), fill=WHITE)
        tracked(d, (100, 512), f'TRAINING VIDEO · VERSION {VERSION}', oswald(30, 500), ORANGE, 5)
        d.text((100, 572), 'From field records to a finished report: picking, layered models, tomography, Vs30 and quality control.',
               font=barlow(30), fill=MUTED)
    elif kind == 'chapter':
        number, title = seg['chapter'].split(' · ', 1)
        d.rectangle([96, 380, 196, 384], fill=ORANGE)
        tracked(d, (96, 410), f'CHAPTER {number}', oswald(30, 500), ORANGE, 5)
        d.text((92, 456), title, font=oswald(92, 600), fill=WHITE)
    elif kind == 'outro':
        d.text((96, 300), 'Thank you', font=oswald(120, 600), fill=WHITE)
        tracked(d, (100, 470), 'QUAKELOGIC SUPPORT', oswald(30, 500), ORANGE, 5)
        for i, line in enumerate(['support@quakelogic.net', '+1 (916) 899-0391', 'products.quakelogic.net']):
            d.text((100, 525 + i * 48), line, font=barlow(36), fill=WHITE if i == 0 else MUTED)
        d.text((100, 820), 'QuakeLogic Inc. · 2008 Opportunity Dr., Suite 130, Roseville, CA 95678, USA', font=barlow(24), fill=MUTED)
    return img


def full(img):
    """A card as a full 1920 x 1080 frame (chapter cards carry no caption)."""
    frame = Image.new('RGB', (W, H), BLACK); frame.paste(img, (0, 0)); return frame


def band(chapter, text):
    img = Image.new('RGB', (W, BAND), BLACK); d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W, 2], fill=ORANGE)
    if chapter:
        tracked(d, (40, 14), chapter.upper(), oswald(17, 500), ORANGE, 3)
    f = barlow(31, 'Medium')
    rows = wrap(d, text, f, 1560) if text else []
    y = 38 + (2 - len(rows)) * 20
    for row in rows:
        d.text(((W - d.textlength(row, font=f)) / 2, y), row, font=f, fill=WHITE); y += 40
    return img


def chunks(text):
    """Sentences, split further at commas, so that every caption fits on two lines."""
    probe = ImageDraw.Draw(Image.new('RGB', (10, 10))); f = barlow(31, 'Medium')
    out = []
    for s in sentences(text):
        if len(wrap(probe, s, f, 1560)) <= 2:
            out.append(s); continue
        cur = ''
        for part in [p.strip() for p in s.split(', ')]:
            candidate = f'{cur}, {part}' if cur else part
            if cur and len(wrap(probe, candidate, f, 1560)) > 2:
                out.append(cur + ','); cur = part
            else:
                cur = candidate
        out.append(cur)
    return out


def ffmpeg(*args):
    subprocess.run(['ffmpeg', '-loglevel', 'error', '-y', *map(str, args)], check=True)


def duration(path):
    out = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', str(path)],
                         capture_output=True, text=True, check=True).stdout
    return float(out.strip())


def report_frames(seg_dir):
    """Three spreads of report pages for the 'pages' segment."""
    pdf = OUT / 'report.pdf'
    groups = [[1, 2, 4], [7, 10, 12], [15, 16, 19]]
    frames = []
    for gi, pages in enumerate(groups):
        img = Image.new('RGB', (UI_W, UI_H), (235, 235, 237))
        images = []
        for p in pages:
            base = seg_dir / f'page-{p}'
            subprocess.run(['pdftoppm', '-r', '110', '-png', '-singlefile', '-f', str(p), '-l', str(p), str(pdf), str(base)], check=True)
            page = Image.open(f'{base}.png').convert('RGB')
            images.append(page.resize((round(page.width * 820 / page.height), 820), Image.LANCZOS))
        gap = 48
        x = (UI_W - sum(i.width for i in images) - gap * (len(images) - 1)) // 2
        for page in images:
            img.paste(page, (x, 70)); x += page.width + gap
        path = seg_dir / f'spread-{gi}.png'; img.save(path); frames.append(path)
    return frames


def segment(index, seg, audio_s, capture, chapter):
    seg_dir = REN / seg['id']; seg_dir.mkdir(parents=True, exist_ok=True)
    total = audio_s + LEAD_IN + 0.8
    inputs, vf = [], ''
    if seg.get('card') in ('intro', 'outro'):
        still = seg_dir / 'card.png'; card(seg['card']).save(still)
        inputs = ['-loop', '1', '-t', f'{total:.3f}', '-i', still]
        vf = '[0:v]pad=1920:1080:0:0:color=0x151517,format=yuv420p[v0]'
    elif seg.get('card') == 'report':
        spreads = report_frames(seg_dir)
        listing = seg_dir / 'spreads.txt'
        each = total / len(spreads)
        listing.write_text(''.join(f"file '{p.name}'\nduration {each:.3f}\n" for p in spreads) + f"file '{spreads[-1].name}'\n")
        inputs = ['-f', 'concat', '-safe', '0', '-i', listing]
        vf = '[0:v]fps=30,pad=1920:1080:0:0:color=0x151517,format=yuv420p[v0]'
    else:
        parts = capture[seg['id']]['parts']
        listing = seg_dir / 'parts.txt'
        listing.write_text(''.join(f"file '{(OUT / 'capture' / p).as_posix()}'\n" for p in parts))
        joined = seg_dir / 'joined.mkv'
        ffmpeg('-f', 'concat', '-safe', '0', '-i', listing, '-c', 'copy', joined)
        total = max(total, duration(joined))
        inputs = ['-i', joined]
        vf = '[0:v]pad=1920:1080:0:0:color=0x151517,format=yuv420p[v0]'
    # Captions, timed in proportion to their length within the narration.
    texts = chunks(seg['text'])
    weights = [len(t) for t in texts]; t0 = LEAD_IN; cues = []
    for i, (t, wgt) in enumerate(zip(texts, weights)):
        span = audio_s * wgt / sum(weights)
        cues.append((t0, t0 + span if i < len(texts) - 1 else total, t)); t0 += span
    filters = [vf]; last = 'v0'
    for i, (a, b, t) in enumerate(cues):
        png = seg_dir / f'cap-{i}.png'; band(chapter, t).save(png)
        inputs += ['-i', png]
        filters.append(f'[{last}][{i + 1}:v]overlay=0:{UI_H}:enable=\'between(t,{a:.3f},{b:.3f})\'[v{i + 1}]'); last = f'v{i + 1}'
    wav = OUT / 'audio' / f"{seg['id']}.wav"
    inputs += ['-i', wav]
    filters.append(f'[{len(cues) + 1}:a]adelay={int(LEAD_IN * 1000)},apad[a]')
    out = REN / f'{index:02d}-{seg["id"]}.mp4'
    ffmpeg(*inputs, '-filter_complex', ';'.join(filters), '-map', f'[{last}]', '-map', '[a]', '-t', f'{total:.3f}', *ENCODE, out)
    return out, total, cues


def chapter_clip(index, seg):
    still = REN / f'{index:02d}-chapter.png'; full(card('chapter', seg)).save(still)
    out = REN / f'{index:02d}-chapter.mp4'
    ffmpeg('-loop', '1', '-t', CHAPTER_CARD, '-i', still, '-f', 'lavfi', '-t', CHAPTER_CARD, '-i', 'anullsrc=r=48000:cl=mono',
           '-shortest', *ENCODE, out)
    return out, CHAPTER_CARD


def stamp(t, srt=True):
    h, rem = divmod(t, 3600); m, s = divmod(rem, 60)
    return f'{int(h):02d}:{int(m):02d}:{int(s):02d},{int(round((s % 1) * 1000)) % 1000:03d}' if srt else f'{int(m):02d}:{int(s):02d}'


def main():
    capture = json.loads((OUT / 'capture.json').read_text())
    assert capture['status'] == 'passed', 'Run capture.py to completion first.'
    capture = {c['id']: c for c in capture['segments']}
    audio = {a['id']: a['seconds'] for a in json.loads((OUT / 'audio' / 'manifest.json').read_text())}
    if REN.exists(): shutil.rmtree(REN)
    REN.mkdir(parents=True)
    clips, srt, chapters, clock, n, current = [], [], [('00:00', 'Introduction')], 0.0, 0, ''
    for i, seg in enumerate(STORY['segments']):
        if seg.get('chapter'):
            current = seg['chapter']
            chapters.append((stamp(clock, srt=False), current.split(' · ', 1)[1]))
            path, length = chapter_clip(i, seg); clips.append(path); clock += length
        if seg.get('card') == 'outro': current = ''
        path, length, cues = segment(i, seg, audio[seg['id']], capture, current)
        clips.append(path)
        for a, b, t in cues:
            n += 1; srt.append(f'{n}\n{stamp(clock + a)} --> {stamp(clock + b)}\n{t}\n')
        clock += length
        print(f"{seg['id']:12s} {length:5.1f} s", flush=True)
    listing = REN / 'clips.txt'
    listing.write_text(''.join(f"file '{c.name}'\n" for c in clips))
    video = OUT / f'{NAME}.mp4'
    ffmpeg('-f', 'concat', '-safe', '0', '-i', listing, '-c', 'copy', '-movflags', '+faststart', video)
    (OUT / f'{NAME}.srt').write_text('\n'.join(srt), 'utf-8')
    (OUT / f'{NAME}-chapters.txt').write_text(''.join(f'{t} {title}\n' for t, title in chapters), 'utf-8')
    full(card('intro')).convert('RGB').save(OUT / f'{NAME}-poster.jpg', quality=90)
    print(f'{video.name}: {duration(video):.1f} s, {video.stat().st_size / 1e6:.1f} MB')


if __name__ == '__main__':
    main()
