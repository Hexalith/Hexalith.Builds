#!/usr/bin/env bash
set -euo pipefail

# semantic-release pushes its tag before its notes. A transient GitHub notes
# failure can otherwise strand a genuine tag before package publication starts.
# Repeat only the identical, non-forced notes push; never rerun the lifecycle.
if [[ $# -ne 4 || $1 != push || $2 != -- ||
      ! $4 =~ ^refs/notes/semantic-release-[A-Za-z0-9][A-Za-z0-9._/-]*$ ||
      ! $3 =~ ^https://([^/@[:space:]]+@)?github\.com/ ]]; then
  exec "${HEXALITH_RELEASE_GIT:?Original Git executable is required}" "$@"
fi

output_directory="$(mktemp -d)"
trap 'rm -rf "$output_directory"' EXIT
for attempt in 1 2 3; do
  status=0
  "${HEXALITH_RELEASE_GIT:?Original Git executable is required}" "$@" \
    > "$output_directory/stdout" 2> "$output_directory/stderr" || status=$?
  if [[ $status -eq 0 ]]; then
    exit 0
  fi
  # Git failures may contain an authenticated remote URL. Retain only the
  # numeric status in diagnostics; captured transport output is private.
  printf 'semantic-release notes push failed (exit %s, attempt %s/3).\n' "$status" "$attempt" >&2
  if [[ $attempt -eq 3 ]] || ! grep -Eq \
    '(^remote: (Internal Server Error|Bad Gateway|Service Unavailable|Gateway Timeout)([[:space:]]|$)|^error: RPC failed; HTTP (500|502|503|504)([[:space:]]|$)|^fatal: unable to access .+: The requested URL returned error: (500|502|503|504)([[:space:]]|$))' \
    "$output_directory/stderr"; then
    exit "$status"
  fi
  printf 'Retrying the same semantic-release notes push after a transient server failure (%s/3).\n' "$attempt" >&2
  sleep "$((attempt * 2))"
done
