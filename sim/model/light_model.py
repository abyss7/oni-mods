# Pictures of the light from light_model.cpp: the game's light and the new one over the same piece of a base.
#   python3 sim/model/light_model.py out_dir   (reads out_dir/light_piece.txt, writes out_dir/light_compare.png)
import math
import sys

from PIL import Image, ImageDraw

out = sys.argv[1] if len(sys.argv) > 1 else "."
lines = open(f"{out}/light_piece.txt").read().split("\n")
w, h = map(int, lines[0].split())
cells = [line.split() for line in lines[1:1 + w * h]]
sources = [tuple(map(int, line.split()[1:])) for line in lines[1 + w * h:] if line.startswith("source")]

SIZE = 9
BASE = {0: (92, 70, 54), 1: (210, 214, 220), 2: (90, 140, 210), 3: (150, 220, 230)}  # rock, gas, liquid, window


def shade(kind, lux):
    # Brightness by the log of lux: 0 lux is a faint trace of the map, 2000 lux and more is the full colour.
    v = 0.16 if lux <= 0 else min(1.0, 0.30 + 0.70 * math.log1p(lux) / math.log1p(2000))
    return tuple(int(c * v) for c in BASE[kind])


def panel(column, title):
    img = Image.new("RGB", (w * SIZE, h * SIZE + 18), (24, 24, 28))
    draw = ImageDraw.Draw(img)
    draw.text((4, 3), title, fill=(230, 230, 230))
    for i, c in enumerate(cells):
        x, y = i % w, i // w
        draw.rectangle([x * SIZE, 18 + y * SIZE, x * SIZE + SIZE - 1, 18 + y * SIZE + SIZE - 1], fill=shade(int(c[0]), float(c[column])))
    for sx, sy in sources:
        cx, cy = sx * SIZE + SIZE // 2, 18 + sy * SIZE + SIZE // 2
        draw.ellipse([cx - 3, cy - 3, cx + 3, cy + 3], outline=(255, 200, 40), width=2)
    return img


game = panel(1, "game: shadow casting, lux / round(steps / 2), cut at the radius; solid cells unlit")
direct = panel(2, "new, direct: rays with transmission, lux / (r / 2), soft fade to 1.5 R, lit walls")
total = panel(3, "new, with scattered light: round corners, into shade, along corridors")
img = Image.new("RGB", (game.width, game.height * 3 + 12), (24, 24, 28))
for k, p in enumerate((game, direct, total)):
    img.paste(p, (0, k * (game.height + 6)))
img.save(f"{out}/light_compare.png")
