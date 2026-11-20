"""Keep Platform package declarations bound to the same release as the Builds catalog."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
from urllib.request import urlopen
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
INDEX = ROOT / "Props/release-declarations.json"
MANIFEST = "module/hexalith.module-manifest.v2.json"
OWNERS = {"eventstore": ("EventStore", "Aspire"), "tenants": ("Tenants", "Server"),
          "memories": ("Memories", "Aspire"), "parties": ("Parties", "Contracts")}
VERSION = re.compile(r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2,3}")


def fetch(url):
    with urlopen(url, timeout=30) as response:
        return response.read()


def release_commit(repository, version):
    tag = "refs/tags/v" + version
    listing = subprocess.check_output(["git", "ls-remote", "--tags",
        "https://github.com/Hexalith/" + repository + ".git", tag, tag + "^{}"], text=True, timeout=40)
    refs = {name: sha for sha, name in (line.split() for line in listing.splitlines())}
    return refs.get(tag + "^{}", refs.get(tag))


def resolve(owner, version, read=fetch, tag_commit=release_commit):
    if not VERSION.fullmatch(version):
        raise ValueError("A stable release is required")
    name, suffix = OWNERS[owner]
    repo = "Hexalith." + name
    package = (repo + "." + suffix).lower()
    metadata = ET.fromstring(read(f"https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.nuspec"))
    if metadata.findtext(".//{*}id", "").lower() != package or metadata.findtext(".//{*}version") != version:
        raise ValueError(f"Unexpected NuGet package identity for {repo} {version}")
    git = metadata.find(".//{*}repository")
    expected_url = "https://github.com/Hexalith/" + repo
    if git is None or git.get("type") != "git" or git.get("url", "").removesuffix(".git") != expected_url:
        raise ValueError(f"Unexpected NuGet source repository for {repo} {version}")
    commit = git.get("commit", "")
    if not re.fullmatch("[0-9a-f]{40}", commit) or tag_commit(repo, version) != commit:
        raise ValueError(f"NuGet source and Git release tag disagree for {repo} {version}")
    body = read(f"https://raw.githubusercontent.com/Hexalith/{repo}/{commit}/{MANIFEST}")
    declaration = json.loads(body)
    if declaration["schema"] != "hexalith.module-manifest.v2" or [m["identity"]["moduleId"] for m in declaration["modules"]] != [owner]:
        raise ValueError(f"Unexpected released declaration for {repo} {version}")
    return {"repository": repo, "version": version, "commit": commit, "source": MANIFEST,
            "sha256": hashlib.sha256(body).hexdigest()}, body


def encoded(value):
    return (json.dumps(value, indent=2) + "\n").encode()


def record(owner, version):
    entry, _ = resolve(owner, version)
    data = json.loads(INDEX.read_bytes())
    key = owner + "/" + version
    if key in data and data[key] != entry:
        raise ValueError(f"Published declaration changed for {key}")
    data[key] = entry
    INDEX.write_bytes(encoded(dict(sorted(data.items()))))


def sync_platform(platform_root, check=False):
    catalog = ET.parse(ROOT / "Props/Directory.Packages.props")
    directory = platform_root / "src/Hexalith.Platform.Composition/Declarations"
    binding_path = platform_root / "eng/current-tuple-source.json"
    binding = json.loads(binding_path.read_bytes())
    released = json.loads(INDEX.read_bytes())
    provenance = {}
    manifests = {}
    for owner in ("eventstore", "tenants", "memories"):
        member = "Hexalith" + OWNERS[owner][0] + "Version"
        pins = catalog.findall("PropertyGroup/" + member)
        if len(pins) != 1 or not pins[0].text or not VERSION.fullmatch(pins[0].text):
            raise ValueError(f"Expected one exact catalog selection for {member}")
        version = pins[0].text
        entry = released[owner + "/" + version]
        if check:
            body = (directory / (owner + ".json")).read_bytes()
            if hashlib.sha256(body).hexdigest() != entry["sha256"]:
                raise ValueError(f"The Platform snapshot differs from {owner} {version}")
        else:
            published, body = resolve(owner, version)
            if published != entry:
                raise ValueError(f"Release authority differs from the catalog for {owner} {version}")
        provenance[owner], manifests[owner] = entry, body
    metadata = encoded(provenance)
    digest = hashlib.sha256(metadata).hexdigest()
    if check:
        if metadata != (directory / "provenance.json").read_bytes() or binding["packageProvenanceSha256"] != digest:
            raise ValueError("Platform release provenance differs from the selected catalog")
        return
    binding["packageProvenanceSha256"] = digest
    for owner, body in manifests.items():
        (directory / (owner + ".json")).write_bytes(body)
    (directory / "provenance.json").write_bytes(metadata)
    binding_path.write_bytes(encoded(binding))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sync-platform", required=True, type=Path)
    parser.add_argument("--check", action="store_true", help="Verify the tracked snapshots offline")
    arguments = parser.parse_args()
    sync_platform(arguments.sync_platform, check=arguments.check)
