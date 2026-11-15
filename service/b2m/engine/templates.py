"""Floor plate templates (PRD F4.1): a stair-and-lobby column in the middle, flats either side.

    T2: left column one flat (A), right column one flat (B), both full depth
    T3: left column front flat (A) and rear flat (C), right column one full-depth flat (B, the largest)
    T4: left column A (front) and C (rear), right column B (front) and D (rear)

The core column, front to rear: stair enclosure (two flights and the mid landing), then the lobby
(floor landing and flat entrances), then the lift shaft if there is one. Flats are entered from the
lobby; front flats can only be entered beside the lobby, never beside the stair flights.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from b2m.engine import catalog
from b2m.engine.geometry import Rect, Space
from b2m.engine.site import Envelope, LayoutError
from b2m.engine.stair import StairCore, stair_core
from b2m.engine.units import MAX_FLAT_WIDTH, DoorSpec, Flat, FlatError, Frame, WindowSpec, lay_out, size_depth
from b2m.spec import DEFAULT_CARPET_M2, TEMPLATES, BuildingSpec

EXT = catalog.WALL_THICKNESS["Brick 230"]
LIFT_INNER = 1900  # clear shaft size for a small passenger lift
UNIT_ORDER = {"3BHK": 0, "2BHK": 1, "1BHK": 2}

# (letter, column, row) per template. Row "full" runs the whole plate depth.
SLOTS = {
    "T2": [("A", "L", "full"), ("B", "R", "full")],
    "T3": [("A", "L", "front"), ("C", "L", "rear"), ("B", "R", "full")],
    "T4": [("A", "L", "front"), ("B", "R", "front"), ("C", "L", "rear"), ("D", "R", "rear")],
}


@dataclass(frozen=True)
class Core:
    x0: int
    x1: int
    stair: Rect
    lobby: Rect
    lift: Rect | None
    stair_core: StairCore


@dataclass
class FloorLayout:
    spaces: list[Space]
    doors: list[DoorSpec]
    windows: list[WindowSpec]
    flats: list[Flat] = field(default_factory=list)


@dataclass
class PlateLayout:
    template: str
    variant: int
    plate: Rect
    core: Core
    slots: list[tuple[str, str, str, str, int, int]]  # letter, column, row, unit type, width, depth
    targets: dict[str, float]
    notes: list[str]
    _spec: BuildingSpec

    def floor(self, level: int) -> FloorLayout:
        """Spaces, doors and windows on one storey (0 = ground)."""
        core_spaces = self._core_spaces(level)
        if level == 0 and self._spec.building.ground == "stilt_parking":
            return FloorLayout(core_spaces, [DoorSpec("core.lobby", None, "building")], [])
        floor = FloorLayout(list(core_spaces), [], [])
        for letter, column, row, utype, w, d in self.slots:
            flat = lay_out(letter, f"F{level}-{letter}", utype, self.variant, w, d, self._entry_from(row), self._frame(column, row))
            floor.flats.append(flat)
            floor.spaces.extend(flat.spaces)
            floor.doors.append(DoorSpec("core.lobby", flat.living_id, "entry", b_cell=0))
            floor.doors.extend(flat.doors)
            floor.windows.extend(flat.windows)
        if level == 0:
            floor.doors.append(DoorSpec("core.lobby", None, "building"))
        return floor

    def _core_spaces(self, level: int) -> list[Space]:
        lobby = Space("core.lobby", "lobby", "Stair & Lobby", "core", (self.core.lobby, self.core.stair),
                      number=f"F{level}-L", door_cells=(0,))
        spaces = [lobby]
        if self.core.lift:
            spaces.append(Space("core.lift", "lift", "Lift", "lift", (self.core.lift,), room=False))
        return spaces

    def _entry_from(self, row: str) -> int:
        """How far from the flat's outer end the lobby starts along its core-side wall."""
        if row == "rear":
            return self.core.lift.height if self.core.lift else 0
        return self.core.stair.height

    def _frame(self, column: str, row: str) -> Frame:
        p = self.plate
        x_core, x_dir = (self.core.x0, -1) if column == "L" else (self.core.x1, 1)
        y_end, y_dir = (p.y1, -1) if row == "rear" else (p.y0, 1)
        return Frame(x_core, x_dir, y_end, y_dir)


def _snap(value: float) -> int:
    return int(value // catalog.GRID * catalog.GRID)


def _snap_up(value: float) -> int:
    return -_snap(-value)


def assign(spec: BuildingSpec, template: str) -> dict[str, str]:
    """Which unit type goes in which slot. The largest type takes T3's full-depth slot."""
    units = [u.type for u in spec.units for _ in range(u.per_floor)]
    letters = [s[0] for s in SLOTS[template]]
    if template == "T3":
        largest = min(units, key=lambda t: (UNIT_ORDER[t], units.index(t)))
        units.remove(largest)
        return {"B": largest, "A": units[0], "C": units[1]}
    return dict(zip(letters, units))


def plan_plate(spec: BuildingSpec, env: Envelope, variant: int) -> PlateLayout:
    template = TEMPLATES[spec.units_per_floor]
    notes: list[str] = []
    stair = stair_core(spec.building.floor_to_floor_mm, spec.core.stair_width_mm)
    core_w = stair.inner_width + EXT
    stair_d = _snap_up(stair.inner_depth + EXT / 2)
    lift_d = LIFT_INNER + EXT if spec.core.lift else 0
    if spec.core.lift and stair.inner_width < LIFT_INNER:
        raise LayoutError(f"The lift shaft needs {LIFT_INNER} mm clear width; the stair core gives {stair.inner_width} mm.")
    if spec.core.lift and spec.building.ground == "flats":
        raise LayoutError("Ground-floor flats with a lift are not supported in v1: the ground lobby would have no "
                          "outside door. Use stilt parking, or no lift.")

    # Centreline bounds inside the envelope (outer walls stand half outside their centreline), on the grid.
    lo_x, hi_x = _snap_up(env.x0 + EXT / 2), _snap(env.x1 - EXT / 2)
    lo_y, hi_y = _snap_up(env.y0 + EXT / 2), _snap(env.y1 - EXT / 2)
    max_w, max_d = hi_x - lo_x, hi_y - lo_y
    flat_w = _snap(min(MAX_FLAT_WIDTH, (max_w - core_w) / 2))
    slot_types = assign(spec, template)
    targets: dict[str, float] = {}
    for u in spec.units:
        targets[u.type] = u.target_carpet_m2 or DEFAULT_CARPET_M2[u.type]
        if u.target_carpet_m2 is None:
            notes.append(f"{u.type} target carpet area defaulted to {DEFAULT_CARPET_M2[u.type]:.0f} m²")

    lobby_min = max(stair.width, catalog.DOORS["entry"].width + 2 * catalog.OPENING_CLEARANCE)
    columns: dict[str, list[tuple[str, str]]] = {"L": [], "R": []}
    for letter, column, row in SLOTS[template]:
        columns[column].append((letter, row))

    def column_depths(column: str, width: int) -> dict[str, int]:
        return {letter: size_depth(slot_types[letter], variant, targets[slot_types[letter]], width,
                                   lift_d if row == "rear" else stair_d, max_d)
                for letter, row in columns[column]}

    # Split the width between the two columns so their depths come out as equal as possible: the
    # column holding more flat area gets more width. Candidates in 100 mm steps around an even split.
    total_w = 2 * flat_w
    best = None
    errors: list[str] = []
    for width_l in sorted({flat_w} | set(range(total_w - MAX_FLAT_WIDTH, MAX_FLAT_WIDTH + 1, 100))):
        width_r = total_w - width_l
        if not (0 < width_l <= MAX_FLAT_WIDTH and 0 < width_r <= MAX_FLAT_WIDTH):
            continue
        try:
            depths = column_depths("L", width_l) | column_depths("R", width_r)
        except FlatError as e:
            if width_l == flat_w:  # Report the even split, not a lopsided trial.
                errors.append(str(e))
            continue
        depth = max(sum(depths[letter] for letter, _ in slots) for slots in columns.values())
        key = (depth, abs(width_l - flat_w), width_l)
        if best is None or key < best[0]:
            best = (key, width_l, width_r, depths)
    if best is None:
        raise LayoutError(f"{template}: {errors[0] if errors else 'no flat width works'}")
    _, width_l, width_r, depths = best
    widths = {"L": width_l, "R": width_r}

    depth = max(sum(depths[letter] for letter, _ in slots) for slots in columns.values())
    depth = max(depth, stair_d + lobby_min + lift_d)
    if depth > max_d:
        raise LayoutError(f"{template} with these flats needs {(depth + EXT) / 1000:.2f} m of building depth; the envelope "
                          f"allows {env.depth / 1000:.2f} m. Lower the target carpet areas, choose fewer or smaller flats, "
                          "or use a deeper plot.")

    # Stretch flats so each column fills the plate depth; spare depth goes to the first flat in the column.
    final: dict[str, int] = {}
    for slots in columns.values():
        spare = depth - sum(depths[letter] for letter, _ in slots)
        for i, (letter, _) in enumerate(slots):
            final[letter] = depths[letter] + (spare if i == 0 else 0)

    plate_w = width_l + core_w + width_r
    x0 = lo_x + _snap((max_w - plate_w) / 2)
    y0 = lo_y
    plate = Rect(x0, y0, x0 + plate_w, y0 + depth)
    cx0, cx1 = x0 + width_l, x0 + width_l + core_w
    stair_rect = Rect(cx0, y0, cx1, y0 + stair_d)
    lift_rect = Rect(cx0, plate.y1 - lift_d, cx1, plate.y1) if lift_d else None
    lobby_rect = Rect(cx0, stair_rect.y1, cx1, plate.y1 - lift_d)
    if lobby_rect.height < lobby_min:
        raise LayoutError(f"The lobby would be {lobby_rect.height} mm long; it needs {lobby_min} mm.")
    core = Core(cx0, cx1, stair_rect, lobby_rect, lift_rect, stair)

    slots = [(letter, column, row, slot_types[letter], widths[column], final[letter]) for letter, column, row in SLOTS[template]]
    return PlateLayout(template, variant, plate, core, slots, targets, notes, spec)
