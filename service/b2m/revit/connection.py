"""Line-based connections to the add-in: a named pipe (Revit 2025+) or loopback TCP (Revit 2024)."""

from __future__ import annotations

import socket
import time
from typing import Protocol

from b2m.revit.discovery import Target

MAX_LINE_BYTES = 1024 * 1024 + 4096  # The add-in caps responses at 1 MiB of JSON.

# The add-in serves one client at a time and re-creates its pipe after each one disconnects. Opening in that
# gap fails with "pipe busy" (EINVAL) or "not found" (ENOENT), so keep trying briefly before giving up.
PIPE_OPEN_SECONDS = 5.0


class Connection(Protocol):
    def send_line(self, line: str) -> None: ...

    def read_line(self) -> str | None: ...

    def close(self) -> None: ...


class PipeConnection:
    def __init__(self, pipe_name: str):
        # Byte-mode duplex pipe opened like a file; requests and responses alternate on one handle.
        self._file = _open_pipe(rf"\\.\pipe\{pipe_name}")
        self._buffer = bytearray()

    def send_line(self, line: str) -> None:
        self._file.write(line.encode("utf-8") + b"\n")

    def read_line(self) -> str | None:
        while b"\n" not in self._buffer:
            if len(self._buffer) > MAX_LINE_BYTES:
                raise ConnectionError("Response line is larger than the add-in's 1 MiB cap.")
            chunk = self._file.read(65536)
            if not chunk:
                return None
            self._buffer.extend(chunk)
        line, _, rest = bytes(self._buffer).partition(b"\n")
        self._buffer = bytearray(rest)
        return line.decode("utf-8").rstrip("\r")

    def close(self) -> None:
        self._file.close()


def _open_pipe(path: str, seconds: float = PIPE_OPEN_SECONDS):
    deadline = time.monotonic() + seconds
    while True:
        try:
            return open(path, "r+b", buffering=0)
        except OSError:
            if time.monotonic() >= deadline:
                raise
            time.sleep(0.05)


class TcpConnection:
    def __init__(self, port: int):
        self._socket = socket.create_connection(("127.0.0.1", port))
        self._reader = self._socket.makefile("rb")

    def send_line(self, line: str) -> None:
        self._socket.sendall(line.encode("utf-8") + b"\n")

    def read_line(self) -> str | None:
        raw = self._reader.readline(MAX_LINE_BYTES)
        if not raw:
            return None
        return raw.decode("utf-8").rstrip("\r\n")

    def close(self) -> None:
        self._reader.close()
        self._socket.close()


def open_connection(target: Target) -> Connection:
    if target.transport == "pipe":
        return PipeConnection(target.pipe_name)
    return TcpConnection(target.port)
