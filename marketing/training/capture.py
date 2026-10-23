"""Film the training video in a separate, empty Refraction Studio (Linux build, Docker, 1920 x 960).

Runs on the host with the standard library only. It starts the container "rfs-capture" from the test
environment image (testenv/start.sh builds it) with a new data folder and synthetic field records,
then drives the real application with X pointer and keyboard events (xdotool) and records each
segment with ffmpeg (x11grab). A segment lasts at least as long as its narration (narrate.py runs
first); ["cut"] ... ["resume"] leaves long calculations out of the recording.

    python3 marketing/training/capture.py [--only seg1,seg2]   (--only re-films those segments from a fresh start)
Output: <OUT>/capture/<segment>-<part>.mkv and <OUT>/capture.json.
"""
import json, math, shutil, subprocess, sys, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from common import OUT, ROOT, STORY

NAME, IMAGE = 'rfs-capture', 'rfs-testenv-app:local'
W, H = STORY['screen']
WORK = ROOT / 'artifacts' / 'training-capture'
CAP = OUT / 'capture'
TAIL = 0.8                      # seconds held after the narration ends
audio = {a['id']: a['seconds'] for a in json.loads((OUT / 'audio' / 'manifest.json').read_text())}


def sh(*args, check=True):
    return subprocess.run(args, check=check, capture_output=True, text=True).stdout


def x(*args):
    sh('docker', 'exec', NAME, 'env', 'DISPLAY=:1', 'xdotool', *map(str, args))


def pointer():
    out = sh('docker', 'exec', NAME, 'env', 'DISPLAY=:1', 'xdotool', 'getmouselocation', '--shell')
    v = dict(line.split('=') for line in out.split())
    return int(v['X']), int(v['Y'])


def glide(tx, ty, seconds=0.6):
    """Moves the pointer along an eased path (one chained xdotool call), so viewers can follow it."""
    sx, sy = pointer()
    steps = max(8, int(seconds * 40))
    chain = []
    for i in range(1, steps + 1):
        t = 0.5 - 0.5 * math.cos(math.pi * i / steps)
        chain += ['mousemove', round(sx + (tx - sx) * t), round(sy + (ty - sy) * t), 'sleep', f'{seconds / steps:.3f}']
    x(*chain)


class Recorder:
    def __init__(self, seg_id):
        self.seg_id, self.part, self.parts = seg_id, 0, []

    def start(self):
        name = f'{self.seg_id}-{self.part}.mkv'
        sh('docker', 'exec', '-d', NAME, 'sh', '-c',
           f'ffmpeg -loglevel error -y -f x11grab -draw_mouse 1 -framerate 30 -video_size {W}x{H} -i :1 '
           f'-c:v libx264 -preset ultrafast -crf 14 -pix_fmt yuv420p /home/tester/Files/.capture/{name}')
        self.parts.append(name); self.part += 1; self.started = time.monotonic()
        time.sleep(0.4)

    def stop(self):
        sh('docker', 'exec', NAME, 'pkill', '-INT', '-f', 'x11grab', check=False)
        for _ in range(100):
            if not sh('docker', 'exec', NAME, 'pgrep', '-f', 'x11grab', check=False).strip(): break
            time.sleep(0.1)
        return time.monotonic() - self.started


def run(seg):
    rec = Recorder(seg['id'])
    rec.start()
    recorded = 0.0
    for action in seg.get('actions', []):
        kind, *a = action
        if kind == 'move': glide(*a)
        elif kind == 'clickat': glide(*a); time.sleep(0.15); x('click', 1)
        elif kind == 'click': x('click', 1)
        elif kind == 'dclick': x('click', '--repeat', 2, '--delay', 120, 1)
        elif kind == 'tclick': x('click', '--repeat', 3, '--delay', 90, 1)
        elif kind == 'type': x('type', '--delay', 45, a[0])
        elif kind == 'key': x('key', a[0])
        elif kind == 'wait': time.sleep(a[0])
        elif kind == 'palette':
            x('key', 'ctrl+k'); time.sleep(0.7); x('type', '--delay', 45, a[0]); time.sleep(0.8); x('key', 'Return')
        elif kind == 'cut': recorded += rec.stop()
        elif kind == 'resume': rec.start()
        else: raise ValueError(f'unknown action {kind}')
        time.sleep(0.05)
    # Hold until the narration (plus a short tail) has been covered by the recorded parts.
    elapsed = time.monotonic() - rec.started
    hold = audio[seg['id']] + TAIL - (recorded + elapsed)
    if hold > 0: time.sleep(hold)
    recorded += rec.stop()
    return {'id': seg['id'], 'parts': rec.parts, 'recorded': round(recorded, 2), 'narration': audio[seg['id']]}


def main():
    only = sys.argv[sys.argv.index('--only') + 1].split(',') if '--only' in sys.argv else None
    filmed = [s for s in STORY['segments'] if 'actions' in s and (only is None or s['id'] in only)]
    if only is None and CAP.exists(): shutil.rmtree(CAP)
    sh('docker', 'rm', '-f', NAME, check=False)
    if WORK.exists(): shutil.rmtree(WORK)
    files = WORK / 'Files'
    for d in ('Field records/P-wave line', 'Field records/SH-wave line', 'My projects', 'Reports', '.capture'):
        (files / d).mkdir(parents=True)
    data = ROOT / 'artifacts' / 'testenv-data' / 'work'
    for wave, folder in (('P', 'P-wave line'), ('SH', 'SH-wave line')):
        for f in sorted((data / wave).glob('*.sg2')): shutil.copy2(f, files / 'Field records' / folder / f.name)
    appdata = WORK / 'appdata'; appdata.mkdir()
    # Settings for filming: no update check, a neutral user name, light theme.
    (appdata / 'settings.json').write_text(json.dumps({'updates': 'manual', 'userName': 'Field engineer', 'theme': 'light'}))
    sh('docker', 'run', '-d', '--name', NAME, '--network', 'none', '-e', 'VNC_PASSWORD=capture', '-e', f'SCREEN={W}x{H}',
       '-v', f'{files}:/home/tester/Files', '-v', f'{appdata}:/home/tester/.refraction-studio', '--shm-size', '256m', IMAGE)
    time.sleep(10)
    x('mousemove', W // 2, H // 2)
    results = []
    for seg in filmed:
        print(f"filming {seg['id']}", flush=True)
        results.append(run(seg))
    CAP.mkdir(parents=True, exist_ok=True)
    for r in results:
        for p in r['parts']: shutil.move(files / '.capture' / p, CAP / p)
    if only is None:
        reports = sorted((files / 'Reports').glob('*.pdf'))
        assert reports, 'the report was not generated'
        shutil.copy2(reports[0], OUT / 'report.pdf')
    else:
        previous = {s['id']: s for s in json.loads((OUT / 'capture.json').read_text())['segments']}
        previous.update({r['id']: r for r in results})
        results = [previous[s['id']] for s in STORY['segments'] if s['id'] in previous]
    (OUT / 'capture.json').write_text(json.dumps({'status': 'passed', 'segments': results}, indent=1))
    sh('docker', 'rm', '-f', NAME, check=False)
    print('capture passed')


if __name__ == '__main__':
    main()
