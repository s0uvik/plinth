"""Where a room ends in a shared space (split_space): the mouth of its alcove, or a question with the candidates.

Synthetic spaces in plan millimetres, anticlockwise. A stub is a wall hanging off the outer wall into the space (a
notch in the space's outline); its end's corners are the reflex corners a mouth can start at.
"""

import math

from shapely.geometry import LineString, Polygon

from plinth.cad.building import Evidence, Room, Wall
from plinth.imported import split_space


def stub_space(width: float, depth: float, stub_x: float, stub_end_y: float, t: float = 200) -> list[tuple]:
    """A width x depth space with a stub t thick at stub_x from the top wall down to stub_end_y."""
    a, b = stub_x - t / 2, stub_x + t / 2
    return [(0, 0), (width, 0), (width, depth), (b, depth), (b, stub_end_y), (a, stub_end_y), (a, depth), (0, depth)]


def room(rid: str, name: str, point, space=None) -> Room:
    return Room(id=rid, name=name, point=point, shared_space=space, evidence=Evidence(sheet="s", handles=[rid]))


def test_the_kitchen_alcove_closes_at_its_mouth():
    space = stub_space(8000, 5000, 5000, 3500)
    mouth = split_space.find_mouth(space, [(6500, 4300)], [(2000, 1500), (6500, 1500)])
    # from the stub's end straight across to the wall facing it; the label 800 mm inside
    assert mouth.line == ((5100, 3500), (8000, 3500)) and mouth.candidates == [mouth.line]


def test_an_alcove_between_two_stubs_closes_from_one_stubs_end_to_the_others():
    space = [(0, 0), (8000, 0), (8000, 5000), (5200, 5000), (5200, 2900), (5000, 2900), (5000, 5000), (3000, 5000),
             (3000, 3100), (2800, 3100), (2800, 5000), (0, 5000)]
    mouth = split_space.find_mouth(space, [(4000, 4300)], [(1500, 1500), (6500, 1500)])
    assert set(mouth.line) == {(3000, 3100), (5000, 2900)}  # the inner corners: the shortest
    # the outer corners close the same mouth; the next distinct line (under the other stub, to the west wall) is far
    # longer, so the mouth is clear
    assert mouth.candidates[0] == mouth.line and len(mouth.candidates) == 2
    assert math.dist(*mouth.candidates[1]) * split_space.FACE_WEIGHT > math.dist(*mouth.line) / split_space.CLEAR_RATIO


def test_no_corner_no_mouth_a_question_without_candidates():
    mouth = split_space.find_mouth([(0, 0), (8000, 0), (8000, 5000), (0, 5000)], [(6500, 4000)], [(2000, 1500)])
    assert mouth.line is None and mouth.candidates == [] and "no wall end or column face" in mouth.reason


def test_two_mouths_alike_a_question_with_both():
    # the stub's end is 2000 mm from the east wall and 2000 mm from the south wall: either closes the kitchen off
    space = stub_space(5100, 5000, 3000, 2000)
    mouth = split_space.find_mouth(space, [(4100, 3500)], [(1500, 3500), (1500, 1000)])
    assert mouth.line is None and "none clearly the shortest" in mouth.reason
    down, across = sorted(mouth.candidates, key=lambda ln: ln[0][0] == ln[1][0])[::-1]
    assert down[1][1] == 0 and down[0][0] == down[1][0] in (2900, 3100)  # down the stub's end to the south wall
    assert across == ((3100, 2000), (5100, 2000))  # across to the east wall


def test_a_line_through_a_label_is_no_mouth():
    # the label sits 100 mm above the only chord: neither side owns it
    space = stub_space(8000, 5000, 5000, 3500)
    mouth = split_space.find_mouth(space, [(6500, 3600)], [(2000, 1500), (6500, 1500)])
    assert mouth.line is None and mouth.candidates == []


def test_plan_split_names_rooms_by_words_draws_into_the_walls_and_keeps_the_rest_together():
    space = stub_space(8000, 5000, 5000, 3500)
    members = [room("r1", "KITCHEN OPEN", (6500, 4300), space), room("r2", "DINING", (6500, 1500), space),
               room("r3", "LIVING ROOM", (2000, 1500), space)]
    walls = [Wall(id="e", start=(8100, -100), end=(8100, 5100), thickness_mm=200, kind="exterior",
                  evidence=Evidence(sheet="s")),
             Wall(id="stub", start=(5000, 5100), end=(5000, 3500), thickness_mm=200, kind="interior",
                  evidence=Evidence(sheet="s"))]
    split = split_space.plan_split(members, ["kitchen"], walls)
    assert split.ok and [r.id for r in split.together] == ["r2", "r3"]
    (r, line), = split.lines
    # the far end runs 25 mm into the east wall; the end on the stub's corner continues the stub's end face, so it
    # stays at the corner
    assert r.id == "r1" and line == [(5100, 3500), (8025.0, 3500.0)]
    assert split_space.split_members(members, {"mode": "separate", "rooms": ["KITCHEN"]}) == (
        [members[0]], members[1:])
    assert split_space.split_members(members, {"mode": "one_room"}) == ([], members)


def test_plan_split_reports_names_that_match_no_label_or_several():
    space = stub_space(8000, 5000, 5000, 3500)
    members = [room("r1", "BED ROOM", (6500, 4300), space), room("r2", "BED ROOM", (2000, 1500), space)]
    split = split_space.plan_split(members, ["KITCHEN", "BED"])
    assert not split.ok and not split.lines
    assert split.problems == ['"KITCHEN" names none of the labels "BED ROOM", "BED ROOM"',
                              '"BED" names more than one of the labels "BED ROOM", "BED ROOM"']


def test_two_rooms_split_off_one_after_the_other():
    # a kitchen behind one stub, a store behind another, the rest one room
    space = [(0, 0), (9000, 0), (9000, 5000), (7100, 5000), (7100, 3500), (6900, 3500), (6900, 5000), (2100, 5000),
             (2100, 3500), (1900, 3500), (1900, 5000), (0, 5000)]
    members = [room("k", "KITCHEN", (8000, 4300), space), room("s", "STORE", (1000, 4300), space),
               room("l", "LIVING", (4500, 1500), space)]
    split = split_space.plan_split(members, ["KITCHEN", "STORE"])
    assert split.ok and [r.id for r, _ in split.lines] == ["k", "s"] and [r.id for r in split.together] == ["l"]
    assert [line for _, line in split.lines] == [[(7100, 3500), (9000, 3500)], [(1900, 3500), (0, 3500)]]
    for _, line in split.lines:
        assert Polygon(space).buffer(1).contains(LineString(line)) and math.dist(*line) == 1900
