#!/usr/bin/env python3
"""Build the ready-to-send client package: one zip with the installer and every customer document.

Usage: python3 packaging/client_package.py --version 1.0.0

Inputs: the installer (artifacts/installer), the PDFs (docs/marketing, built by marketing/documents/build.py),
the training video (artifacts/training-video-<version>, built by marketing/training), the example field records
(artifacts/testenv-data, written by the CLI "sample" command) and the licence texts (packaging/windows).
Output: download/Refraction-Studio-<version>-Client-Package.zip (stays below GitHub's 100 MB file limit).
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
LIMIT = 100 * 1000 * 1000


def start_here(v: str) -> str:
    return f'''QUAKELOGIC REFRACTION STUDIO {v} - START HERE
==============================================

Thank you for choosing QuakeLogic Refraction Studio: Windows software for seismic
refraction surveys with P and SH waves. It takes your shot records to first-arrival
picks, layered velocity models, tomography, Vs30 and a technical PDF report.

This package contains everything you need. First right-click the zip file and
choose "Extract All...", then open the extracted folder.


WHAT IS IN THIS PACKAGE
-----------------------
  1 - Install Refraction Studio            The Windows installer (full version)
  2 - Field Guide (set up your equipment)  Equipment, line layout, recording P and
                                           SH shots, and the analysis, step by step
  3 - Training Video                       6-minute narrated walkthrough
                                           (captions and chapter list included)
  4 - User Guide                           The complete illustrated user guide
  5 - Product Datasheet                    Features and technical specifications
  6 - Example field records                P and SH SEG-2 shot records to practise
                                           the import
  Licence and notices                      Licence agreement and open-source notices
  SHA256SUMS.txt                           Checksums to verify the files


INSTALL IN THREE STEPS
----------------------
Requirements: Windows 10 (version 1809 or later) or Windows 11, 64-bit;
about 250 MB of disk space. No internet connection needed.

1. Open the folder "1 - Install Refraction Studio" and double-click
   RefractionStudio-{v}-win-x64-setup.exe.

2. Windows may show "Windows protected your PC" or "Unknown publisher",
   because the installer is not yet code-signed.
   Choose "More info", then "Run anyway".

3. Accept the licence agreement, follow the steps and click Finish.

This is the FULL VERSION: there is no trial, no activation and no licence key.
Later, start Refraction Studio from Start > Refraction Studio or the desktop icon.
Installing over an earlier version keeps all your projects and settings.


YOUR FIRST SURVEY
-----------------
1. Read the Field Guide (folder 2) before going to the field. It covers the
   equipment, the shot layout, the seismograph settings and recording P and
   SH shots.
2. Watch the Training Video (folder 3) to see the whole analysis once.
3. In Refraction Studio, choose "Explore the sample" on the start page: a
   complete P and SH survey. Then import the example field records (folder 6)
   to practise the workflow from the beginning. Press F1 on any page for help.


SUPPORT AND SALES
-----------------
Support:  support@quakelogic.net  (reply within 3 business days)
Sales:    sales@quakelogic.net  |  +1 (916) 899-0391
Web:      products.quakelogic.net
Updates:  https://github.com/omerozgul11/ql-refract-studio

QuakeLogic Inc., 2008 Opportunity Dr., Suite 130, Roseville, CA 95678, USA
'''


EXAMPLE_README = '''EXAMPLE FIELD RECORDS
=====================

Synthetic shot records written by Refraction Studio (no real site): one P-wave
line and one SH-wave line, 48 geophones at 3 m spacing, seven shot positions
from -36 m to 177 m. Channel 49 of the P records is a reference geophone at the
shot. Each SH position has a left (L) and a right (R) record.

In Refraction Studio: New project > Import > Choose folder...
  P-wave line:  wave type P, records at the same position "Separate".
  SH-wave line: wave type SH, records at the same position "Opposite SH impacts".
'''


def crlf(text: str) -> bytes:
    return text.replace('\r\n', '\n').replace('\n', '\r\n').encode('utf-8')


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--version', required=True)
    v = ap.parse_args().version
    video = ROOT / 'artifacts' / f'training-video-{v}'
    records = ROOT / 'artifacts' / 'testenv-data' / 'work'
    files = [
        (ROOT / 'artifacts/installer' / f'RefractionStudio-{v}-win-x64-setup.exe', f'1 - Install Refraction Studio/RefractionStudio-{v}-win-x64-setup.exe'),
        (ROOT / 'docs/marketing/QuakeLogic-Refraction-Studio-Field-Guide.pdf', '2 - Field Guide (set up your equipment)/QuakeLogic-Refraction-Studio-Field-Guide.pdf'),
        (video / f'Refraction-Studio-Training-{v}.mp4', f'3 - Training Video/Refraction-Studio-Training-{v}.mp4'),
        (video / f'Refraction-Studio-Training-{v}.srt', f'3 - Training Video/Refraction-Studio-Training-{v}.srt'),
        (video / f'Refraction-Studio-Training-{v}-chapters.txt', f'3 - Training Video/Chapters.txt'),
        (ROOT / 'docs/marketing/QuakeLogic-Refraction-Studio-User-Guide.pdf', '4 - User Guide/QuakeLogic-Refraction-Studio-User-Guide.pdf'),
        (ROOT / 'docs/marketing/QuakeLogic-Refraction-Studio-Datasheet.pdf', '5 - Product Datasheet/QuakeLogic-Refraction-Studio-Datasheet.pdf'),
        (ROOT / 'packaging/windows/EULA.txt', 'Licence and notices/EULA.txt'),
        (ROOT / 'packaging/windows/THIRD-PARTY-NOTICES.txt', 'Licence and notices/THIRD-PARTY-NOTICES.txt'),
    ]
    for wave, folder in (('P', 'P-wave line'), ('SH', 'SH-wave line')):
        files += [(f, f'6 - Example field records/{folder}/{f.name}') for f in sorted((records / wave).glob('*.sg2'))]
    missing = [str(src) for src, _ in files if not src.exists()]
    if missing:
        raise SystemExit('Missing inputs:\n  ' + '\n  '.join(missing))

    top = f'Refraction-Studio-{v}-Client-Package'
    out = ROOT / 'download' / f'{top}.zip'
    out.parent.mkdir(exist_ok=True)
    tmp = out.with_suffix('.zip.tmp')
    entries = []
    with zipfile.ZipFile(tmp, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        def add(name: str, data: bytes, when: datetime.datetime, stored: bool = False):
            info = zipfile.ZipInfo(f'{top}/{name}', date_time=when.timetuple()[:6])
            info.compress_type = zipfile.ZIP_STORED if stored else zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, data)
            entries.append((name, hashlib.sha256(data).hexdigest()))
        now = datetime.datetime.now()
        add('START HERE.txt', crlf(start_here(v)), now)
        add('6 - Example field records/README.txt', crlf(EXAMPLE_README), now)
        for src, name in files:
            data = src.read_bytes()
            if name.endswith('.txt'):
                data = crlf(data.decode('utf-8-sig'))
            add(name, data, datetime.datetime.fromtimestamp(src.stat().st_mtime), stored=name.endswith(('.exe', '.mp4')))
        add('SHA256SUMS.txt', crlf(''.join(f'{h}  {n}\n' for n, h in entries)), now)
    size = tmp.stat().st_size
    if size >= LIMIT:
        tmp.unlink()
        raise SystemExit(f'The package is {size / 1e6:.1f} MB; GitHub accepts files below 100 MB.')
    tmp.replace(out)
    digest = hashlib.sha256(out.read_bytes()).hexdigest()
    out.with_name(out.name + '.sha256').write_text(f'{digest}  {out.name}\n', encoding='ascii')
    print(f'{out.relative_to(ROOT)}: {size / 1e6:.1f} MB, {len(entries)} files, SHA-256 {digest}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
