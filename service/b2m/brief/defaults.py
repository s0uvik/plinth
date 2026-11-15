"""What the brief agent may fill in when a brief is silent (F2.3), and what it must never invent (F2.2)."""

from __future__ import annotations

from b2m.spec import DEFAULT_CARPET_M2

# Spec fields a brief must state or the user must answer. Never defaulted.
ESSENTIALS = {
    "plot_width_mm": "plot frontage (width along the road)",
    "plot_depth_mm": "plot depth",
    "upper_floors": "number of floors above the ground storey (G+3 or G+4)",
    "units": "flat types and how many of each per floor",
}

# Defaults applied when a brief says nothing, each listed back to the user before the build.
DEFAULTS: list[tuple[str, object, str]] = [
    ("ground", "stilt_parking", "Most Kolkata walk-ups park cars in a stilt ground storey"),
    ("floor_to_floor_mm", 3000, "Typical residential floor height"),
    ("road_width_mm", 6000, "A common width for a local Kolkata road; setbacks may depend on it"),
    ("stair_width_mm", 1200, "Comfortable width for a residential dog-leg stair"),
    ("lift", False, "Walk-up blocks of G+3/G+4 usually have no lift"),
    ("template", "auto", "Chosen from the number of flats per floor"),
    ("target_carpet_m2", None, "Per flat type: " + ", ".join(f"{t} {a:.0f} m²" for t, a in DEFAULT_CARPET_M2.items())
     + "; left empty so the engine applies and reports them"),
]


def defaults_table() -> str:
    lines = [f"- {field}: {value!r} ({why})" for field, value, why in DEFAULTS]
    return "\n".join(lines)
