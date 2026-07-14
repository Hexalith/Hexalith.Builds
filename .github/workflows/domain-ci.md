# Hexalith Domain CI reusable workflow

`domain-ci.yml` is a reusable (`workflow_call`) CI pipeline for Hexalith domain
modules. It factors the common skeleton: checkout with submodules, .NET SDK from
`global.json`, NuGet cache, restore, `Release -warnaserror` build, optional
consumer validation, Dapr bootstrap, multi-tier tests, optional coverage gate,
and artifact upload.

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
skipped. It uses the TRX `Counters.executed` value for the last check because
xUnit v3 can report a skipped result as `NotExecuted` while leaving the
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

## Usage

```yaml
jobs:
  ci:
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@main
    with:
      solution: Hexalith.<Module>.slnx
      run-consumer-validation: true
      run-coverage-gate: true
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
