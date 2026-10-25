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
live_sha="$(gh api "repos/${REPOSITORY}/git/ref/heads/${branch}" --jq '.object.sha')"
if [[ ! "$live_sha" =~ ^[0-9a-f]{40}$ ]] || [ "$DISPATCH_SHA" != "$live_sha" ]; then
  echo "Builds Release source is not the exact current ${branch} tip." >&2
  exit 1
fi

runs="$(gh api "repos/${REPOSITORY}/actions/workflows/ci.yml/runs?head_sha=${DISPATCH_SHA}&event=push&status=completed&per_page=100")"
if ! jq -e --arg sha "$DISPATCH_SHA" --arg branch "$branch" \
  '[.workflow_runs[] | select(.head_sha == $sha and .head_branch == $branch and .event == "push" and .conclusion == "success")] | length > 0' <<< "$runs" >/dev/null; then
  echo "Builds Release requires successful push CI for exact ${branch} SHA ${DISPATCH_SHA}." >&2
  exit 1
fi
