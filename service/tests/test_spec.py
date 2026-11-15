from b2m.spec import BuildingSpec, parse_spec

PRD_EXAMPLE = {
    "schema_version": "1.0",
    "site": {"plot_mm": [15000, 25000], "road_side": "front", "city": "kolkata", "road_width_mm": 9000},
    "building": {"ground": "stilt_parking", "upper_floors": 3, "floor_to_floor_mm": 3000, "template": "auto"},
    "units": [{"type": "2BHK", "per_floor": 4, "target_carpet_m2": 60}],
    "core": {"stair": "dog_leg", "stair_width_mm": 1200, "lift": False},
    "standards": {"external_wall": "Brick 230", "internal_wall": "Brick 115", "slab": "RCC 150",
                  "door_types": "default", "window_types": "default"},
    "assumptions": ["floor_to_floor_mm defaulted to 3000"],
}


def test_prd_example_is_valid():
    spec, errors = parse_spec(PRD_EXAMPLE)
    assert errors == []
    assert spec.units_per_floor == 4


def test_minimal_spec_fills_defaults():
    spec, errors = parse_spec({
        "site": {"plot_mm": [12000, 20000], "road_width_mm": 6000},
        "building": {"ground": "flats", "upper_floors": 4},
        "units": [{"type": "1BHK", "per_floor": 2}],
    })
    assert errors == []
    assert spec.building.floor_to_floor_mm == 3000 and spec.core.stair_width_mm == 1200


def test_errors_are_plain_and_name_the_field():
    bad = {**PRD_EXAMPLE, "building": {**PRD_EXAMPLE["building"], "upper_floors": 7}, "colour": "red"}
    _, errors = parse_spec(bad)
    assert any(e.startswith("building.upper_floors:") for e in errors)
    assert any(e.startswith("colour:") for e in errors)


def test_unit_mix_rules():
    _, errors = parse_spec({**PRD_EXAMPLE, "units": [{"type": "2BHK", "per_floor": 5}]})
    assert any("per_floor" in e for e in errors)
    _, errors = parse_spec({**PRD_EXAMPLE, "units": [{"type": "2BHK", "per_floor": 1}]})
    assert any("1 flats per floor" in e for e in errors)
    _, errors = parse_spec({**PRD_EXAMPLE, "units": [{"type": "2BHK", "per_floor": 1}, {"type": "2BHK", "per_floor": 1}]})
    assert any("each unit type once" in e for e in errors)
    _, errors = parse_spec({**PRD_EXAMPLE, "building": {**PRD_EXAMPLE["building"], "template": "T2"}})
    assert any("holds 2 flats per floor" in e for e in errors)


def test_plot_bounds():
    _, errors = parse_spec({**PRD_EXAMPLE, "site": {**PRD_EXAMPLE["site"], "plot_mm": [3000, 25000]}})
    assert any("plot_mm" in e for e in errors)


def test_hash_is_stable_and_order_independent():
    a = BuildingSpec.model_validate(PRD_EXAMPLE)
    reordered = {k: PRD_EXAMPLE[k] for k in reversed(list(PRD_EXAMPLE))}
    b = BuildingSpec.model_validate(reordered)
    assert a.hash() == b.hash()
    assert a.hash() != BuildingSpec.model_validate({**PRD_EXAMPLE, "assumptions": []}).hash()


def test_structure_options_default_and_keep_old_hashes():
    spec = BuildingSpec.model_validate(PRD_EXAMPLE)
    assert (spec.standards.structure, spec.standards.chajja_mm, spec.standards.roof_parapet_mm) == ("rcc_frame", 600, 1000)
    # A spec written before these options existed hashes as it did then; changing one changes the hash.
    assert "structure" not in spec.canonical_json() and "chajja_mm" not in spec.canonical_json()
    explicit = {**PRD_EXAMPLE, "standards": {**PRD_EXAMPLE["standards"], "structure": "rcc_frame", "chajja_mm": 600}}
    assert BuildingSpec.model_validate(explicit).hash() == spec.hash()
    changed = {**PRD_EXAMPLE, "standards": {**PRD_EXAMPLE["standards"], "structure": "load_bearing"}}
    assert BuildingSpec.model_validate(changed).hash() != spec.hash()
    _, errors = parse_spec({**PRD_EXAMPLE, "standards": {**PRD_EXAMPLE["standards"], "chajja_mm": -1, "structure": "steel"}})
    assert any(e.startswith("standards.chajja_mm") for e in errors) and any(e.startswith("standards.structure") for e in errors)
