"""Draw one floor of a spec's layout as a PNG, for checking the engine by eye.

    .venv\\Scripts\\python scripts\\render_floor.py <spec.json> <out.png> [--level 1]
"""

from __future__ import annotations

import argparse
import json
import sys

from PIL import Image, ImageDraw

from b2m.engine import catalog
from b2m.engine.floors import build_floor
from b2m.engine.site import envelope
from b2m.engine.templates import plan_plate
from b2m.engine import generate
from b2m.spec import BuildingSpec

COLOURS = {"living": (255, 236, 179), "kitchen": (200, 230, 201), "toilet": (179, 229, 252), "bedroom": (248, 187, 208),
           "bedroom_master": (244, 143, 177), "lobby": (224, 224, 224), "lift": (158, 158, 158)}
SCALE = 0.04  # px per mm
PAD = 60


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("spec")
    ap.add_argument("out")
    ap.add_argument("--level", type=int, default=1)
    args = ap.parse_args()
    spec = BuildingSpec.model_validate(json.load(open(args.spec, encoding="utf-8")))
    result = generate(spec)
    plate = plan_plate(spec, envelope(spec), result.report["variant"] - 1)
    floor = build_floor(plate, args.level)
    env = envelope(spec)
    plot_w, plot_d = spec.site.plot_mm

    def pt(x: float, y: float) -> tuple[float, float]:
        return PAD + x * SCALE, PAD + (plot_d - y) * SCALE  # road at the bottom

    img = Image.new("RGB", (int(plot_w * SCALE + 2 * PAD), int(plot_d * SCALE + 2 * PAD)), "white")
    d = ImageDraw.Draw(img)
    d.rectangle([pt(0, plot_d), pt(plot_w, 0)], outline=(0, 128, 0), width=2)
    d.rectangle([pt(env.x0, env.y1), pt(env.x1, env.y0)], outline=(0, 200, 0))
    for s in floor.spaces:
        for r in s.cells:
            d.rectangle([pt(r.x0, r.y1), pt(r.x1, r.y0)], fill=COLOURS.get(s.kind, (255, 255, 255)))
        cx, cy = s.cells[s.anchor].center
        d.text(pt(cx, cy), s.name.replace("Living/Dining", "Living"), fill="black", anchor="mm")
    for w in floor.walls:
        width = max(1, int(w.thickness * SCALE))
        d.line([pt(*w.start), pt(*w.end)], fill="black", width=width)
    for o in floor.doors + floor.windows:
        w = floor.walls[o.wall]
        half = o.type.width / 2
        a = (w.c, o.center - half) if w.axis == "x" else (o.center - half, w.c)
        b = (w.c, o.center + half) if w.axis == "x" else (o.center + half, w.c)
        d.line([pt(*a), pt(*b)], fill=(220, 0, 0) if o.kind == "door" else (0, 90, 255), width=max(3, int(260 * SCALE)))
    d.text((PAD, 10), f"{result.report['template']} variant {result.report['variant']} level {args.level} - road at bottom",
           fill="black")
    img.save(args.out)
    print(f"{args.out}: {len(floor.walls)} walls, {len(floor.doors)} doors, {len(floor.windows)} windows")
    return 0


if __name__ == "__main__":
    sys.exit(main())
