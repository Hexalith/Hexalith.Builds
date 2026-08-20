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
publication_preflight="${HEXALITH_PUBLICATION_PREFLIGHT:-$(dirname "$0")/publication_preflight.py}"
builds_execution_sha="${HEXALITH_BUILDS_EXECUTION_SHA:-}"
source_sha="${GITHUB_SHA:-}"
release_repository="${GITHUB_REPOSITORY:-}"
release_environment="${HEXALITH_RELEASE_ENVIRONMENT:-}"
source_branch="${HEXALITH_RELEASE_SOURCE_BRANCH:-main}"
source_ci_workflow="${HEXALITH_RELEASE_SOURCE_CI_WORKFLOW:-ci.yml}"
package_manifest="${HEXALITH_RELEASE_PACKAGE_MANIFEST:-tools/release-packages.json}"
expected_package_count="${HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT:-}"
reserved_version="${HEXALITH_RELEASE_RESERVED_VERSION:-}"
authority_issue_url="${HEXALITH_RELEASE_AUTHORITY_ISSUE_URL:-}"
authority_owner="${HEXALITH_RELEASE_AUTHORITY_OWNER:-}"
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

[[ -n "$version" ]] || fail "Release version argument is required."
[[ "$version" =~ $semver_pattern ]] || fail "Release version '$version' must be SemVer without build metadata."
[[ -n "${projects//[[:space:]]/}" ]] || fail "HEXALITH_CONTAINER_PROJECTS is empty."
[[ -n "$username" ]] || fail "HEXALITH_ZOT_USERNAME is required to publish containers."
[[ -n "$api_key" ]] || fail "HEXALITH_ZOT_API_KEY is required to publish containers."
[[ -x "$validator" ]] || fail "OCI registry validator is required and must be executable."
[[ -x "$smoke" ]] || fail "Container platform smoke helper is required and must be executable."
[[ -x "$publication_preflight" ]] || fail "Publication preflight is required and must be executable."
[[ "$builds_execution_sha" =~ ^[0-9a-f]{40}$ ]] || fail "Exact Builds execution SHA is required."
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || fail "Exact workflow source SHA is required."
[[ "$release_repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "Release repository is invalid."
[[ -n "${release_environment//[[:space:]]/}" ]] || fail "Protected release environment is required."
[[ "$source_branch" = "main" ]] || fail "Release source branch must be exactly main."
[[ "$source_ci_workflow" =~ ^[A-Za-z0-9_.-]+\.ya?ml$ ]] || fail "Release CI workflow is invalid."
[[ -f "$package_manifest" ]] || fail "Release package manifest is required."
[[ "$expected_package_count" =~ ^[1-9][0-9]*$ ]] ||
  fail "HEXALITH_RELEASE_EXPECTED_PACKAGE_COUNT must declare the module's package count as a positive integer."
[[ "$reserved_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] ||
  fail "HEXALITH_RELEASE_RESERVED_VERSION must be a stable semantic version."
[[ "$version" == "$reserved_version" ]] ||
  fail "Semantic Release selected a version different from the authorized reservation."
[[ -n "$authority_issue_url" ]] || fail "HEXALITH_RELEASE_AUTHORITY_ISSUE_URL is required."
[[ "$authority_owner" =~ ^github:[A-Za-z0-9][A-Za-z0-9-]{0,38}$ ]] ||
  fail "HEXALITH_RELEASE_AUTHORITY_OWNER must identify the expected GitHub release owner."

workspace_root="$(realpath -e "$PWD")"
evidence_directory="$(realpath -m "$evidence_directory")"
case "$evidence_directory/" in
  "$workspace_root"/*) ;;
  *) fail "Container evidence directory must remain below the workspace." ;;
esac

declare -a container_projects=()
declare -a container_names=()
declare -a container_repositories=()
declare -A seen_repositories=()

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

  full_repository="${registry}/${repository}"
  [ -z "${seen_repositories[$full_repository]+x}" ] ||
    fail "Container repository '$repository' is declared more than once."
  seen_repositories["$full_repository"]=1
  container_projects+=("$resolved_project")
  container_names+=("$repository")
  container_repositories+=("$full_repository")
done <<< "$projects"

[ "${#container_projects[@]}" -gt 0 ] || fail "HEXALITH_CONTAINER_PROJECTS is empty."

preflight_arguments=(
  --repository "$release_repository"
  --version "$version"
  --source-sha "$source_sha"
  --source-branch "$source_branch"
  --source-ci-workflow "$source_ci_workflow"
  --builds-execution-sha "$builds_execution_sha"
  --environment-name "$release_environment"
  --authority-issue-url "$authority_issue_url"
  --authority-owner "$authority_owner"
  --package-manifest "$package_manifest"
  --expected-package-count "$expected_package_count"
  --contract-directory "$(dirname "$0")"
  --evidence-directory "${evidence_directory}/preflight"
  --phase container
)
for repository in "${container_repositories[@]}"; do
  preflight_arguments+=(--container-repository "$repository")
done

# Prove the complete frozen destination set is still absent before the first
# registry login or container write. One phase record covers the whole set.
"$publication_preflight" "${preflight_arguments[@]}"

echo "$api_key" | docker login "$registry" --username "$username" --password-stdin

for index in "${!container_projects[@]}"; do
  resolved_project="${container_projects[$index]}"
  repository="${container_names[$index]}"

  mapping_evidence="${evidence_directory}/${repository}"
  mkdir -p "$mapping_evidence"

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
    -p:Version="$version" \
    -p:ContainerProvenanceSourceSha="$source_sha" \
    -p:ContainerProvenanceReleaseVersion="$version"

  "$validator" \
    --image "${registry}/${repository}:${version}" \
    --repository "$release_repository" \
    --source-sha "$source_sha" \
    --version "$version" \
    --evidence-directory "$mapping_evidence"
  "$smoke" \
    --image "${registry}/${repository}:${version}" \
    --evidence-directory "$mapping_evidence"
done
