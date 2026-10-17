# Pictures of the light from light_model.cpp: the game's light and the new one over the same scene.
#   python3 sim/model/light_model.py out_dir   (reads out_dir/light_piece.txt, writes out_dir/light_compare.png)
import math
import sys

from PIL import Image, ImageDraw

out = sys.argv[1] if len(sys.argv) > 1 else "."
lines = open(f"{out}/light_piece.txt").read().split("\n")
w, h = map(int, lines[0].split())
cells = [line.split() for line in lines[1:1 + w * h]]
sources = [tuple(map(int, line.split()[1:])) for line in lines[1 + w * h:] if line.startswith("source")]

SIZE = 8
GAS, LIQUID = (210, 214, 220), {"~": (90, 140, 210), "%": (120, 130, 80)}
SOLID = {"S": (205, 170, 115), "A": (70, 66, 92), "W": (235, 240, 246), "G": (140, 132, 126)}


def shade(char, lux):
    # Brightness by the log of lux: 0 lux is a faint trace of the map, 2000 lux and more is the full colour.
    base = SOLID.get(char) or LIQUID.get(char) or GAS
    v = 0.16 if lux < 1 else min(1.0, 0.30 + 0.70 * math.log1p(lux) / math.log1p(2000))
    return tuple(int(c * v) for c in base)


def panel(column, title):
    img = Image.new("RGB", (w * SIZE, h * SIZE + 18), (24, 24, 28))
    draw = ImageDraw.Draw(img)
    draw.text((4, 3), title, fill=(230, 230, 230))
    for i, c in enumerate(cells):
        x, y = i % w, i // w
        draw.rectangle([x * SIZE, 18 + y * SIZE, x * SIZE + SIZE - 1, 18 + y * SIZE + SIZE - 1], fill=shade(c[1], float(c[column])))
    for sx, sy in sources:
        cx, cy = sx * SIZE + SIZE // 2, 18 + sy * SIZE + SIZE // 2
        draw.ellipse([cx - 3, cy - 3, cx + 3, cy + 3], outline=(255, 200, 40), width=2)
    return img


panels = [
    panel(2, "game: shadow casting, lux / round(steps / 2), cut at the radius; liquid like air; walls unlit"),
    panel(3, "new, direct: rays, liquids by element, lux / (r / 2), soft fade to 1.5 R, lit walls"),
    panel(4, "new, with reflected light: sandstone | abyssalite / snow | granite without a back wall"),
]
img = Image.new("RGB", (panels[0].width, (panels[0].height + 6) * len(panels)), (24, 24, 28))
for k, p in enumerate(panels):
    img.paste(p, (0, k * (p.height + 6)))
img.save(f"{out}/light_compare.png")
