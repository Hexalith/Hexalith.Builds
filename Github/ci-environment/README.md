# Shared CI environment

This composite prepares the enabled Dapr, browser and registry tools once after
the shared Release package-mode solution build. Hooks reuse that environment.
The local module checker does not initialize it.

Use the `registry-pull-username` and `registry-pull-password` action inputs on
`Github/prepare` and `Github/ci-environment` when registry access is enabled.
The presence check runs before tool installation. Docker receives the password
on stdin, then subsequent steps use a private `DOCKER_CONFIG` directory under
`RUNNER_TEMP`. The runner clears that directory at job completion. Steps with
access to this configuration can pull images for the remainder of the job.
Pass secrets as action inputs so earlier product commands and unrelated steps
execute without ambient registry credentials.

With Dapr enabled, successful initialization exports
`HEXALITH_DAPR_INITIALIZATION_RECEIPT` through `GITHUB_ENV`. The path is a private
mode-0600 `hexalith-dapr-initialization.json` file directly under the real,
absolute `RUNNER_TEMP`. The producer creates it exclusively before the single
pinned `dapr init`, with schema `hexalith.dapr-initialization.v1` and status
`initializing`. Retained files, symlinks and a supplied receipt environment
variable refuse another initialization without overwriting evidence.

The receipt records the UTC `startedAt`, runtime version, `preexistingVolumes`,
`preexistingContainerIds`, and `identity` containing the actual
`GITHUB_REPOSITORY`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT` and `GITHUB_JOB` values.
It supports the shared `ci` job and other valid job IDs; consumers must bind to
the current identity rather than a legacy module job name.

After successful initialization and inspection, status becomes `complete` and
`completedAt` is added. `createdContainers` records the actual newly observed
Docker IDs, names, image, UTC creation time, running state and mount identities
(`Type`, `Source`, `Destination`, `RW`, plus `Name` for volumes).
`mountedVolumes` records names, driver, scope, mountpoint and Docker's UTC
creation times. Raw Docker configuration and environment variables are omitted.
An init or inspection failure keeps incomplete, unpublished evidence and fails
the environment stage. The receipt path is exported only after all these steps
succeed.

This generic receipt records observations around initialization. Before minting
private product material, consumers must strictly parse its schema and complete
status, match the current run/job/attempt, and verify current Docker resources
against its exact IDs, creation times, preexisting-volume inventory and mounts.
They must also establish their own running-backend, localhost-port, fresh
logical-database, component-hash and private-custody requirements. An old volume
mounted by a newly created container remains old; this receipt never certifies
an empty database or authorizes clearing one.

The complete CI configuration includes required `ciTimeoutMinutes`, an integer
from 1 to 360 with renderer default 30. CiCd forwards it to the reusable CI
workflow's existing `job-timeout-minutes` input when adopting this contract.
Old release tags retain their own schema, loader and default behavior.
The existing optional native `job-timeout-minutes` override is preserved;
CiCd gives an explicit override precedence over `ciTimeoutMinutes`.
