# Dapr 1.19.2 Linux x64 runtime qualification

On 20 November 2026, the shared Builds catalog selected runtime **1.19.2** and CLI **1.19.1** in a local Release build. The Dapr .NET packages retain their own version pins. The results below cover native self-hosted development on Linux x64.

## Binaries and configuration

The CLI, `daprd`, placement and scheduler archives each matched the upstream checksum file, the digest in GitHub release metadata and a fresh SHA-256 of the download. The extracted `daprd` SHA-256 was `96d2a0aeaafc8993ed41151e184e2b1c67dc03fc21c9ba561c92e34cb5696596`. The Platform setup commands installed all four binaries in an isolated directory. The installed CLI reported CLI 1.19.1 and runtime 1.19.2. The generated Release catalog reported the same pair. [Archive hashes](../Props/dapr-toolchain-sha256.json) · [Runtime release](https://github.com/dapr/dapr/releases/tag/v1.19.2) · [CLI release](https://github.com/dapr/cli/releases/tag/v1.19.1)

## Live behavior

Placement and Scheduler reached their health endpoints. A native sidecar reported runtime 1.19.2 through its metadata endpoint. A Redis state write and read succeeded, a fresh sidecar with the same app ID read the saved value after restart, and the delete removed it. The run used private loopback ports, disposable data and the Redis executable from the EventStore fixture image `redis@sha256:c35b83ce044bb6d148c484d36e059ad28e02d5714ba6731fb55b6421e2ed0ccf`.

The EventStore live-sidecar project built with .NET SDK 11.0.100 in Release package mode. **23 tests passed** with zero skipped, zero not run and zero errors. They exercised command routing, actor concurrency, tenant isolation, reminder recovery, event persistence, snapshot reads and reloads, ETags, read-model batches and Dapr serialization. [EventStore test source](https://github.com/Hexalith/Hexalith.EventStore/tree/c426b454a44a0167d400693997b7cda36028dfbb/tests/Hexalith.EventStore.Server.LiveSidecar.Tests)

To repeat this sample, start isolated Redis, placement and scheduler instances. Set `EVENTSTORE_TEST_REDIS_PORT`, `EVENTSTORE_TEST_PLACEMENT_PORT` and `EVENTSTORE_TEST_SCHEDULER_PORT` to their ports. Set `EVENTSTORE_TEST_DAPRD_PATH` to the verified 1.19.2 executable. Run the following commands from the EventStore revision above.

```bash
project=tests/Hexalith.EventStore.Server.LiveSidecar.Tests
dotnet build "$project/Hexalith.EventStore.Server.LiveSidecar.Tests.csproj" \
  -c Release -p:UseHexalithProjectReferences=false -p:UseNuGetDeps=true -m:1
dotnet "$project/bin/Release/net11.0/Hexalith.EventStore.Server.LiveSidecar.Tests.dll" \
  -class '*ActorConcurrencyConflictTests' -class '*ActorTenantIsolationTests' \
  -class '*ReminderRecoveryLiveSidecarTests' -class '*FoldedSnapshotReloadLiveSidecarTests' \
  -class '*CommandRoutingIntegrationTests' -class '*EventPersistenceIntegrationTests' \
  -class '*SnapshotIntegrationTests' -class '*DaprSerializationRoundTripTests' \
  -class '*DaprETagServiceLiveSidecarTests' -class '*ReadModelBatchLiveSidecarTests'
```

## Release scope

The Builds Release build finished with zero warnings and errors. All 117 focused .NET tests passed, including the checks that the current composition runner reads the catalog and the frozen v1 manifest still reports 1.19.1. The Platform and Works verifiers selected the expected executable hash for each catalog version.

The EventStore hosted qualification workflow still binds its published 1.19.1 profile. The full Works fleet, the broker and Kubernetes profiles, the cross-project G-6 qualification and the Platform newcomer journey require separate runs.
