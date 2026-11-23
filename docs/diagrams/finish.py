#!/usr/bin/env python3
"""Finishes a Graphviz SVG: hard offset shadows under nodes, a dotted paper background, font fallbacks.
Usage: finish.py file.svg   (run by render.sh inside the container)"""
import re
import sys

INK = '#12183a'
SANS = "Inter,'Bricolage Grotesque','Segoe UI',system-ui,Helvetica,Arial,sans-serif"
MONO = "'IBM Plex Mono','DejaVu Sans Mono',ui-monospace,Menlo,monospace"

path = sys.argv[1]
svg = open(path, encoding='utf-8').read()

svg = svg.replace('font-family="Inter"', f'font-family="{SANS}"').replace('font-family="DejaVu Sans Mono"', f'font-family="{MONO}"')

# Hard shadow: a copy of each node's outline, shifted down and right, drawn first.
def shadow(match):
    group = match.group(0)
    shape = re.search(r'<(polygon|path|ellipse)\b[^>]*?/>', group)
    if not shape:
        return group
    s = shape.group(0)
    s = re.sub(r'\b(fill|stroke)="[^"]*"', '', s)
    s = re.sub(r'stroke-width="[^"]*"', '', s)
    s = re.sub(r'stroke-dasharray="[^"]*"', '', s)
    s = s.replace('/>', f' fill="{INK}" stroke="{INK}" stroke-width="1.8" transform="translate(3.5,3.5)"/>', 1)
    return group.replace(shape.group(0), s + shape.group(0), 1)

svg = re.sub(r'<g id="node\d+" class="node">.*?</g>', shadow, svg, flags=re.S)

# Dotted paper: lay a dot pattern over the page background.
bg = re.search(r'<polygon fill="#f6f3ea" stroke="(?:transparent|none)" points="[^"]*"/>', svg)
if bg:
    dots = bg.group(0).replace('fill="#f6f3ea"', 'fill="url(#dots)"')
    pattern = (f'<defs><pattern id="dots" width="22" height="22" patternUnits="userSpaceOnUse">'
               f'<circle cx="1.5" cy="1.5" r="1.1" fill="{INK}" fill-opacity=".14"/></pattern></defs>')
    svg = svg.replace(bg.group(0), pattern + bg.group(0) + dots, 1)

open(path, 'w', encoding='utf-8').write(svg)
