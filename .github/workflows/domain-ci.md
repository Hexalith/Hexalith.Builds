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
| `performance-tests` | `aspire-test-project` is set and the event is `schedule`. | Tier 3 performance tests using `Category=Performance` by default. |

## Consuming Repository Conventions

The reusable workflow checks out the caller repository, so these paths resolve
against the consuming repository:

- `scripts/pack-release-packages.py`,
  `scripts/validate-nuget-packages.py`, and
  `scripts/validate-consumer-package-references.py` are required when
  `run-consumer-validation` is `true`.
- `scripts/validate-coverage.py` is required when `run-coverage-gate` is
  `true`.
- `global.json`, or the path supplied through `dotnet-global-json`, pins the
  .NET SDK.

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

Use `@main` for Hexalith.Builds reusable workflow references so consuming
repositories run the latest shared CI logic.
