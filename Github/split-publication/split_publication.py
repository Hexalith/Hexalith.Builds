#!/usr/bin/env python3
"""Owner-controlled commands for the split-publication-v1 reusable."""

from __future__ import annotations

import os
import sys
import zipfile
from pathlib import Path

_ACTION_ROOT = Path(__file__).resolve().parent
if str(_ACTION_ROOT) not in sys.path:
    sys.path.insert(0, str(_ACTION_ROOT))

from archive_scan import materialize_archive, scan_archive
import attestation
from attestation import HELPER_VERSION, subject_checksums
from canonical import ContractError, canonical_bytes, expect_commit, expect_sha256, expect_version, parse_json, sha256_bytes
from descriptor import DESCRIPTOR_SCHEMA, validate_descriptor
import graph
from handoff import build_verification_handoff
import publication


__version__ = HELPER_VERSION


def _env(name: str) -> str:
    value = os.environ.get(name, "")
    if not value:
        raise ContractError(f"required publisher input {name} is empty")
    return value


def prepare_candidate(package_directory: Path, output_directory: Path, metadata: dict) -> dict:
    """Stage one descriptor and its regular files. This output does not authorize publication."""

    output_directory.mkdir(parents=True, exist_ok=False)
    packages = sorted(path for path in package_directory.glob("*.nupkg") if path.is_file() and not path.is_symlink())
    symbols = sorted(path for path in package_directory.glob("*.snupkg") if path.is_file() and not path.is_symlink())
    files = []
    version = metadata.get("version") or ""
    if version:
        expect_version(version, "candidate version")
        if len(packages) < 1 or len(packages) != len(symbols):
            raise ContractError("a releasable candidate requires one symbol package for every package")
    destination_packages = output_directory / "packages"
    destination_packages.mkdir()
    paired = zip(packages, symbols, strict=True) if version else []
    for package, symbol in paired:
        for source, role in ((package, "package"), (symbol, "symbol")):
            payload = source.read_bytes()
            target = destination_packages / source.name
            target.write_bytes(payload)
            files.append(
                {
                    "path": f"packages/{source.name}",
                    "asset_name": source.name,
                    "sha256": sha256_bytes(payload),
                    "size": len(payload),
                    "role": role,
                }
            )
    evidence = output_directory / "evidence"
    evidence.mkdir()
    diagnostic = canonical_bytes(
        {"schema": "hexalith.builds-publication-diagnostic.v1", "authorizes": False, "checks": ["inventory", "symbols", "checksums", "sbom"]}
    )
    sbom = canonical_bytes(
        {
            "spdxVersion": "SPDX-2.3",
            "dataLicense": "CC0-1.0",
            "SPDXID": "SPDXRef-DOCUMENT",
            "name": "hexalith-publication-candidate",
            "documentNamespace": f"https://github.com/{metadata['repository_slug']}/publication-candidate/{metadata['run_id']}/{metadata['run_attempt']}",
            "packages": [{"name": row["asset_name"], "checksum": row["sha256"]} for row in files if row["role"] == "package"],
        }
    )
    for name, payload in (("early-denial.json", diagnostic), ("sbom.spdx.json", sbom)):
        (evidence / name).write_bytes(payload)
        files.append(
            {
                "path": f"evidence/{name}",
                "asset_name": name,
                "sha256": sha256_bytes(payload),
                "size": len(payload),
                "role": "evidence",
            }
        )
    plan = None
    if version:
        notes = metadata.get("notes") or f"Release {version}\n"
        notes_bytes = notes.encode("utf-8")
        (output_directory / "release-notes.md").write_bytes(notes_bytes)
        files.append(
            {
                "path": "release-notes.md",
                "asset_name": None,
                "sha256": sha256_bytes(notes_bytes),
                "size": len(notes_bytes),
                "role": "release-metadata",
            }
        )
        plan = {
            "version": version,
            "tag": f"v{version}",
            "release_name": version,
            "release_notes_path": "release-notes.md",
        }
    files.sort(key=lambda row: row["path"])
    descriptor = {
        "schema": DESCRIPTOR_SCHEMA,
        "release_run": metadata["release_run"],
        "candidate": metadata["candidate"],
        "ci_handoff_sha256": metadata["ci_handoff_sha256"],
        "dependency_policy": metadata["dependency_policy"],
        "evaluator": metadata["evaluator"],
        "release_plan": plan,
        "files": files,
    }
    payload = canonical_bytes(descriptor)
    (output_directory / "publication-candidate.json").write_bytes(payload)
    validated = validate_descriptor(parse_json(payload, "publication candidate", max_bytes=16 * 1024 * 1024))
    return {"descriptor_sha256": sha256_bytes(payload), "release_plan_present": plan is not None, "version": version, "descriptor": validated}


def authenticate_directory(archive: bytes) -> dict[str, bytes]:
    """Return authenticated archive members and refuse to execute any of them."""

    members = {member.name: member.payload for member in scan_archive(archive)}
    if "publication-candidate.json" not in members:
        raise ContractError("publication archive has no publication-candidate.json")
    marker = Path(os.environ.get("SPLIT_EXECUTION_MARKER", ""))
    if marker:
        if marker.exists():
            raise ContractError("candidate bytes executed before archive authentication")
    descriptor = parse_json(members["publication-candidate.json"], "publication candidate", max_bytes=16 * 1024 * 1024)
    validate_descriptor(descriptor, members)
    return members


def classify_authenticated(directory: Path, handoff_path: Path, bundle_path: Path | None, state: dict) -> dict:
    """Seal and classify from authenticated files. Builder diagnostics cannot authorize."""

    from handoff import validate_ci_handoff
    import manifest as manifest_module

    members = {}
    for path in directory.rglob("*"):
        if path.is_symlink() or not path.is_file():
            continue
        relative = path.relative_to(directory).as_posix()
        members[relative] = path.read_bytes()
    descriptor = validate_descriptor(
        parse_json(members["publication-candidate.json"], "publication candidate", max_bytes=16 * 1024 * 1024),
        members,
    )
    handoff_bytes = handoff_path.read_bytes()
    handoff = validate_ci_handoff(
        parse_json(handoff_bytes, "CI handoff", max_bytes=16 * 1024 * 1024),
        handoff_bytes,
    )
    if handoff["evidence_sha256"] != descriptor["ci_handoff_sha256"] or handoff["run"]["candidate"] != descriptor["candidate"]:
        raise ContractError("authenticated CI handoff does not match the publication candidate")
    diagnostic = members.get("evidence/early-denial.json")
    builder_authorizes = False
    if diagnostic is not None and b'"authorizes":true' in diagnostic.replace(b" ", b""):
        builder_authorizes = True
    attestation_verified = False
    attestation_projection = None
    if bundle_path is not None and bundle_path.is_file():
        bundle_bytes = bundle_path.read_bytes()
        bundle = parse_json(bundle_bytes, "attestation bundle", max_bytes=16 * 1024 * 1024)
        _payload, _signature, statement = attestation.decode_statement(bundle)
        attestation.validate_statement(statement, state["attestation_expected"])
        attestation_verified = True
        attestation_projection = {
            "status": "attested",
            "bundle": {"path": "provenance-attestation.json", "sha256": sha256_bytes(bundle_bytes)},
            "fallback": None,
        }
    if attestation_projection is None:
        result = publication.classify({**state, "attestation_verified": False, "builder_authorizes": builder_authorizes})
        return result
    sealed = manifest_module.seal_manifest(
        dependency_graph=handoff["dependency_graph"],
        dependency_policy=descriptor["dependency_policy"],
        quality_run=state["quality_run"],
        attestation=attestation_projection,
        workflow_provenance=state["workflow_provenance"],
        candidate=descriptor["candidate"],
        sealed_at=state["sealed_at"],
    )
    result = publication.classify(
        {
            "publish_gate": state["publish_gate"],
            "release_plan": descriptor["release_plan"],
            "attestation_verified": attestation_verified,
            "manifest_verified": True,
            "live_verified": state["live_verified"],
            "bindings_verified": True,
            "archive_verified": True,
            "builder_authorizes": builder_authorizes,
        }
    )
    manifest_bytes = canonical_bytes(sealed)
    result["manifest_sha256"] = sha256_bytes(manifest_bytes)
    result["manifest_seal"] = sealed["seal"]["hash"]
    result["attestation_status"] = attestation_projection["status"]
    result["manifest"] = sealed
    return result


def inventory_closure(workflow: Path, action_root: Path) -> dict:
    """List the statically reachable split-publication executable closure."""

    text = workflow.read_text(encoding="utf-8")
    uses = []
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("uses:"):
            uses.append(stripped.split(":", 1)[1].strip())
    helpers = sorted(
        path.relative_to(action_root.parent.parent).as_posix()
        for path in action_root.rglob("*")
        if path.is_file() and path.suffix not in {".pyc", ".pyo"} and "__pycache__" not in path.parts
    )
    return {"uses": uses, "helpers": helpers}


def main(argv: list[str] | None = None) -> int:
    """Dispatch one literal owner-controlled command."""

    arguments = list(sys.argv[1:] if argv is None else argv)
    if len(arguments) != 1:
        print("split-publication requires exactly one command", file=sys.stderr)
        return 1
    command = arguments[0]
    try:
        if command == "prepare-candidate":
            metadata = parse_json(Path(_env("SPLIT_METADATA")).read_bytes(), "candidate metadata", max_bytes=16 * 1024 * 1024)
            result = prepare_candidate(Path(_env("SPLIT_PACKAGE_DIRECTORY")), Path(_env("SPLIT_OUTPUT")), metadata)
            print(canonical_bytes({"descriptor_sha256": result["descriptor_sha256"], "release_plan_present": result["release_plan_present"], "version": result["version"]}).decode("ascii"))
            return 0
        if command == "download-archive":
            expected = _env("SPLIT_EXPECTED_SHA256").removeprefix("sha256:")
            expect_sha256(expected, "artifact digest")
            payload = publication.download_archive(_env("SPLIT_ARTIFACT_URL"), _env("SPLIT_TOKEN"))
            if sha256_bytes(payload) != expected:
                raise ContractError("downloaded artifact digest does not match the authenticated upload digest")
            Path(_env("SPLIT_OUTPUT")).write_bytes(payload)
            return 0
        if command == "extract-member":
            members = {member.name: member.payload for member in scan_archive(Path(_env("SPLIT_ARCHIVE")).read_bytes())}
            name = _env("SPLIT_MEMBER")
            if name not in members:
                raise ContractError(f"authenticated archive has no member {name}")
            Path(_env("SPLIT_OUTPUT")).write_bytes(members[name])
            return 0
        if command == "verify-live":
            handoff_bytes = Path(_env("SPLIT_HANDOFF")).read_bytes()
            handoff = parse_json(handoff_bytes, "CI handoff", max_bytes=16 * 1024 * 1024)
            stores = {
                identity.name: identity
                for identity in Path(_env("SPLIT_OBJECT_STORE")).iterdir()
                if identity.is_dir()
            }
            # Identity directories are nested github.com/owner/repository.
            stores = {}
            root = Path(_env("SPLIT_OBJECT_STORE"))
            for owner in root.glob("*/*/*"):
                if owner.is_dir():
                    stores["/".join(owner.relative_to(root).parts)] = owner
            policy = parse_json(Path(_env("SPLIT_METADATA")).read_bytes(), "active policy", max_bytes=16 * 1024 * 1024)
            trusted = {row["identity"] for row in policy["trusted_identities"]}
            graph.verify_live(graph.GitDirectoryReader(stores), handoff["dependency_graph"], trusted)
            return 0
        if command == "release-authenticated":
            bundle = Path(os.environ["SPLIT_BUNDLE"]) if os.environ.get("SPLIT_BUNDLE") else None
            state = parse_json(Path(_env("SPLIT_METADATA")).read_bytes(), "publication state", max_bytes=16 * 1024 * 1024)
            handoff_bytes = Path(_env("SPLIT_HANDOFF")).read_bytes()
            handoff = parse_json(handoff_bytes, "CI handoff", max_bytes=16 * 1024 * 1024)
            stores = {}
            root = Path(_env("SPLIT_OBJECT_STORE"))
            for owner in root.glob("*/*/*"):
                if owner.is_dir():
                    stores["/".join(owner.relative_to(root).parts)] = owner
            graph.verify_live(graph.GitDirectoryReader(stores), handoff["dependency_graph"], {row["identity"] for row in state["policy"]["trusted_identities"]})
            state["live_verified"] = True
            directory = Path(_env("SPLIT_OUTPUT"))
            result = classify_authenticated(directory, Path(_env("SPLIT_HANDOFF")), bundle, state)
            if not result.get("publish_authorized"):
                print(canonical_bytes(result).decode("ascii"), file=sys.stderr)
                return 1
            descriptor = validate_descriptor(parse_json((directory / "publication-candidate.json").read_bytes(), "publication candidate", max_bytes=16 * 1024 * 1024))
            version = descriptor["release_plan"]["version"]
            packages = []
            assets = []
            for row in descriptor["files"]:
                payload = (directory / row["path"]).read_bytes()
                if row["asset_name"] is not None:
                    assets.append((row["asset_name"], payload))
                if row["role"] == "package":
                    suffix = f".{version}.nupkg"
                    if not row["asset_name"].endswith(suffix):
                        raise ContractError("package asset name does not end with the release version")
                    packages.append((row["asset_name"][: -len(suffix)], row["asset_name"], payload))
            notes = (directory / descriptor["release_plan"]["release_notes_path"]).read_text(encoding="utf-8")
            archive_name = f"publication-candidate-{descriptor['release_run']['run_id']}-{descriptor['release_run']['run_attempt']}.zip"
            assets.append((archive_name, Path(_env("SPLIT_ARCHIVE")).read_bytes()))
            assets.append(("publication-candidate.json", (directory / "publication-candidate.json").read_bytes()))
            assets.append(("dependency-release-handoff.json", handoff_bytes))
            assets.append(("release-evidence.json", canonical_bytes(result["manifest"])))
            authorization = "provenance-attestation.json" if result["attestation_status"] == "attested" else "attestation-fallback-authorization.json"
            assets.append((authorization, bundle.read_bytes() if bundle else b""))
            from handoff import durable_asset_names
            durable_asset_names(
                descriptor["release_run"]["run_id"],
                descriptor["release_run"]["run_attempt"],
                [row["asset_name"] for row in descriptor["files"] if row["asset_name"] is not None],
                authorization,
            )

            def mark() -> None:
                Path("publication-started.json").write_bytes(canonical_bytes({"schema": "hexalith.publication-started.v1", "publication_started": True}))

            published = publication.execute_publication(
                transport=publication.UrllibTransport(),
                token=_env("SPLIT_TOKEN"),
                nuget_api_key=_env("SPLIT_NUGET_API_KEY"),
                repository_slug=_env("SPLIT_REPOSITORY_SLUG"),
                candidate=descriptor["candidate"],
                plan={"version": version, "tag": descriptor["release_plan"]["tag"]},
                notes=notes,
                packages=packages,
                assets=assets,
                mark_started=mark,
            )
            print(canonical_bytes({"publish_authorized": True, "github_release_id": published["github_release_id"]}).decode("ascii"))
            return 0
        if command == "authenticate-archive":
            archive = Path(_env("SPLIT_ARCHIVE")).read_bytes()
            members = authenticate_directory(archive)
            destination = Path(_env("SPLIT_OUTPUT"))
            materialize_archive(archive, destination)
            packages = []
            descriptor = parse_json(members["publication-candidate.json"], "publication candidate", max_bytes=16 * 1024 * 1024)
            validated = validate_descriptor(descriptor, members)
            for row in validated["files"]:
                if row["role"] == "package":
                    packages.append((row["asset_name"], row["sha256"]))
            Path(_env("SPLIT_SUBJECTS")).write_bytes(subject_checksums(packages))
            return 0
        if command == "emit-handoff":
            document = build_verification_handoff(
                parse_json(Path(_env("SPLIT_METADATA")).read_bytes(), "verification handoff", max_bytes=16 * 1024 * 1024)
            )
            Path(_env("SPLIT_OUTPUT")).write_bytes(canonical_bytes(document))
            return 0
        if command == "inventory-closure":
            document = inventory_closure(Path(_env("SPLIT_WORKFLOW")), _ACTION_ROOT)
            sys.stdout.buffer.write(canonical_bytes(document))
            return 0
        if command == "helper-version":
            sys.stdout.write(__version__)
            return 0
    except (ContractError, OSError, zipfile.BadZipFile) as error:
        print(str(error), file=sys.stderr)
        return 1
    print(f"unknown split-publication command {command!r}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
