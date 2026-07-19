#!/usr/bin/env bash
set -euo pipefail

version="${1:-}"
registry="${HEXALITH_ZOT_REGISTRY:-registry.hexalith.com}"
projects="${HEXALITH_CONTAINER_PROJECTS:-}"
username="${HEXALITH_ZOT_USERNAME:-}"
api_key="${HEXALITH_ZOT_API_KEY:-}"
runtime_identifiers="linux-musl-x64;linux-musl-arm64"
validator="${HEXALITH_OCI_VALIDATOR:-$(dirname "$0")/oci_registry_validator.py}"
smoke="${HEXALITH_CONTAINER_SMOKE:-$(dirname "$0")/smoke-container-platforms.sh}"
authority_validator="${HEXALITH_PUBLICATION_AUTHORITY_VALIDATOR:-$(dirname "$0")/publication_authority.py}"
authority_url="${HEXALITH_RELEASE_AUTHORITY_URL:-}"
builds_execution_sha="${HEXALITH_BUILDS_EXECUTION_SHA:-}"
source_sha="${GITHUB_SHA:-}"
release_repository="${GITHUB_REPOSITORY:-}"
role_allowlist="${HEXALITH_RELEASE_OWNER_ALLOWLIST_PATH:-}"
evidence_directory="${HEXALITH_CONTAINER_EVIDENCE_DIRECTORY:-$PWD/.hexalith/release-evidence/$version}"
semver_pattern='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'

trim() {
  local value="$1"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "$value"
}

fail() {
  echo "[publish-containers] $1" >&2
  exit 1
}

[ -n "$version" ] || fail "Release version argument is required."
[[ "$version" =~ $semver_pattern ]] || fail "Release version '$version' must be SemVer without build metadata."
[ -n "${projects//[[:space:]]/}" ] || fail "HEXALITH_CONTAINER_PROJECTS is empty."
[ -n "$username" ] || fail "HEXALITH_ZOT_USERNAME is required to publish containers."
[ -n "$api_key" ] || fail "HEXALITH_ZOT_API_KEY is required to publish containers."
[ -x "$validator" ] || fail "OCI registry validator is required and must be executable."
[ -x "$smoke" ] || fail "Container platform smoke helper is required and must be executable."
[ -x "$authority_validator" ] || fail "Publication authority validator is required and must be executable."
[[ "$builds_execution_sha" =~ ^[0-9a-f]{40}$ ]] || fail "Exact Builds execution SHA is required."
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || fail "Exact workflow source SHA is required."
[[ "$release_repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "Release repository is invalid."
[[ "$authority_url" =~ ^https:// ]] || fail "Durable release authority URL is required."
[ -f "$role_allowlist" ] || fail "Release-owner allowlist is required."

workspace_root="$(realpath -e "$PWD")"
evidence_directory="$(realpath -m "$evidence_directory")"
case "$evidence_directory/" in
  "$workspace_root"/*) ;;
  *) fail "Container evidence directory must remain below the workspace." ;;
esac

echo "$api_key" | docker login "$registry" --username "$username" --password-stdin

while IFS= read -r raw_line; do
  line="$(trim "${raw_line%$'\r'}")"
  [ -n "$line" ] || continue

  IFS='|' read -r project repository extra <<< "$line"
  project="$(trim "$project")"
  repository="$(trim "$repository")"
  extra="$(trim "${extra:-}")"

  [ -z "$extra" ] || fail "Invalid container mapping '$line'. Expected project|repository."
  [ -n "$project" ] || fail "Container project path is empty in '$line'."
  [[ "$repository" =~ ^[a-z0-9]+([._-][a-z0-9]+)*$ ]] ||
    fail "Container repository '$repository' is invalid."
  [ -f "$project" ] || fail "Container project not found: $project"
  resolved_project="$(realpath -e "$project")"
  case "$resolved_project" in
    "$workspace_root"/*) ;;
    *) fail "Container project must remain below the workspace." ;;
  esac

  mapping_evidence="${evidence_directory}/${repository}"
  mkdir -p "$mapping_evidence"

  "$authority_validator" \
    --authority-url "$authority_url" \
    --repository "$release_repository" \
    --version "$version" \
    --source-sha "$source_sha" \
    --container-repository "${registry}/${repository}" \
    --builds-execution-sha "$builds_execution_sha" \
    --role-allowlist "$role_allowlist" \
    --contract-directory "$(dirname "$0")" \
    --evidence-directory "${evidence_directory}/authority" \
    --phase container

  echo "[publish-containers] Publishing ${registry}/${repository}:${version} from ${resolved_project}"
  dotnet publish "$resolved_project" \
    --configuration Release \
    /t:PublishContainer \
    "-p:RuntimeIdentifiers=\"$runtime_identifiers\"" \
    "-p:ContainerRuntimeIdentifiers=\"$runtime_identifiers\"" \
    -p:ContainerImageFormat=OCI \
    -p:UseHexalithProjectReferences=false \
    -p:ContainerRegistry="$registry" \
    -p:ContainerRepository="$repository" \
    -p:ContainerImageTag="$version" \
    -p:Version="$version"

  "$validator" \
    --image "${registry}/${repository}:${version}" \
    --evidence-directory "$mapping_evidence"
  "$smoke" \
    --image "${registry}/${repository}:${version}" \
    --evidence-directory "$mapping_evidence"
done <<< "$projects"
