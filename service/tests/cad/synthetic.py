"""Small drawings built with ezdxf for T4 tests: every expectation is exact because the drawing is ours."""

from __future__ import annotations

from pathlib import Path

import ezdxf

from b2m.cad import reader, sheet


class Drawing:
    def __init__(self):
        self.doc = ezdxf.new("R2018", setup=True)
        self.doc.header["$INSUNITS"] = 4  # mm
        self.msp = self.doc.modelspace()
        for name in ("dim", "notes", "grid", "beams", "points"):
            self.doc.layers.add(name)

    def line(self, a, b, layer="0", **attribs):
        return self.msp.add_line(a, b, dxfattribs={"layer": layer, **attribs})

    def hline(self, y, x0, x1, layer="0"):
        return self.line((x0, y), (x1, y), layer)

    def vline(self, x, y0, y1, layer="0"):
        return self.line((x, y0), (x, y1), layer)

    def rect(self, x0, y0, x1, y1, layer="0", lines=False):
        if lines:
            for a, b in (((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)), ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))):
                self.line(a, b, layer)
            return None
        return self.msp.add_lwpolyline([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], close=True,
                                       dxfattribs={"layer": layer})

    def solid_rect(self, x0, y0, x1, y1, layer="0"):
        hatch = self.msp.add_hatch(dxfattribs={"layer": layer})
        hatch.paths.add_polyline_path([(x0, y0), (x1, y0), (x1, y1), (x0, y1)], is_closed=True)
        return hatch

    def arrow(self, tip, base_mid, half_width, layer="0", color=None):
        dx, dy = base_mid[0] - tip[0], base_mid[1] - tip[1]
        length = (dx * dx + dy * dy) ** 0.5
        nx, ny = -dy / length * half_width, dx / length * half_width
        hatch = self.msp.add_hatch(dxfattribs={"layer": layer, **({"color": color} if color else {})})
        hatch.paths.add_polyline_path([tip, (base_mid[0] + nx, base_mid[1] + ny), (base_mid[0] - nx, base_mid[1] - ny)],
                                      is_closed=True)
        return hatch

    def text(self, s, at, height=200, layer="0"):
        return self.msp.add_text(s, height=height, dxfattribs={"layer": layer, "insert": at})

    def mtext(self, s, at, height=200, layer="notes"):
        return self.msp.add_mtext(s, dxfattribs={"layer": layer, "insert": at, "char_height": height,
                                                 "attachment_point": 7})

    def vdim(self, x_line, p1, p2, layer="dim", text="<>"):
        dim = self.msp.add_linear_dim(base=(x_line, p1[1]), p1=p1, p2=p2, angle=90, text=text,
                                      dxfattribs={"layer": layer})
        dim.render()
        return dim.dimension

    def hdim(self, y_line, p1, p2, layer="dim", text="<>"):
        dim = self.msp.add_linear_dim(base=(p1[0], y_line), p1=p1, p2=p2, angle=0, text=text,
                                      dxfattribs={"layer": layer})
        dim.render()
        return dim.dimension

    def point(self, at, layer="points"):
        return self.msp.add_point(at, dxfattribs={"layer": layer})

    def sheet(self, tmp_path: Path, name: str) -> sheet.Sheet:
        path = tmp_path / name
        self.doc.saveas(path)
        return sheet.detect(reader.read_dxf(path))


def section(tmp_path: Path, marks: bool = True) -> sheet.Sheet:
    """A three-storey section: PL 0, GL -450, floors every 3000 with 200 slabs, roof 9000, parapet 900, a 400
    beam below a soffit, a 1200 window 900 above the first floor, a 2100 door on the ground floor, a 1200 deep
    founding level with a 400 footing, a 100 mm PCC note, and a 2400 structure on the roof."""
    d = Drawing()
    for y in (0, 3000, 6000, 9000):
        d.hline(y, 0, 10000)
        d.hline(y - 200, 0, 10000)
    d.hline(-300, 0, 10000)  # PCC bottom
    d.hline(9900, 0, 10000)  # parapet top, reaching both edges
    d.vline(0, -1650, 9900)
    d.vline(10000, -1650, 9900)
    d.hline(-450, -3000, 0)  # ground line outside the building
    d.hline(-450, 10000, 13000)
    d.hline(-1650, -500, 10500)  # founding level
    d.hline(-1250, -500, 10500)  # top of footing
    if marks:
        d.text("GL", (-2500, -440))
        d.text("PL", (-600, -100))
    for lo, hi in ((-1650, -450), (-450, 0), (0, 3000), (3000, 6000), (6000, 9000), (9000, 9900)):
        d.vdim(11000, (10000, lo), (10000, hi))
    d.vdim(800, (500, 8800), (500, 9000))  # roof slab
    d.vdim(-800, (-500, -1650), (-500, -1250))  # footing
    # beam under the second floor's soffit (5800)
    d.vline(4000, 5400, 5800)
    d.vline(4300, 5400, 5800)
    d.hline(5400, 4000, 4300)
    d.vdim(3800, (4000, 5400), (4000, 5800))
    # window in storey 1: frame 7000..8500, 3900..5100 with frame lines inside
    d.rect(7000, 3900, 8500, 5100, lines=True)
    d.hline(3950, 7050, 8450)
    d.hline(5050, 7050, 8450)
    d.vdim(6800, (7000, 3900), (7000, 5100))
    # door on the ground floor: 2000..3000, 0..2100 with frame lines inside
    d.vline(2000, 0, 2100)
    d.vline(3000, 0, 2100)
    d.hline(2100, 2000, 3000)
    d.hline(2050, 2050, 2950)
    d.hline(1000, 2050, 2950)
    d.vdim(1800, (2000, 0), (2000, 2100))
    # structure on the roof
    d.hline(11400, 4000, 6000)
    d.vline(4000, 9000, 11400)
    d.vline(6000, 9000, 11400)
    d.vdim(6300, (6000, 9000), (6000, 11400))
    # floor build-up note
    d.text("100 MM THICK PCC", (-3500, -800), height=150, layer="notes")
    d.msp.add_lwpolyline([(-1500, -750), (5000, -250)], dxfattribs={"layer": "notes"})
    # a horizontal dimension (a width): never a height
    d.hdim(10500, (0, 9900), (10000, 9900))
    return d.sheet(tmp_path, "section.dxf")
