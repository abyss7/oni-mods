#!/usr/bin/env python3
"""Draws the mixtures overlay icon (src/Common/overlay_mixtures.png) in the style of the game's overlay icons: a ball of red gas
with curls above grey liquid with a wave and bubbles, a thick uneven black outline, shading at the lower left; the colours are
those of the game's icons.

  python3 scripts/draw_overlay_icon.py src/Common/overlay_mixtures.png
"""
import sys
import math, random
from PIL import Image, ImageDraw, ImageChops, ImageFilter

S = 4                      # supersampling
N = 256 * S
RED, RED_SH, RED_DK = (240, 72, 68), (168, 48, 48), (112, 32, 32)
GRAY, GRAY_SH = (194, 202, 226), (136, 144, 160)
INK = (8, 6, 8)
OUT = 13 * S               # outline width

def wobble(points, amp=2.2, seed=1):
    rnd = random.Random(seed)
    ph = [rnd.random() * 6.28 for _ in range(6)]
    res = []
    n = len(points)
    for k, (x, y) in enumerate(points):
        t = k / n * 6.283
        d = amp * S * (math.sin(3 * t + ph[0]) + 0.6 * math.sin(7 * t + ph[1]) + 0.3 * math.sin(13 * t + ph[2]))
        e = amp * S * (math.cos(4 * t + ph[3]) + 0.5 * math.sin(9 * t + ph[4]))
        res.append((x + d, y + e))
    return res

def circle(cx, cy, r, n=90):
    return [(cx * S + r * S * math.cos(a), cy * S + r * S * math.sin(a)) for a in (i / n * 6.283 for i in range(n))]

def drop(cx, cy, r, n=120):
    pts = []
    for i in range(n):
        a = i / n * 6.283
        # teardrop: circle bottom, pointed top
        x = math.sin(a) * r * (1 - 0.0) * (0.5 + 0.5 * math.cos(a / 2) ** 0) 
        x = r * math.sin(a) * (math.sin(a / 2) ** 1.6 if True else 1)
        y = -r * math.cos(a)
        pts.append(((cx + x * 1.0) * S, (cy + y * 1.25) * S))
    return pts

def mask_of(polys):
    m = Image.new("L", (N, N), 0)
    d = ImageDraw.Draw(m)
    for p in polys:
        d.polygon(p, fill=255)
    return m

def paint(img, shape_mask, fill, shade, light_dir=(-1, -1), shade_px=22):
    """Fill a shape with its colour and a shaded crescent away from the light, then outline it."""
    solid = Image.new("RGBA", (N, N), fill + (255,))
    img.paste(solid, (0, 0), shape_mask)
    shifted = ImageChops.offset(shape_mask, light_dir[0] * shade_px * S // 2, light_dir[1] * shade_px * S // 2)
    crescent = ImageChops.subtract(shape_mask, shifted)
    img.paste(Image.new("RGBA", (N, N), shade + (255,)), (0, 0), crescent)

def outline(img, shape_mask, width=OUT):
    grown = shape_mask.filter(ImageFilter.MaxFilter(width // 2 * 2 + 1))
    ring = ImageChops.subtract(grown, shape_mask)
    img.paste(Image.new("RGBA", (N, N), INK + (255,)), (0, 0), ring)

def layer(img, polys, fill, shade, light=(-1, -1), shade_px=22, width=OUT):
    """Draw a part over what is already there: black outline around it first, then the part itself."""
    m = mask_of(polys)
    grown = m.filter(ImageFilter.MaxFilter(width // 2 * 2 + 1))
    img.paste(Image.new("RGBA", (N, N), INK + (255,)), (0, 0), grown)
    paint(img, m, fill, shade, light, shade_px)
    return m

def stroke(img, pts, width, colour=INK):
    d = ImageDraw.Draw(img)
    d.line(pts, fill=colour + (255,), width=width * S, joint="curve")

def arc_pts(cx, cy, r, a0, a1, n=30):
    return [((cx + r * math.cos(a)) * S, (cy + r * math.sin(a)) * S) for a in (a0 + (a1 - a0) * i / n for i in range(n + 1))]

def cloud(cx, cy, scale, seed):
    blobs = [(-34, 10, 30), (-6, -14, 36), (30, 4, 30), (8, 22, 30), (-22, 26, 22), (34, 28, 20)]
    return [wobble(circle(cx + x * scale, cy + y * scale, r * scale), 1.2, seed + i) for i, (x, y, r) in enumerate(blobs)]

def swirls(img, cx, cy, scale):
    for (x, y, r, a0, a1) in [(-12, -10, 12, 0.5, 4.2), (20, 10, 10, 3.6, 7.0), (-26, 22, 8, 4.0, 7.5)]:
        stroke(img, arc_pts(cx + x * scale, cy + y * scale, r * scale, a0, a1), 5, RED_DK)

def finish(img, name):
    small = img.resize((256, 256), Image.LANCZOS)
    small.save(name)
    return small

def new():
    return Image.new("RGBA", (N, N), (0, 0, 0, 0))

# 3. Gas over liquid in a circle: red curly gas above, grey liquid with a wave and bubbles below.
img = new()
disc = wobble(circle(128, 128, 104), 1.2, 11)
dm = layer(img, [disc], RED, RED_SH, (1, -1), 18)
swirls(img, 120, 80, 1.3)
wave = [(0, 140)] + [(x, 140 + 14 * math.sin(x / 26)) for x in range(0, 257, 4)] + [(256, 256), (0, 256)]
wm = ImageChops.multiply(mask_of([[(x * S, y * S) for x, y in wave]]), dm)
img.paste(Image.new("RGBA", (N, N), GRAY + (255,)), (0, 0), wm)
img.paste(Image.new("RGBA", (N, N), GRAY_SH + (255,)), (0, 0), ImageChops.subtract(wm, ImageChops.offset(wm, 12 * S, -10 * S)))
stroke(img, [(x * S, (140 + 14 * math.sin(x / 26)) * S) for x in range(24, 236, 4)], 7)
for (x, y, r) in [(96, 190, 12), (150, 176, 8), (172, 208, 9)]:
    layer(img, [wobble(circle(x, y, r), 0.5, x)], (226, 232, 246), GRAY, (1, -1), 4, 8 * S)
outline(img, dm)
finish(img, sys.argv[1] if len(sys.argv) > 1 else "overlay_mixtures.png")

