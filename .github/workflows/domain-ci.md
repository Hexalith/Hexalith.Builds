# Hexalith Domain CI — reusable workflow

`domain-ci.yml` is a reusable (`workflow_call`) CI pipeline for Hexalith domain modules.
It factors the common skeleton — checkout (+submodules), .NET SDK from `global.json`,
NuGet cache, restore, `Release -warnaserror` build, Dapr bootstrap, multi-tier tests,
coverage gate, and artifact upload — so every domain repo can consume one pinned pipeline.

## Jobs

| Job | Runs when | Tiers |
|-----|-----------|-------|
| `build-and-test`   | always                                   | consumer validation, Tier 1 (unit), Tier 2 (integration, Dapr), coverage gate |
| `aspire-tests`     | `aspire-test-project` set                | Tier 3 Aspire contract tests (`Category!=Performance`) |
| `performance-tests`| `aspire-test-project` set + `schedule`   | Tier 3 performance tests (`Category=Performance`) |

## Conventions assumed in the consuming repo

The reusable workflow checks out the **caller** repository, so these paths resolve against
the consumer:

- `scripts/pack-release-packages.py`, `scripts/validate-nuget-packages.py`,
  `scripts/validate-consumer-package-references.py` — required when
  `run-consumer-validation: true`.
- `scripts/validate-coverage.py` — required when `run-coverage-gate: true`.
- `global.json` (or the path given in `dotnet-global-json`).

## Inputs

See the `inputs:` block in `domain-ci.yml`. Lists (test projects, isolation targets) are
newline-separated strings (`workflow_call` does not support arrays); the workflow splits
them in bash. Test result folders are derived from each project's basename under
`TestResults/`.

## Usage

```yaml
jobs:
  ci:
    uses: Hexalith/Hexalith.Builds/.github/workflows/domain-ci.yml@<commit-sha>
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

Pin to a commit SHA (not `@main`) in repositories that enforce SHA-pinned actions. The
internally-used third-party actions are already SHA-pinned inside this workflow.
