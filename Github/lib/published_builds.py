"""Authenticate a historical signed producer independently of the active catalog.

Ported closed handoff controls from the original producer consumer. No module
script, nested checkout, candidate authority or arbitrary endpoint is executed.
"""
from __future__ import annotations
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request

from ci_config import ConfigError, _read_json
from package_archive import MAX_PACKAGE, bounded_process, members, private_environment, verify_signature
from release_draft import bounded, limited_read, strict_json

API = "https://api.github.com/repos/Hexalith/Hexalith.Builds/"

def validate_binding(binding, require_publication=True):
    """Validate either the original inventory binding or the finite shared release handoff."""
    if (not isinstance(binding, dict) or set(binding) != {"schemaVersion", "candidateAcceptance", "producer", "publication"}
            or not isinstance(binding["producer"], dict) or set(binding["producer"]) != {"repository", "sourceRevision", "catalog"}
            or not isinstance(binding["producer"]["catalog"], dict) or set(binding["producer"]["catalog"]) != {"path", "blob", "sha256"}
            or not isinstance(binding["publication"], dict) or binding["publication"].get("kind") != "shared-release-handoff/v1"):
        raise ValueError("Published preparation requires the closed official shared release handoff.")
    if type(binding.get("schemaVersion")) is not int or binding.get("schemaVersion") != 1 or binding.get("candidateAcceptance") is not False:
        raise ValueError("Tuple binding must use schema 1 and candidateAcceptance:false.")
    producer = binding["producer"]
    catalog = producer["catalog"]
    if producer["repository"] != "https://github.com/Hexalith/Hexalith.Builds.git":
        raise ValueError("Tuple producer must be the official Builds repository.")
    for value in (producer["sourceRevision"], catalog["blob"]):
        if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{40}", value):
            raise ValueError("Producer source and catalog blob require full lowercase Git identities.")
    if catalog["path"] != "Props/Directory.Packages.props" or not isinstance(catalog["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", catalog["sha256"]):
        raise ValueError("Producer catalog path and SHA-256 are required.")
    if require_publication:
        publication = binding["publication"]
        common = {"version", "sourceRevision", "releaseTag", "catalogSha256", "archiveSha256", "toolingSha256"}
        shared = publication.get("kind") == "shared-release-handoff/v1"
        if "kind" in publication and not shared:
            raise ValueError("Unsupported official publication kind.")
        required = common | ({"kind", "releaseId", "runId", "artifactId", "artifactSha256", "releaseBodySha256",
                              "manifestJson", "manifestSha256"}
                             if shared else {"inventorySha256", "inventoryUrl"})
        missing = [key for key in required if not publication.get(key)]
        if missing:
            raise ValueError("Official publication binding is unavailable: " + ", ".join(missing) + ". Preparation is unqualified.")
        if set(publication) != required:
            raise ValueError("Unsupported official publication fields.")
        version = publication["version"]
        if not isinstance(version, str) or re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", version) is None:
            raise ValueError("Published preparation requires an exact stable producer version.")
        major, minor, patch = map(int, version.split("."))
        if major < 4 or max(major, minor, patch) > 2147483647:
            raise ValueError("Unsupported producer version.")
        if major >= 5 and not shared:
            raise ValueError("NET11 Builds " + str(major) + " requires the shared release handoff; legacy inventory authority is unavailable.")
        if publication["sourceRevision"] != producer["sourceRevision"] or publication["releaseTag"] != "v" + version:
            raise ValueError("Publication source/tag differs from the exact producer binding.")
        if publication["catalogSha256"] != catalog["sha256"]:
            raise ValueError("Publication catalog differs from the prepared source catalog.")
        digests = ("archiveSha256", "toolingSha256") + (("artifactSha256", "releaseBodySha256", "manifestSha256") if shared else ("inventorySha256",))
        for key in digests:
            if not isinstance(publication[key], str) or not re.fullmatch(r"[0-9a-f]{64}", publication[key]):
                raise ValueError("Publication requires a lowercase SHA-256 for " + key + ".")
        if shared:
            for key in ("releaseId", "runId", "artifactId"):
                if type(publication[key]) is not int or publication[key] <= 0:
                    raise ValueError("Shared release requires a positive numeric " + key + ".")
            raw = publication["manifestJson"]
            if not isinstance(raw, str) or len(raw.encode()) > 4096 or hashlib.sha256(raw.encode()).hexdigest() != publication["manifestSha256"]:
                raise ValueError("Original shared release manifest bytes differ from their exact digest.")
            def unique(pairs):
                result = {}
                for key, value in pairs:
                    if key in result:
                        raise ValueError("Duplicate original shared release manifest key.")
                    result[key] = value
                return result
            try:
                manifest = json.loads(raw, object_pairs_hook=unique)
            except (ValueError, TypeError) as error:
                raise ValueError("Original shared release manifest is invalid.") from error
            expected = {"sha": producer["sourceRevision"], "version": version, "packages": [
                {"id": identity, "path": "packages/" + identity + "." + version + ".nupkg"}
                for identity in ("Hexalith.Builds.Evidence.Cli", "Hexalith.Builds.Module.Cli")], "containers": []}
            if manifest != expected:
                raise ValueError("Original shared release manifest source/version/package closure differs.")
        else:
            expected_inventory = "https://github.com/Hexalith/Hexalith.Builds/releases/download/" + publication["releaseTag"] + "/g4-tool-package-inventory.json"
            if publication["inventoryUrl"] != expected_inventory:
                raise ValueError("Qualification inventory requires the exact official release asset locator.")
    return binding



def verify_handoff(binding, read):
    publication = binding["publication"]
    if publication.get("kind") != "shared-release-handoff/v1":
        return None
    source, tag = binding["producer"]["sourceRevision"], publication["releaseTag"]
    release = read("releases/" + str(publication["releaseId"]))
    body = release.get("body")
    if release.get("id") != publication["releaseId"] or release.get("tag_name") != tag \
            or release.get("draft") is not False or release.get("prerelease") is not False \
            or not release.get("published_at") or not isinstance(body, str) \
            or hashlib.sha256(body.encode()).hexdigest() != publication["releaseBodySha256"]:
        raise ValueError("Official shared release identity or body differs from the bound publication.")
    markers = {"hexalith-release": source,
               "hexalith-release-run": str(publication["runId"]),
               "hexalith-release-artifact": str(publication["artifactId"]) + " sha256:" + publication["artifactSha256"],
               "hexalith-release-packages": "Hexalith.Builds.Evidence.Cli,Hexalith.Builds.Module.Cli",
               "hexalith-release-containers": ""}
    for name, expected in markers.items():
        observed = [line.partition(":")[2].strip() for line in body.splitlines() if line.startswith(name + ":")]
        if observed != [expected]:
            raise ValueError("Official shared release marker differs: " + name)
    reference = read("git/ref/tags/" + tag)["object"]
    for _ in range(4):
        if reference.get("type") == "commit":
            break
        if reference.get("type") != "tag":
            raise ValueError("Official release tag does not identify a commit.")
        reference = read("git/tags/" + reference["sha"])["object"]
    if reference.get("type") != "commit" or reference.get("sha") != source:
        raise ValueError("Official shared release tag differs from the bound producer source.")
    run = read("actions/runs/" + str(publication["runId"]))
    if run.get("id") != publication["runId"] or run.get("head_sha") != source \
            or run.get("status") != "completed" or run.get("conclusion") != "success" \
            or run.get("path") != ".github/workflows/builds-release.yml":
        raise ValueError("Original shared release run is not successful at the bound source.")
    return {"releaseId": publication["releaseId"], "runId": publication["runId"],
            "artifactId": publication["artifactId"], "artifactSha256": publication["artifactSha256"],
            "releaseBodySha256": publication["releaseBodySha256"], "tagSource": source,
            "manifestSha256": publication["manifestSha256"], "artifactDigestSource": "public release body",
            "qualificationCredit": False, "legacyG4AcceptanceClaimed": False}


def read_official(path):
    request = urllib.request.Request(API + path, headers={"User-Agent": "Hexalith-shared-preparation/1.0",
        "Accept": "application/vnd.github+json"})
    deadline = time.monotonic() + 60
    def download():
        with urllib.request.urlopen(request, timeout=30) as response:
            return strict_json(limited_read(response, 2_000_000))
    return bounded(download, deadline=deadline)


def binding(prepared):
    return validate_binding(_read_json(prepared.root / prepared.document["publishedBuilds"]))


def catalog_bytes(builds, source, *, git):
    blob = git(builds, "rev-parse", source + ":Props/Directory.Packages.props")
    try:
        data = subprocess.check_output(["git", "cat-file", "blob", blob], cwd=builds, timeout=30)
    except (OSError, subprocess.SubprocessError) as error:
        raise ConfigError("recorded producer catalog is unavailable") from error
    return blob, hashlib.sha256(data).hexdigest()


def inputs(prepared, authority, *, git, real, pin, tool, digest):
    root = real(prepared.root)
    if (authority.get("schema") != "hexalith.published-build.v1" or authority.get("kind") != "accepted-delivery"
            or "qualification" in authority or authority.get("moduleRoot") != str(root)
            or git(root, "rev-parse", "HEAD") != authority.get("moduleHead")
            or git(root, "write-tree") != git(root, "rev-parse", "HEAD^{tree}")
            or git(root, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all")):
        raise ConfigError("published preparation requires exact clean accepted-delivery inputs")
    result = {"authority": digest(authority), "configuration": digest(prepared.document)}
    for name in (prepared.document["publishedBuilds"], ".github/hexalith-ci.json", ".config/dotnet-tools.json", ".gitmodules"):
        path = real(root / name)
        if not path.is_relative_to(root) or not path.is_file():
            raise ConfigError("published preparation input escapes its repository")
        row = git(root, "ls-tree", "-z", "HEAD", "--", name)
        if not re.fullmatch(r"100(?:644|755) blob [0-9a-f]{40,64}\t" + re.escape(name) + "\x00", row):
            raise ConfigError("published preparation requires committed regular binding inputs")
        result[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    if result[".github/hexalith-ci.json"] != authority.get("configurationDigest") or _read_json(root / ".github/hexalith-ci.json") != prepared.document:
        raise ConfigError("accepted published preparation configuration changed")
    builds = real(root / "references/Hexalith.Builds")
    if (str(builds) != authority.get("buildsRoot")
            or git(builds, "rev-parse", "--show-toplevel") != str(builds)
            or git(builds, "rev-parse", "HEAD") != authority.get("buildsCommit")
            or pin(root, "references/Hexalith.Builds") != authority.get("buildsCommit")
            or git(builds, "rev-parse", "--verify", "refs/tags/" + authority["buildsTag"] + "^{commit}") != authority["buildsCommit"]
            or git(builds, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all")):
        raise ConfigError("exact active Builds checkout/tag changed")
    value = binding(prepared)
    producer, publication = value["producer"], value["publication"]
    if tool(root) != publication["version"] or authority.get("producerPin") != publication["version"]:
        raise ConfigError("historical producer differs from exact tool manifest pin")
    source = producer["sourceRevision"]
    if git(builds, "rev-parse", "--verify", "refs/tags/" + publication["releaseTag"] + "^{commit}") != source:
        raise ConfigError("historical producer release tag/source changed")
    blob, sha = catalog_bytes(builds, source, git=git)
    if blob != producer["catalog"]["blob"] or sha != producer["catalog"]["sha256"]:
        raise ConfigError("historical producer catalog blob/digest differs")
    active_blob, active_sha = catalog_bytes(builds, authority["buildsCommit"], git=git)
    result.update(activeCatalogBlob=active_blob, activeCatalogSha256=active_sha,
        historicalSource=source, historicalCatalogBlob=blob, historicalCatalogSha256=sha)
    return result


def prepare(prepared, workspace, *, read=read_official, download=None, run=None):
    value = binding(prepared)
    if int(value["publication"]["version"].split(".")[0]) < 5:
        raise ConfigError("Module preparation requires a published .NET 11 Builds tool.")
    verify_handoff(value, read)
    publication = value["publication"]
    version, source = publication["version"], value["producer"]["sourceRevision"]
    if download is None:
        def download(url):
            deadline = time.monotonic() + 120
            def request():
                with urllib.request.urlopen(url, timeout=30) as response:
                    return limited_read(response, MAX_PACKAGE)
            return bounded(request, deadline=deadline)
    name = "hexalith.builds.module.cli." + version + ".nupkg"
    data = download("https://api.nuget.org/v3-flatcontainer/hexalith.builds.module.cli/" + version + "/" + name)
    if hashlib.sha256(data).hexdigest() != publication["archiveSha256"]:
        raise ConfigError("historical signed producer archive SHA-256 differs")
    verify_signature(data, run=run)
    files = members(data, require_signature=True)
    workspace.mkdir()
    (workspace / name).write_bytes(data)
    extracted = workspace / "published"
    for name, body in files.items():
        path = extracted / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(body)
    prefix = "tools/net11.0/any/"
    tool = extracted / prefix
    if hashlib.sha256(files.get(prefix + "Hexalith.Builds.Tooling.dll", b"")).hexdigest() != publication["toolingSha256"]:
        raise ConfigError("historical Tooling assembly SHA-256 differs")
    for required in ("Hexalith.Builds.Module.Cli.dll", "g4-host/bin/EventStoreHost/Hexalith.Builds.Module.EventStoreHost.dll"):
        if not (tool / required).is_file():
            raise ConfigError("historical producer required assembly is missing")
    try:
        result = (run or bounded_process)(["dotnet", str(tool / "Hexalith.Builds.Module.Cli.dll"), "--version"],
            cwd=workspace, env=private_environment(workspace), capture_output=True, check=False, timeout=60)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ConfigError("historical producer executable unavailable or timed out") from error
    output = result.stdout.decode() if isinstance(result.stdout, bytes) else result.stdout
    if result.returncode or len(output) > 4096 or output.strip() != version + "+" + source:
        raise ConfigError("historical producer executable source/version differs")
    return {"HexalithBuildsPublishedToolRoot": str(tool), "Configuration": "Release"}
