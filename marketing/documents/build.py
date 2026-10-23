"""Build the Refraction Studio client documents in the QuakeLogic house style (HTML -> PDF with Chromium).

    marketing/toolbox/run.sh python marketing/documents/build.py

Inputs: docs/field-guide/FIELD-GUIDE.md, docs/user-guide/USER-GUIDE.md, the diagrams in diagrams.py,
the screenshots in docs/images and the brand assets of the application.
Output (docs/marketing):
  QuakeLogic-Refraction-Studio-Field-Guide.pdf    equipment, layout, recording and analysis
  QuakeLogic-Refraction-Studio-User-Guide.pdf     the complete user guide, illustrated
  QuakeLogic-Refraction-Studio-Datasheet.pdf      two-page feature and specification sheet
  *-preview.png                                   page previews for the product page
"""
import base64, html, re, shutil, subprocess, sys
from pathlib import Path
import markdown
from playwright.sync_api import sync_playwright

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE))
from diagrams import DIAGRAMS, rays  # noqa: E402

FONTS = ROOT / 'src/RefractionStudio.App/Assets/Fonts'
BRAND = ROOT / 'src/RefractionStudio.App/Assets/Brand'
IMAGES = ROOT / 'docs/images'
OUT = ROOT / 'docs/marketing'
BUILD = ROOT / 'artifacts/documents'
VERSION = '1.0.0'

COMPANY = ('<b>QuakeLogic Inc.</b> · 2008 Opportunity Dr., Suite 130, Roseville, CA 95678, USA · '
           '+1 (916) 899-0391 · sales@quakelogic.net · products.quakelogic.net')


def data_uri(path):
    mime = 'image/png' if path.suffix == '.png' else 'image/svg+xml'
    return f'data:{mime};base64,' + base64.b64encode(path.read_bytes()).decode()


def install_fonts():
    """Chromium's page footers use installed fonts only: put the brand fonts in ~/.fonts."""
    target = Path.home() / '.fonts'; target.mkdir(parents=True, exist_ok=True)
    for f in FONTS.glob('*.ttf'): shutil.copy2(f, target / f.name)
    subprocess.run(['fc-cache', '-f'], capture_output=True)


FONT_FACES = ''.join(
    f"@font-face{{font-family:'Barlow';font-weight:{w};src:url('file://{FONTS}/Barlow-{n}.ttf');}}"
    for w, n in ((300, 'Light'), (400, 'Regular'), (500, 'Medium'), (600, 'SemiBold'), (700, 'Bold'))
) + f"@font-face{{font-family:'Oswald';font-weight:200 700;src:url('file://{FONTS}/Oswald-VF.ttf');}}"

CSS = FONT_FACES + """
:root{--orange:#ED6A23;--black:#151517;--ink:#231F20;--grey:#58595B;--grey2:#6E6F72;--mute:#77787B;--rule:#E3E3E5;--panel:#F3F3F4;}
*{box-sizing:border-box;}
html,body{margin:0;padding:0;}
body{font-family:'Barlow';color:var(--ink);font-size:10pt;line-height:1.45;-webkit-print-color-adjust:exact;print-color-adjust:exact;}
h1{font-family:'Barlow';font-weight:600;font-size:20pt;margin:0 0 8pt;}
h2{font-family:'Barlow';font-weight:600;font-size:16pt;margin:20pt 0 6pt;padding-top:7pt;border-top:2px solid var(--orange);break-after:avoid;}
h3{font-family:'Oswald';font-weight:500;font-size:10pt;letter-spacing:.12em;text-transform:uppercase;color:var(--ink);margin:14pt 0 4pt;break-after:avoid;}
p{margin:0 0 6pt;color:#333;} li{margin:0 0 3pt;color:#333;} ul,ol{margin:0 0 8pt;padding-left:16pt;}
strong{font-weight:600;color:var(--ink);}
code{font-family:'Barlow';font-weight:500;background:var(--panel);padding:0 3pt;border-radius:3px;}
table{width:100%;border-collapse:collapse;margin:4pt 0 10pt;font-size:8.8pt;break-inside:avoid;}
th{background:var(--ink);color:#fff;font-weight:600;text-align:left;padding:4pt 6pt;}
td{border-bottom:1px solid var(--rule);padding:4pt 6pt;vertical-align:top;color:#333;}
.tip{background:var(--panel);border-radius:4px;padding:8pt 11pt;margin:6pt 0 10pt;font-size:9.4pt;break-inside:avoid;}
.tip p{margin:0;}
figure{margin:6pt 0 12pt;break-inside:avoid;}
figure svg,figure img{display:block;width:100%;height:auto;}
figure img{border:1px solid var(--rule);border-radius:4px;}
figcaption{font-size:8.5pt;color:var(--mute);margin-top:4pt;}
.sheet td{height:24pt;}
hr{border:0;border-top:1px solid var(--rule);margin:12pt 0;}
"""

COVER_CSS = """
@page{size:Letter;margin:0;}
.cover{width:8.5in;height:11in;position:relative;overflow:hidden;}
.hero{background:var(--black);color:#fff;height:6.6in;padding:0.55in 0.6in;position:relative;}
.hero .logo{height:20px;}
.hero .doc{position:absolute;right:0.6in;top:0.58in;font-family:'Oswald';font-size:9.5pt;letter-spacing:.16em;color:#A3A4A7;}
.hero .name{font-family:'Oswald';font-weight:600;font-size:52pt;line-height:1.02;margin:0.42in 0 10pt;}
.hero .cat{font-family:'Oswald';font-weight:500;font-size:13pt;letter-spacing:.14em;color:var(--orange);}
.hero .tag{font-size:11pt;color:#C9CACD;margin-top:8pt;max-width:5.6in;}
.hero .graphic{position:absolute;left:0.6in;width:6.4in;bottom:0.4in;}
.hero .graphic svg{width:100%;height:auto;}
.lower{padding:0.35in 0.6in 0;}
.lower h3{margin-top:0;}
.toc{columns:2;column-gap:28pt;margin-top:6pt;}
.toc div{font-size:10.5pt;line-height:1.9;break-inside:avoid;border-bottom:1px solid var(--rule);}
.toc b{font-family:'Oswald';font-weight:500;color:var(--orange);margin-right:8pt;}
.foot{position:absolute;left:0.6in;right:0.6in;bottom:0.45in;border-top:1px solid var(--rule);padding-top:7pt;font-size:7.5pt;color:var(--mute);display:flex;justify-content:space-between;align-items:center;}
.foot b{color:var(--ink);font-weight:600;} .foot img{height:17px;}
"""


def footer_template():
    logo = data_uri(BRAND / 'quakelogic-logo.png')
    return (f"<div style=\"font-family:Barlow;font-size:7pt;color:#77787B;width:100%;margin:0 0.6in;border-top:1px solid #E3E3E5;"
            f"padding-top:5px;display:flex;justify-content:space-between;align-items:center;\">"
            f"<span>{COMPANY.replace('<b>', '<b style=\"color:#231F20\">')}</span>"
            f"<span style=\"display:flex;align-items:center;gap:10px;\"><span class=\"pageNumber\"></span>"
            f"<img src=\"{logo}\" style=\"height:13px\"></span></div>")


def white_rays():
    """The cover version of the ray diagram: light linework on the near-black hero."""
    svg, _ = rays()
    for a, b in (("#231F20", "#FFFFFF"), ("#58595B", "#C9CACD"), ("#6E6F72", "#8C8D91"), ("#F3F3F4", "#1E1E22"),
                 ("#E9E9EC", "#26262B"), ("#77787B", "#8C8D91")):
        svg = svg.replace(a, b)
    return svg


def md_body(text, images_base):
    lines, out, tip = text.split('\n'), [], []
    for line in lines + ['']:  # "> " blockquotes become grey tip panels
        if line.startswith('> '):
            tip.append(line[2:].strip()); continue
        if tip:
            out.append('TIPMARK ' + ' '.join(tip)); tip = []
        out.append(line)
    body = markdown.markdown('\n'.join(out), extensions=['tables'])
    body = re.sub(r'^<h1>.*?</h1>\n', '', body)
    body = re.sub(r'<p>TIPMARK (.*?)</p>', r'<div class="tip"><p>\1</p></div>', body, flags=re.S)
    body = body.replace('&amp;nbsp;', '&nbsp;')
    body = body.replace('<table>\n<thead>\n<tr>\n<th>File</th>', '<table class="sheet">\n<thead>\n<tr>\n<th>File</th>')

    def diagram(m):
        svg, caption = DIAGRAMS[m.group(1)]()
        return f'<figure>{svg}<figcaption>{html.escape(caption)}</figcaption></figure>'
    body = re.sub(r'<p>\{\{diagram:([a-z]+)\}\}</p>', diagram, body)

    def image(m):
        caption, name = m.group(1), m.group(2)
        return f'<figure><img src="{data_uri(images_base / "images" / name)}"><figcaption>{caption}</figcaption></figure>'
    body = re.sub(r'<p><img alt="(.*?)" src="images/(.*?)" ?/?></p>', image, body)
    assert '{{' not in body, 'unknown diagram marker'
    return body


def page(css, body, title):
    return f'<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>{html.escape(title)}</title><style>{CSS}{css}</style></head><body>{body}</body></html>'


def cover(doc_label, name, category, tagline, toc_items, graphic):
    toc = ''.join(f'<div><b>{n}</b>{html.escape(t)}</div>' for n, t in toc_items)
    return f'''<section class="cover">
  <div class="hero">
    <img class="logo" src="{data_uri(BRAND / 'quakelogic-logo-white.png')}" alt="QuakeLogic">
    <div class="doc">{doc_label}</div>
    <div class="name">{name}</div>
    <div class="cat">{category}</div>
    <div class="tag">{tagline}</div>
    <div class="graphic">{graphic}</div>
  </div>
  <div class="lower"><h3>Contents</h3><div class="toc">{toc}</div></div>
  <div class="foot"><span>{COMPANY}</span><img src="{data_uri(BRAND / 'quakelogic-logo.png')}" alt="QuakeLogic"></div>
</section>'''


def render(browser, html_text, pdf, footer, margin):
    BUILD.mkdir(parents=True, exist_ok=True)
    src = BUILD / (pdf.stem + '.html'); src.write_text(html_text, 'utf-8')
    tab = browser.new_page()
    tab.goto(src.as_uri()); tab.wait_for_load_state('networkidle')
    tab.evaluate('document.fonts.ready')
    options = dict(path=str(pdf), format='Letter', print_background=True, margin=margin, prefer_css_page_size=False)
    if footer:
        options.update(display_header_footer=True, header_template='<span></span>', footer_template=footer_template())
    tab.pdf(**options); tab.close()


def guide(browser, source, out, label, name, category, tagline, images_base):
    text = source.read_text('utf-8')
    heads = re.findall(r'^## (\d+)\. (.+)$', text, flags=re.M)
    body_pdf, cover_pdf = BUILD / (out.stem + '-body.pdf'), BUILD / (out.stem + '-cover.pdf')
    render(browser, page('@page{size:Letter;}', md_body(text, images_base), name), body_pdf, True,
           {'top': '0.6in', 'bottom': '0.75in', 'left': '0.6in', 'right': '0.6in'})
    render(browser, page(COVER_CSS, cover(label, name, category, tagline, heads, white_rays()), name), cover_pdf, False,
           {'top': '0', 'bottom': '0', 'left': '0', 'right': '0'})
    subprocess.run(['pdfunite', str(cover_pdf), str(body_pdf), str(out)], check=True)


def user_guide_source():
    """The user guide with a screenshot after the introduction of the main sections."""
    text = (ROOT / 'docs/user-guide/USER-GUIDE.md').read_text('utf-8')
    shots = {'1': ('start.png', 'The start page'), '3': ('import.png', 'Import: inspection and validation before anything is added'),
             '5': ('waveforms.png', 'Waveforms: a shot gather and its amplitude spectrum'),
             '7': ('picking.png', 'P picks: gather, pick table and travel-time plot'),
             '8': ('quality-control.png', 'Quality control: summary cards and findings'),
             '9': ('velocity-model.png', 'Velocity model: travel times and the tomographic model'),
             '10': ('engineering.png', 'Engineering: Vs30, velocity profile and Poisson\'s ratio section'),
             '11': ('report.png', 'Report: details, output, branding and sections')}
    out = []
    for line in text.split('\n'):
        out.append(line)
        m = re.match(r'^## (\d+)\. ', line)
        if m and m.group(1) in shots and (IMAGES / shots[m.group(1)][0]).exists():
            f, cap = shots[m.group(1)]
            out += ['', f'![{cap}](images/{f})', '']
    path = BUILD / 'USER-GUIDE-illustrated.md'; BUILD.mkdir(parents=True, exist_ok=True)
    path.write_text('\n'.join(out), 'utf-8')
    return path


DATASHEET = """
@page{size:Letter;margin:0;}
.p{width:8.5in;height:11in;position:relative;overflow:hidden;page-break-after:always;}
.hero{background:var(--black);color:#fff;height:3.3in;padding:0.42in 0.5in;position:relative;}
.hero .logo{height:18px;}
.hero .doc{position:absolute;right:0.5in;top:0.47in;font-family:'Oswald';font-size:9pt;letter-spacing:.16em;color:#A3A4A7;}
.hero .name{font-family:'Oswald';font-weight:600;font-size:46pt;line-height:1;margin:0.32in 0 8pt;}
.hero .cat{font-family:'Oswald';font-weight:500;font-size:12pt;letter-spacing:.14em;color:var(--orange);}
.hero .tag{font-size:10.5pt;color:#C9CACD;margin-top:7pt;max-width:4.1in;}
.hero .graphic{position:absolute;right:0.5in;bottom:0.28in;width:2.9in;}
.hero .graphic svg{width:100%;height:auto;}
.body{padding:0.26in 0.5in 0;}
.lead h1{font-size:17pt;margin-bottom:4pt;} .lead p{font-size:10pt;color:var(--grey);}
.cols{display:grid;grid-template-columns:1fr 1fr 1fr;gap:18pt;margin-top:8pt;}
.cols h3{border-top:2px solid var(--orange);padding-top:6pt;margin:0 0 4pt;}
.item{padding:4pt 0;border-bottom:1px solid var(--rule);}
.item b{display:block;font-size:9.6pt;font-weight:600;}
.item span{font-size:8.6pt;color:var(--grey2);line-height:1.35;display:block;}
.spot{background:var(--panel);border-radius:4px;padding:10pt 14pt;margin-top:10pt;display:grid;grid-template-columns:1fr 1.25fr;gap:14pt;align-items:center;}
.spot h3{margin:0 0 4pt;} .spot p{font-size:9pt;color:var(--grey);margin:0;}
.spot img{width:100%;height:1.45in;object-fit:cover;object-position:left top;border-radius:3px;border:1px solid var(--rule);display:block;}
.foot{position:absolute;left:0.5in;right:0.5in;bottom:0.35in;border-top:1px solid var(--rule);padding-top:6pt;font-size:7.4pt;color:var(--mute);display:flex;justify-content:space-between;align-items:center;}
.foot b{color:var(--ink);font-weight:600;} .foot img{height:16px;}
.shots{display:grid;grid-template-columns:1fr 1fr;gap:10pt;margin:10pt 0 4pt;}
.shots img{width:100%;height:1.55in;object-fit:cover;object-position:left top;border:1px solid var(--rule);border-radius:3px;display:block;}
.shots div{font-size:8pt;color:var(--mute);margin-top:3pt;}
.spec td:first-child{width:1.75in;font-weight:600;color:var(--ink);}
.spec td{font-size:8.3pt;padding:2.6pt 6pt;line-height:1.32;}
.note{font-size:7.6pt;color:var(--mute);margin-top:6pt;}
"""

FEATURES = [
    ('Data and quality', [
        ('Import with inspection', 'SEG-2 and SEG-Y records are inspected before import; findings come with proposed corrections and nothing is repaired silently.'),
        ('Geometry', 'Positions from the file headers, channel roles with a reference geophone, reusable spread profiles and a surveyed ground profile.'),
        ('Non-destructive processing', 'Filters, AGC, trace balance and more run on copies; advice flags filters that shift first breaks.'),
        ('Shot-time correction', 'Trigger delays corrected from a reference geophone or entered per shot; raw and corrected times are kept.'),
    ]),
    ('Picking and models', [
        ('Automatic picking', 'AIC, STA/LTA, modified energy ratio and an SH polarity-divergence picker, each pick with a confidence and its reasons.'),
        ('Manual review', 'Click, drag and nudge picks; propagate a reviewed pick along the line. Manual picks are never overwritten.'),
        ('Layered interpretation', 'Automatic layer assignment with draggable crossovers; intercept-time, plus–minus, GRM and delay-time methods.'),
        ('Tomography', 'Regularised, robust travel-time inversion with ray coverage, convergence and the depth of investigation.'),
    ]),
    ('Results and reporting', [
        ('Engineering parameters', 'Vs30 or any averaging depth, with labelled extrapolation; Poisson\'s ratio and the G, E and K moduli.'),
        ('Quality control', 'Findings on picks, timing, geometry and models, each with what to check and a link to the trace.'),
        ('Exports', 'XLSX and CSV tables, PNG, SVG and PDF figures, and an audit record of methods, settings and history.'),
        ('PDF report', 'A complete technical report with your branding, in A4 or Letter and SI or US customary units.'),
    ]),
]

SPECS = [
    ('Input formats', 'SEG-2 (.sg2, .seg2, .dat), SEG-Y (.sgy, .segy; also recognised by content); recording delays and header positions applied'),
    ('Wave types', 'P (compressional) and SH (horizontal shear) lines in one project; opposite SH impacts combined as (left − right)/2'),
    ('Processing', 'Demean, detrend, taper, Butterworth high/low/band-pass (causal or zero-phase), notch, normalisation, trace balance, AGC, polarity, time shift, resampling; amplitude spectrum'),
    ('Pickers', 'AIC, STA/LTA, modified energy ratio, SH polarity-divergence; confidence High / Medium / Low; neighbour guidance; cross-correlation propagation'),
    ('Layered methods', 'Intercept-time, plus–minus, generalised reciprocal method (with XY analysis), delay-time least squares; forward-model check'),
    ('Tomography', 'Shortest-path ray tracing; regularised, robust inversion; gradient starting model; ray coverage; depth of investigation'),
    ('Engineering', 'Vs30 and averages over chosen intervals; ν, G, E, K; measured or estimated density (Gardner, Brocher), estimates labelled'),
    ('Quality control', 'Signal-to-noise, onset sharpness, reciprocal times, trigger problems, dead, noisy or clipped traces, model residuals'),
    ('Outputs', 'PDF report (A4 or Letter; SI or US customary), XLSX, CSV, PNG, SVG and PDF figures, audit record (JSON)'),
    ('Projects', 'One .rfsx file with the raw data; full history with undo and redo; autosave and crash recovery; snapshots; analysis scenarios'),
    ('Validation', '37 of 37 validation cases pass against hand calculations, closed-form solutions and synthetic models (version 1.0.0)'),
    ('System requirements', 'Windows 10 (version 1809 or later) or Windows 11, 64-bit; about 250 MB of disk space; screen 1024 × 660 or larger'),
    ('Licence', 'Full version on every installation: no trial, no activation and no internet connection needed'),
]


def datasheet(browser, out):
    foot = f'<div class="foot"><span>{COMPANY}</span><img src="{data_uri(BRAND / "quakelogic-logo.png")}" alt="QuakeLogic"></div>'
    cols = ''.join(f'<div><h3>{g}</h3>' + ''.join(f'<div class="item"><b>{t}</b><span>{d}</span></div>' for t, d in items) + '</div>'
                   for g, items in FEATURES)
    shot = lambda f: data_uri(IMAGES / f)
    p1 = f'''<section class="p">
  <div class="hero"><img class="logo" src="{data_uri(BRAND / 'quakelogic-logo-white.png')}" alt="QuakeLogic"><div class="doc">FEATURE OVERVIEW</div>
    <div class="name">Refraction Studio</div><div class="cat">SEISMIC REFRACTION SOFTWARE · P AND SH WAVES</div>
    <div class="tag">From field records to a finished report: first-arrival picking, layered models, tomography, Vs30 and quality control.</div>
    <div class="graphic">{white_rays()}</div></div>
  <div class="body">
    <div class="lead"><h1>Seismic refraction analysis, step by step</h1>
    <p>Refraction Studio takes P- and SH-wave shot records from import to a technical PDF report on a Windows computer. Every step is
    recorded and reversible, raw data are never modified, and quality control explains what to check before results are reported.</p></div>
    <div class="cols">{cols}</div>
    <div class="spot"><div><h3>Reproducible by design</h3><p>A project file holds the raw records, every processing step and pick, and the complete
    history with the user and software version. Undo any change, keep snapshots, and compare alternative interpretations of the same
    data as scenarios. The report states the methods, settings, warnings and limitations.</p></div>
    <img src="{shot('velocity-model.png')}" alt="Velocity model page"></div>
  </div>{foot}</section>'''
    rows = ''.join(f'<tr><td>{a}</td><td>{b}</td></tr>' for a, b in SPECS)
    p2 = f'''<section class="p"><div class="body" style="padding-top:0.42in">
    <h2 style="margin-top:0">Workflow</h2>
    <p>Project → Import → Geometry → Waveforms → Processing → P and SH picks → Quality control → Velocity model → Engineering → Results → Report.
    Each step shows its status; Guided mode explains it, Expert mode keeps the screen compact.</p>
    <div class="shots"><div><img src="{shot('picking.png')}"><div>P picks: automatic picks with confidence, pick table and travel times</div></div>
    <div><img src="{shot('engineering.png')}"><div>Engineering: Vs30, velocity profiles and the Poisson's ratio section</div></div></div>
    <h2>Technical specifications</h2>
    <table class="spec"><thead><tr><th>Item</th><th>Specification</th></tr></thead><tbody>{rows}</tbody></table>
    <p class="note">Version {VERSION}. Results support engineering interpretation; they do not replace site investigation or professional judgement.</p>
  </div>{foot}</section>'''
    render(browser, page(DATASHEET, p1 + p2, 'Refraction Studio — Datasheet'), out, False, {'top': '0', 'bottom': '0', 'left': '0', 'right': '0'})


def previews(pdf, stem, pages):
    """PNG previews of the first pages, side by side, for the product page."""
    tmp = BUILD / f'{stem}-prev'
    subprocess.run(['pdftoppm', '-r', '50', '-png', '-f', '1', '-l', str(pages), str(pdf), str(tmp)], check=True)
    files = sorted(BUILD.glob(f'{stem}-prev*.png'))
    from PIL import Image
    imgs = [Image.open(f).convert('RGB') for f in files]
    gap = 14; w = sum(i.width for i in imgs) + gap * (len(imgs) + 1); h = max(i.height for i in imgs) + 2 * gap
    sheet = Image.new('RGB', (w, h), (243, 243, 244)); x = gap
    for i in imgs:
        sheet.paste(i, (x, gap)); x += i.width + gap
    sheet.save(OUT / f'{stem}-preview.png', optimize=True)
    for f in files: f.unlink()


def main():
    install_fonts()
    OUT.mkdir(parents=True, exist_ok=True)
    with sync_playwright() as p:
        browser = p.chromium.launch()
        fg = OUT / 'QuakeLogic-Refraction-Studio-Field-Guide.pdf'
        guide(browser, ROOT / 'docs/field-guide/FIELD-GUIDE.md', fg, 'FIELD GUIDE', 'Seismic Refraction<br>Field Guide',
              'SET UP · RECORD · ANALYSE', 'Equipment, line layout, recording P and SH shots, and the analysis in Refraction Studio, '
              'step by step, for your first survey.', ROOT / 'docs')
        ug = OUT / 'QuakeLogic-Refraction-Studio-User-Guide.pdf'
        guide(browser, user_guide_source(), ug, f'USER GUIDE · VERSION {VERSION}', 'Refraction Studio<br>User Guide',
              'SEISMIC REFRACTION SOFTWARE · P AND SH WAVES', 'Every page, setting and method of Refraction Studio, from import to the report.',
              ROOT / 'docs')
        ds = OUT / 'QuakeLogic-Refraction-Studio-Datasheet.pdf'
        datasheet(browser, ds)
        browser.close()
    previews(fg, 'field-guide', 4); previews(ug, 'user-guide', 4); previews(ds, 'datasheet', 2)
    for f in (fg, ug, ds):
        n = subprocess.run(['pdfinfo', str(f)], capture_output=True, text=True).stdout
        print(f.name, re.search(r'Pages:\s+(\d+)', n).group(1), 'pages', f'{f.stat().st_size / 1e6:.1f} MB')


if __name__ == '__main__':
    main()
