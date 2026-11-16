"""ODA discovery and DWG conversion. The conversion test needs ODA File Converter on this PC and skips without it."""

from collections import Counter
from pathlib import Path

import ezdxf
import pytest

from b2m.cad import convert, reader
from tests.cad.conftest import SAMPLE, SAMPLE_DXF

try:
    ODA = convert.find_oda()  # looked up before any test points B2M_HOME elsewhere
except convert.OdaNotFound:
    ODA = None


@pytest.fixture
def no_oda_anywhere(monkeypatch, tmp_path):
    monkeypatch.setenv("B2M_HOME", str(tmp_path / "home"))
    monkeypatch.delenv("B2M_ODA", raising=False)
    monkeypatch.setenv("ProgramFiles", str(tmp_path / "pf"))
    monkeypatch.setenv("ProgramFiles(x86)", str(tmp_path / "pf86"))
    return tmp_path


def test_missing_converter_is_a_clear_error_and_nothing_is_installed(no_oda_anywhere):
    with pytest.raises(convert.OdaNotFound) as error:
        convert.find_oda()
    message = str(error.value)
    assert "user's OK" in message and "never downloads or installs" in message
    assert str(no_oda_anywhere / "home" / "tools" / "oda" / convert.EXE) in message
    assert not (no_oda_anywhere / "home" / "tools").exists()


def test_converter_lookup_order(no_oda_anywhere, monkeypatch):
    def fake(path: Path) -> Path:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"")
        return path

    older = fake(no_oda_anywhere / "pf" / "ODA" / "ODAFileConverter 25.12.0" / convert.EXE)
    newer = fake(no_oda_anywhere / "pf" / "ODA" / "ODAFileConverter 27.10.0" / convert.EXE)
    assert convert.find_oda() == newer != older
    portable = fake(no_oda_anywhere / "home" / "tools" / "oda" / convert.EXE)
    assert convert.find_oda() == portable
    override = fake(no_oda_anywhere / "elsewhere" / convert.EXE)
    monkeypatch.setenv("B2M_ODA", str(override.parent))
    assert convert.find_oda() == override
    assert convert.find_oda(newer) == newer


def test_dxf_is_used_as_is_and_other_files_are_refused(tmp_path):
    dxf = SAMPLE_DXF / "3.ROOF PLAN.dxf"
    assert convert.to_dxf(dxf) == dxf
    with pytest.raises(ValueError):
        convert.to_dxf(SAMPLE / "drawing-set.pdf")


@pytest.mark.skipif(ODA is None, reason="ODA File Converter is not installed on this PC")
def test_dwg_converts_once_into_the_hash_named_cache(monkeypatch, tmp_path):
    monkeypatch.setenv("B2M_HOME", str(tmp_path))
    source = SAMPLE / "dwg" / "3.ROOF PLAN.dwg"
    before = convert.sha256(source)
    dxf = convert.to_dxf(source, ODA)
    assert dxf == tmp_path / "cache" / "dxf" / f"{before}.dxf"
    assert convert.sha256(source) == before  # the source is only read
    sheet = reader.read(source, ODA)
    assert sheet.source == str(source) and sheet.sha256 == before and sheet.dxf == str(dxf)
    expected = Counter(e.dxftype() for e in ezdxf.readfile(SAMPLE_DXF / "3.ROOF PLAN.dxf").modelspace())
    assert Counter(e.dxftype for e in sheet.entities) == expected

    def no_converter(*args, **kwargs):
        raise AssertionError("a cached DWG must not be converted again")

    monkeypatch.setattr(convert, "find_oda", no_converter)
    assert convert.to_dxf(source) == dxf
