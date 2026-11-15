import pytest

from b2m.engine import units
from b2m.engine.units import FlatError, carpet_m2, lay_out, min_lengths, size_depth


@pytest.mark.parametrize("utype", ["1BHK", "2BHK", "3BHK"])
def test_min_layout_meets_room_minimums_and_fills_depth(utype):
    living, rooms = min_lengths(utype, 0, 4500, 3080)
    flat = lay_out("A", "F1-A", utype, 0, 4500, living + sum(rooms) + 1230, 3080)
    assert flat.living_length + sum(flat.lengths) == flat.d
    assert flat.living_length >= 3080 + units.ENTRY_SPAN
    assert all(length % 10 == 0 for length in flat.lengths)
    kinds = [s.kind for s in flat.spaces]
    assert kinds.count("kitchen") == 1 and kinds[0] == "living"
    assert sum(k.startswith("bedroom") for k in kinds) == int(utype[0])


def test_attached_toilet_opens_into_the_bedroom_before_it():
    flat = lay_out("A", "F1-A", "2BHK", 0, 4500, 16000, 3080)
    by_id = {s.id: s for s in flat.spaces}
    attached = [d for d in flat.doors if by_id[d.a].name == "Toilet 1"][0]
    assert by_id[attached.b].name == "Bedroom 1"
    common = [d for d in flat.doors if by_id[d.a].name == "Toilet 2"][0]
    assert common.b == flat.living_id


def test_narrow_flat_is_refused_with_the_width_it_needs():
    with pytest.raises(FlatError, match="at least 3.77"):
        min_lengths("2BHK", 0, 3500, 3080)


def test_depth_grows_with_target_area_and_is_deterministic():
    small = size_depth("2BHK", 0, 65, 4500, 3080, 30000)
    large = size_depth("2BHK", 0, 80, 4500, 3080, 30000)
    assert small < large
    assert size_depth("2BHK", 0, 80, 4500, 3080, 30000) == large
    assert carpet_m2(lay_out("A", "x", "2BHK", 0, 4500, large, 3080)) >= 80
    assert carpet_m2(lay_out("A", "x", "2BHK", 0, 4500, large - 10, 3080)) < 80


def test_target_that_cannot_fit_says_how_much_does():
    with pytest.raises(FlatError, match="does not fit: at most"):
        size_depth("3BHK", 0, 140, 4000, 3080, 25000)
