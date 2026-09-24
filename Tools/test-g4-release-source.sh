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
  */git/ref/heads/*) printf '%s\n' "$MOCK_LIVE_SHA" ;;
  */actions/workflows/ci.yml/runs*) printf '%s\n' "$MOCK_RUNS" ;;
  *) exit 91 ;;
esac
EOF
chmod +x "$test_dir/gh"
export PATH="$test_dir:$PATH"
export REPOSITORY=Hexalith/Hexalith.Builds
export DISPATCH_SHA=0123456789abcdef0123456789abcdef01234567
export MOCK_LIVE_SHA="$DISPATCH_SHA"
export DISPATCH_REF=refs/heads/main
export MOCK_RUNS='{"workflow_runs":[{"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"success"}]}'

expect_pass() {
  bash "$script_dir/verify-g4-release-source.sh" >/dev/null
}
expect_fail() {
  if bash "$script_dir/verify-g4-release-source.sh" >"$test_dir/output" 2>&1; then
    echo "Release source guard accepted invalid case: $1" >&2
    exit 1
  fi
}

expect_pass
DISPATCH_REF=refs/heads/prerelease
MOCK_RUNS='{"workflow_runs":[{"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"prerelease","event":"push","conclusion":"success"}]}'
export DISPATCH_REF MOCK_RUNS
expect_pass

MOCK_LIVE_SHA=0000000000000000000000000000000000000000
export MOCK_LIVE_SHA
expect_fail stale
MOCK_LIVE_SHA="$DISPATCH_SHA"
export MOCK_LIVE_SHA
MOCK_RUNS='{"workflow_runs":[{"head_sha":"0000000000000000000000000000000000000000","head_branch":"prerelease","event":"push","conclusion":"success"}]}'
export MOCK_RUNS
expect_fail wrong-ci-sha
MOCK_RUNS='{"workflow_runs":[{"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"main","event":"push","conclusion":"success"}]}'
export MOCK_RUNS
expect_fail wrong-ci-branch
MOCK_RUNS='{"workflow_runs":[{"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"prerelease","event":"workflow_dispatch","conclusion":"success"}]}'
export MOCK_RUNS
expect_fail non-push-ci
MOCK_RUNS='{"workflow_runs":[{"head_sha":"0123456789abcdef0123456789abcdef01234567","head_branch":"prerelease","event":"push","conclusion":"failure"}]}'
export MOCK_RUNS
expect_fail failed-ci
DISPATCH_SHA=malformed
export DISPATCH_SHA
expect_fail malformed-sha
DISPATCH_REF=refs/heads/feature
export DISPATCH_REF
expect_fail unsupported-branch

echo 'G-4 release source guard checks passed.'
