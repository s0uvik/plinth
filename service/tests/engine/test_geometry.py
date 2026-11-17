import pytest

from plinth.engine.geometry import (GeometryError, Rect, Space, atomic_edges, derive_walls, net_area_mm2, net_rect,
                                 wall_thickness_on)

T = {"ext": 230, "int": 115}


def classify(a, b):
    return "ext" if a is None or b is None else "int"


def walls_of(spaces):
    edges = atomic_edges(spaces)
    return edges, derive_walls(edges, classify, T)


def test_box_gets_four_walls():
    _, walls = walls_of([Space("r", "bedroom", "R", "A", (Rect(0, 0, 4000, 3000),))])
    assert sorted((w.start, w.end) for w in walls) == sorted([
        ((0, 0), (0, 3000)), ((4000, 0), (4000, 3000)), ((0, 0), (4000, 0)), ((0, 3000), (4000, 3000))])
    assert all(w.type == "ext" for w in walls)


def test_two_rooms_share_one_partition_and_outer_walls_run_through():
    spaces = [Space("a", "bedroom", "A", "A", (Rect(0, 0, 3000, 3000),)),
              Space("b", "bedroom", "B", "A", (Rect(3000, 0, 7000, 3000),))]
    _, walls = walls_of(spaces)
    partitions = [w for w in walls if w.type == "int"]
    assert [(w.start, w.end) for w in partitions] == [((3000, 0), (3000, 3000))]
    bottom = [w for w in walls if w.axis == "y" and w.c == 0]
    assert len(bottom) == 1 and (bottom[0].a, bottom[0].b) == (0, 7000)  # T junction does not split it
    assert 3000 in bottom[0].junctions


def test_cells_of_one_space_have_no_wall_between_them():
    living = Space("l", "living", "L", "A", (Rect(0, 0, 4000, 3000), Rect(0, 3000, 1200, 6000)))
    edges, walls = walls_of([living])
    assert not any(w.axis == "y" and w.c == 3000 and w.a == 0 and w.b == 1200 for w in walls)
    assert sum(w.length for w in walls) == 4000 + 3000 + 2800 + 3000 + 1200 + 6000


def test_crossing_walls_are_split_at_the_crossing():
    q = [Space(n, "bedroom", n, "A", (r,)) for n, r in
         (("a", Rect(0, 0, 3000, 3000)), ("b", Rect(3000, 0, 6000, 3000)), ("c", Rect(0, 3000, 3000, 6000)), ("d", Rect(3000, 3000, 6000, 6000)))]
    _, walls = walls_of(q)
    inner = sorted((w.axis, w.c, w.a, w.b) for w in walls if w.type == "int")
    assert inner == [("x", 3000, 0, 3000), ("x", 3000, 3000, 6000), ("y", 3000, 0, 3000), ("y", 3000, 3000, 6000)]


def test_type_change_along_a_line_makes_two_walls():
    spaces = [Space("a", "bedroom", "A", "A", (Rect(0, 0, 3000, 3000),)),
              Space("b", "bedroom", "B", "A", (Rect(0, 3000, 3000, 5000),))]
    _, walls = walls_of(spaces)
    left = sorted((w.a, w.b, w.type) for w in walls if w.axis == "x" and w.c == 0)
    assert left == [(0, 5000, "ext")]


def test_overlapping_cells_are_rejected():
    with pytest.raises(GeometryError):
        atomic_edges([Space("a", "k", "A", "A", (Rect(0, 0, 3000, 3000),)), Space("b", "k", "B", "A", (Rect(2000, 0, 5000, 3000),))])


def test_net_area_and_rect_subtract_half_walls():
    spaces = [Space("a", "bedroom", "A", "A", (Rect(0, 0, 3000, 4000),)),
              Space("b", "bedroom", "B", "A", (Rect(3000, 0, 6000, 4000),))]
    edges, walls = walls_of(spaces)
    t = wall_thickness_on(edges, walls)
    assert net_rect(spaces[0], 0, edges, t) == Rect(115, 115, 2942, 3885)
    expected = 3000 * 4000 - (3000 * 115 + 3000 * 115 + 4000 * 115 + 4000 * 115 // 2)
    assert net_area_mm2(spaces[0], edges, t) == expected
