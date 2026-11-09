#!/usr/bin/env bash
set -euo pipefail

: "${REPOSITORY:?Repository identity is required}"
: "${DISPATCH_REF:?Dispatch branch is required}"
: "${DISPATCH_SHA:?Dispatch SHA is required}"

case "$DISPATCH_REF" in
  refs/heads/main|refs/heads/prerelease) ;;
  *) echo "Builds Release requires main or prerelease branch." >&2; exit 1 ;;
esac
if [[ ! "$DISPATCH_SHA" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Builds Release requires a full lowercase dispatch SHA." >&2
  exit 1
fi

branch="${DISPATCH_REF#refs/heads/}"
live_ref="$(gh api "repos/${REPOSITORY}/git/ref/heads/${branch}")"
if ! jq -e --arg repository "$REPOSITORY" --arg ref "$DISPATCH_REF" --arg sha "$DISPATCH_SHA" '
  type == "object" and .ref == $ref
  and .url == ("https://api.github.com/repos/" + $repository + "/git/" + $ref)
  and .object.type == "commit" and .object.sha == $sha
  and .object.url == ("https://api.github.com/repos/" + $repository + "/git/commits/" + $sha)
' <<< "$live_ref" >/dev/null; then
  echo "Builds Release source is not the exact current ${branch} tip." >&2
  exit 1
fi

runs="$(gh api "repos/${REPOSITORY}/actions/workflows/builds-ci.yml/runs?head_sha=${DISPATCH_SHA}&event=push&status=completed&per_page=100")"
if ! ci_run_id="$(jq -er --arg sha "$DISPATCH_SHA" --arg branch "$branch" \
  '[.workflow_runs[] | select(.head_sha == $sha and .head_branch == $branch and .event == "push" and .conclusion == "success" and (.id | type == "number"))] | max_by(.id).id // empty' <<< "$runs")"; then
  echo "Builds Release requires successful push CI for exact ${branch} SHA ${DISPATCH_SHA}." >&2
  exit 1
fi

# Late lifecycle calls must retain the recorded attempt, even if the list API
# filters out its now-running rerun and exposes an older successful run instead.
if [[ -n "${EXPECTED_CI_RUN_ID:-}${EXPECTED_CI_RUN_ATTEMPT:-}" ]]; then
  if [[ ! "${EXPECTED_CI_RUN_ID:-}" =~ ^[1-9][0-9]*$ ]] || [[ ! "${EXPECTED_CI_RUN_ATTEMPT:-}" =~ ^[1-9][0-9]*$ ]] || [[ "$ci_run_id" != "$EXPECTED_CI_RUN_ID" ]]; then
    echo 'Late release CI selection differs from its qualified run and attempt.' >&2
    exit 1
  fi
  recorded_run="$(gh api "repos/${REPOSITORY}/actions/runs/${EXPECTED_CI_RUN_ID}")"
  if ! jq -e --arg id "$EXPECTED_CI_RUN_ID" --arg attempt "$EXPECTED_CI_RUN_ATTEMPT" --arg sha "$DISPATCH_SHA" --arg branch "$branch" --arg repository "$REPOSITORY" \
    '(.id | tostring) == $id and (.run_attempt | tostring) == $attempt and .head_sha == $sha and .head_branch == $branch and .repository.full_name == $repository and .path == ".github/workflows/builds-ci.yml" and .event == "push" and .status == "completed" and .conclusion == "success"' <<< "$recorded_run" >/dev/null; then
    echo 'Late release CI run is no longer the qualified successful attempt.' >&2
    exit 1
  fi
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  printf 'ci-run-id=%s\n' "$ci_run_id" >> "$GITHUB_OUTPUT"
fi
