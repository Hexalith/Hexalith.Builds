# Governed Workflow Provenance GitHub Action

## Overview

This composite action proves what a governed Hexalith CI or Release run actually
executed. It validates the running job against the approved Hexalith.Builds
reusable-workflow identity, walks the bounded static `uses:` closure of that
workflow inside an immutable Builds checkout, hashes every Builds-owned source,
and records every third-party action as a pinned 40-hex coordinate.

It is used by the BUILD-REL-1 / GOV-1 governed paths in `domain-ci.yml` and
`domain-release.yml`. The action reads no secret, contacts no network service,
and never decides whether a release may publish. Callers own their evidence
logic; this action only supplies provenance.

## Functionality

1. **Identity validation.** `job.workflow_repository`, `job.workflow_sha`,
   `job.workflow_ref`, and (when the runner provides it) `job.workflow_file_path`
   must all resolve to the declared governed workflow at `builds-execution-sha`.
2. **Closure collection.** Starting from that workflow, every `uses:` edge is
   followed through composite actions. Builds-owned sources are read from the
   approved checkout and hashed, together with every helper file in the
   composite's subtree, including nested directories. A nested Python package,
   shell library, or JavaScript entrypoint executes exactly like a top-level
   helper, so it is hashed and it counts toward the source ceiling. Only
   never-tracked build output (`__pycache__`, `node_modules`, `.git`, `.pyc`) is
   excluded.
3. **Projection.** The result is serialized canonically and reduced to a single
   `closure_digest`, so any change to any byte in the closure changes the digest
   a consumer previously accepted.

There is exactly one serialization of the emitted document: compact, sorted,
ASCII-escaped JSON with no trailing newline. The file at `provenance-path`, the
`provenance-json` output, and the bytes covered by `provenance-sha256` are all
that same byte string, so a consumer can hash the artifact it received and
compare it directly with the emitted digest.

The collector fails closed rather than guessing. It rejects tags, branches,
expressions, `docker://` references, Docker actions, sources outside the
approved checkout, cycles, and anything that exceeds the AD-13 depth, source
count, or byte ceilings.

It implements a deliberately closed YAML subset instead of importing a YAML
package: GitHub-hosted runners carry no guaranteed YAML dependency, and an
ambiguous parse must fail rather than silently drop a `uses:` edge.

## Usage Example

```yaml
      - name: Checkout approved Builds actions
        uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          repository: Hexalith/Hexalith.Builds
          ref: ${{ inputs.builds-execution-sha }}
          path: .hexalith/builds-execution
          persist-credentials: false

      - name: Evaluate governed workflow provenance
        id: governed-provenance
        uses: ./.hexalith/builds-execution/Github/governed-provenance
        with:
          stage: ci
          builds-execution-sha: ${{ inputs.builds-execution-sha }}
          workflow-path: .github/workflows/domain-ci.yml
          candidate: ${{ inputs.candidate-commit }}
          # The four dependency-policy coordinates are all-or-nothing. The governed
          # domain workflows always pass all four, so this example does too; passing
          # a subset is rejected rather than treated as an unpolicied run.
          dependency-policy-repository: ${{ inputs.dependency-policy-repository }}
          dependency-policy-path: ${{ inputs.dependency-policy-path }}
          dependency-policy-commit: ${{ inputs.dependency-policy-commit }}
          dependency-policy-sha256: ${{ inputs.dependency-policy-sha256 }}
          # When supplied, the evaluated closure digest must equal this value exactly.
          expected-evaluator-digest: ${{ inputs.expected-ci-evaluator-digest }}
          output: .hexalith/ci/governed/ci-workflow-provenance.json
```

Only `dependency-policy-*` and `expected-evaluator-digest` are optional at the
action boundary. `domain-ci.yml` and `domain-release.yml` require all of them
from a governed caller, so a governed module always passes the full set.

## Inputs

| Input | Required | Default | Description |
|-------|----------|---------|-------------|
| `stage` | Yes | - | Governed stage the provenance is produced for: `ci` or `release`. |
| `builds-root` | No | `.hexalith/builds-execution` | Path of the approved Hexalith.Builds checkout that defines this job. |
| `builds-execution-sha` | Yes | - | Exact approved Hexalith.Builds commit resolved for the reusable workflow. |
| `workflow-path` | Yes | - | Repository-relative path of the governed reusable workflow. |
| `candidate` | Yes | - | Exact candidate commit every governed phase consumes. |
| `dependency-policy-repository` | No | `''` | Normalized `github.com/owner/repository` identity owning the active dependency policy. |
| `dependency-policy-path` | No | `''` | Repository-relative path of the active dependency policy. |
| `dependency-policy-commit` | No | `''` | Exact 40-hex commit of the active dependency policy. |
| `dependency-policy-sha256` | No | `''` | Exact 64-hex SHA-256 of the active dependency-policy bytes. |
| `expected-evaluator-digest` | No | `''` | Caller-declared evaluator-authorization digest. When set, the evaluated closure digest must equal it exactly. |
| `output` | No | `.hexalith/release/governed/workflow-provenance.json` | Path the provenance document is written to. |

The four dependency-policy coordinates are all-or-nothing: declare all of them
or none. A partial coordinate is rejected so an incomplete claim cannot look
complete.

`expected-evaluator-digest` is enforced, not merely recorded. If the closure the
action evaluates is not byte-identical to the closure the caller authorized, the
action fails closed instead of emitting a provenance document that pairs an
authorized digest with an unauthorized closure.

## Outputs

| Output | Description |
|--------|-------------|
| `provenance-path` / `provenance-sha256` | The emitted document and the SHA-256 of its exact bytes. |
| `provenance-json` | The complete document as compact canonical JSON, byte-identical to the file at `provenance-path`. |
| `candidate` | Exact candidate commit recorded in the provenance. |
| `closure-digest` | Canonical digest of the whole evaluated closure. |
| `reusable-repository` / `reusable-workflow-path` / `reusable-commit` / `reusable-blob-sha256` | Identity and blob hash of the reusable workflow that executed. |
| `actions-json` | Canonical JSON array of the Builds-owned composite sources. |
| `external-actions-json` | Canonical JSON array of the pinned external action coordinates. |

## Emitted Document

The document uses the `hexalith.builds-governed-provenance.v1` schema and
contains the stage, generation timestamp, candidate commit, caller run identity,
the closure (reusable workflow, local composite actions, composite helper files,
external action coordinates, and the closure digest), the dependency-policy
projection, and the expected evaluator digest.

Everything except `generated_at_utc` is deterministic: the same closure always
produces the same canonical bytes and the same digest.

## Prerequisites

- The job must have checked out the approved Hexalith.Builds commit at
  `builds-root` before this action runs.
- Every `uses:` reference reachable from the governed workflow must be a literal
  lowercase 40-hex commit or a local `./.hexalith/builds-execution/...` path.
  See `ci-cd-standards.md`, "Governed closure exception".
- Python 3 must be available on the runner. `ubuntu-latest` provides it.

## Tests

`Github/governed-provenance/tests/test_governed_provenance.py`, run by
`Tools/test-governed-provenance.ps1` and by this repository's own CI. The suite
evaluates both shipped governed workflows end to end, so a reference that stops
being resolvable fails the pull request rather than a release.
