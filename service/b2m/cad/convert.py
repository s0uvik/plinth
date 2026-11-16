"""DWG to DXF with the ODA File Converter (PRD Step 9, stage 1).

B2M never downloads or installs the converter: it must already be on the PC, put there with the user's OK.
It is looked for in this order: the ``oda`` argument, the B2M_ODA environment variable (the .exe or its folder),
%LOCALAPPDATA%\\B2M\\tools\\oda (portable copy), then Program Files\\ODA\\*. Converted files land in
%LOCALAPPDATA%\\B2M\\cache\\dxf\\<sha256 of the DWG>.dxf; source files are only ever read.
"""

from __future__ import annotations

import hashlib
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

from b2m import paths

EXE = "ODAFileConverter.exe"
OUTPUT_VERSION = "ACAD2018"
TIMEOUT_S = 600


class OdaNotFound(RuntimeError):
    pass


class ConversionFailed(RuntimeError):
    pass


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _candidates(oda: Path | str | None) -> list[Path]:
    found = []
    for given in (oda, os.environ.get("B2M_ODA")):
        if given:
            path = Path(given)
            found.append(path / EXE if path.is_dir() else path)
    found.append(paths.tools() / "oda" / EXE)
    for env in ("ProgramFiles", "ProgramFiles(x86)"):
        base = os.environ.get(env)
        if base and (Path(base) / "ODA").is_dir():
            installed = (Path(base) / "ODA").glob(f"*/{EXE}")
            found.extend(sorted(installed, key=lambda p: _version(p.parent.name), reverse=True))
    return found


def _version(folder_name: str) -> tuple[int, ...]:
    return tuple(int(n) for n in re.findall(r"\d+", folder_name))


def find_oda(oda: Path | str | None = None) -> Path:
    """The converter's .exe, or OdaNotFound naming every place looked at."""
    candidates = _candidates(oda)
    for path in candidates:
        if path.is_file():
            return path
    looked = "\n  ".join(str(p) for p in candidates)
    raise OdaNotFound(
        "ODA File Converter is needed to read DWG files but was not found. Looked in:\n  " + looked + "\n"
        "It must be installed with the user's OK (B2M never downloads or installs it by itself): install it, or "
        "unzip the portable version to %LOCALAPPDATA%\\B2M\\tools\\oda, or set B2M_ODA to its .exe."
    )


def to_dxf(source: Path, oda: Path | str | None = None) -> Path:
    """The DXF to read for ``source``: a DXF is used as it is; a DWG is converted once into the cache."""
    source = Path(source)
    suffix = source.suffix.lower()
    if suffix == ".dxf":
        return source
    if suffix != ".dwg":
        raise ValueError(f"{source.name}: only .dwg and .dxf drawings can be read")
    target = paths.dxf_cache() / f"{sha256(source)}.dxf"
    if target.is_file():
        return target
    exe = find_oda(oda)
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="b2m-oda-") as temp:
        in_dir, out_dir = Path(temp, "in"), Path(temp, "out")
        in_dir.mkdir()
        out_dir.mkdir()
        # The converter works on folders, so it gets a copy under a plain name and never sees the source.
        shutil.copyfile(source, in_dir / f"{target.stem}.dwg")
        args = [str(exe), str(in_dir), str(out_dir), OUTPUT_VERSION, "DXF", "0", "1", "*.DWG"]
        result = subprocess.run(args, capture_output=True, text=True, timeout=TIMEOUT_S)
        produced = out_dir / f"{target.stem}.dxf"
        if not produced.is_file():
            detail = (result.stdout + result.stderr).strip() or f"exit code {result.returncode}"
            raise ConversionFailed(f"{source.name}: ODA File Converter made no DXF ({detail})")
        shutil.move(str(produced), target)
    return target
