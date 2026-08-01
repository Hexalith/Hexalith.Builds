---
work_package_id: 6.1-P1R
artifact_kind: qualification-record
created: 2026-08-01
status: pending-acceptance
candidate_baseline: 3.88.0
candidate_builds_revision: 4351d7cba7545a96661ca2ee2ca2629df6d0a118
rollback_baseline: 3.70.1
approved_change_proposal: "Hexalith.Projects working tree based on 3fe3c7eea4de1056f69438d8ed94147872506384:_bmad-output/planning-artifacts/sprint-change-proposal-2026-08-01-p1r-baseline-revalidation.md"
projects_post_change_revision: pending
repository_authority: EventStore, Builds, and Projects Architecture Spine
---

# 6.1-P1R EventStore Source, Architecture, and Runner Revalidation Record

## Decision and completion boundary

This finite ledger qualifies one exact source/package/catalog/runner/
architecture tuple for the post-P1 `3.88.0` candidate. It remains
`pending-acceptance`: the aligned Builds working tree has no immutable accepted
revision, EventStore package-mode validation is incomplete, the rollback tuple
is not independently executable, the Architecture Spine remains bound to
`3.70.1`, and no required owner signature is claimed.

Closing P1R would not close P0, P2, P3, P4, or Story 6.1. It would only remove
the baseline-revalidation dependency after every required validation and owner
acceptance below is complete.

The approved proposal exists in the Hexalith.Projects working tree based on
revision `3fe3c7eea4de1056f69438d8ed94147872506384`; its exact post-change Projects
revision remains pending and cannot be inferred from working-tree content.

## Candidate and accepted baseline tuple

| Surface | Exact value | Disposition |
| --- | --- | --- |
| EventStore source | tag `v3.88.0`; revision `4843b492dff7c16a4bc74db67509263f969c78c6` | Candidate; immutable source verified locally |
| EventStore package version | `3.88.0` | Candidate; all 13 catalog rows are listed stable on NuGet, but publication of the full 14-package release manifest and package-mode restore/build/test are not proven |
| EventStore release manifest | `tools/release-packages.json`; SHA-256 `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`; 14 package IDs | Candidate source evidence |
| Builds package catalog | `HexalithEventStoreVersion=3.88.0`; introducing revision `0e51a2115581028c8d9ab9395a93dd186ee51071`; all 13 EventStore package rows evaluate to the shared property | Candidate catalog source aligned |
| Builds runner source | `src/libraries/Hexalith.Builds.Tooling/Manifest/SupportedPlatformPins.cs`; `EventStoreVersion=3.88.0` | Candidate committed at `4351d7cba7545a96661ca2ee2ca2629df6d0a118`, based on `4132725d8bda647cc65880199679f047f7366048`; not accepted |
| Builds manifest schema | `schemas/hexalith.module-manifest.v1.json`; exact allowed EventStore pin `3.88.0` | Candidate; schema/runtime parity tested |
| Exact post-alignment Builds runner revision | `4351d7cba7545a96661ca2ee2ca2629df6d0a118` | Immutable candidate implementation revision; owner acceptance remains pending |
| Approved change proposal | `Hexalith.Projects@working-tree-based-on-3fe3c7eea4de1056f69438d8ed94147872506384:_bmad-output/planning-artifacts/sprint-change-proposal-2026-08-01-p1r-baseline-revalidation.md` | Approved planning authority; exact post-change Projects revision pending |
| Architecture Spine | `Hexalith.Projects@3fe3c7eea4de1056f69438d8ed94147872506384:_bmad-output/planning-artifacts/architecture/architecture-projects-2026-07-15/ARCHITECTURE-SPINE.md`; binding `3.70.1` | Preserved current binding; candidate rebinding forbidden before acceptance |
| Accepted tuple and date | pending | No acceptance claimed |

The runner alignment changes only the EventStore pin. Dapr runtime `1.18.0`,
Dapr SDK `1.18.5`, and FrontComposer `4.0.1` remain unchanged.

### Release-manifest package inventory

The immutable `v3.88.0` manifest names exactly these 14 package IDs:

1. `Hexalith.EventStore.Contracts`
2. `Hexalith.EventStore.Client`
3. `Hexalith.EventStore.Server`
4. `Hexalith.EventStore.SignalR`
5. `Hexalith.EventStore.Testing`
6. `Hexalith.EventStore.Testing.Integration`
7. `Hexalith.EventStore.Aspire`
8. `Hexalith.EventStore.ServiceDefaults`
9. `Hexalith.EventStore.DomainService`
10. `Hexalith.EventStore.RestApi.Generators`
11. `Hexalith.EventStore.Gateway`
12. `Hexalith.EventStore.Admin.Abstractions`
13. `Hexalith.EventStore.Admin.Cli`
14. `Hexalith.EventStore.Admin.Server`

This inventory proves release-manifest intent, not package publication or
successful package consumption.

## Seven-API compatibility evidence

| API/type | Source path | `v3.70.1` blob | `v3.88.0` blob | Result |
| --- | --- | --- | --- | --- |
| `IAsyncDomainProjectionHandler` | `src/Hexalith.EventStore.DomainService/IAsyncDomainProjectionHandler.cs` | `99ca7b0bc3e8bea370ecd2674f54f9f7f198933e` | `99ca7b0bc3e8bea370ecd2674f54f9f7f198933e` | Byte-identical |
| `IReadModelStore` | `src/Hexalith.EventStore.Client/Projections/IReadModelStore.cs` | `fb07789943f28e7594e3f31c748204645dbb0c75` | `fb07789943f28e7594e3f31c748204645dbb0c75` | Byte-identical |
| `IReadModelBatchStore` | `src/Hexalith.EventStore.Client/Projections/IReadModelBatchStore.cs` | `bf8952b3ebf868fb407539fcebb85fad2f203011` | `bf8952b3ebf868fb407539fcebb85fad2f203011` | Byte-identical |
| `ReadModelWritePolicy` | `src/Hexalith.EventStore.Client/Projections/ReadModelWritePolicy.cs` | `ff0d5d14b5cb90ab0d31771c9bbf4cac63b7d2f3` | `ff0d5d14b5cb90ab0d31771c9bbf4cac63b7d2f3` | Byte-identical |
| `IDomainQueryHandler` | `src/Hexalith.EventStore.DomainService/IDomainQueryHandler.cs` | `693ff04e7d1eb32af9b515d14781c2a69c421f08` | `693ff04e7d1eb32af9b515d14781c2a69c421f08` | Byte-identical |
| `IQueryCursorCodec` | `src/Hexalith.EventStore.Client/Queries/IQueryCursorCodec.cs` | `18e67dc0eaf3a3b2b5ae3dc1b52ddf73b073b6f7` | `18e67dc0eaf3a3b2b5ae3dc1b52ddf73b073b6f7` | Byte-identical |
| `QueryCursorScope` | `src/Hexalith.EventStore.Client/Queries/QueryCursorScope.cs` | `15d65af7a6f6325a4fcb121656dd02717d80292f` | `53065de441d5863d56ae0953e772b3e1117bebee` | Additive-compatible: adds `AddProjectionWatermark(long?)`; no existing compared API removed or changed |

## Package, catalog, and runner qualification

Direct evaluation of `Props/Directory.Packages.props` confirms that all 13
EventStore package rows resolve through `HexalithEventStoreVersion=3.88.0`.
Separately, the structural catalog contract was observed passing for 49
approved package identities and three representative shared bindings; it is
not a freshness or package-version-history audit.

The deterministic package-version audit was refreshed from live NuGet metadata
at `2026-08-01T08:56:31.3453199+00:00`, generated from revision
`d3239aff003c64b40cbc074e68ec7923924cfc96`. It records all 13 cataloged
EventStore rows as listed stable `3.88.0`, preserves every unrelated package
decision, and passes the repository validator for 284 packages, 139 families,
and one source. `Hexalith.EventStore.Admin.Cli`, the fourteenth source release
manifest package, is not a Builds catalog row and is not proven published by
this audit.

Focused runner tests were observed passing, and an isolated consumer restored
disposable `999.0.0-p1r` G-4 tool packages from a local-only NuGet source. That
consumer produced canonical redacted module evidence,
accepted the `3.88.0` positive manifest, rejected the isolated stale `3.70.1`
pin with exit `1` / `HXM016`, preserved `HXM009` for the unrelated invalid
profile control, passed all 14 module negative controls and all 12 evidence
negative controls, and returned exit `2` / `HXR002` for the unavailable live
prerequisite control.

The disposable tool-package hashes were:

| Package | Size | SHA-256 | Disposition |
| --- | ---: | --- | --- |
| `Hexalith.Builds.Module.Cli.999.0.0-p1r.nupkg` | 502969 bytes | `831a264dd22a1785dd0d043340a4e0a5226ffe377aad4e0b6ed5447ca608243c` | Local test-only package; not retained and not an EventStore package |
| `Hexalith.Builds.Evidence.Cli.999.0.0-p1r.nupkg` | 502241 bytes | `84f3230e093341919be144b6f465871a6e393c49d06db9e2dcb74b166eeb9cb2` | Local test-only package; not retained and not an EventStore package |

The canonical packaged module evidence ended with a newline, contained neither
the raw filter nor `Bearer`, identified schema
`hexalith.module-run-evidence.v1`, retained the consumer-relative manifest
path, and had SHA-256
`bdd49315601f293bd84c161a32a376153b4f1cabb14c731ef99cdbfc0723c688`.

The official `test-g4-tool-package-contracts.ps1` entry point remains
inconclusive because its unconditional solution restore stalled. The isolated
manual package-consumer proof narrows that blocker but does not replace a clean
end-to-end official-gate result.

## Validation ledger

All commands used the named repository root as `cwd`. Exit `143`, cancellation,
or a missing immutable artifact cannot count as passing evidence. Exact start
and end UTC timestamps were not retained for these exploratory runs; that
deficiency is itself acceptance-blocking and must be corrected by the final
clean qualification run.

| Date | Repository | Exact command or control | Exit/result | Evidence disposition |
| --- | --- | --- | --- | --- |
| 2026-08-01 | EventStore | `git rev-parse 'v3.88.0^{commit}'` | `0`; `4843b492dff7c16a4bc74db67509263f969c78c6` | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | EventStore | `git show v3.88.0:tools/release-packages.json \| sha256sum` plus 14-ID count/list assertion | `0`; manifest SHA-256 recorded above | OBSERVED PASS; NON-QUALIFYING source inventory only |
| 2026-08-01 | EventStore | `git diff --name-status v3.70.1..v3.88.0 -- <seven exact API paths>` plus per-file `git rev-parse` blob comparisons | `0`; six identical, one additive | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | EventStore | `dotnet restore Hexalith.EventStore.slnx -p:UseHexalithProjectReferences=false --force --no-cache --verbosity minimal` | Cancelled after about 3m23s without output | INCONCLUSIVE |
| pending | EventStore | Package-mode Release build and Contracts tests with `UseHexalithProjectReferences=false` | Not run because package-mode restore did not complete | PENDING |
| 2026-08-01 | Builds | `MSBUILDDISABLENODEREUSE=1 dotnet restore Hexalith.Builds.slnx --verbosity minimal` | `0` | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | Builds | `MSBUILDDISABLENODEREUSE=1 dotnet test test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj --configuration Release --no-restore -p:NuGetAudit=false --verbosity minimal` | `0`; 107/107 passed | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | Builds | `MSBUILDDISABLENODEREUSE=1 dotnet test test/Hexalith.Builds.Evidence.Tests/Hexalith.Builds.Evidence.Tests.csproj --configuration Release --no-restore -p:NuGetAudit=false --verbosity minimal` | `0`; 24/24 passed | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | Builds | Direct MSBuild evaluation of all 13 EventStore package rows through `HexalithEventStoreVersion` | `0`; every row resolves `3.88.0` | OBSERVED PASS; NON-QUALIFYING evaluated-catalog evidence |
| 2026-08-01 | Builds | `pwsh -NoProfile -File ./Tools/test-authoritative-package-catalog.ps1` | `0`; 49 identities and three representative shared bindings | OBSERVED PASS; NON-QUALIFYING structural catalog contract only |
| 2026-08-01 | Builds | `pwsh -NoProfile -File ./Tools/validate-package-version-audit.ps1` | `0`; refreshed live audit validates 284 packages, 139 families, and one source; all 13 cataloged EventStore rows are listed stable `3.88.0` | OBSERVED PASS; NON-QUALIFYING until the final retained qualification run |
| 2026-08-01 | Builds | JSON parse, four evidence SHA-256 binding, sole-stale-pin, and all-`F` mismatch-preservation assertions | `0` | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | Builds | Two `dotnet pack <G-4 CLI project> --configuration Release --no-restore --output /tmp/hexalith-p1r-packages.TAHmXY -p:GeneratePackageOnBuild=false -p:IDEBuild=false -p:Version=999.0.0-p1r -p:PackageVersion=999.0.0-p1r -m:1` invocations | `0`; two `.nupkg` plus two `.snupkg` files | OBSERVED PASS; NON-QUALIFYING disposable tool packages only |
| 2026-08-01 | Isolated consumer | `dotnet tool restore --configfile NuGet.Config --no-http-cache` | `0`; both local-only tools restored | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | Isolated consumer | Six packaged help surfaces; positive `hexalith-module down`; positive `hexalith-evidence validate`; unavailable-run control; all 14 module negatives; all 12 evidence negatives | Expected `0`, `1`, and `2` exits/rule IDs; all assertions passed | OBSERVED PASS; NON-QUALIFYING packaged control matrix |
| 2026-08-01 | Builds | `pwsh -NoProfile -File ./Tools/test-g4-tool-package-contracts.ps1 -Version 999.0.0-p1r -RequireControls` | `143`; unconditional solution restore made no progress for more than five minutes and only that process group was terminated | INCONCLUSIVE |
| 2026-08-01 | Builds | Same official gate with `-SkipSourceValidation`, first with default caches and then isolated `NUGET_PACKAGES`/`DOTNET_CLI_HOME` | Both cancelled after more than five minutes in the built-in solution restore | INCONCLUSIVE; isolated caches rule out a shared package-cache lock as the sole cause |
| 2026-08-01 | Builds | Repeat focused Module test command without `MSBUILDDISABLENODEREUSE=1` while unrelated workspace builds were active | Cancelled after about one minute without output; only the new process group was terminated | INCONCLUSIVE repeat; does not replace the earlier passing serialized run |
| 2026-08-01 | Projects | Parse changed YAML and assert P1R/P0 open, P0 `in-progress-external`, Story 6.1 and readiness rows blocked, and Architecture `3.70.1` | `0` | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | Projects | `git diff --check` | `0` | OBSERVED PASS; NON-QUALIFYING |
| 2026-08-01 | Builds | `git -c core.whitespace=cr-at-eol diff --check` | `0` | OBSERVED PASS; NON-QUALIFYING; default check misclassifies repository-standard CRLF line ends |
| pending | Builds and EventStore | Timestamped clean serialized end-to-end qualification with retained logs/hashes | Not run | PENDING |
| pending | Rollback worktree | Same complete ladder with `3.70.1` accepted and `3.88.0` rejected | Not run | PENDING |

## Canonical evidence binding

Current and unrelated-negative module manifests use candidate pin `3.88.0`.
`test/fixtures/module/negative/tampered-platform-pin.json` alone retains
`3.70.1` and produces exit `1` / `HXM016`. Evidence artifact SHA-256 values
were recomputed when artifact bytes changed; the all-`F` mismatch control
remains intentionally incorrect and is still rejected.

## Rollback tuple and procedure

- EventStore tag/version: `v3.70.1` / `3.70.1`.
- EventStore revision:
  `f13f9925fdca53efa2ab8c90d396ab106f91bb9c`.
- Builds catalog source: commit
  `c074d0ddacadb63df2c6af6ac1f869a552e097eb` selects
  `HexalithEventStoreVersion=3.70.1`.
- Runner/schema/fixtures: restore exact pin `3.70.1` atomically and recompute
  every coupled evidence hash.
- Architecture Spine: retain its current `3.70.1` binding.

No single historical Builds commit contains both the `3.70.1` catalog and the
`3.70.1` runner: `c074d0d` has runner `3.70.0`, while the later runner
normalization occurred after the catalog had advanced. Therefore an immutable
rollback Builds revision and an executed rollback verification remain pending;
the historical P1 record is not rewritten to conceal that fact.

Before the rollback tuple can be called independently executable, create an
immutable Builds revision that atomically binds catalog, runner, schema,
fixtures, and evidence to `3.70.1`, then run the same timestamped source/API,
restore, build, Module, Evidence, catalog, packaged-control, hash, and state
assertion ladder. The rollback run must accept `3.70.1`, reject `3.88.0` with
`HXM016`, retain logs and package hashes, and record all four owner decisions.

## Scope guard

This candidate work does not accept or close 6.1-P1R. It does not close P0,
P2, P3, P4, or Story 6.1; prove the supported persisted live runner; select
G-1; or establish EventStore package publication. Historical 6.1-P1 evidence
remains accepted and unchanged.

## Required owner acceptance

| Role | Named approver | Decision | UTC date | Accepted tuple/revision | Evidence reference |
| --- | --- | --- | --- | --- | --- |
| EventStore Owner | pending | pending | pending | pending | pending |
| Builds Owner | pending | pending | pending | pending | pending |
| Solution Architect | pending | pending | pending | pending | pending |
| Test Architect | pending | pending | pending | pending | pending |

Architecture rebinding and the P1R completion transition are forbidden until
every required validation row passes with retained timestamped evidence, the
candidate and rollback Builds revisions are immutable, and all four roles
record explicit acceptance of the exact tuple.
