#!/usr/bin/env python3
"""Temporary story 3-4 original-byte hosted producer; never a production caller."""
from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Github/lib"))
from release_build import release_flags
from release_state import Git, highest_reachable_stable_tag, outputs
from oci_archive import combine_archives, inspect_archive


def main():
    sha = os.environ["GITHUB_SHA"]
    base = highest_reachable_stable_tag(Git(ROOT), sha)
    major, minor, patch = map(int, base[1:].split(".")) if base else (1, 0, -1)
    version = f"{major}.{minor}.{patch + 1}"
    destination = Path(os.environ["RUNNER_TEMP"]) / "publish-probe-payload"
    (destination / "packages").mkdir(parents=True)
    (destination / "containers").mkdir()
    package_id = "Hexalith.Builds.PublishProbe.20261111"
    image = "cicd-publish-spike-3-1"
    flags = release_flags(version, sha, os.environ["GITHUB_REPOSITORY"])
    with tempfile.TemporaryDirectory(prefix="publish-probe-build-") as temporary:
        work = Path(temporary)
        shutil.copyfile(ROOT / "global.json", work / "global.json")
        project = work / "PublishProbe.csproj"
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <PackageId>{package_id}</PackageId><Authors>Hexalith</Authors>
    <Description>Disposable story 3-4 original publication qualification.</Description>
    <RuntimeIdentifiers>linux-x64;linux-arm64</RuntimeIdentifiers>
    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>
    <ContainerRepository>{image}</ContainerRepository>
    <ContainerBaseImage>mcr.microsoft.com/dotnet/runtime:10.0-alpine</ContainerBaseImage>
    <ContainerUser>app</ContainerUser><DebugType>none</DebugType>
  </PropertyGroup>
</Project>
''')
        (work / "Program.cs").write_text('System.Console.WriteLine("Disposable original publication probe");\n')
        subprocess.run(["dotnet", "restore", str(project), *flags], cwd=work, check=True)
        archives = []
        for rid, architecture in (("linux-x64", "amd64"), ("linux-arm64", "arm64")):
            archive = work / (architecture + ".tar")
            subprocess.run(["dotnet", "publish", str(project), "--no-restore", "-t:PublishContainer", "--runtime", rid,
                            f"-p:ContainerArchiveOutputPath={archive}", *flags], cwd=work, check=True)
            archives.append((archive, {"os": "linux", "architecture": architecture}))
        combined = destination / "containers/probe.tar"
        graph = combine_archives(archives, combined, revision=sha, version=version,
                                 repository=os.environ["GITHUB_REPOSITORY"], name=image)
        subprocess.run(["dotnet", "pack", str(project), "--no-build", "--no-restore", "-p:RuntimeIdentifier=linux-x64",
                        "--output", str(destination / "packages"), *flags], cwd=work, check=True)
    manifest = {"sha": sha, "version": version,
        "packages": [{"id": package_id, "path": f"packages/{package_id}.{version}.nupkg"}],
        "containers": [{"name": image, "path": "containers/probe.tar"}]}
    (destination / "release-manifest.json").write_text(json.dumps(manifest) + "\n")
    print(json.dumps({"manifest": manifest, "graph": graph}, indent=2))
    outputs({"version": version, "last-tag": base})


if __name__ == "__main__":
    main()
