"""SVG diagrams for the field guide (QuakeLogic style: ink and grey linework, orange for the key marks)."""
INK, GREY, MUTE, RULE, PANEL, ORANGE = '#231F20', '#6E6F72', '#77787B', '#E3E3E5', '#F3F3F4', '#ED6A23'
FONT = "font-family='Barlow' font-size='11' fill='#58595B'"
LABEL = "font-family='Oswald' font-size='10.5' letter-spacing='1.2' fill='#58595B'"


def _svg(w, h, body):
    return f"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {w} {h}' width='{w}' height='{h}'>{body}</svg>"


def _geophone(x, y, fill=INK):
    return f"<path d='M{x - 5},{y - 9} L{x + 5},{y - 9} L{x},{y} Z' fill='{fill}'/>"


def _star(x, y, r=7, fill=ORANGE):
    import math
    pts = []
    for i in range(10):
        a = math.pi / 2 + i * math.pi / 5
        rr = r if i % 2 == 0 else r * 0.45
        pts.append(f'{x + rr * math.cos(a):.1f},{y - rr * math.sin(a):.1f}')
    return f"<polygon points='{' '.join(pts)}' fill='{fill}'/>"


def rays():
    """Cross-section with the direct and the refracted (head) wave, and the travel-time curve."""
    b = []
    b.append(f"<rect x='0' y='60' width='440' height='70' fill='{PANEL}'/>")
    b.append(f"<rect x='0' y='130' width='440' height='70' fill='#E9E9EC'/>")
    b.append(f"<line x1='0' y1='60' x2='440' y2='60' stroke='{INK}' stroke-width='1.5'/>")
    b.append(f"<line x1='0' y1='130' x2='440' y2='130' stroke='{GREY}' stroke-width='1.2' stroke-dasharray='5 4'/>")
    b.append(_star(30, 60))
    for i in range(12):
        b.append(_geophone(70 + i * 30, 60))
    b.append(f"<path d='M30,62 L130,62' stroke='{GREY}' stroke-width='2' fill='none'/>")
    b.append(f"<path d='M30,60 L95,130 L330,130 L395,60' stroke='{ORANGE}' stroke-width='2.2' fill='none'/>")
    for x in (150, 210, 270):
        b.append(f"<path d='M{x},130 L{x + 65},60' stroke='{ORANGE}' stroke-width='1' stroke-dasharray='3 3' fill='none'/>")
    b.append(f"<text x='105' y='90' {FONT}>Layer 1 · V1</text>")
    b.append(f"<text x='10' y='182' {FONT}>Layer 2 (refractor) · V2 &gt; V1</text>")
    b.append(f"<text x='150' y='148' {FONT} fill='{ORANGE}'>head wave along the refractor</text>")
    b.append(f"<text x='36' y='40' {FONT}>direct wave</text>")
    b.append(f"<text x='0' y='238' {LABEL}>CROSS-SECTION</text>")
    # Travel-time plot
    ox, oy = 500, 200
    b.append(f"<line x1='{ox}' y1='{oy}' x2='{ox + 200}' y2='{oy}' stroke='{INK}'/>")
    b.append(f"<line x1='{ox}' y1='{oy}' x2='{ox}' y2='{oy - 170}' stroke='{INK}'/>")
    b.append(f"<line x1='{ox}' y1='{oy}' x2='{ox + 95}' y2='{oy - 120}' stroke='{GREY}' stroke-width='2'/>")
    b.append(f"<line x1='{ox}' y1='{oy - 70}' x2='{ox + 195}' y2='{oy - 150}' stroke='{ORANGE}' stroke-width='2'/>")
    for i in range(1, 11):
        x = ox + i * 18
        t = min((x - ox) * 120 / 95, 70 + (x - ox) * 80 / 195)  # first arrival: the earlier wave
        b.append(f"<circle cx='{x}' cy='{oy - t:.1f}' r='3' fill='{INK}'/>")
    b.append(f"<line x1='{ox + 55}' y1='{oy}' x2='{ox + 55}' y2='{oy - 70}' stroke='{MUTE}' stroke-dasharray='3 3'/>")
    b.append(f"<text x='{ox + 18}' y='{oy + 16}' {FONT}>crossover distance</text>")
    b.append(f"<text x='{ox + 112}' y='{oy - 160}' {FONT} fill='{ORANGE}'>slope 1/V2</text>")
    b.append(f"<text x='{ox + 62}' y='{oy - 38}' {FONT}>slope 1/V1</text>")
    b.append(f"<text x='{ox - 8}' y='{oy - 175}' {FONT}>time</text>")
    b.append(f"<text x='{ox + 150}' y='{oy + 16}' {FONT}>offset</text>")
    b.append(f"<text x='{ox}' y='238' {LABEL}>FIRST-ARRIVAL TIMES</text>")
    return _svg(720, 245, ''.join(b)), 'Direct and refracted waves, and the travel-time curve they produce'


def spread():
    """The sample layout: 48 geophones at 3 m and seven shots."""
    b = []
    x0, scale, y = 40, 640 / 213, 70
    X = lambda m: x0 + (m + 36) * scale
    b.append(f"<line x1='{X(-40)}' y1='{y}' x2='{X(181)}' y2='{y}' stroke='{RULE}' stroke-width='6'/>")
    for i in range(48):
        b.append(_geophone(round(X(i * 3), 1), y))
    shots = [(-36, 'off-end'), (-1.5, 'end'), (34.5, 'interior'), (70.5, 'interior'), (106.5, 'interior'), (142.5, 'end'), (177, 'off-end')]
    for m, kind in shots:
        b.append(_star(round(X(m), 1), y + 16, 7))
        b.append(f"<text x='{X(m):.1f}' y='{y + 40}' text-anchor='middle' {FONT}>{m:g} m</text>")
        b.append(f"<text x='{X(m):.1f}' y='{y + 55}' text-anchor='middle' {FONT} fill='{ORANGE if kind != 'interior' else '#58595B'}'>{kind}</text>")
    b.append(f"<text x='{X(0):.1f}' y='{y - 18}' {FONT}>channel 1 at 0 m</text>")
    b.append(f"<text x='{X(141):.1f}' y='{y - 18}' text-anchor='end' {FONT}>channel 48 at 141 m</text>")
    b.append(f"<line x1='{X(0)}' y1='{y - 32}' x2='{X(141)}' y2='{y - 32}' stroke='{GREY}' marker-start='url(#a)' marker-end='url(#a)'/>")
    b.append(f"<text x='{X(70.5):.1f}' y='{y - 38}' text-anchor='middle' {LABEL}>SPREAD 141 M (460 FT) · 3 M (10 FT) SPACING</text>")
    defs = f"<defs><marker id='a' markerWidth='8' markerHeight='8' refX='4' refY='4' orient='auto-start-reverse'><path d='M0,1 L7,4 L0,7' fill='none' stroke='{GREY}'/></marker></defs>"
    return _svg(720, 140, defs + ''.join(b)), 'Shot layout of the sample project: two off-end, two end and three interior shots'


def orientation():
    """Plan view: vertical P geophones; horizontal SH geophones across the line, all the same way."""
    b = []
    for row, (label, y) in enumerate((('P LINE · VERTICAL GEOPHONES', 50), ('SH LINE · HORIZONTAL GEOPHONES', 150))):
        b.append(f"<text x='20' y='{y - 28}' {LABEL}>{label}</text>")
        b.append(f"<line x1='20' y1='{y}' x2='560' y2='{y}' stroke='{RULE}' stroke-width='6'/>")
        for i in range(10):
            x = 60 + i * 52
            if row == 0:
                b.append(f"<circle cx='{x}' cy='{y}' r='8' fill='#fff' stroke='{INK}' stroke-width='1.5'/><circle cx='{x}' cy='{y}' r='2.5' fill='{INK}'/>")
            else:
                b.append(f"<rect x='{x - 7}' y='{y - 7}' width='14' height='14' fill='#fff' stroke='{INK}' stroke-width='1.5'/>")
                b.append(f"<line x1='{x}' y1='{y + 22}' x2='{x}' y2='{y - 22}' stroke='{ORANGE}' stroke-width='2' marker-end='url(#o)'/>")
    b.append(f"<text x='575' y='54' {FONT}>line direction →</text>")
    b.append(f"<text x='575' y='140' {FONT}>sensitive axis</text>")
    b.append(f"<text x='575' y='155' {FONT}>across the line,</text>")
    b.append(f"<text x='575' y='170' {FONT}>all the same way</text>")
    defs = f"<defs><marker id='o' markerWidth='8' markerHeight='8' refX='4' refY='4' orient='auto'><path d='M0,1 L7,4 L0,7 Z' fill='{ORANGE}'/></marker></defs>"
    return _svg(720, 195, defs + ''.join(b)), 'Plan view: geophone orientation on P and SH lines'


def shbeam():
    """Shear beam across the line, struck from both ends; the L and R records have opposite polarity."""
    b = []
    b.append(f"<line x1='20' y1='110' x2='420' y2='110' stroke='{RULE}' stroke-width='6'/>")
    b.append(f"<text x='330' y='128' {FONT}>geophone line</text>")
    b.append(f"<rect x='200' y='30' width='26' height='160' rx='3' fill='#C9B79C' stroke='{INK}'/>")
    b.append(f"<rect x='196' y='26' width='34' height='8' fill='{GREY}'/><rect x='196' y='186' width='34' height='8' fill='{GREY}'/>")
    b.append(f"<rect x='180' y='88' width='66' height='44' rx='10' fill='none' stroke='{INK}' stroke-dasharray='4 3'/>")
    b.append(f"<text x='254' y='104' {FONT}>vehicle wheel</text><text x='254' y='118' {FONT}>or weights</text>")
    b.append(f"<line x1='213' y1='-2' x2='213' y2='20' stroke='{ORANGE}' stroke-width='3' marker-end='url(#h)'/>")
    b.append(f"<line x1='213' y1='222' x2='213' y2='200' stroke='{ORANGE}' stroke-width='3' marker-end='url(#h)'/>")
    b.append(f"<text x='226' y='14' {FONT} fill='{ORANGE}'>L: hit this end</text>")
    b.append(f"<text x='226' y='216' {FONT} fill='{ORANGE}'>R: hit the other end</text>")
    b.append(f"<text x='20' y='60' {FONT}>beam across the line,</text><text x='20' y='74' {FONT}>at the shot mark</text>")
    # Wiggles
    import math
    def wig(x, y, sign, colour):
        pts = ' '.join(f'{x + t * 2.2:.1f},{y - sign * 16 * math.exp(-((t - 14) / 6) ** 2) * math.sin((t - 8) / 2.2):.1f}' for t in range(60))
        return f"<polyline points='{pts}' fill='none' stroke='{colour}' stroke-width='1.6'/>"
    b.append(f"<text x='480' y='40' {LABEL}>SAME GEOPHONE</text>")
    b.append(wig(480, 80, 1, INK)); b.append(f"<text x='620' y='84' {FONT}>L record</text>")
    b.append(wig(480, 130, -1, INK)); b.append(f"<text x='620' y='134' {FONT}>R record</text>")
    b.append(wig(480, 180, 1, ORANGE)); b.append(f"<text x='620' y='184' {FONT} fill='{ORANGE}'>(L − R)/2</text>")
    defs = f"<defs><marker id='h' markerWidth='8' markerHeight='8' refX='4' refY='4' orient='auto'><path d='M0,1 L7,4 L0,7 Z' fill='{ORANGE}'/></marker></defs>"
    return _svg(720, 228, defs + ''.join(b)), 'SH source: a loaded shear beam struck from both ends gives records of opposite polarity'


DIAGRAMS = {'rays': rays, 'spread': spread, 'orientation': orientation, 'shbeam': shbeam}
