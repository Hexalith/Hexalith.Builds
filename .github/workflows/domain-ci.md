# Hexalith Domain CI reusable workflow

`domain-ci.yml` is a reusable (`workflow_call`) CI pipeline for Hexalith domain
modules. It factors the common skeleton: checkout with submodules, .NET SDK from
`global.json`, NuGet cache, restore, `Release -warnaserror` build, optional
consumer validation, Dapr bootstrap, multi-tier tests, optional coverage gate,
and artifact upload.

The `test-platform` input defaults to `vstest` for backward compatibility.
Callers whose `global.json` selects `Microsoft.Testing.Platform` must set
`test-platform: microsoft-testing-platform`; those lanes use xUnit v3 MTP-native
TRX reporters and trait filters. MTP callers cannot enable `run-coverage-gate`
until they configure an MTP-compatible coverage extension.

## Jobs

| Job | Runs when | Tiers |
|-----|-----------|-------|
| `build-and-test` | Always. | Consumer validation, Tier 1 unit tests, Tier 2 Dapr integration tests, coverage gate. |
| `aspire-tests` | `aspire-test-project` is set. | Tier 3 Aspire contract tests using `Category!=Performance` by default. |
| `performance-tests` | `aspire-test-project` is set and the event is `schedule`. | Strict Tier 3 performance evidence using `Category=Performance` by default. |

The Aspire tier is **advisory (non-blocking) by default**
(`aspire-continue-on-error: true`): full-topology Aspire runs on shared runners
are inherently flakier than Tier 1/2, so they signal without gating merges. Its
coverage is deliberately not collected and is excluded from the coverage gates;
TRX results are always uploaded as the `aspire-test-results` artifact. Set
`aspire-continue-on-error: false` in a module that wants the tier blocking.

## Scheduled Performance Evidence

The scheduled performance step sets both
`HEXALITH_EVENTSTORE_RUN_PERFORMANCE_TESTS=1` and
`HEXALITH_TENANTS_RUN_PERFORMANCE_TESTS=1` for the selected `dotnet test`
process only. The opt-ins are not set on ordinary build, integration, or Aspire
contract-test steps.

After the test process finishes, the job always reads
`TestResults/performance/perf-results.trx` and writes
`TestResults/performance/performance-test-summary.json`. The summary records the
workflow run identity, selected filter, test-step outcome, and total, executed,
passed, failed, and skipped counts. The guard fails with distinct diagnostics
when the TRX is missing, the filter matched no tests, or every selected test was
skipped. It also fails when the test step or TRX reports test failures, preserving
that state in the JSON summary. The performance directory is recreated before
each run so stale TRX cannot satisfy the current execution guard. The guard uses
the TRX `Counters.executed` value for the all-skipped check because xUnit v3 can
report a skipped result as `NotExecuted` while leaving the
`Counters.notExecuted` value at zero.

The complete `TestResults/performance` directory is retained for seven days as
the `performance-test-results` artifact, even when the test or guard fails.
Consumers should write structured benchmark reports into that directory so
dataset fingerprints, phase timings, run distributions, resource metrics, and
invariant results are preserved unchanged beside the shared execution summary.
The shared summary proves only that selected evidence executed; benchmark
reports remain consumer-owned and are responsible for product-specific
performance claims.

## Consuming Repository Conventions

The reusable workflow checks out the caller repository, so these paths resolve
against the consuming repository:

- `scripts/pack-release-packages.py`,
  `scripts/validate-nuget-packages.py`, and
  `scripts/validate-consumer-package-references.py` are required when
  `run-consumer-validation` is `true`.
- `scripts/validate-coverage.py` is required when `run-coverage-gate` is
  `true`. When the `coverage-line-scope` input is set, the script must support
  a repeatable `--line-scope <path-prefix>` argument scoping the line gate.
- `global.json`, or the path supplied through `dotnet-global-json`, pins the
  .NET SDK.
- Scheduled performance tests may add support-safe JSON, Markdown, logs, or
  other evidence beneath `TestResults/performance`; the workflow uploads the
  directory without interpreting consumer-specific reports.

## Inputs

See the `inputs:` block in `domain-ci.yml` for the full list of supported
inputs and defaults.

List inputs such as test projects and isolation targets are newline-separated
strings because `workflow_call` does not support arrays. The workflow splits
those strings in bash. Test result folders are derived from each project
basename under `TestResults/`.

## Governed CI mode (BUILD-REL-1 / GOV-1)

Setting `governed-ci: true` opts the `build-and-test` job into the governed
contract. Every governed input is optional and defaults to the pre-BUILD-REL-1
behavior; a caller that leaves them unset gets exactly the previous workflow,
with `contents: read` as the only permission.

| Input | Required | Default | Description |
|-------|----------|---------|-------------|
| `governed-ci` | No | `false` | Opt into governed CI: workflow-identity validation and closure provenance. |
| `builds-execution-sha` | Governed | `''` | Exact approved Hexalith.Builds commit this reusable workflow was called at. |
| `candidate-commit` | Governed | `''` | Exact candidate commit the CI run evaluates. Must equal the event head. |
| `dependency-policy-repository` | Governed | `''` | Normalized `github.com/owner/repository` identity owning the active dependency policy. |
| `dependency-policy-path` | Governed | `''` | Repository-relative path of the active dependency policy. |
| `dependency-policy-commit` | Governed | `''` | Exact 40-hex commit of the active dependency policy. |
| `dependency-policy-sha256` | Governed | `''` | Exact 64-hex SHA-256 of the active dependency-policy bytes. |
| `expected-ci-evaluator-digest` | Governed | `''` | Expected CI evaluator-authorization digest recorded into the provenance. |

Governed CI adds three conditional steps before the build:

1. **Contract validation.** All governed coordinates are validated, and the
   run proves its own identity: `job.workflow_repository`, `job.workflow_sha`,
   and `job.workflow_ref` must resolve to
   `Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml` at
   `builds-execution-sha`, and the checked-out `HEAD` must equal
   `candidate-commit`. Every violation is collected and reported together, so a
   misconfigured caller sees the whole list rather than one failure per run.
2. **Approved Builds checkout** at `builds-execution-sha`.
3. **Closure provenance** through `Github/governed-provenance`, loaded from
   that checkout.

Because governed mode must load Builds-owned composites from the
reusable-workflow commit itself, the `build-and-test` job carries **two forms**
of `initialize-build` and `dapr-init`: a governed form using the local
`./.hexalith/builds-execution/...` path, and a legacy form pinned to a literal
40-hex commit for callers that never check Builds out. Exactly one of each pair
runs. The static closure records both, which is intentional: an
over-approximated closure is safe, an under-approximated one is not.

### Governed outputs

`build-and-test` exposes the provenance the caller needs to assemble its own
`hexalith.dependency-release-handoff.v1` artifact: `governed-candidate`,
`governed-provenance-json`, `governed-provenance-sha256`,
`governed-closure-digest`, `governed-reusable-repository`,
`governed-reusable-workflow-path`, `governed-reusable-commit`,
`governed-reusable-blob-sha256`, `governed-actions-json`, and
`governed-external-actions-json`.

`governed-actions-json` lists the Builds-owned composite sources with their blob
SHA-256 values; `governed-external-actions-json` lists every third-party action
in the closure as a pinned 40-hex coordinate. Builds emits provenance only —
the caller owns its handoff schema and evidence logic.

## Usage

```yaml
jobs:
  ci:
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@main
    with:
      solution: Hexalith.<Module>.slnx
      test-platform: microsoft-testing-platform
      run-consumer-validation: true
      run-coverage-gate: false
      unit-test-projects: |
        tests/Hexalith.<Module>.Contracts.Tests
        tests/Hexalith.<Module>.Client.Tests
      integration-test-projects: |
        tests/Hexalith.<Module>.Server.Tests
      aspire-test-project: tests/Hexalith.<Module>.IntegrationTests
      coverage-isolation-targets: |
        src/Hexalith.<Module>.Server/Aggregates/SomeAggregate.cs
```

## Version Reference

Use `Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@main` from
consuming repositories — Hexalith.Builds references always track `main` (see
`ci-cd-standards.md`, "Action References"); third-party actions inside the
shared workflows are the ones pinned to commit SHAs.

A **governed** caller cannot use `@main`. Governed mode validates
`job.workflow_sha` against `builds-execution-sha`, so the `uses:` revision and
`builds-execution-sha` must be the same literal 40-character commit:

```yaml
jobs:
  ci:
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@0123456789abcdef0123456789abcdef01234567
    with:
      solution: Hexalith.<Module>.slnx
      governed-ci: true
      builds-execution-sha: 0123456789abcdef0123456789abcdef01234567
      candidate-commit: ${{ github.sha }}
      dependency-policy-repository: github.com/hexalith/hexalith.<module>
      dependency-policy-path: eng/dependency-graph-policy.json
      dependency-policy-commit: ${{ needs.policy.outputs.commit }}
      dependency-policy-sha256: ${{ needs.policy.outputs.sha256 }}
      expected-ci-evaluator-digest: ${{ vars.HEXALITH_CI_EVALUATOR_DIGEST }}
```
