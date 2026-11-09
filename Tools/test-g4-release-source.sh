#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_dir="$(mktemp -d)"
trap 'rm -rf "$test_dir"' EXIT
cat > "$test_dir/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [ "$1" != api ]; then exit 90; fi
case "$2" in
  repos/Hexalith/Hexalith.Builds/git/ref/heads/main|repos/Hexalith/Hexalith.Builds/git/ref/heads/prerelease)
    [ "$#" = 2 ] || exit 92
    if [ "${MOCK_REF_EXIT:-0}" != 0 ]; then exit "$MOCK_REF_EXIT"; fi
    printf '%s\n' "$MOCK_LIVE_REF" ;;
  repos/Hexalith/Hexalith.Builds/actions/runs/123)
    [ "$#" = 2 ] || exit 92
    printf '%s\n' "$MOCK_RUN" ;;
  "repos/Hexalith/Hexalith.Builds/actions/workflows/builds-ci.yml/runs?head_sha=${DISPATCH_SHA}&event=push&status=completed&per_page=100")
    [ "$#" = 2 ] || exit 92
    printf '%s\n' "$MOCK_RUNS" ;;
  *) exit 91 ;;
esac
EOF
chmod +x "$test_dir/gh"
export PATH="$test_dir:$PATH"
export REPOSITORY=Hexalith/Hexalith.Builds
export DISPATCH_SHA=0123456789abcdef0123456789abcdef01234567
export DISPATCH_REF=refs/heads/main
export GITHUB_OUTPUT="$test_dir/github-output"
export MOCK_RUNS='{"workflow_runs":[{"id":123,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"success"}]}'
case_count=0
unset EXPECTED_CI_RUN_ID EXPECTED_CI_RUN_ATTEMPT

set_live_ref() {
  local source_sha="$1"
  MOCK_LIVE_REF=$(jq -n --arg sha "$source_sha" --arg ref "$DISPATCH_REF" '{ref:$ref,
    url:("https://api.github.com/repos/Hexalith/Hexalith.Builds/git/"+$ref),
    object:{type:"commit",sha:$sha,url:("https://api.github.com/repos/Hexalith/Hexalith.Builds/git/commits/"+$sha)}}')
  export MOCK_LIVE_REF
}
set_live_ref "$DISPATCH_SHA"

expect_pass() {
  case_count=$((case_count + 1))
  : > "$GITHUB_OUTPUT"
  bash "$script_dir/verify-g4-release-source.sh" >/dev/null
  if [[ "$(cat "$GITHUB_OUTPUT")" != 'ci-run-id=123' ]]; then
    echo 'Release source guard did not retain the verified CI run ID.' >&2
    exit 1
  fi
}
expect_fail() {
  case_count=$((case_count + 1))
  if bash "$script_dir/verify-g4-release-source.sh" >"$test_dir/output" 2>&1; then
    echo "Release source guard accepted invalid case: $1" >&2
    exit 1
  fi
}

expect_pass

# A correct digest on a foreign/malformed reference is never the current branch.
valid_live_ref="$MOCK_LIVE_REF"
for mutation in '.ref="refs/heads/other"' '.url="https://api.github.com/repos/Other/Repository/git/refs/heads/main"' \
  '.object.type="tag"' '.object.url="https://api.github.com/repos/Other/Repository/git/commits/"+.object.sha' \
  '.object.sha="not-a-commit"' 'del(.ref)' 'del(.object)' 'null'; do
  MOCK_LIVE_REF=$(jq "$mutation" <<< "$valid_live_ref")
  export MOCK_LIVE_REF
  expect_fail "foreign-or-malformed-ref:$mutation"
done
MOCK_LIVE_REF='not-json'
export MOCK_LIVE_REF
expect_fail malformed-ref-json
MOCK_LIVE_REF="$valid_live_ref"
export MOCK_LIVE_REF MOCK_REF_EXIT=1
expect_fail unavailable-ref
export MOCK_REF_EXIT=0
MOCK_RUNS='{"workflow_runs":[{"id":122,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"success"},{"id":124,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"failure"},{"id":123,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"success"}]}'
export MOCK_RUNS
expect_pass
MOCK_RUNS='{"workflow_runs":[]}'
export MOCK_RUNS
expect_fail missing-ci
MOCK_RUNS='{"workflow_runs":[{"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"success"}]}'
export MOCK_RUNS
expect_fail missing-run-id
DISPATCH_REF=refs/heads/prerelease
MOCK_RUNS='{"workflow_runs":[{"id":123,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"prerelease","event":"push","conclusion":"success"}]}'
export DISPATCH_REF MOCK_RUNS
set_live_ref "$DISPATCH_SHA"
expect_pass

set_live_ref 0000000000000000000000000000000000000000
expect_fail stale
set_live_ref "$DISPATCH_SHA"
MOCK_RUNS='{"workflow_runs":[{"id":123,"head_sha":"0000000000000000000000000000000000000000","head_branch":"prerelease","event":"push","conclusion":"success"}]}'
export MOCK_RUNS
expect_fail wrong-ci-sha
MOCK_RUNS='{"workflow_runs":[{"id":123,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"success"}]}'
export MOCK_RUNS
expect_fail wrong-ci-branch
MOCK_RUNS='{"workflow_runs":[{"id":123,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"prerelease","event":"workflow_dispatch","conclusion":"success"}]}'
export MOCK_RUNS
expect_fail non-push-ci
MOCK_RUNS='{"workflow_runs":[{"id":123,"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"prerelease","event":"push","conclusion":"failure"}]}'
export MOCK_RUNS
expect_fail failed-ci
DISPATCH_REF=refs/heads/main
export DISPATCH_REF
set_live_ref "$DISPATCH_SHA"
export EXPECTED_CI_RUN_ID=123 EXPECTED_CI_RUN_ATTEMPT=1
export MOCK_RUN='{"id":123,"run_attempt":1,"repository":{"full_name":"Hexalith/Hexalith.Builds"},"path":".github/workflows/builds-ci.yml","head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","status":"completed","conclusion":"success"}'
MOCK_RUNS="$(jq -cn --argjson run "$MOCK_RUN" '{workflow_runs:[$run]}')"
export MOCK_RUNS
expect_pass
# The filtered list falls back to an older success while the recorded run reruns.
MOCK_RUNS="$(jq -c '.workflow_runs[0].id=122' <<< "$MOCK_RUNS")"
expect_fail older-success-fallback
MOCK_RUNS="$(jq -cn --argjson run "$MOCK_RUN" '{workflow_runs:[$run]}')"
for field in run_attempt status conclusion; do
  original="$MOCK_RUN"
  case "$field" in
    run_attempt) MOCK_RUN="$(jq -c '.run_attempt=2' <<< "$MOCK_RUN")" ;;
    status) MOCK_RUN="$(jq -c '.status="in_progress"' <<< "$MOCK_RUN")" ;;
    conclusion) MOCK_RUN="$(jq -c '.conclusion="failure"' <<< "$MOCK_RUN")" ;;
  esac
  export MOCK_RUN
  expect_fail "recorded-$field-changed"
  MOCK_RUN="$original"
  export MOCK_RUN
 done
EXPECTED_CI_RUN_ATTEMPT=''
export EXPECTED_CI_RUN_ATTEMPT
expect_fail missing-expected-attempt
unset EXPECTED_CI_RUN_ID EXPECTED_CI_RUN_ATTEMPT
DISPATCH_SHA=malformed
export DISPATCH_SHA
expect_fail malformed-sha
DISPATCH_REF=refs/heads/feature
export DISPATCH_REF
expect_fail unsupported-branch

echo "G-4 release source guard checks passed: $case_count scenarios."
