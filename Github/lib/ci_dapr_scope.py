"""Own a local qualification's Dapr processes without touching a user's install.

Host ports are part of the product contract. An occupied control plane refuses;
network-only initialization would silently change the retained runtime checks.
"""
from __future__ import annotations

from contextlib import contextmanager
import json
import os
import platform
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import uuid

from ci_environment import dapr_versions, download_dapr_cli
from ci_pipeline import PipelineError

NAMES = ("dapr_placement", "dapr_scheduler", "dapr_redis", "dapr_zipkin")
PORTS = (6379, 9411, 50005, 58080, 59090, 50006, 2379, 58081, 59091)
LABEL = "com.hexalith.cicd.qualification"


def command(arguments: list[str], root: Path) -> str:
    try:
        with subprocess.Popen(arguments, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              text=True, start_new_session=True) as process:
            try:
                output, error_output = process.communicate(timeout=180)
            finally:
                # The CLI can return its first init error while Docker children
                # still run. Quiesce this entire invocation before owned cleanup.
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                if process.poll() is None:
                    process.wait(timeout=10)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise PipelineError(f"isolated Dapr command failed: {arguments[0]}: {error}") from error
    if process.returncode:
        raise PipelineError(f"isolated Dapr {arguments[1]} failed ({process.returncode}): {error_output}")
    return output.strip()


def available_ports() -> None:
    sockets = []
    try:
        for port in PORTS:
            listener = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
            sockets.append(listener)
            listener.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
            listener.bind(("::", port))
    except OSError as error:
        raise PipelineError(f"local qualification requires an unused Dapr control plane: {error}") from error
    finally:
        for listener in sockets:
            listener.close()


def containers(docker: str, root: Path) -> list[dict]:
    identifiers = command([docker, "ps", "-aq"], root).splitlines()
    return json.loads(command([docker, "inspect", *identifiers], root)) if identifiers else []


def write_shims(directory: Path, docker: str, binary: Path, runtime: Path, token: str) -> None:
    """Bind child CLI execution and label only the four init-created containers."""
    directory.mkdir()
    dapr = directory / "dapr"
    dapr.write_text(f'''#!{sys.executable}
import os, sys
args = sys.argv[1:]
if any(value == '--runtime-path' or value.startswith('--runtime-path=') for value in args) or any(value in ('init', 'uninstall') for value in args):
    raise SystemExit('qualification children cannot replace or mutate their Dapr installation')
os.execv({str(binary)!r}, [{str(binary)!r}, '--runtime-path', {str(runtime)!r}, *args])
''')
    dapr.chmod(0o755)
    shim = directory / "docker"
    shim.write_text(f'''#!{sys.executable}
import json, os, subprocess, sys
args = sys.argv[1:]
if args and args[0] in ('run', 'create'):
    names = [args[index + 1] for index, value in enumerate(args[:-1]) if value == '--name']
    names += [value.split('=', 1)[1] for value in args if value.startswith('--name=')]
    if any(name in {NAMES!r} for name in names):
        if len(names) != 1 or any({LABEL!r} in value for value in args):
            raise SystemExit('invalid Dapr qualification ownership')
        args[1:1] = ['--label', {LABEL + '=' + token!r}]
if args and args[0] == 'start' and any(value in {NAMES!r} for value in args[1:]):
    for name in args[1:]:
        data = json.loads(subprocess.check_output([{docker!r}, 'inspect', name], text=True))
        if len(data) != 1 or (data[0]['Config'].get('Labels') or {{}}).get({LABEL!r}) != {token!r}:
            raise SystemExit('refusing to start a foreign Dapr container')
os.execv({docker!r}, [{docker!r}, *args])
''')
    shim.chmod(0o755)


@contextmanager
def isolated_dapr(root: Path, temporary: Path):
    """Set up the exact CLI/runtime; restore PATH and remove only owned IDs."""
    if platform.system() != "Linux":
        raise PipelineError("isolated local Dapr qualification requires Linux")
    docker = shutil.which("docker")
    if docker is None:
        raise PipelineError("isolated Dapr requires Docker")
    docker = str(Path(docker).resolve(strict=True))
    existing = containers(docker, root)
    if any(item["Name"].removeprefix("/") in NAMES for item in existing):
        raise PipelineError("local qualification refuses an existing user Dapr control plane")
    available_ports()
    token = uuid.uuid4().hex
    volume = "hexalith-qualification-" + token
    previous_path = os.environ.get("PATH")
    volume_attempted = False
    with tempfile.TemporaryDirectory(prefix="dapr-qualification-", dir=temporary) as directory:
        private = Path(directory)
        runtime = private / "runtime"
        runtime.mkdir()
        try:
            volume_attempted = True
            command([docker, "volume", "create", "--label", LABEL + "=" + token, volume], root)
            binary, version = download_dapr_cli(private / "verified-cli")
            write_shims(private / "bin", docker, binary, runtime, token)
            os.environ["PATH"] = str(private / "bin") + os.pathsep + (previous_path or "")
            command([str(binary), "init", "--runtime-version", version, "--runtime-path", str(runtime), "--scheduler-volume", volume, "--container-runtime", "docker"], root)
            observed = containers(docker, root)
            owned = [item for item in observed if (item["Config"].get("Labels") or {}).get(LABEL) == token]
            if {item["Name"].removeprefix("/") for item in owned} != set(NAMES):
                raise PipelineError("isolated Dapr did not create its four owned control-plane containers")
            result = command([str(private / "bin/dapr"), "--version"], root)
            _, cli_version = dapr_versions()
            if f"CLI version: {cli_version}" not in result or f"Runtime version: {version}" not in result:
                raise PipelineError("isolated Dapr executable/runtime differs from the executing Builds pins")
            yield {"cli": str(binary), "runtimePath": str(runtime), "version": version,
                   "containers": sorted(item["Id"] for item in owned), "schedulerVolume": volume}
        finally:
            if previous_path is None:
                os.environ.pop("PATH", None)
            else:
                os.environ["PATH"] = previous_path
            # Explicit IDs and ownership labels prevent cleanup from selecting
            # an existing or concurrently replaced user's named container.
            failures = []
            try:
                for item in containers(docker, root):
                    if item["Name"].removeprefix("/") in NAMES and (item["Config"].get("Labels") or {}).get(LABEL) == token:
                        command([docker, "rm", "-f", "--volumes", item["Id"]], root)
            except PipelineError as error:
                failures.append(str(error))
            if volume_attempted:
                try:
                    names = command([docker, "volume", "ls", "--format", "{{.Name}}"], root).splitlines()
                    if volume in names:
                        data = json.loads(command([docker, "volume", "inspect", volume], root))
                        if len(data) != 1 or (data[0].get("Labels") or {}).get(LABEL) != token:
                            raise PipelineError("qualification scheduler volume ownership changed; refusing deletion")
                        command([docker, "volume", "rm", volume], root)
                except PipelineError as error:
                    failures.append(str(error))
            if failures:
                raise PipelineError("isolated Dapr cleanup failed: " + "; ".join(failures))
