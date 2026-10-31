"""Deny-only classification and exact-byte publication planning.

Candidate bytes stay data. The first external package or Release mutation happens
only after classify returns publish_authorized and the owner-controlled marker is set.
"""

from __future__ import annotations

import json
from typing import Any, Callable
from urllib.parse import urlsplit

from canonical import ContractError, canonical_bytes, expect_version, sha256_bytes
from handoff import durable_asset_names


NUGET_INDEX = "https://api.nuget.org/v3/index.json"
NUGET_HOSTS = frozenset({"api.nuget.org", "www.nuget.org", "globalcdn.nuget.org"})
GITHUB_API = "https://api.github.com"


class Response:
    """One HTTP response observed by the publication transport."""

    def __init__(self, status: int, headers: dict[str, str], body: bytes) -> None:
        self.status = status
        self.headers = {key.lower(): value for key, value in headers.items()}
        self.body = body


class Transport:
    """Data-only HTTP transport. Implementations must not follow a second redirect."""

    def request(self, method: str, url: str, headers: dict[str, str], body: bytes | None) -> Response:
        raise NotImplementedError


class UrllibTransport(Transport):
    """Platform HTTP client that does not follow redirects."""

    def request(self, method: str, url: str, headers: dict[str, str], body: bytes | None) -> Response:
        import urllib.error
        import urllib.request

        class _NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, request, file_pointer, code, message, response_headers, new_url):
                return None

        opener = urllib.request.build_opener(_NoRedirect)
        request = urllib.request.Request(url, data=body, headers=headers, method=method)
        try:
            with opener.open(request, timeout=60) as response:
                return Response(response.status, dict(response.headers), response.read())
        except urllib.error.HTTPError as error:
            return Response(error.code, dict(error.headers), error.read())


def gate_allows(value: str | None) -> bool:
    """Return whether the deny-only publication gate is the literal string true."""

    return value == "true"


def classify(state: dict[str, Any]) -> dict[str, Any]:
    """Return the publisher classification. Builder claims are ignored."""

    denial = None
    authorized = True
    if not gate_allows(state.get("publish_gate")):
        authorized = False
        denial = None
    elif state.get("release_plan") is None:
        authorized = False
    elif state.get("attestation_verified") is not True:
        authorized = False
        denial = state.get("attestation_denial") or "attestation-unavailable"
    elif state.get("manifest_verified") is not True:
        authorized = False
        denial = "manifest-invalid"
    elif state.get("live_verified") is not True:
        authorized = False
        denial = "live-verification-failed"
    elif state.get("bindings_verified") is not True or state.get("archive_verified") is not True:
        authorized = False
        denial = "classification-blocked"
    if state.get("builder_authorizes") is True:
        authorized = False
        denial = denial or "classification-blocked"
    return {"publish_authorized": authorized is True and denial is None and gate_allows(state.get("publish_gate")) and state.get("release_plan") is not None, "prepublication_denial": denial}


def _nuget_url(resource: str) -> None:
    parsed = urlsplit(resource)
    if parsed.scheme != "https" or parsed.hostname not in NUGET_HOSTS or parsed.username or parsed.password or parsed.port not in (None, 443):
        raise ContractError(f"NuGet resource {resource!r} is outside the v3 index trust set")


def nuget_resources(index_payload: bytes) -> dict[str, str]:
    """Read the package publish and content bases from the NuGet v3 index."""

    document = json.loads(index_payload)
    resources = {}
    for row in document.get("resources", []):
        identifier = row.get("@type", "")
        if identifier.startswith("PackagePublish/"):
            resources["publish"] = row["@id"]
        elif identifier.startswith("PackageBaseAddress/"):
            resources["content"] = row["@id"]
    if set(resources) != {"publish", "content"}:
        raise ContractError("NuGet v3 index did not advertise publish and content resources")
    for resource in resources.values():
        _nuget_url(resource)
    return resources


def compare_repository_signed_package(original: bytes, published: bytes) -> None:
    """Require byte-equivalent ZIP members except the root repository signature."""

    from archive_scan import scan_archive

    before = {member.name: member.payload for member in scan_archive(original)}
    after = {member.name: member.payload for member in scan_archive(published)}
    signature = after.pop(".signature.p7s", None)
    if signature is None or not signature:
        raise ContractError("published package has no NuGet.org repository signature")
    if before != after:
        raise ContractError("published package bytes differ beyond the repository signature")


def publish_plan(descriptor: dict[str, Any], authorization_name: str) -> dict[str, Any]:
    """Build the collision-checked asset plan without contacting a service."""

    plan = descriptor["release_plan"]
    if plan is None:
        raise ContractError("publication requires a non-null release plan")
    version = expect_version(plan["version"], "publication version")
    names = [row["asset_name"] for row in descriptor["files"] if row["asset_name"] is not None]
    run = descriptor["release_run"]
    durable = durable_asset_names(run["run_id"], run["run_attempt"], names, authorization_name)
    return {"version": version, "tag": plan["tag"], "assets": durable}


def execute_publication(
    *,
    transport: Transport,
    token: str,
    nuget_api_key: str,
    repository_slug: str,
    candidate: str,
    plan: dict[str, Any],
    notes: str,
    packages: list[tuple[str, str, bytes]],
    assets: list[tuple[str, bytes]],
    mark_started: Callable[[], None],
) -> dict[str, Any]:
    """Publish exact bytes after the caller has set the publication-started marker."""

    owner, repository = repository_slug.split("/", 1)
    tag_probe = transport.request("GET", f"{GITHUB_API}/repos/{owner}/{repository}/git/ref/tags/{plan['tag']}", _github_headers(token), None)
    if tag_probe.status != 404:
        raise ContractError("publication tag already exists")
    release_probe = transport.request("GET", f"{GITHUB_API}/repos/{owner}/{repository}/releases/tags/{plan['tag']}", _github_headers(token), None)
    if release_probe.status != 404:
        raise ContractError("publication release already exists")
    index = transport.request("GET", NUGET_INDEX, {"accept": "application/json"}, None)
    if index.status != 200:
        raise ContractError("NuGet v3 index is unavailable")
    resources = nuget_resources(index.body)
    for package_id, package_name, _payload in packages:
        version = plan["version"].lower()
        content = resources["content"].rstrip("/") + f"/{package_id.lower()}/{version}/{package_id.lower()}.{version}.nupkg"
        _nuget_url(content)
        existing = transport.request("GET", content, {}, None)
        if existing.status != 404:
            raise ContractError(f"NuGet version {plan['version']} already exists for {package_id}")
    mark_started()
    created = transport.request(
        "POST",
        f"{GITHUB_API}/repos/{owner}/{repository}/releases",
        _github_headers(token),
        canonical_bytes(
            {
                "tag_name": plan["tag"],
                "target_commitish": candidate,
                "name": plan["version"],
                "body": notes,
                "draft": True,
                "prerelease": "-" in plan["version"],
            }
        ),
    )
    if created.status not in (200, 201):
        raise ContractError("GitHub draft release was not created")
    release = json.loads(created.body)
    release_id = release["id"]
    upload_url = str(release["upload_url"]).split("{", 1)[0]
    for name, payload in assets:
        uploaded = transport.request(
            "POST",
            f"{upload_url}?name={name}",
            {**_github_headers(token), "content-type": "application/octet-stream"},
            payload,
        )
        if uploaded.status not in (200, 201):
            raise ContractError(f"GitHub Release asset {name} was not uploaded")
    for package_id, package_name, payload in packages:
        pushed = transport.request(
            "PUT",
            resources["publish"],
            {"X-NuGet-ApiKey": nuget_api_key, "content-type": "application/octet-stream"},
            payload,
        )
        if pushed.status not in (200, 201, 202):
            raise ContractError(f"NuGet package {package_name} was not accepted")
        content = resources["content"].rstrip("/") + f"/{package_id.lower()}/{plan['version'].lower()}/{package_id.lower()}.{plan['version'].lower()}.nupkg"
        verified = transport.request("GET", content, {}, None)
        if verified.status != 200:
            raise ContractError(f"published NuGet package {package_name} could not be read back")
        compare_repository_signed_package(payload, verified.body)
    published = transport.request(
        "PATCH",
        f"{GITHUB_API}/repos/{owner}/{repository}/releases/{release_id}",
        _github_headers(token),
        canonical_bytes({"draft": False}),
    )
    if published.status != 200:
        raise ContractError("GitHub Release draft was not published")
    tag = transport.request("GET", f"{GITHUB_API}/repos/{owner}/{repository}/git/ref/tags/{plan['tag']}", _github_headers(token), None)
    if tag.status != 200:
        raise ContractError("published tag could not be read back")
    tag_document = json.loads(tag.body)
    if tag_document.get("object", {}).get("sha") != candidate:
        raise ContractError("published tag does not resolve to the authenticated candidate")
    return {"github_release_id": release_id, "published": True}


def _github_headers(token: str) -> dict[str, str]:
    return {
        "authorization": f"Bearer {token}",
        "accept": "application/vnd.github+json",
        "content-type": "application/json",
        "x-github-api-version": "2022-11-28",
    }


def download_archive(url: str, token: str, limit: int = 512 * 1024 * 1024) -> bytes:
    """Download one raw archive, following at most one credential-free redirect."""

    import urllib.error
    import urllib.request

    class _NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, file_pointer, code, message, headers, new_url):
            return None

    opener = urllib.request.build_opener(_NoRedirect)
    primary = urllib.request.Request(url, headers=artifact_request_headers(token), method="GET")
    try:
        with opener.open(primary, timeout=60) as response:
            return _read_limited(response, limit)
    except urllib.error.HTTPError as error:
        if error.code not in {301, 302, 303, 307, 308}:
            raise ContractError(f"artifact download failed with HTTP {error.code}") from error
        redirected, headers = redirected_artifact_request(error.headers.get("Location", ""))
        secondary = urllib.request.Request(redirected, headers=headers, method="GET")
        try:
            with opener.open(secondary, timeout=60) as response:
                return _read_limited(response, limit)
        except urllib.error.HTTPError as redirected_error:
            raise ContractError("artifact redirect did not return the archive") from redirected_error


def _read_limited(response, limit: int) -> bytes:
    chunks = []
    remaining = limit + 1
    while remaining:
        chunk = response.read(min(1024 * 1024, remaining))
        if not chunk:
            break
        chunks.append(chunk)
        remaining -= len(chunk)
    payload = b"".join(chunks)
    if len(payload) > limit:
        raise ContractError(f"artifact download exceeded the {limit}-byte ceiling")
    return payload


def artifact_request_headers(token: str) -> dict[str, str]:
    """Return the authenticated artifact request headers that must be stripped on redirect."""

    return {"authorization": f"Bearer {token}", "accept": "application/vnd.github+json"}


def redirected_artifact_request(location: str) -> tuple[str, dict[str, str]]:
    """Accept exactly one GitHub-supplied HTTPS redirect with no credential headers."""

    parsed = urlsplit(location)
    if parsed.scheme != "https" or parsed.username or parsed.password or parsed.port not in (None, 443) or not parsed.hostname:
        raise ContractError("artifact redirect is not a credential-free HTTPS URL")
    return location, {}
