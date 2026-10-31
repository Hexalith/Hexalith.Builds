# Hexalith Domain CI v2 reusable workflow

`domain-ci-v2.yml` builds Release/package dependencies on one disposable Linux
GitHub-hosted runner. It runs isolated tests before any integration provisioning,
then requires runner-owned real-service package integration for enrolled modules.
V2 refuses direct project integration and advisory AppHost acceptance. The workflow
never publishes packages or images.

## Caller migration

Declare nonempty package suites in the v2 module declaration, pin
`hexalith.builds.module.cli` exactly in `.config/dotnet-tools.json` with
`rollForward: false`, and pass:

```yaml
with:
  solution: Hexalith.Module.slnx
  module-manifest: module/hexalith.module-manifest.v2.json
  integration-test-profiles: platform-package
  test-platform: microsoft-testing-platform
  unit-test-projects: |
    tests/Hexalith.Module.Contracts.Tests
    tests/Hexalith.Module.Domain.Tests
  build-timeout-minutes: 60
```

Use the versioned `.github/workflows/domain-ci-v2.yml` entry point. The workflow
reference must select an approved exact producer commit containing this file.
The tool version must identify a separately approved
receipt-capable Builds release. This example intentionally supplies no invented
release pin. EventStore and Parties enrollment is staged; existing accepted pins
remain unchanged pending the publication handoff in Platform Story 2.8 evidence.
A missing tool, absent suite, wrong mode, incompatible package, unavailable image
or unqualified release input fails. There is no advisory or legacy fallback.

Preserve existing required test coverage while changing lanes. Keep self-contained
projects in the isolated list and enroll real-service cases in declared package
suites; selecting a new platform suite must not silently drop existing projects.

`integration-test-projects` and `aspire-test-project` are retained as migration
inputs only: a nonempty value fails before integration provisioning. The legacy Aspire/performance hook, filter, timeout and browser inputs are absent
from v2, so GitHub refuses callers that still provide them. Modules needing
independent benchmark workflows maintain separately scoped workflows; benchmark
outcomes cannot supply this integration gate. The Dapr CLI/runtime and Aspire CLI
versions come from the verified tool catalog. V2 accepts no caller Dapr-version
overrides.

## Execution and cleanup

`Github/prepare-module-build/prepare_module_build.py` restores and verifies the
exact local tool and validates the generated runtime-free Release/package imports.
Governed callers load this action from `builds-execution-sha`; other enrolled
callers check out the actual reusable `job.workflow_sha`. Preparation does not
allocate services. An optional `package-prepare-script` still prepares dependencies
before restore, with repository-relative link-free paths and a finite deadline.

Each configured isolated project executes independently. VSTest and
Microsoft.Testing.Platform keep their distinct reporter/coverage options, preserve
all shard results and emit explicit failure counts. Missing/invalid outputs or any
failure block provisioning. Unit suites must be nonempty for integration callers.
Coverage gates and optional consumer package validation remain blocking.

After isolated success, the workflow installs the catalog-selected Aspire CLI and
isolated Dapr binaries without starting default Dapr services. The runner tool
probes the actual versions and container runtime before allocating its own services.
Integration uses declared suites, real services and package/Release dependencies;
it never runs a consumer-owned AppHost.

`Github/run-module-integration/run_module_integration.py` invokes:

```text
hexalith-module test --manifest <v2> --mode package --profile <declared-suite>
  --ci-owner-receipt "$RUNNER_TEMP/hexalith-ci-owner.json" --output json
```

Before allocation, the tool durably publishes the exact owner run, environment,
producing attempt, actual repository/run/attempt/job and durable metadata root.
Receipt publication must succeed and the file must remain outside deleted run
workspaces. A receipt cannot be rebound to another allocation.

The helper finalizes in `finally`, and an independent `always()` workflow step
retries exact `down --run-id` after success, failure, cancellation or partial startup.
It refuses foreign repository/run/attempt/job receipts or mismatched durable owner
identities before destructive commands. Every finalizer is bounded. Unknown
discovery and any owned process/container/workspace/claim prevent completion and
retain retryable owner metadata. Peer owners are outside the cleanup scope.

CI admits compatible active attachments only within that exact job. It closes
admission and durably reports active attachments as withdrawn before stopping
resources. End reasons are `ci-job-completed` or `ci-job-cancelled`; already terminal
outcomes and absolute original expiries remain unchanged. CI never waits beyond job
completion for holds and never retains failure resources. Local hold waiting and
failure retention continue under the existing policy.

An `always()` artifact upload preserves only classified redacted result and
attachment documents from durable diagnostics. Endpoint credentials, bearer tokens
and raw resource logs are excluded. This evidence remains available after workspace
deletion and records cleanup uncertainty independently of the first test outcome.

## Permissions and publication boundary

The workflow grants `contents: read`. Callers explicitly map the required
`registry-pull-username` and `registry-pull-password` inputs to repository secrets
`HEXALITH_CI_REGISTRY_USERNAME` and `HEXALITH_CI_REGISTRY_PASSWORD`. They identify the
dedicated `ci-reader` account with registry read permission and no admin membership.
Provider, publisher and cluster-workload accounts are not accepted for this job.
There is no secret inheritance or `pull_request_target` credential path.

Login runs only after isolated suites succeed, using password stdin and a private
job-temporary Docker config. The runtime already inherits `DOCKER_CONFIG`; the
integration step also requires successful login. An unconditional finalizer removes
the credential directory after exact owner teardown and before diagnostics upload.
A failed login removes its temporary directory immediately as well. Credential
values and the Docker config are never retained as artifacts.

Account/secret provisioning and workflow activation remain unexecuted prerequisites.
Only explicit v2 adopters enroll in the required suite and secret interface.
The legacy domain-ci.yml contract remains available unchanged for existing callers.
EventStore and Parties adopt an approved exact v2 revision after compatible
releases; their existing pins remain unchanged until that adoption. No
publication or provenance gate is weakened.

A passing local control or baseline integration result does not establish candidate
acceptance. Accepted tool/package pins, exact released owner snapshots and image
provenance, and revision-bound hosted CI cancellation/partial-startup cleanup
qualification remain separate requirements. Do not update accepted pins or claim
hosted qualification from staged caller files.

## Governed CI mode

`governed-ci: true` retains the BUILD-REL-1/GOV-1 exact-source contract. Callers must
supply `builds-execution-sha`, `candidate-commit`, dependency policy
repository/path/commit/SHA-256 and `expected-ci-evaluator-digest`. The workflow checks
its actual reusable repository/ref/SHA, exact checked-out candidate and bounded
reusable/composite closure before emitting governed provenance. Existing governed
outputs and consumer/publication handoff meanings are unchanged. V2 requires its
own authorized closure digest; a legacy digest or job workflow path is refused.
The direct integration helper is hashed at the exact Builds execution revision. This mode grants
no candidate test-environment acceptance or publication authorization by itself.

## Verification

```bash
python3 -m unittest discover -s Github/prepare-module-build/tests -v
python3 -m unittest discover -s Github/run-module-integration/tests -v
pwsh -NoProfile -File Tools/test-domain-workflow-test-platforms.ps1
pwsh -NoProfile -File Tools/test-domain-workflow-test-platforms.ps1 -Contract v2
```

The executable controls cover isolated gating, required prerequisites, same-job
binding, cancellation, partial startup, first outcomes, exact retry, classified
retention and failure on unknown discovery/leftovers. Hosted runner observations
are still required before Story 2.8 can complete.
