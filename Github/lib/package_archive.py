"""Bounded NuGet body inventories and credential-free repository signature checks."""
from __future__ import annotations

import hashlib
import io
import os
from pathlib import Path
import re
import selectors
import signal
import subprocess
import tempfile
import time
from types import SimpleNamespace
import zipfile

MAX_PACKAGE = 512 * 1024 ** 2
MAX_MEMBERS = 16384


def members(data: bytes, *, require_signature: bool = False) -> dict[str, bytes]:
    """Read actual regular member bodies; reject path aliases and ZIP bombs."""
    if len(data) > MAX_PACKAGE:
        raise ValueError("package archive exceeds size bound")
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            if len(entries) > MAX_MEMBERS:
                raise ValueError("package member count exceeds bound")
            result, seen, file_names, total = {}, set(), set(), 0
            for entry in entries:
                name = entry.filename.removesuffix("/") if entry.is_dir() else entry.filename
                if (entry.orig_filename != entry.filename or not name or name.startswith("/") or "\\" in name or ":" in name
                        or any(part in ("", ".", "..") for part in name.split("/"))
                        or any(ord(c) < 32 or ord(c) == 127 for c in name)
                        or name.casefold() in seen or entry.flag_bits & 1
                        or (entry.external_attr >> 16) & 0o170000 not in (0, 0o040000 if entry.is_dir() else 0o100000)):
                    raise ValueError("unsafe or duplicate package member")
                parts = name.casefold().split("/")
                if any("/".join(parts[:index]) in file_names for index in range(1, len(parts))) or (not entry.is_dir() and any(other.startswith(name.casefold() + "/") for other in seen)):
                    raise ValueError("conflicting package file/directory members")
                seen.add(name.casefold())
                if not entry.is_dir():
                    file_names.add(name.casefold())
                total += entry.file_size
                if entry.file_size > MAX_PACKAGE or total > MAX_PACKAGE:
                    raise ValueError("package expanded payload exceeds bound")
                if not entry.is_dir():
                    result[name] = archive.read(entry)
            if require_signature and not result.get(".signature.p7s"):
                raise ValueError("package repository signature is missing")
            return result
    except (zipfile.BadZipFile, OSError, RuntimeError) as error:
        raise ValueError("invalid package archive") from error


def body_inventory(data: bytes) -> dict[str, str]:
    return {name: hashlib.sha256(body).hexdigest() for name, body in members(data).items()
            if name != ".signature.p7s"}


def private_environment(directory: Path) -> dict[str, str]:
    """Do not inherit publication credentials, source configuration or module code."""
    environment = {name: value for name, value in os.environ.items() if name in ("PATH", "LANG", "TMPDIR", "SYSTEMROOT")}
    environment.update(HOME=str(directory), DOTNET_CLI_HOME=str(directory), DOTNET_NOLOGO="true",
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE="true", DOTNET_CLI_TELEMETRY_OPTOUT="true")
    return environment


def bounded_process(arguments, *, cwd, env, capture_output=True, check=False, timeout=120):
    """Bound process output as well as wall time; never emit untrusted output."""
    if os.name != "posix":
        raise ValueError("bounded verification requires POSIX process containment")
    deadline = time.monotonic() + timeout
    process = subprocess.Popen(arguments, cwd=cwd, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, start_new_session=True)
    output = bytearray()
    try:
        os.set_blocking(process.stdout.fileno(), False)
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise subprocess.TimeoutExpired(arguments, timeout)
                if not selector.select(min(remaining, 0.1)):
                    continue
                chunk = os.read(process.stdout.fileno(), 65536)
                if not chunk:
                    break
                output.extend(chunk)
                if len(output) > 1024 * 1024:
                    raise ValueError("verification process output exceeds bound")
        code = process.wait(timeout=max(0.001, deadline - time.monotonic()))
        return SimpleNamespace(returncode=code, stdout=bytes(output))
    finally:
        # A completed leader can leave a child holding stdout. Kill the entire
        # owned group before closing the pipe; no reader thread can block close.
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.stdout.close()
        process.wait(timeout=1)


def verify_signature(data: bytes, *, run=None) -> None:
    members(data, require_signature=True)
    with tempfile.TemporaryDirectory(prefix="hexalith-package-verification-") as temporary:
        directory = Path(temporary)
        archive = directory / "package.nupkg"
        archive.write_bytes(data)
        try:
            result = (run or bounded_process)(["dotnet", "nuget", "verify", str(archive), "--all"],
                cwd=directory, env=private_environment(directory), capture_output=True, check=False, timeout=120)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ValueError("NuGet signature verification failed or timed out") from error
        if result.returncode:
            raise ValueError("NuGet signature verification failed")
