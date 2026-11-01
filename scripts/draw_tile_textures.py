#!/usr/bin/env python3
"""Draws the fill of two opaque permeable tiles in the style of the game's tiles and puts it into the frame of the game's
Mesh Tile atlas:

  light trap  - lets gas through, stops light: stacked zigzag blades as in the light-tight vents of photographic
                darkrooms; air winds between the blades, a straight ray cannot;
  ceramic     - lets liquid through, stops light and gas: a soaked porous ceramic plate (a tensiometer cup, a ceramic
                water filter); water creeps through the pores, light is scattered by the grains, and air cannot push
                through the wet pores (the bubble point).

The frame and the layout come from the game's atlases, which are not in the repository: pass the folder with the game's
textures tiles_mesh.png, tiles_gasmembrane.png, tiles_mesh_place.png and tiles_mesh_spec.png extracted from
sharedassets0.assets. The fill is periodic with one cell, so neighbouring tiles join without seams.

  python3 scripts/draw_tile_textures.py <game textures> <output folder>

Writes tiles_<name>.png (colour), tiles_<name>_place.png (blueprint), tiles_<name>_spec.png (shine) for both tiles, and
preview.png: both tiles next to the game's Airflow and Mesh tiles, laid out by the game's rules.
"""
import math, os, random, sys
from PIL import Image, ImageChops, ImageDraw, ImageFilter

S = 4                 # supersampling of the fill
P = 128               # one cell in atlas pixels; the fill repeats with it
ITEM, PITCH, ORG = 192, 208, 8   # atlas items: 192 px (a cell + 32 px on each side), 16 of them on a 208 px grid
TRIM = 32
INK = (10, 8, 10, 255)


def wrap(draw_fn, background):
    """Draws a periodic pattern: the drawing is repeated around the cell and the middle cell is cut out. It goes in
    passes (what lies below first, for all the copies), so that a shape reaching into the next cell stays whole."""
    layers = {"col": Image.new("RGBA", (3 * P * S,) * 2, background), "place": Image.new("L", (3 * P * S,) * 2, 0),
              "spec": Image.new("L", (3 * P * S,) * 2, 0)}
    draws = {k: ImageDraw.Draw(v) for k, v in layers.items()}
    for phase in range(3):
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                draw_fn(draws, lambda x, y, ox=(P + dx * P) * S, oy=(P + dy * P) * S: (ox + x * S, oy + y * S), phase)
    box = (P * S, P * S, 2 * P * S, 2 * P * S)
    return {k: v.crop(box).resize((P, P), Image.LANCZOS) for k, v in layers.items()}


def wob(x, y, amp=1.0):
    """A small hand-drawn wobble, periodic with the cell."""
    t = 2 * math.pi / P
    return amp * (math.sin(3 * t * x + 1.3) * 0.6 + math.sin(5 * t * y + 0.4) * 0.4 + math.sin(7 * t * (x + y)) * 0.3)


# ---- light trap: zigzag blades ---------------------------------------------------------------------------------------
BLADE_HI, BLADE, BLADE_SH, BLADE_DK = (240, 214, 120), (208, 184, 99), (160, 136, 66), (128, 106, 50)
CHANNEL, CHANNEL_SH = (46, 40, 36), (26, 22, 20)
ROWS, THICK, AMP = 3, 21, 16       # three blades a cell, their thickness and the height of the bend; one chevron a cell


def blades():
    for r in range(ROWS):
        yc = r * P / ROWS + P / ROWS / 2
        yield yc, [(x, yc + h * AMP / 2 + wob(x, yc, 0.7)) for x, h in ((0, -1), (P / 2, 1), (P, -1))]


def light_trap(L, T, phase):
    for yc, mid in blades():
        for i in range(2):
            (x0, y0), (x1, y1) = mid[i], mid[i + 1]
            rising = y1 < y0             # the face turned up to the light is brighter
            band = lambda d0, d1: [T(x0, y0 + d0), T(x1, y1 + d0), T(x1, y1 + d1), T(x0, y0 + d1)]
            if phase == 0:               # the shadow a blade drops into the channel below it
                L["col"].polygon(band(THICK / 2, THICK / 2 + 6), fill=CHANNEL_SH)
            elif phase == 1:
                L["col"].polygon(band(-THICK / 2, THICK / 2), fill=BLADE if rising else BLADE_SH)
                L["col"].polygon(band(-THICK / 2 + 2, -THICK / 2 + 7), fill=BLADE_HI if rising else BLADE)   # rolled edge
                L["col"].polygon(band(THICK / 2 - 5, THICK / 2), fill=BLADE_SH if rising else BLADE_DK)
                if rising:
                    L["spec"].line([T(x0 + 4, y0 - THICK / 2 + 4.5), T(x1 - 4, y1 - THICK / 2 + 4.5)], fill=210, width=3 * S)
        if phase == 2:
            for edge in (-1, 1):
                line = [T(x, y + edge * THICK / 2) for x, y in mid]
                L["col"].line(line, fill=INK, width=int(4.4 * S), joint="curve")
                L["place"].line(line, fill=255, width=int(3.4 * S), joint="curve")
            x, y = mid[1]                # the fold of the bend and the rivets holding the blade to the frame behind
            L["col"].line([T(x, y - THICK / 2 + 2), T(x, y + THICK / 2 - 2)], fill=BLADE_DK, width=int(1.6 * S))
            for xr in (P / 4, 3 * P / 4):
                yr = yc + wob(xr, yc, 0.7)
                L["col"].ellipse([T(xr - 2.6, yr - 2.6), T(xr + 2.6, yr + 2.6)], fill=INK)
                L["col"].ellipse([T(xr - 1.6, yr - 2.0), T(xr + 0.8, yr + 0.4)], fill=BLADE_HI)


# ---- ceramic: soaked porous grains -----------------------------------------------------------------------------------
GRAIN_HI, GRAIN, GRAIN_SH = (246, 210, 168), (224, 166, 118), (178, 116, 80)
WATER, WATER_DK, WATER_HI = (58, 104, 138), (34, 64, 92), (160, 212, 236)


def torus(x, y, a, b):
    dx, dy = abs(x - a) % P, abs(y - b) % P
    return math.hypot(min(dx, P - dx), min(dy, P - dy))


def grains():
    rnd = random.Random(7)
    pts = []
    for lo, hi, tries in ((14, 20, 6000), (8, 12, 6000), (5, 7, 4000)):    # big grains, then smaller ones in the gaps
        for _ in range(tries):
            r = rnd.uniform(lo, hi)
            x, y = rnd.uniform(0, P), rnd.uniform(0, P)
            if all(torus(x, y, a, b) >= r + c - 1.0 for a, b, c in pts):
                pts.append((x, y, r))
    return pts


GRAINS = grains()
rnd_ = random.Random(11)
POOLS = [(rnd_.uniform(0, P), rnd_.uniform(0, P)) for _ in range(26)]       # deeper water in the narrow pores
GLINTS = [(x, y) for x, y in ((rnd_.uniform(0, P), rnd_.uniform(0, P)) for _ in range(40))
          if all(torus(x, y, a, b) > c + 3 for a, b, c in GRAINS)][:14]
SHEEN = [rnd_.uniform(-0.3, 0.3) for _ in GRAINS]


def blob(cx, cy, r, seed, k=28):
    rnd = random.Random(seed)
    ph = [rnd.random() * 6.28 for _ in range(3)]
    pts = []
    for i in range(k):
        t = i / k * 2 * math.pi
        rr = r * (1 + 0.07 * math.sin(2 * t + ph[0]) + 0.05 * math.sin(3 * t + ph[1]) + 0.03 * math.sin(5 * t + ph[2]))
        pts.append((cx + rr * math.cos(t), cy + rr * math.sin(t) * 0.92))
    return pts


def ceramic(L, T, phase):
    if phase == 0:
        for x, y in POOLS:
            L["col"].ellipse([T(x - 6, y - 4), T(x + 6, y + 4)], fill=WATER_DK)
    elif phase == 1:
        for i, (x, y, r) in enumerate(GRAINS):
            g = lambda dx, dy, rr: [T(px + dx, py + dy) for px, py in blob(x, y, rr, i)]
            L["col"].polygon(g(0, 0, r + 2.2), fill=INK)
            L["place"].polygon(g(0, 0, r + 1.4), fill=255)
            L["place"].polygon(g(0, 0, r - 1.2), fill=0)
            L["col"].polygon(g(0, 0, r - 2.0), fill=GRAIN_SH)
            L["col"].polygon(g(1.3, -1.5, max(r - 4.6, r * 0.55)), fill=GRAIN)   # shading at the lower left
            if r > 7:
                L["col"].polygon(g(2.4, -2.6, r * 0.38), fill=GRAIN_HI)
            if r < 9:
                continue
            # a wet sheen on the grain's upper right
            arc = [T(x + (r - 4) * math.cos(t), y - (r - 4) * math.sin(t)) for t in [SHEEN[i] + 0.35 + 0.1 * k for k in range(7)]]
            L["col"].line(arc, fill=(255, 238, 214), width=int(1.8 * S), joint="curve")
            L["spec"].line(arc, fill=230, width=int(2.2 * S), joint="curve")
    else:
        for x, y in GLINTS:              # water glinting between the grains
            L["col"].ellipse([T(x - 1.6, y - 1.1), T(x + 1.6, y + 1.1)], fill=WATER_HI)
            L["spec"].ellipse([T(x - 1.8, y - 1.3), T(x + 1.8, y + 1.3)], fill=255)


TILES = {"lighttrap": (light_trap, CHANNEL), "ceramic": (ceramic, WATER)}


# ---- atlas -----------------------------------------------------------------------------------------------------------
def items():
    """Item k of the game's 16 (required connections Up = 8, Left = 4, Right = 2, Down = 1 in k) and its box."""
    for k in range(16):
        x, y = ORG + PITCH * (k % 4), ORG + PITCH * (k // 4)
        yield k, (x, y, x + ITEM, y + ITEM)


def fill_mask(mesh, gas):
    """The fill of the game's tiles: where the Mesh and Airflow tiles differ (their frames are the same). Places where both
    happen to have ink or a hole are swallowed, and the frame's own inner ink line is kept."""
    def diff(a, b):
        return ImageChops.difference(a, b).convert("L").point(lambda v: 255 if v > 5 else 0)
    def flat(img):   # colour over a fixed backdrop, so that a transparent hole differs from an opaque plate
        return Image.alpha_composite(Image.new("RGBA", img.size, (255, 0, 255, 255)), img).convert("RGB")
    d = diff(flat(mesh), flat(gas))
    d = d.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))     # stray pixels out
    d = d.filter(ImageFilter.MaxFilter(15)).filter(ImageFilter.MinFilter(15))   # where both have ink or a hole: in
    alpha = mesh.getchannel("A").point(lambda v: 255 if v > 0 else 0)
    frame = ImageChops.multiply(alpha, ImageChops.invert(d))
    d = ImageChops.multiply(d, ImageChops.invert(frame.filter(ImageFilter.MaxFilter(3))))
    return d.filter(ImageFilter.GaussianBlur(0.6))


def build(src, out):
    L = lambda n: Image.open(os.path.join(src, n + ".png")).convert("RGBA")
    mesh, gas, place, spec = L("tiles_mesh"), L("tiles_gasmembrane"), L("tiles_mesh_place"), L("tiles_mesh_spec")
    mask = fill_mask(mesh, gas)
    mask.save(os.path.join(out, "mask.png"))
    made = {}
    for name, (fn, background) in TILES.items():
        tile = wrap(fn, background)
        # one item: the cell's fill from 32 px in, repeated over the overhangs
        piece = {}
        for key, mode in (("col", "RGBA"), ("place", "L"), ("spec", "L")):
            piece[key] = Image.new(mode, (ITEM, ITEM))
            for ty in range(-1, 2):
                for tx in range(-1, 2):
                    piece[key].paste(tile[key], (TRIM + tx * P, TRIM + ty * P))
        white = Image.new("L", (ITEM, ITEM), 255)
        item_place = Image.merge("RGBA", (white, white, white, piece["place"]))
        item_spec = Image.merge("RGBA", (piece["spec"],) * 4)
        col_fill = Image.new("RGBA", mesh.size, (0, 0, 0, 0))
        place_fill = Image.new("RGBA", mesh.size, (255, 255, 255, 0))
        spec_fill = Image.new("RGBA", mesh.size, (0, 0, 0, 0))
        for k, (x0, y0, x1, y1) in items():
            col_fill.paste(piece["col"], (x0, y0))
            place_fill.paste(item_place, (x0, y0))
            spec_fill.paste(item_spec, (x0, y0))
        col = Image.composite(col_fill, mesh, mask)
        pl = Image.composite(place_fill, place, mask)
        sp = Image.composite(spec_fill, spec, mask)
        col.save(os.path.join(out, f"tiles_{name}.png"))
        pl.save(os.path.join(out, f"tiles_{name}_place.png"))
        sp.save(os.path.join(out, f"tiles_{name}_spec.png"))
        made[name] = col
    preview(out, [("Airflow Tile", gas), ("light trap", made["lighttrap"]), ("Mesh Tile", mesh), ("ceramic", made["ceramic"])])


# ---- preview: tiles laid out by the game's rules ---------------------------------------------------------------------
SHAPE = [
    "#######",
    "#.....#",
    "#.###.#",
    "#.#...#",
    "#.#.###",
    "...#...",
]


def preview(out, atlases):
    rows, cols = len(SHAPE), len(SHAPE[0])
    W, H = (cols + 1) * P, (rows + 1) * P
    sheet = Image.new("RGBA", (W * len(atlases), H), (64, 62, 82, 255))
    on = lambda x, y: 0 <= y < rows and 0 <= x < cols and SHAPE[y][x] == "#"
    for n, (_, atlas) in enumerate(atlases):
        layer = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        for y in range(rows):
            for x in range(cols):
                if not on(x, y):
                    continue
                up, left, right, down = on(x, y - 1), on(x - 1, y), on(x + 1, y), on(x, y + 1)
                k = up * 8 + left * 4 + right * 2 + down
                bx, by = ORG + PITCH * (k % 4), ORG + PITCH * (k // 4)
                l, t = (TRIM if left else 0), (TRIM if up else 0)
                r, b = (ITEM - TRIM if right else ITEM), (ITEM - TRIM if down else ITEM)
                piece = atlas.crop((bx + l, by + t, bx + r, by + b))
                layer.alpha_composite(piece, (P // 2 + x * P + l - TRIM, P // 2 + y * P + t - TRIM))
        sheet.alpha_composite(layer, (n * W, 0))
    sheet.resize((sheet.width * 3 // 8, sheet.height * 3 // 8), Image.LANCZOS).save(os.path.join(out, "preview.png"))
    # a close look at one cell of each
    close = Image.new("RGBA", (len(atlases) * 300, 300), (64, 62, 82, 255))
    for n, (_, atlas) in enumerate(atlases):
        close.alpha_composite(atlas.crop((ORG, ORG, ORG + ITEM, ORG + ITEM)).resize((288, 288), Image.LANCZOS), (n * 300 + 6, 6))
    close.save(os.path.join(out, "preview_close.png"))


if __name__ == "__main__":
    os.makedirs(sys.argv[2], exist_ok=True)
    build(sys.argv[1], sys.argv[2])
