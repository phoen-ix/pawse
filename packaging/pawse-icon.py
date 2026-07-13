#!/usr/bin/env python3
"""Generates packaging/pawse.ico - an ORIGINAL paw-print icon for the Pawse
installer and Start Menu shortcut. Not derived from any vendor emoji font.
Requires Pillow (`pip install pillow`). Run: python3 pawse-icon.py
"""
import os
from PIL import Image, ImageDraw

TOES = [(-46, -26, 13, 17), (-16, -47, 14, 19), (16, -47, 14, 19), (46, -26, 13, 17)]
HEEL = (0, 34, 46, 37)
BG = ('#6D5AE6', '#8B6CF0')   # indigo -> violet squircle
PAW = '#FFF7ED'               # cream paw

def _hex(h): h = h.lstrip('#'); return tuple(int(h[i:i+2], 16) for i in (0, 2, 4))

def render(out, ss=4):
    S = out * ss; K = S / 256.0
    grad = Image.new('RGB', (S, S)); gd = ImageDraw.Draw(grad)
    c0, c1 = _hex(BG[0]), _hex(BG[1])
    for y in range(S):
        t = y / (S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(int(c0[i] + (c1[i] - c0[i]) * t) for i in range(3)))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([12 * K, 12 * K, 244 * K, 244 * K], radius=52 * K, fill=255)
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0)); img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    cx, cy, s = 128, 128, 1.08
    for dx, dy, rx, ry in TOES + [HEEL]:
        d.ellipse([(cx + (dx - rx) * s) * K, (cy + (dy - ry) * s) * K,
                   (cx + (dx + rx) * s) * K, (cy + (dy + ry) * s) * K], fill=PAW)
    return img.resize((out, out), Image.LANCZOS)

here = os.path.dirname(os.path.abspath(__file__))
sizes = [256, 128, 64, 48, 32, 24, 16]
imgs = [render(s) for s in sizes]
ico = os.path.join(here, 'pawse.ico')
try:
    imgs[0].save(ico, format='ICO', sizes=[(s, s) for s in sizes], append_images=imgs[1:])
except TypeError:
    imgs[0].save(ico, format='ICO', sizes=[(s, s) for s in sizes])
render(256).save(os.path.join(here, 'pawse.png'))
print('wrote', ico)
