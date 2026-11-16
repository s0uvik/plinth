"""DXF to a normalised CadSheet (PRD Step 9, stage 1): every model-space entity in millimetres and world
coordinates, with its layer and DXF handle, block references exploded (nested blocks too) so later stages
never deal with OCS, block transforms or drawing units.

Geometry per kind (all points are world XY in mm, z is dropped):

    line       points = [start, end]
    arc        points = [centre]; radius; start_angle/end_angle in degrees, counter-clockwise in world XY
    circle     points = [centre]; radius
    polyline   points = vertices; bulges (one per vertex, None for 3D polylines); closed.
               Ellipses, splines and leaders become polylines too, with approximated=True for curves.
    hatch      boundaries = polygons (curved edges flattened); solid; pattern. SOLID/TRACE/3DFACE are solid hatches.
    point      points = [location]
    text       points = [alignment point] or [first, second] for ALIGNED/FIT; text (plain), raw_text, underlined,
               height, rotation, alignment; tag for attributes. TEXT, MTEXT, ATTRIB and ATTDEF.
    dimension  points = DXF defining points (defpoint, defpoint2, ... as present); measurement (mm, or degrees
               for angular kinds); dim_kind; rotation (linear); raw_text = text override ("" or "<>" = measured)
    insert     points = [insertion point]; block; effective_block (the dynamic block an anonymous *U block was
               made from); scale (x, y); rotation; children = the block's entities in world coordinates
    other      anything else (proxy entities, images, multileaders, ...), kept for the audit with its dxftype

Children of an insert get handle "<parent handle>:<index>" and parent = the insert's handle; layer is as drawn
in the block (layer "0" inherits the insert's layer when AutoCAD displays it).
"""

from __future__ import annotations

import math
from collections import Counter
from pathlib import Path
from typing import Any, Iterator, Literal

import ezdxf
from ezdxf import path as ezpath
from ezdxf import units as ezunits
from ezdxf.enums import MTextEntityAlignment
from ezdxf.math import OCS, Vec3
from pydantic import BaseModel, ConfigDict

from b2m.cad import convert

Point = tuple[float, float]
Kind = Literal["line", "arc", "circle", "polyline", "hatch", "point", "text", "dimension", "insert", "other"]
FLATTEN_MM = 0.5  # chord tolerance when curves (hatch edges, ellipses, splines) become polygons
MAX_BLOCK_DEPTH = 32
DIM_KINDS = {0: "linear", 1: "aligned", 2: "angular", 3: "diameter", 4: "radius", 5: "angular3p", 6: "ordinate"}
_DIGITS = 4


class Issue(BaseModel):
    """Something the reader or sheet detection could not settle; the import project turns it into a question."""

    model_config = ConfigDict(extra="forbid")
    topic: str
    text: str
    handles: list[str] = []


class CadEntity(BaseModel):
    model_config = ConfigDict(extra="forbid")
    handle: str
    parent: str | None = None
    kind: Kind
    dxftype: str
    layer: str
    color: int | None = None  # AutoCAD colour index: 256 = by layer, 0 = by block
    linetype: str | None = None
    points: list[Point] = []
    bulges: list[float] | None = None
    closed: bool = False
    approximated: bool = False
    radius: float | None = None
    start_angle: float | None = None
    end_angle: float | None = None
    boundaries: list[list[Point]] | None = None
    solid: bool | None = None
    pattern: str | None = None
    text: str | None = None
    raw_text: str | None = None
    underlined: bool = False
    height: float | None = None
    rotation: float | None = None
    alignment: str | None = None
    tag: str | None = None
    measurement: float | None = None
    dim_kind: str | None = None
    block: str | None = None
    effective_block: str | None = None
    scale: tuple[float, float] | None = None
    children: list[CadEntity] = []

    def walk(self) -> Iterator[CadEntity]:
        yield self
        for child in self.children:
            yield from child.walk()

    def bounds(self) -> tuple[float, float, float, float] | None:
        """(xmin, ymin, xmax, ymax) of the defining points (text: its alignment points only; insert: its children,
        as the insertion point may lie far from the block's geometry); None if empty."""
        points = [] if self.children else list(self.points)
        if self.radius is not None and points:
            points = _circle_extremes(points[0], self.radius, self.start_angle, self.end_angle)
        for boundary in self.boundaries or []:
            points.extend(boundary)
        for child in self.children:
            box = child.bounds()
            if box:
                points.extend([box[:2], box[2:]])
        if not points:
            return None
        xs, ys = [p[0] for p in points], [p[1] for p in points]
        return (min(xs), min(ys), max(xs), max(ys))


class CadSheet(BaseModel):
    model_config = ConfigDict(extra="forbid")
    source: str  # the drawing as given (DWG or DXF)
    sha256: str  # of the source file
    dxf: str  # the DXF actually read (the source itself, or the converted copy in the cache)
    units: str  # the drawing's $INSUNITS, e.g. "mm"; "unitless" raises an issue
    unit_factor: float  # drawing units to mm
    entities: list[CadEntity]
    issues: list[Issue] = []

    def walk(self) -> Iterator[CadEntity]:
        """Every entity, children of inserts included (depth first)."""
        for entity in self.entities:
            yield from entity.walk()

    def by_handle(self) -> dict[str, CadEntity]:
        return {e.handle: e for e in self.walk()}


def kind_counts(entities: list[CadEntity], deep: bool = False) -> dict[str, int]:
    items = (e for top in entities for e in (top.walk() if deep else [top]))
    return dict(sorted(Counter(e.kind for e in items).items()))


def layer_counts(entities: list[CadEntity]) -> dict[str, int]:
    return dict(sorted(Counter(e.layer for e in entities).items()))


def read(source: Path | str, oda: Path | str | None = None) -> CadSheet:
    """Read a DWG (converted through the cache) or DXF drawing."""
    source = Path(source)
    return read_dxf(convert.to_dxf(source, oda), source)


def read_dxf(dxf: Path | str, source: Path | str | None = None) -> CadSheet:
    dxf = Path(dxf)
    source = Path(source) if source else dxf
    doc = ezdxf.readfile(dxf)
    issues: list[Issue] = []
    insunits = doc.header.get("$INSUNITS", 0)
    if insunits:
        factor = ezunits.conversion_factor(insunits, ezunits.InsertUnits.Millimeters)
        unit = ezunits.decode(insunits) or ezunits.unit_name(insunits)
    else:
        factor, unit = 1.0, "unitless"
        issues.append(Issue(topic="units", text=f"{source.name} does not say its drawing units ($INSUNITS is "
                                                 "unset); its values are kept as drawn. Which unit is it drawn in?"))
    reader = _Reader(doc, factor, issues)
    entities = [reader.entity(e, e.dxf.handle, None, 0) for e in doc.modelspace()]
    return CadSheet(source=str(source), sha256=convert.sha256(source), dxf=str(dxf), units=unit,
                    unit_factor=factor, entities=entities, issues=issues)


class _Reader:
    def __init__(self, doc: Any, factor: float, issues: list[Issue]):
        self.doc = doc
        self.f = factor
        self.issues = issues

    def p(self, v: Any) -> Point:
        return (round(float(v[0]) * self.f, _DIGITS), round(float(v[1]) * self.f, _DIGITS))

    def mm(self, value: float) -> float:
        return round(float(value) * self.f, _DIGITS)

    def entity(self, e: Any, handle: str, parent: str | None, depth: int) -> CadEntity:
        kind_fields = self._geometry(e, handle, depth)
        dxf = e.dxf
        return CadEntity(handle=handle, parent=parent, dxftype=e.dxftype(), layer=dxf.get("layer", "0"),
                         color=dxf.get("color", 256), linetype=dxf.get("linetype", "BYLAYER"), **kind_fields)

    def _geometry(self, e: Any, handle: str, depth: int) -> dict[str, Any]:
        t = e.dxftype()
        if t == "LINE":
            return {"kind": "line", "points": [self.p(e.dxf.start), self.p(e.dxf.end)]}
        if t == "CIRCLE":
            return {"kind": "circle", "points": [self.p(e.ocs().to_wcs(e.dxf.center))], "radius": self.mm(e.dxf.radius)}
        if t == "ARC":
            return self._arc(e)
        if t == "LWPOLYLINE":
            flip = -1 if e.dxf.extrusion.z < 0 else 1
            bulges = [flip * float(b) for _, _, b in e.get_points("xyb")]
            return {"kind": "polyline", "points": [self.p(v) for v in e.vertices_in_wcs()], "bulges": bulges,
                    "closed": e.closed}
        if t == "POLYLINE":
            return self._polyline(e)
        if t in ("SPLINE", "ELLIPSE"):
            points = [self.p(v) for v in e.flattening(FLATTEN_MM / self.f)]
            closed = len(points) > 2 and math.dist(points[0], points[-1]) < 1e-6
            return {"kind": "polyline", "points": points[:-1] if closed else points, "closed": closed,
                    "approximated": True}
        if t == "LEADER":
            return {"kind": "polyline", "points": [self.p(v) for v in e.vertices]}
        if t == "HATCH":
            return self._hatch(e)
        if t in ("SOLID", "TRACE", "3DFACE"):
            corners = e.wcs_vertices() if t != "3DFACE" else [e.dxf.get(f"vtx{i}") for i in range(4)]
            return {"kind": "hatch", "boundaries": [_dedupe([self.p(v) for v in corners])], "solid": True,
                    "pattern": "SOLID"}
        if t == "POINT":
            return {"kind": "point", "points": [self.p(e.dxf.location)]}
        if t in ("TEXT", "ATTRIB", "ATTDEF"):
            return self._text(e)
        if t == "MTEXT":
            raw = e.text
            return {"kind": "text", "points": [self.p(e.dxf.insert)], "text": e.plain_text(), "raw_text": raw,
                    "underlined": "\\L" in raw, "height": self.mm(e.dxf.char_height),
                    "rotation": round(e.get_rotation() % 360, _DIGITS),
                    "alignment": MTextEntityAlignment(e.dxf.attachment_point).name}
        if t == "DIMENSION":
            return self._dimension(e)
        if t == "INSERT":
            return self._insert(e, handle, depth)
        return {"kind": "other"}

    def _arc(self, e: Any) -> dict[str, Any]:
        ocs = e.ocs()
        centre = ocs.to_wcs(e.dxf.center)
        start, end = Vec3(e.start_point), Vec3(e.end_point)
        if e.dxf.extrusion.z < 0:  # seen from below: the counter-clockwise arc runs clockwise in world XY
            start, end = end, start
        return {"kind": "arc", "points": [self.p(centre)], "radius": self.mm(e.dxf.radius),
                "start_angle": _angle(start - centre), "end_angle": _angle(end - centre)}

    def _polyline(self, e: Any) -> dict[str, Any]:
        if e.is_polygon_mesh or e.is_poly_face_mesh:
            return {"kind": "other"}
        if e.is_3d_polyline:
            return {"kind": "polyline", "points": [self.p(v) for v in e.points()], "closed": e.is_closed}
        flip = -1 if e.dxf.extrusion.z < 0 else 1
        return {"kind": "polyline", "points": [self.p(v) for v in e.points_in_wcs()],
                "bulges": [flip * v.dxf.bulge for v in e.vertices], "closed": e.is_closed}

    def _hatch(self, e: Any) -> dict[str, Any]:
        boundaries = []
        for boundary in ezpath.from_hatch(e):
            boundaries.append(_dedupe([self.p(v) for v in boundary.flattening(FLATTEN_MM / self.f)]))
        return {"kind": "hatch", "boundaries": boundaries, "solid": bool(e.dxf.solid_fill),
                "pattern": e.dxf.pattern_name}

    def _text(self, e: Any) -> dict[str, Any]:
        ocs = e.ocs()
        alignment, first, second = e.get_placement()
        points = [self.p(ocs.to_wcs(first))] + ([self.p(ocs.to_wcs(second))] if second is not None else [])
        raw = e.dxf.text
        return {"kind": "text", "points": points, "text": e.plain_text(), "raw_text": raw,
                "underlined": "%%u" in raw.lower(), "height": self.mm(e.dxf.height),
                "rotation": _ocs_angle(ocs, e.dxf.rotation), "alignment": alignment.name,
                "tag": e.dxf.tag if e.dxftype() != "TEXT" else None}

    def _dimension(self, e: Any) -> dict[str, Any]:
        kind = DIM_KINDS.get(e.dimtype, "unknown")
        points = [self.p(e.dxf.get(name)) for name in ("defpoint", "defpoint2", "defpoint3", "defpoint4")
                  if e.dxf.hasattr(name)]
        measurement = e.dxf.get("actual_measurement")
        if measurement is None:
            value = e.get_measurement()
            measurement = value if isinstance(value, (int, float)) else None
        if measurement is not None and not kind.startswith("angular"):
            measurement = self.mm(measurement)
        return {"kind": "dimension", "points": points, "measurement": measurement, "dim_kind": kind,
                "raw_text": e.dxf.get("text", ""),
                "rotation": round(e.dxf.get("angle", 0.0), _DIGITS) if kind == "linear" else None}

    def _insert(self, e: Any, handle: str, depth: int) -> dict[str, Any]:
        ocs = e.ocs()
        fields: dict[str, Any] = {
            "kind": "insert", "points": [self.p(ocs.to_wcs(e.dxf.insert))], "block": e.dxf.name,
            "effective_block": self._effective_name(e.dxf.name),
            "scale": (float(e.dxf.get("xscale", 1.0)), float(e.dxf.get("yscale", 1.0))),
            "rotation": _ocs_angle(ocs, e.dxf.get("rotation", 0.0)),
        }
        if depth >= MAX_BLOCK_DEPTH:
            self.issues.append(Issue(topic="blocks", text=f"Block nesting deeper than {MAX_BLOCK_DEPTH} levels "
                                                          f"under {handle}; not exploded further.", handles=[handle]))
            return fields
        children: list[Any] = []
        skipped: list[str] = []

        def skip(entity: Any, reason: str) -> None:
            skipped.append(f"{entity.dxftype()} ({reason})")

        copies = e.multi_insert() if e.mcount > 1 else [e]
        for copy in copies:
            children.extend(copy.virtual_entities(skipped_entity_callback=skip))
        children.extend(e.attribs)
        fields["children"] = [self.entity(child, f"{handle}:{i}", handle, depth + 1)
                              for i, child in enumerate(children)]
        if skipped:
            self.issues.append(Issue(topic="blocks", text=f"Block {e.dxf.name} at {handle}: could not explode "
                                                          + ", ".join(skipped), handles=[handle]))
        return fields

    def _effective_name(self, name: str) -> str | None:
        """An anonymous dynamic-block copy (*U..) points at its source block through AcDbBlockRepBTag xdata."""
        record = self.doc.block_records.get(name)
        if record is None or not record.has_xdata("AcDbBlockRepBTag"):
            return None
        for tag in record.get_xdata("AcDbBlockRepBTag"):
            if tag.code == 1005:
                source = self.doc.entitydb.get(tag.value)
                return source.dxf.name if source is not None else None
        return None


def _circle_extremes(centre: Point, r: float, start: float | None, end: float | None) -> list[Point]:
    """Points that bound a circle, or an arc from start to end (degrees, counter-clockwise)."""
    def at(degrees: float) -> Point:
        return (centre[0] + r * math.cos(math.radians(degrees)), centre[1] + r * math.sin(math.radians(degrees)))

    if start is None or end is None:
        return [at(a) for a in (0, 90, 180, 270)]
    sweep = (end - start) % 360 or 360
    return [at(start), at(end)] + [at(a) for a in (0, 90, 180, 270) if (a - start) % 360 < sweep]


def _angle(v: Vec3) -> float:
    return round(math.degrees(math.atan2(v.y, v.x)) % 360, _DIGITS)


def _ocs_angle(ocs: OCS, degrees: float) -> float:
    """An angle measured in an entity's OCS, as seen in world XY (mirrors when the extrusion points down)."""
    return _angle(ocs.to_wcs(Vec3.from_deg_angle(degrees)))


def _dedupe(points: list[Point]) -> list[Point]:
    """Drop repeated consecutive points and the repeated closing point."""
    out: list[Point] = []
    for point in points:
        if not out or math.dist(out[-1], point) > 1e-6:
            out.append(point)
    if len(out) > 1 and math.dist(out[0], out[-1]) <= 1e-6:
        out.pop()
    return out
