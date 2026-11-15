"""Hand-written build plans used by tests and gate scripts."""

from __future__ import annotations

from b2m.plan import BuildPlan


def two_storey_house(door_type: str, window_type: str, origin: tuple[float, float] = (0, 0), prefix: str = "TH") -> BuildPlan:
    """A two-storey 8 x 6 m house: two rooms per floor, a stair shaft through the first floor slab,
    three doors and three windows. ``origin`` shifts it clear of anything already in the project."""
    ox, oy = origin

    def p(x: float, y: float) -> tuple[float, float]:
        return (ox + x, oy + y)

    l0, l1, l2 = f"{prefix} L0", f"{prefix} L1", f"{prefix} L2"
    ops: list[dict] = [
        {"op": "level", "key": "L0", "name": l0, "elevation_mm": 0},
        {"op": "level", "key": "L1", "name": l1, "elevation_mm": 3000},
        {"op": "level", "key": "L2", "name": l2, "elevation_mm": 6000},
    ]
    ops += [{"op": "grid", "key": f"G{n}", "name": f"{prefix}-{n}", "start": p(x, -1500), "end": p(x, 7500)}
            for n, x in (("1", 0), ("2", 4000), ("3", 8000))]
    ops += [{"op": "grid", "key": f"G{n}", "name": f"{prefix}-{n}", "start": p(-1500, y), "end": p(9500, y)}
            for n, y in (("A", 0), ("B", 6000))]

    corners = [p(0, 0), p(8000, 0), p(8000, 6000), p(0, 6000)]
    for floor, (base, top) in enumerate(((l0, l1), (l1, l2))):
        names = ("S", "E", "N", "W")
        for i, name in enumerate(names):
            ops.append({"op": "wall", "key": f"F{floor}-W{name}", "level": base, "top_level": top,
                        "start": corners[i], "end": corners[(i + 1) % 4]})
        ops.append({"op": "wall", "key": f"F{floor}-WI", "level": base, "top_level": top, "start": p(4000, 0), "end": p(4000, 6000)})
        ops.append({"op": "floor", "key": f"F{floor}-SLAB", "level": base, "points": corners})
        ops += [
            {"op": "room", "key": f"F{floor}-R1", "level": base, "point": p(2000, 2000), "name": "Living" if floor == 0 else "Bedroom 1",
             "number": f"{floor}01", "flat": f"{prefix}-A"},
            {"op": "room", "key": f"F{floor}-R2", "level": base, "point": p(6000, 3000), "name": "Kitchen" if floor == 0 else "Bedroom 2",
             "number": f"{floor}02", "flat": f"{prefix}-A"},
        ]
        ops.append({"op": "door", "key": f"F{floor}-DI", "host_key": f"F{floor}-WI", "type": door_type, "offset_mm": 4500})
        ops.append({"op": "window", "key": f"F{floor}-WN1", "host_key": f"F{floor}-WN", "type": window_type, "offset_mm": 2000, "sill_mm": 900})
    ops.append({"op": "door", "key": "F0-DS", "host_key": "F0-WS", "type": door_type, "offset_mm": 1500})
    ops.append({"op": "window", "key": "F1-WS1", "host_key": "F1-WS", "type": window_type, "offset_mm": 6000, "sill_mm": 900})
    # Stair core: a shaft cutting only the first-floor slab.
    ops.append({"op": "shaft", "key": "STAIR", "base_level": l0, "top_level": l1, "base_offset_mm": 2000, "top_offset_mm": 500,
                "points": [p(500, 3500), p(1700, 3500), p(1700, 5500), p(500, 5500)]})
    return BuildPlan.model_validate({"ops": ops})
