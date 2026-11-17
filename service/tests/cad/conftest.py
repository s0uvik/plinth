from pathlib import Path

import pytest

from plinth.cad import reader, sheet

SAMPLE = Path(__file__).resolve().parents[1] / "fixtures" / "samples" / "g4-sample"
SAMPLE_DXF = SAMPLE / "dxf"


@pytest.fixture(scope="session")
def sample_sheets() -> dict[str, sheet.Sheet]:
    """The G+4 sample's nine sheets, read and framed once per test session, by file name."""
    return {p.name: sheet.detect(reader.read_dxf(p)) for p in sorted(SAMPLE_DXF.glob("*.dxf"))}
