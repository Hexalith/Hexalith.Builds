---
work_package_id: 6.1-P1R
artifact_kind: qualification-record
created: 2026-08-01
status: pending-acceptance
candidate_baseline: 3.106.0
candidate_builds_revision: ad52f350a2f0bc47849179ae17b4594dafff5363
accepted_predecision_sha256: none
invalidated_predecision_sha256: 477949fc59d0cdd3a00d0c46de7aada46164074675c16c253b2015db369c0bab
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
## Pending `3.90.0` supersession candidate — 2026-08-04

This section appends candidate evidence and does not rewrite or accept the
historical `3.88.0` record above. The candidate is non-acceptance evidence:
P1R, the Architecture binding, rollback qualification, and all four owner
decisions remain pending.

### Candidate coordinates

| Field | Exact value | Disposition |
| --- | --- | --- |
| EventStore source revision | `7854f8e51ce9b852bb6c3cac6012670122e93792` | Clean checkout observed at `v3.90.0` |
| EventStore source describe | `v3.90.0` | Source identity |
| EventStore package version/tag | `3.90.0` / `v3.90.0` | Package identity |
| EventStore package source revision | `7854f8e51ce9b852bb6c3cac6012670122e93792` | Resolves to the source revision |
| Source/package equivalent | `true` | Both immutable identities resolve to one revision |
| EventStore release manifest | SHA-256 `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`; 14 package IDs | Source manifest evidence; not remote-consumption acceptance |
| Builds catalog introducing revision | `a53166539bf4441d5e33d04281b14c2d59e950c3` | Selects `HexalithEventStoreVersion=3.90.0` |
| Builds candidate workspace | working tree based on `a53166539bf4441d5e33d04281b14c2d59e950c3` | Mutable; `builds_qualifying_revision` remains pending |
| Architecture binding | `3.70.1` | Unchanged and still accepted rollback binding |
| Rollback EventStore revision | `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` | Retained; clean reciprocal qualification pending |

The `v3.90.0` release manifest names the same exact 14 IDs listed in the
historical inventory above, including `Hexalith.EventStore.Admin.Cli`, which is
not one of the 13 Builds catalog rows.

### Static runner and API binding

| Artifact | SHA-256 |
| --- | --- |
| `Props/Directory.Packages.props` | `eff93e3b335f0054b1be18eac289ac018bf10d0bc8f3825485f86ef2506b4671` |
| `SupportedPlatformPins.cs` | `a450bc045c25f1f8116e72c3d0cc6547f2dec3ebb3613e013294a3529949fb84` |
| `hexalith.module-manifest.v1.json` schema | `b5afc7781cc126111f28fd457ce9b91b2bdb63877f46870b3354e74f829219c9` |
| Positive module manifest | `cf39c3363870956a40c0a9ed2e4a13e440ef19c9422d6b7d89203a375b57ca18` |
| Positive module-run evidence | `8553bcef8ed8448096df28b201d064a0798d2ede41a27b5ebb1e9940abc61d92` |
| Package-version audit | `39b71b73d51222469f7f2770de50675f442753d9f37ed83023e1e873444049b2` |

All seven compared APIs at `v3.90.0` and source revision
`7854f8e51ce9b852bb6c3cac6012670122e93792` have identical blobs:

| API/type | Blob |
| --- | --- |
| `IAsyncDomainProjectionHandler` | `99ca7b0bc3e8bea370ecd2674f54f9f7f198933e` |
| `IReadModelStore` | `fb07789943f28e7594e3f31c748204645dbb0c75` |
| `IReadModelBatchStore` | `bf8952b3ebf868fb407539fcebb85fad2f203011` |
| `ReadModelWritePolicy` | `ff0d5d14b5cb90ab0d31771c9bbf4cac63b7d2f3` |
| `IDomainQueryHandler` | `693ff04e7d1eb32af9b515d14781c2a69c421f08` |
| `IQueryCursorCodec` | `18e67dc0eaf3a3b2b5ae3dc1b52ddf73b073b6f7` |
| `QueryCursorScope` | `53065de441d5863d56ae0953e772b3e1117bebee` |

The first six also match `v3.70.1`; `QueryCursorScope` retains only the
previously recorded additive `AddProjectionWatermark(long?)` difference.

### Audit and exact controls

The live audit was regenerated at `2026-08-04T16:34:07.9198968+00:00` from
the current catalog and NuGet V3 metadata. The validator passed all 284 package
rows, 139 families, and one source with zero catalog/audit mismatches. All 13
EventStore rows are listed stable `3.90.0`. The changed EventStore family is
conservatively retained with no invented direct consumer; still-current owner
decisions were preserved, while prior live-metadata claims that became false
were replaced with conservative evidence-aware defaults.

Source and packaged controls assert complete ordered diagnostic sequences for
every Module and Evidence negative. Transient `3.88.0` and `3.70.1` manifests
each exit `1` with exactly `HXM016`. The unrelated invalid-profile control
retains exactly `HXM009`, and the deliberate all-`F` evidence hash remains
invalid with exactly `HXE147`.

### First-result and isolated-rerun ledger

| Run | Exact command/result | Retention and disposition |
| --- | --- | --- |
| `.3` first result | `test-g4-tool-package-contracts.ps1 -Version 0.0.0-p1r-revalidate-390.3 -RequireControls -RetainPackageDirectory`; initial solution restore made no output or progress for more than six minutes; only PIDs `12085`, `12497`, and `12577` from that invocation were terminated | `INCONCLUSIVE`; no package inventory existed; exact timestamps/log were not retained, so this row cannot qualify |
| `.4` isolated rerun | Same gate at `0.0.0-p1r-revalidate-390.4`; restore/build passed, then `dotnet test ... -m:1` was forwarded to Microsoft.Testing.Platform as unsupported `--m`, exit `5`, zero tests | `FAIL`; no package inventory existed |
| `.5` corrected isolated rerun | Same gate at `0.0.0-p1r-revalidate-390.5`; Evidence `36/36` passed; Module `108/109` passed and the new source evidence-pin assertion exposed a null manifest test setup | `FAIL`; no package inventory existed |
| `.6` isolated rerun | Same source-validation-enabled gate at `0.0.0-p1r-revalidate-390.6`; Evidence `36/36`, Module `109/109`, integration `1/1`, package restore, help surfaces, positive controls, both transient stale pins, all persisted negatives, and unavailable evidence passed | `PASS`; retained diagnostic rerun; superseded by the timestamped `.7` candidate |
| `.7` final retained candidate | `NUGET_PACKAGES=<isolated> DOTNET_CLI_HOME=<isolated> TMPDIR=<isolated> MSBUILDDISABLENODEREUSE=1 pwsh -NoProfile -File ./Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-p1r-revalidate-390.7 -PackageDirectory /tmp/hexalith-builds-p1r-390-final.dtJqiS/packages -RequireControls -RetainPackageDirectory`; `2026-08-04T16:27:17.794478922Z`–`2026-08-04T16:27:59.857833253Z`; exit `0`; log SHA-256 `b4406e1c2e03553640ed8033b4605ee27eccde1b060a522e7795ee2d6df788d4` | `PASS`; non-acceptance candidate evidence |
| `.8` post-edit final retained candidate | `NUGET_PACKAGES=/tmp/hexalith-builds-p1r-390-final.YhznX7/nuget DOTNET_CLI_HOME=/tmp/hexalith-builds-p1r-390-final.YhznX7/dotnet-home TMPDIR=/tmp/hexalith-builds-p1r-390-final.YhznX7/tmp MSBUILDDISABLENODEREUSE=1 pwsh -NoProfile -File ./Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-p1r-revalidate-390.8 -PackageDirectory /tmp/hexalith-builds-p1r-390-final.YhznX7/packages -RequireControls -RetainPackageDirectory`; `2026-08-04T16:41:14.498514549Z`–`2026-08-04T16:43:42.062479348Z`; exit `0`; log SHA-256 `2603caa8aa18a410cdf5d2e456ee7c787e45729df86101785b8d8b726267a78e` | `PASS`; source-validation-enabled final implementation state, superseding `.7` as the durable non-acceptance candidate evidence |
| Post-`.8` independent focused rerun | `dotnet test test/Hexalith.Builds.Module.Tests/Hexalith.Builds.Module.Tests.csproj --configuration Release --no-restore -p:NuGetAudit=false --verbosity minimal` followed conditionally by the Evidence project; the Module command produced no output for more than 2m36s, so only its exact shell/process PIDs `83206` and `83418` were terminated; the Evidence command never started | `INCONCLUSIVE`; retained as an independent non-isolated result and does not replace the isolated `.8` pass |

### Final retained candidate inventory

The package inventory was withheld until all `.7` controls passed. Its SHA-256
is `352cddad124580ebcf5d6cfc6f12d565d9eb1a3e5a4929569d5ba5e8598a094e`.

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r-revalidate-390.7.nupkg` | 503038 | `51ecf8c3383836045064867fabdd97dfe56c36a1104665271f2c1adcfaa4ec0b` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r-revalidate-390.7.snupkg` | 52585 | `137580f7f49d576510f3a35ecf03fd1535cbb9bc67ca42d571b7bc4baf6afb18` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r-revalidate-390.7.nupkg` | 502298 | `b1cce6cf32940f22bc5b49158a4bdcd7db3362c100d984f7366ba7d7f17b44d1` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r-revalidate-390.7.snupkg` | 52334 | `64dee162230a2365e5858b6e39d10b4532193e13a0c32389227f077e176febfe` |
| Passing packaged evidence | — | `7c0091c9f458cbe4d5baf383c4ce773cfd8c440c2a945161f6e6f6b81020284e` |
| Unavailable packaged evidence | — | `16239bf1680b80dfa13df8626f1e9067b376bac5ade6ed66db9fa4d77e4b031b` |

Both evidence artifacts serialize EventStore `3.90.0`. The passing artifact
records exit `0`; the unavailable artifact remains non-passing with exit `2`
and `HXR002`. The retained inventory records both hashes and
`controls=passed`.

### Post-edit final retained candidate inventory

The `.8` lane ran after the success-only inventory removal order was finalized.
Its isolated restore retried one timed-out NuGet metadata request and then
completed. The build passed with zero warnings and zero errors; Evidence tests
passed `36/36`, Module tests passed `109/109`, integration tests passed `1/1`,
and all packaged qualification controls passed. No source-validation bypass was
used. The finalized inventory SHA-256 is
`20d21a89db7baf174c81dc8c089ed688b1d96085e66cad16c5d8ac3043b869d4`.

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r-revalidate-390.8.nupkg` | 503045 | `cbb23f712d39ee6f4868941c46194edff3a390c442a6d56c8e3440ae33d142a0` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r-revalidate-390.8.snupkg` | 52590 | `e39325c38ee211095eb9c1461ee325bc2c8206b5b2b8abc70139729523d9adff` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r-revalidate-390.8.nupkg` | 502300 | `16ae44c4368b61cb66e7dc7ba01db6f82a693b8aa8f3bd36b0651918cabdf178` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r-revalidate-390.8.snupkg` | 52335 | `a8bd2e01267ac85bc7910a50c04159da96b07683f5920770a69c03f5a685acb6` |
| Passing packaged evidence | — | `725ef49d0415a7d75b55a80f90e2d895db0ac57c5d0a4f805c757a07c3263a42` |
| Unavailable packaged evidence | — | `00cdf6bbd400e78c2eddac128058ab9fbcf9e329cd3eb2d75111e2daaae7faa1` |

The `.8` qualification log SHA-256 is
`2603caa8aa18a410cdf5d2e456ee7c787e45729df86101785b8d8b726267a78e`.
The durable umbrella evidence copy now contains the `.8` log, inventory, and
both evidence artifacts. The `.7` evidence above remains an append-only
historical result and is superseded, not rewritten.

### Pending gates and owner state

- An immutable clean `builds_qualifying_revision` containing these changes is
  pending because this work was neither committed nor authorized for commit.
- Final clean EventStore source/package/remote-restore qualification and the
  independently executable reciprocal `3.70.1` rollback remain pending.
- The Architecture binding remains `3.70.1`; P1R and P0/G-1 remain open.
- EventStore Owner, Builds Owner, Solution Architect, and Test Architect
  decisions all remain `pending`; no acceptance is inferred from these passes.

### Review-loop-3 hardening and retained `.9` verification

The loop-3 implementation preserves the `.3`–`.8` ledger above and adds a
new non-acceptance verification result after provenance, exact-contract,
success-only inventory, durable-path, reused-directory, and line-ending
hardening.

The audit was regenerated at `2026-08-04T17:26:03.3714227+00:00` from
Builds revision `a53166539bf4441d5e33d04281b14c2d59e950c3`. Its SHA-256 is
`6d967eb2568a0cc6778c442e00e15fd0d78faae3e4ec7c9685030c452f2ffdd3`.
The live validator passes 284 packages, 139 family decisions, and one source.
The `hexalith-eventstore` family is retained with no current direct consumer;
its prior `3.88.0` owner/runtime claims survive only as explicitly labeled
historical context and are not current acceptance evidence.

Audit generator and validator regression suites pass 28 and 18 scenarios,
respectively. The generator rejects malformed source, package, family, and
consumer identity relations and preserves accepted decisions only when catalog,
source scope, package metadata, and owned direct-consumer provenance are
unchanged. Consumer or metadata drift refreshes the current decision
conservatively while retaining durable constraints as historical facts.

The `0.0.0-p1r390-loop3.9` source-validation-enabled package gate passed in a
new directory. It observed a zero-warning/zero-error Release build, Evidence
`31/31`, Module `108/108`, integration `1/1`, both package restores, all help
surfaces, all exact negative contracts, passing evidence at EventStore
`3.90.0` with final status `completed` / exit `0` / no rule, and unavailable
evidence at EventStore `3.90.0` with final status `unavailable` / exit `2` /
`HXR002`. The behavioral gate regression separately proved that reused and
failed directories expose no new complete inventory.

The working-tree-only fixture additions could not satisfy the gate's
fresh-checkout tracking assertion before an authorized commit. For that reason,
the `.9` verification used a byte-identical isolated fixture copy while leaving
source validation enabled. This limitation is recorded rather than treating the
run as immutable accepted evidence.

The successful directory is retained byte-for-byte under the umbrella-relative
path
`_bmad-output/implementation-artifacts/qualification-evidence/6-1-p1r-390-loop3-9/`.
Its inventory SHA-256 is
`9e94b8cfe0f5052dbc305eb288ed545b2b2ded47ec1fc78f1a1a9708b4c77254`;
the qualification log SHA-256 is
`0b423136e42f7c03312f1f467b122a52f196e0d399e33ca1721aaef29bf50561`.
The inventory records and re-verifies four package artifacts and 34 relative
`qualification-evidence/` files:

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r390-loop3.9.nupkg` | 503006 | `bb0e93665e241d3e8a6f9431160d360dcb7b9569928a9e6e6114b8d8e69d9c05` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r390-loop3.9.snupkg` | 52570 | `ad6153319223b6df0252b5f94008cc567d786d3e88db9ca54321111011302a1e` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r390-loop3.9.nupkg` | 502277 | `acb9b5504fc803329b6f8701b18760fb2d014b4dd1cf837823836c5f138994ab` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r390-loop3.9.snupkg` | 52329 | `98650fa22f46e158a0e8ee4b98b53f5b4e05877bb9564e5f067f2cd6b54a90aa` |
| Passing packaged evidence | — | `e8b717df27fb0f6674b4cae4819f77f5fd36c1609ce893bd6d2db468e333a641` |
| Unavailable packaged evidence | — | `7e9be52056e3a1f8767851e2c1bdcdfbe549674270660914238f20ddff235785` |

This supersession remains pending. The Architecture binding is still
`3.70.1`; the immutable Builds qualifying revision, reciprocal rollback run,
P1R transition, and EventStore Owner, Builds Owner, Solution Architect, and Test
Architect decisions are all still pending. No acceptance is inferred from the
passing implementation checks.

### Review-loop-4 provenance, release, and retained `.13` verification

The loop-4 implementation preserves every earlier result above and closes the
remaining wrapper-help, declaration-byte, historical-field, mode, partial
evidence, and fixture-provenance gaps. Local-tool help is now invoked as
`dotnet tool run <command> -- <arguments>` and matched to tool-specific root and
subcommand text; retained logs reject wrapper help and raw secret material.
Module and Evidence contracts separately decode the numeric process exit and
the serialized outcome enum, then assert the complete ordered diagnostic,
status, phase, category, rule, EventStore pin, and invocation-command contract.

The current audit was regenerated at
`2026-08-04T18:30:56.2260129+00:00`. Its SHA-256 is
`0c3e30c6336a4608c16ab5915d9ff0a46557669919a9d8ffd4bf96ef6c08867f`;
the exact catalog-byte hash is
`eff93e3b335f0054b1be18eac289ac018bf10d0bc8f3825485f86ef2506b4671`,
and the consumer-relation hash is
`1f8d336aa5b5ddafdbc93965f4bfa8ce19b1f2dc538b94a38760a07b53c2cd3d`.
The live validator passes 284 packages, 139 families, and one source with zero
mismatches; all 13 EventStore catalog rows are stable `3.90.0`. All 139 family
decisions are conservatively retained because accepted legacy evidence without
the new trusted declaration provenance is refreshed rather than grandfathered.
Generator and validator regression suites pass 41 and 27 scenarios,
respectively, including every catalog/source/package/consumer/declaration drift,
malformed relation, binding, and typed-history rejection branch.

The first default restore/build command in this loop produced no output for
more than five minutes amid unrelated long-lived build processes. It is retained
as `INCONCLUSIVE`; only the exact process IDs from that invocation were
terminated. An isolated retry under `/tmp/p1r-loop4-validation.vHaxl7` then
passed restore, a zero-warning/zero-error Release build, Module `108/108`,
Evidence `31/31`, and integration `1/1`. Package attempt `.10` reached package
controls and exposed an incorrect numeric cast of the serialized outcome enum;
`.11` failed the source analyzer; `.12` passed build, tests, and packaging but
correctly rejected a stale copied fixture set. None wrote an inventory. The
behavioral gate also passes explicit reused-output, pre-package failure,
post-package failure after four package artifacts, and duplicate-evidence-name
scenarios; each leaves no inventory.

The fresh source-validation-enabled `0.0.0-p1r390-loop4.13` lane passed first
under `/tmp/p1r-loop4-durable.rr77LX/6-1-p1r-390-loop4-13` and is now retained
at the umbrella-relative path
`_bmad-output/implementation-artifacts/qualification-evidence/6-1-p1r-390-loop4-13/`.
It contains the original 39 files—four packages, 34 uniquely named
qualification-evidence files, and the final inventory—plus two durable
verification logs. The inventory SHA-256 is
`89c63650da9001a341c35db86e4e60d332cc8420a415c79e6217fa53fe67eb67`;
the retained qualification log SHA-256 is
`5a50decff2bdc458511c68c0c63c4131aacb3f6103724e560d5d1a971ddbd129`.

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r390-loop4.13.nupkg` | 502293 | `ea6077e153271a2e2884bd96a480ce77a15330671abb88264627f0c752256304` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r390-loop4.13.snupkg` | 52335 | `26b0f6b9d03095470d6f35fb5c5df547fa2be1dc51a52e5c74eac221c3b60433` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r390-loop4.13.nupkg` | 503028 | `7f40a4479bb9c52ad74fe524d0ec3629b2a14316f5ba900527242763c7e60b2c` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r390-loop4.13.snupkg` | 52575 | `240963a9fae1c8caf0a706562e957ebcb457c77b9bad5abb73d4029188b2ab14` |
| Passing packaged evidence | 1964 | `1106bab899a6e74dc6ea437416da735504a36ffcc068db571fb28d10bc8739aa` |
| Unavailable packaged evidence | 1884 | `3c85f5c9290c3142d0206d92a522c77d6e52ba0278b21eb4f7eadc6f6de6a325` |

The passing artifact records EventStore `3.90.0`, final status `completed`,
exit `0`, phase/category `None`, no rule, and a `hexalith-module down` command.
The unavailable artifact records the same pin, final status `unavailable`, exit
`2`, phase `Prerequisite`, category `PrerequisiteUnavailable`, rule `HXR002`,
and a `hexalith-module run` command. The fixture manifest contains every copied
fixture and has SHA-256
`030d0d2324e0e40813efa47a9a187c523d9ee8136f374cc87a425b10aa5cb6fa`.

Because the new fixture files are working-tree-only until an authorized commit,
the `.13` lane truthfully records `fixtures.mode=external` and
`releaseEligible=false`; publisher validation rejects it before token or push.
The publisher regression suite separately passes stable/prerelease success and
all skipped-source, skipped-control, fixture-build, external-fixture,
false-release-eligibility, and missing-evidence rejection branches, always with
zero pushes on rejection. A future candidate can become release-eligible only
after an official build, executed/passed source validation and controls, and an
exact repository-tracked fixture/evidence proof.

The `.13` audit-validation log SHA-256 is
`7b4f3b9815caf6dcdf7184361d005e3142d56e4afcb7c561e916bb39d95c8e36`;
the coordinate-verification log SHA-256 is
`b71021b1770d05912c2bbcf989b2551bd851fdf43a49ab7e6b91c176fa54667c`.
They retain the exact commands/results for the live 284/139/1 audit, catalog
and audit hashes, EventStore tag/revision and 14-package manifest, Builds
revision/catalog/runner coordinates, stale controls, and unchanged Architecture
binding.

All three preserved candidates now resolve under the umbrella evidence root:

| Bundle | Files | Verified inventory-relative entries | Inventory SHA-256 |
| --- | ---: | ---: | --- |
| `qualification-evidence/6-1-p1r-390/` (`.8`) | 11 | 6 | `20d21a89db7baf174c81dc8c089ed688b1d96085e66cad16c5d8ac3043b869d4` |
| `qualification-evidence/6-1-p1r-390-loop3-9/` (`.9`) | 39 | 38 | `9e94b8cfe0f5052dbc305eb288ed545b2b2ded47ec1fc78f1a1a9708b4c77254` |
| `qualification-evidence/6-1-p1r-390-loop4-13/` (`.13`) | 41 | 38 | `89c63650da9001a341c35db86e4e60d332cc8420a415c79e6217fa53fe67eb67` |

Every original file in `/tmp/p1r-loop2-durable.Uv8PjZ/6-1-p1r-390`,
`/tmp/p1r-loop3-durable.IJZjFl/6-1-p1r-390-loop3-9`, and
`/tmp/p1r-loop4-durable.rr77LX/6-1-p1r-390-loop4-13` matches its relocated copy
byte-for-byte. Every path named by all three inventories resolves after
relocation and matches the recorded byte size and SHA-256. The `.8` durable copy
was made self-contained with its four still-resolvable packages and its two
evidence files copied to their legacy inventory-relative paths; its five
original preserved files were not changed.

Architecture remains `3.70.1`; the immutable qualifying revision, reciprocal
rollback run, P1R transition, and EventStore Owner, Builds Owner, Solution
Architect, and Test Architect decisions all remain pending. No acceptance is
inferred from these implementation passes.

### Review-loop-5 exact package, evidence, and audit verification

Loop 5 preserves every prior bundle and decision above. It hardens the package
boundary so that release eligibility is a JSON Boolean, every declared
qualification artifact has exact byte coverage, and each `.nupkg` and
`.snupkg` contains exactly one nuspec whose ID and version match the inventory.
Publisher regression controls reject string eligibility bypass, same-length
evidence tampering, missing evidence coverage, row-version drift, and both
nupkg and snupkg identity/version drift before any push.

The live audit was regenerated at
`2026-08-04T19:39:24.5686211+00:00` from Builds revision
`a53166539bf4441d5e33d04281b14c2d59e950c3`. Its SHA-256 is
`811284bcb4ff8e20e922760676934fa7db962c818fec321a8f3f2ce7c1436509`;
the catalog SHA-256 remains
`eff93e3b335f0054b1be18eac289ac018bf10d0bc8f3825485f86ef2506b4671`,
and the owned-consumer relation/declaration-byte SHA-256 is
`c1926c58e2385c8349db5b54b966696e413dd61cfb25e79f531c7ed20ca0c6f7`.
Validation passes 284 packages, 139 families, and one source. The audit now
discovers tracked `.csproj`, `.props`, and `.targets` declarations; its 17
relations include five exact `Hexalith.Build.props` PackageReferences. Every
package and family history record has a typed schema. There are zero current
accepted families, so no owner decision or current acceptance is invented.

Generator and validator regression suites pass 55 and 33 scenarios,
respectively. They cover exact accepted owner-field round trips, tracked XML
PackageReference semantics, explicit fixture byte and semantic binding,
catalog/source/package/consumer/declaration drift, typed legacy migration,
unknown and missing history schemas, and rejection of an accepted decision
whose current provenance is not `preserved`.

The first two official loop-5 attempts (`.14` and isolated `.15`) each spent
ten minutes without output in their initial solution restore amid unrelated
long-lived host processes. Only their own process trees were terminated. They
are `INCONCLUSIVE`; neither produced a package directory or inventory. The
warm-cache source for the later retries was the preserved successful loop-4
isolated cache `/tmp/p1r-loop4-validation.vHaxl7/nuget`. It was copied, never
shared live, into a fresh unique cache for each retry. `.16` then passed
restore, a zero-warning/zero-error Release build, Evidence `31/31`, Module
`108/108`, integration `1/1`, packaging, and local tool restore before a new
exact null-rule assertion exposed PowerShell string coercion. It failed with
four package artifacts and no inventory. The assertion now preserves JSON
null as distinct from the empty string. `.17` passed the complete lane but is
superseded because its correctly nonrelease repository-untracked fixture mode
rendered an inaccurate `<external>` root label.

The final source-validation-enabled `.18` command was:

`NUGET_PACKAGES=/tmp/p1r-loop5-warm18.eyP0J2/nuget DOTNET_CLI_HOME=/tmp/p1r-loop5-warm18.eyP0J2/dotnet-home TMPDIR=/tmp/p1r-loop5-warm18.eyP0J2/tmp MSBUILDDISABLENODEREUSE=1 pwsh -NoProfile -File ./Tools/test-g4-tool-package-contracts.ps1 -Version 0.0.0-p1r390-loop5.18 -RequireControls -RetainPackageDirectory -PackageDirectory /tmp/p1r-loop4-durable.bdA0hw/qualification-evidence/6-1-p1r-390-loop5-18`

It passed restore, the zero-warning/zero-error Release build, Evidence `31/31`,
Module `108/108`, integration `1/1`, both official package builds, the real
local-tool help surfaces, positive down/test/readiness paths, unavailable
run/test evidence, all module negative fixtures, and all Evidence negative
fixtures. The success-only inventory contains two package rows and 36 exact
qualification-evidence rows. Its SHA-256 is
`6a03cd08baea8fc3fdabde4ba564681de54d45f0aa7cbc7301b8ad1c18b812e4`;
the qualification log SHA-256 is
`d841ef2b04996c724c75b9724742a3d4ab63fa28e8a6aaeaaabc471427e861ca`.

| Artifact | Size | SHA-256 |
| --- | ---: | --- |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r390-loop5.18.nupkg` | 502295 | `8abe015b46f1b09efb7fc33bb982adc50570db673e95c9ff714354d0987e8d54` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r390-loop5.18.snupkg` | 52334 | `ddb27625f4e0f48b5814ad1cd84cb3fd1b86847a9a9898392ae4df70f3172de5` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r390-loop5.18.nupkg` | 503016 | `a11e314c87b7491ce589837a2b8bbd740a90e411f6c4fa2304771ef8acc2d97a` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r390-loop5.18.snupkg` | 52573 | `ca94c4cab1bc2d8b9ac53a8018aafc1e4f2061733e5bba300754a109e2a117df` |
| Passing packaged down evidence | 1964 | `73418f3328588a85edfbc9c37af100c834d9e60085f06b86fc10aba2ee37ea8d` |
| Packaged test evidence | 2023 | `f8c28c51cb4f0ed2d360f41183fb681c4b8b97ae63fc34929c20c43303f57a18` |
| Unavailable packaged run evidence | 1884 | `aa8e14b2f3746a62355df41e51336ab78d236672f4cd54d9a94db89f4320f7d6` |

All four package archives contain exactly one nuspec and bind their respective
`Hexalith.Builds.Evidence.Cli` or `Hexalith.Builds.Module.Cli` ID to exact
version `0.0.0-p1r390-loop5.18`. Packaged evidence records exact tool version
`0.0.0-p1r390-loop5.18+a53166539bf4441d5e33d04281b14c2d59e950c3`,
source revision `unavailable`, dirty marker `dirty`, and the exact invocation
and manifest hash. The new packaged `module test` path is retained as
non-passing prerequisite evidence with exit `2`, phase `Prerequisite`, category
`PrerequisiteUnavailable`, and rule `HXR002`; it is not mislabeled as a pass.

The `.18` inventory truthfully records Boolean `releaseEligible=false`,
`fixtures.mode=repository-untracked`, and `fixtures.root=test/fixtures` because
the new frozen fixture files are not committed. The real publisher rejects
this inventory before token lookup or push; no publication occurred. A clean
checkout can become release-eligible only after those exact bytes are tracked
and every same control passes. The publisher and qualification-gate suites
pass, including success channels and all bypass/tamper/no-inventory branches.

The umbrella evidence tree now contains the preserved `.8`, `.9`, and `.13`
bundles plus loop-5 `.16`, `.17`, and final `.18`. The `.18` bundle has 41
files. At loop start, the restored loop-4 source contained 92 files: 91 in the
three prior candidate bundles plus the root index. All 91 prior bundle files
still match their recorded SHA-256 values; the root README was append-only
updated for the loop-5 rows. The complete umbrella copy matches
`/tmp/p1r-loop4-durable.bdA0hw/qualification-evidence` byte-for-byte.

Architecture remains bound to `3.70.1`. The immutable Builds qualifying
revision, final EventStore package/remote-consumption proof, reciprocal
rollback qualification, P1R transition, and EventStore Owner, Builds Owner,
Solution Architect, and Test Architect decisions all remain pending. No
acceptance is inferred from the `.18` implementation pass.

## Stopped `3.97.0` supersession attempt — 2026-08-24

This append-only section supersedes no historical result and records a blocked,
non-qualifying attempt only. The selected candidate remained the equivalent
EventStore tuple `v3.97.0` / `3.97.0` /
`94591f3539ce30372db58e5fdd3ba017ea8c07b8`; current EventStore HEAD
`da52e2c85ecc5909fa8ce2547e626f3968c056ef` remained an unselected
observation. Architecture, planning/status, dependency gitlinks, publication,
release state, and all owner decisions were not changed.

### Completed Builds alignment evidence

A local immutable candidate commit
`fb05dd84625abdcd1a62d2664e8557379fd631bb` on
`fix/p1r-397-candidate`, based on
`2f46aaee2ecb0b3f121d50ab8cc58601901046f4`, aligns the active runner,
schema, tests, unrelated fixtures, serialized evidence, package-control
assertions, and coupled hashes to `3.97.0`. It preserves separate `3.88.0`
and `3.70.1` manifests with exact `HXM016` expectations, invalid-profile
`HXM009`, and the deliberate all-`F` evidence-hash mismatch. Before the stop,
static parity passed, the authoritative catalog test passed 49 identities and
three shared versions, and the complete package audit passed 284 packages, 139
families, and one source. The exact candidate message
`fix(runner): align EventStore pin to 3.97.0` passed repository-pinned
commitlint before and after commit. These results do not qualify the candidate
because no later clean lane ran.

### First non-passing gate and disposition

The first EventStore coordinate row, `es-001-coordinates`, ran in the isolated
`94591f3539ce30372db58e5fdd3ba017ea8c07b8` worktree from
`2026-08-24T16:34:36.095461306Z` to
`2026-08-24T16:34:36.633336524Z`. It exited `2` and is `FAIL`.
The evidence-wrapper call placed the inner command in an outer double-quoted
shell argument, so two intended inner command substitutions executed before
the wrapper changed to the EventStore worktree. The retained command therefore
contains malformed `test -z` and `test  -eq 14` assertions and stopped with
`bash: line 1: test: -eq: unary operator expected`.

The exact failed log is retained at
`_bmad-output/implementation-artifacts/qualification-evidence/6-1-p1r-397-20260824/logs/es-001-coordinates.log`
with SHA-256
`eb0a2e11dc9bbb2bd4e94377b8a8c544c33f9a5dcc588da63e39a3411a8f1a8e`.
The partial evidence bundle contains ten hashed artifacts plus its manifest;
`artifact-manifest.sha256` has SHA-256
`1c535eac91ae9433a21a20ab1e948e3951a2f62184ac7af137316dfcf0fb0265`.
The row emitted the 14-ID `v3.97.0` release manifest before failing, but those
partial observations cannot qualify coordinate selection or substitute for the
remote-consumption gate.

Per the stop-first rule, the command was not rerun and no later gate started.
The EventStore source/package/API lanes, 14-package remote restore, clean
candidate Builds qualification, packaged G-4 controls, rollback commit and
reciprocal `3.70.1` execution all remain pending. The next authorized attempt
must retain this failed row, correct the argument boundary, restart at clean
coordinate capture, and proceed only if that new row passes.

### Pending owner decisions for this attempt

| Role | Named approver | Decision | UTC date | Coordinates/evidence | Required next action |
| --- | --- | --- | --- | --- | --- |
| EventStore Owner | pending | pending | pending | `v3.97.0` attempt; stopped bundle above | Authorize or execute a new clean coordinate attempt; no acceptance inferred |
| Builds Owner | pending | pending | pending | candidate commit `fb05dd84625abdcd1a62d2664e8557379fd631bb`; unqualified | Retain the local commit and await complete candidate plus rollback qualification |
| Solution Architect | pending | pending | pending | Architecture remains `3.70.1` | Make no rebinding decision until every required lane passes |
| Test Architect | pending | pending | pending | `es-001-coordinates` = `FAIL` | Require a new clean run beginning at coordinate capture and preserving this failure |

## Stopped `3.97.0` supersession attempt 2 — 2026-08-24

This append-only section preserves the earlier stopped attempt and records a
second blocked, non-qualifying attempt. The selected equivalent EventStore tuple
remained `v3.97.0` / `3.97.0` /
`94591f3539ce30372db58e5fdd3ba017ea8c07b8`. Current EventStore HEAD
`da52e2c85ecc5909fa8ce2547e626f3968c056ef` remained an unselected
observation. Architecture remained bound to `3.70.1`; planning/status,
dependency gitlinks, publication, release state, and owner decisions were not
changed.

### Passed evidence before the stop

The clean coordinate row passed at the selected EventStore revision, which
described exactly as `v3.97.0` and resolved the package tag to the same commit.
The 14-ID release manifest retained SHA-256
`6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`.
The unselected HEAD described as `v3.97.0-5-gda52e2c8` and had no `src/`
diff from the selected tag. Exact selected, HEAD, and rollback dependency
gitlinks were retained.

The clean Builds candidate at
`fb05dd84625abdcd1a62d2664e8557379fd631bb` passed static parity, the
49-identity/three-shared-version catalog contract, and the complete
284-package/139-family/one-source audit. Its runner, schema, active fixtures,
serialized evidence, package controls, and coupled hashes bind `3.97.0`; the
`3.88.0` and `3.70.1` manifests remain exact `HXM016` negatives,
invalid-profile remains `HXM009`, and the deliberate all-`F` evidence hash
remains invalid.

The seven-API comparison passed. Selected package/source and unselected HEAD
blobs were identical. The rollback differs only by the previously recorded
additive `QueryCursorScope.AddProjectionWatermark(long?)` surface; no compared
API was removed or changed.

### First non-passing gate and disposition

`es-source-001-restore` ran in the clean selected EventStore worktree from
`2026-08-24T17:43:55.766785516Z` to
`2026-08-24T17:43:59.678633800Z`. The exact mandated Debug source-mode restore
exited `1` and is `FAIL`. Its retained log SHA-256 is
`d44d2b3788179e9d945e2bbf6aafcb081f28f0e4e9fb853c63e23dd100011ccc`.

The exact EventStore worktree retained empty nested-submodule directories, as
required by the prohibition on nested initialization. Its import-only
`Directory.Packages.props` therefore found none of its three relative
`Hexalith.Builds/Props/Directory.Packages.props` candidates. NuGet Central
Package Management emitted widespread `NU1010` missing-`PackageVersion`
diagnostics and ended with a NuGet null-reference error. The post-failure state
capture proved that the worktree remained clean at the selected revision.

The complete 18-entry artifact manifest is retained at
`_bmad-output/implementation-artifacts/qualification-evidence/6-1-p1r-397-20260824-attempt2/artifact-manifest.sha256`
with SHA-256
`fbe5596f7dc1dd5d3a3bbc3b3ba02f9ff8b7e9da284b9b890fe5baccfe9f25d4`.
The command ledger contains six `PASS` rows and one `FAIL` row; every retained
artifact resolves and verifies.

Per the stop-first rule, no later source build/test, package-source lane,
remote 14-package restore, clean Builds restore/build/test/package lane,
rollback commit, or reciprocal rollback execution started. The candidate
Builds commit remains implemented but unqualified.

### Pending owner decisions for attempt 2

| Role | Named approver | Decision | UTC date | Coordinates/evidence | Required next action |
| --- | --- | --- | --- | --- | --- |
| EventStore Owner | pending | pending | pending | equivalent `v3.97.0` tuple; source restore `FAIL` | Select an exact dependency-materialization or import-routing method for the recorded EventStore dependency gitlinks without changing source identity |
| Builds Owner | pending | pending | pending | candidate `fb05dd84625abdcd1a62d2664e8557379fd631bb`; static/catalog/audit passed only | Attest the exact Builds catalog source made available to a future EventStore lane and retain the unqualified candidate |
| Solution Architect | pending | pending | pending | Architecture remains `3.70.1`; nested initialization forbidden | Approve a clean-worktree dependency method consistent with AD-6 and the qualification contract |
| Test Architect | pending | pending | pending | attempt-2 manifest above; `es-source-001-restore` = `FAIL` | Require a new clean attempt from coordinate capture with both stopped bundles preserved |

No acceptance is inferred. P1R, P0, P2, P3, P4, Story 6.1, readiness, and all
transitive Epic 7/8 gates remain blocked by their own contracts.

## Stopped `3.97.0` supersession attempt 3 — 2026-08-24

This append-only section preserves both earlier stopped attempts and records a
third blocked, non-qualifying attempt. The selected equivalent EventStore tuple
remained `v3.97.0` / `3.97.0` /
`94591f3539ce30372db58e5fdd3ba017ea8c07b8`. Current EventStore HEAD
`da52e2c85ecc5909fa8ce2547e626f3968c056ef` remained an unselected
observation. Architecture remained bound to `3.70.1`; planning/status,
dependency gitlinks, publication, release state, and owner decisions were not
changed.

### Approved dependency route and passed evidence

The approved route used repository-supported sibling materialization. Each
clean detached EventStore worktree lived at `<lane>/EventStore`; a separate
clean detached Builds worktree lived at `<lane>/references/Hexalith.Builds` at
candidate `fb05dd84625abdcd1a62d2664e8557379fd631bb`. The EventStore
import search resolved that exact catalog without nested submodule
initialization, EventStore source changes, dependency-gitlink movement, or
mandated-command changes.

Before this attempt began, `history-001-preserve` verified every artifact in
both earlier stopped bundles against manifest SHA-256 values
`1c535eac91ae9433a21a20ab1e948e3951a2f62184ac7af137316dfcf0fb0265`
and
`fbe5596f7dc1dd5d3a3bbc3b3ba02f9ff8b7e9da284b9b890fe5baccfe9f25d4`.
It also verified the append-only record ancestry from
`fb82c0caedfa4a98b413164dc630e122b50b2970` through
`0942bcb351c82dd46c0aa6c02b503992eff90fdf`.

The clean coordinate/routing row passed. The selected revision described
exactly as `v3.97.0`, the package tag resolved to the same commit, and the
14-ID manifest retained SHA-256
`6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`.
Unselected HEAD still had no `src/` diff from the selected tag. The exact
sibling Builds catalog passed with SHA-256
`c51967e25c5a8770fcae6469f335b38775904c5c56b8a4fc4fb67605a1cb84bb`.

The immutable Builds candidate again passed static parity, the
49-identity/three-shared-version catalog contract, and the complete
284-package/139-family/one-source audit. The seven-API comparison passed with
selected package/source and unselected HEAD blobs identical; rollback differed
only by the already recorded additive
`QueryCursorScope.AddProjectionWatermark(long?)` surface.

The exact mandated Debug source-mode restore then passed with the isolated
cache, proving the sibling catalog route resolved the attempt-2 restore
blocker. The exact serialized Debug source-mode build passed with zero warnings
and zero errors.

### First non-passing gate and disposition

`es-source-003-contracts-test` ran in the clean selected EventStore worktree
from `2026-08-24T18:22:22.543299647Z` to
`2026-08-24T18:23:51.038975740Z`. The exact mandated Contracts test command
exited `1` and is `FAIL`: 29 failed, 1503 passed, zero skipped, 1532 total.
Its retained log SHA-256 is
`a85c4b7a94f7277000665059b6677f017dceea4836a09d98565bbaa1037130b5`.

The failures prove the sibling catalog is sufficient for MSBuild import and
compilation but not for repository-governance tests that directly consume
nested dependency bytes. The earliest failures reported absent
`Hexalith.Tenants` project content, the absent shared LLM instruction file,
and absent `Hexalith.Builds/Github/dapr-init/action.yml` at recorded nested
paths. Other failures reported a Builds tool path unavailable in pinned commit
`a53166539bf4441d5e33d04281b14c2d59e950c3`, unavailable Git tree
`a07078ad74d3727bc5a6b6d85d47d56a6e5c9fec`, and consequent OCI/evidence
mutation assertion failures. No result was filtered, retried, or relabeled.

The administrative post-failure capture proved the EventStore worktree was
still clean at the selected revision, the sibling Builds worktree was still
clean at the exact candidate, and all seven recorded nested submodules remained
uninitialized. The retained attempt-3 bundle contains 27 verified entries.
Its manifest SHA-256 is
`efe58b140a3a981d4035c1cd0e88b249e7ebf2463f1c57a946adf9d71070425b`;
the command ledger contains nine `PASS` rows, including preservation and
administrative state capture, and one `FAIL` row.

Per the stop-first rule, the remaining source tests, package-source lane,
remote 14-package restore, clean Builds restore/build/test/package lane,
official G-4 controls, rollback commit, and reciprocal rollback execution did
not start. The candidate Builds commit remains implemented but unqualified.
Any future attempt requires new owner authorization, must preserve all three
stopped bundles, and must restart at coordinate capture with an exact
dependency-object/materialization route that satisfies the repository-
governance tests without nested submodule initialization or source/gitlink
changes.

### Pending owner decisions for attempt 3

| Role | Named approver | Decision | UTC date | Coordinates/evidence | Required next action |
| --- | --- | --- | --- | --- | --- |
| EventStore Owner | pending | pending | pending | equivalent `v3.97.0` tuple; restore/build `PASS`, Contracts `FAIL` | Decide whether to authorize another clean attempt and select an exact dependency-object route; no acceptance inferred |
| Builds Owner | pending | pending | pending | candidate `fb05dd84625abdcd1a62d2664e8557379fd631bb`; import/static/catalog/audit only | Attest any future nested dependency bytes and retain the unqualified candidate |
| Solution Architect | pending | pending | pending | Architecture remains `3.70.1`; nested initialization forbidden | Approve no rebinding until every required lane and reciprocal rollback pass |
| Test Architect | pending | pending | pending | attempt-3 manifest above; `es-source-003-contracts-test` = `FAIL` | Require the retained 29-failure result and a newly authorized run from coordinate capture |

No acceptance is inferred. P1R, P0, P2, P3, P4, Story 6.1, readiness, and all
transitive Epic 7/8 gates remain blocked by their own contracts.

## 3.102.0 candidate / 3.70.1 rollback revalidation attempt — 2026-09-05

This append-only section supersedes no historical result above (the `3.88.0`,
`3.90.0`, and stopped `3.97.0` sections are preserved byte-for-byte) and records
a **partially executed, non-qualifying** attempt per `spec-6-1-p1r-revalidate-baseline-3-102-0.md`.
The selected candidate tuple is the equivalent EventStore identity `v3.102.0` /
`3.102.0` / `4ae9cee1e9abe050402fd1405a9abd54892ba13f`; the rollback tuple is
`v3.70.1` / `3.70.1` / `f13f9925fdca53efa2ab8c90d396ab106f91bb9c`. Architecture,
planning/status status fields, dependency gitlinks, publication, release state,
and all owner decisions are unchanged. **Two structural blockers stopped this
attempt short of Phase 6**; both are recorded honestly below with root cause
rather than routed around. All work ran in isolated worktrees/branches; the
base checkouts at `references/Hexalith.EventStore` and `references/Hexalith.Builds`
in the `Hexalith.Projects` umbrella were never modified.

### 1. Selected and rollback coordinate tables

| Field | Selected value | Rollback value |
| --- | --- | --- |
| `eventstore_source_revision` | `4ae9cee1e9abe050402fd1405a9abd54892ba13f` | `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` |
| `eventstore_source_describe` | `v3.102.0` (exact, clean) | `v3.70.1` (exact, clean) |
| `eventstore_package_version` | `3.102.0` | `3.70.1` |
| `eventstore_package_tag` | `v3.102.0` | `v3.70.1` |
| `eventstore_package_source_revision` | `4ae9cee1e9abe050402fd1405a9abd54892ba13f` (same commit) | `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` (same commit) |
| `source_package_equivalent` | `true` — trivially, source and package-source are the identical commit | `true` — same construction |
| `eventstore_release_manifest_sha256` | `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae` | same (byte-identical manifest at this tag too) |
| `builds_catalog_introducing_revision` | `308e3921d60d2e8f87dd69a7f9b6f3dd016df9ef` ("fix: update HexalithEventStoreVersion to 3.102.0", `Hexalith.Builds`, ancestor of the current `main` HEAD `e81e62770bcc72bc3d6722aefe6844775b82b6cf`) | n/a (rollback rebinds the catalog itself, see below) |
| `builds_qualifying_revision` | `3d16d3e090ae822bc9cdc64c4156d31c9acf1146` on local branch `fix/p1r-3102-candidate` (unpushed, unmerged), based on `e81e62770bcc72bc3d6722aefe6844775b82b6cf` | — |
| `rollback_builds_revision` | — | `6ea4c27ade695964ee3c95b1d28e0058f4e1430e` on local branch `fix/p1r-3701-rollback` (unpushed, unmerged), based on `e81e62770bcc72bc3d6722aefe6844775b82b6cf` |
| `architecture_pre_acceptance_revision` | `Hexalith.Projects@393bd990047d9d80160e1aabdcc290c4c67f91ef` (current superproject HEAD; Architecture Spine content re-read and confirmed unchanged: `references/... /ARCHITECTURE-SPINE.md` line 368 still reads `Hexalith.EventStore package binding | 3.70.1; ... submodule revision f13f9925fdca53efa2ab8c90d396ab106f91bb9c, Builds HexalithEventStoreVersion=3.70.1 ...`) | same |
| `rollback_eventstore_revision` | `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` (contract-fixed, unchanged) | same |

### 2. Source/package divergence decision and affected production diff

`source_package_equivalent = true` for both tuples by construction (source and
package-source are literally the same commit), so the `git diff --name-status
<PACKAGE_SOURCE_REV>..<SOURCE_REV> -- src` required by the contract is trivially
empty for each — not separately re-proven.

**Unselected observation (post-tag drift), explicitly classified per the
contract's I/O matrix**: current `Hexalith.EventStore` `main` HEAD
(`6d436b3c271af1c3d0ef15b102c786465ed4a258`) is **21 commits** past `v3.102.0`
(the originating spec assumed 18), and `git diff --name-status
v3.102.0..HEAD -- src` touches **11 production files** (the originating spec's
Boundaries named only 4 `Actors/*` files):

- `src/Hexalith.EventStore.Server/Actors/ActorStateRemediationException.cs` (added)
- `src/Hexalith.EventStore.Server/Actors/AggregateActor.cs` (modified)
- `src/Hexalith.EventStore.Server/Actors/IdempotencyChecker.cs` (modified)
- `src/Hexalith.EventStore.Server/Actors/UnpublishedPublicationIndex.cs` (modified)
- `src/Hexalith.EventStore.Client/Subscriptions/DaprEventStoreDomainEventMarkerStore.cs` (modified)
- `src/Hexalith.EventStore.Client/Subscriptions/EventStoreDomainEventMarkerAcquisitionResult.cs` (modified)
- `src/Hexalith.EventStore.Client/Subscriptions/EventStoreDomainEventMarkerRecord.cs` (modified)
- `src/Hexalith.EventStore.Client/Subscriptions/EventStoreDomainEventMarkerState.cs` (modified)
- `src/Hexalith.EventStore.Client/Subscriptions/EventStoreDomainEventProcessor.cs` (modified)
- `src/Hexalith.EventStore.Client/Subscriptions/IEventStoreDomainEventMarkerStore.cs` (modified)
- `src/Hexalith.EventStore.Client/Subscriptions/InMemoryEventStoreDomainEventMarkerStore.cs` (modified)

Per the contract ("additional production changes discovered from the selected
coordinates join the affected-behavior lane; they cannot be omitted because
they were absent from the proposal"), all 11 are recorded here, not just the 4
`Actors/*` files the spec anticipated. None of these files are part of the
selected `v3.102.0` coordinate; they remain an unselected drift observation
only. The three proposal-named paths (`CanonicalIdempotencyIntentEncoder.cs`,
`IdempotencyIntentAdapterRegistry.cs`, `ServiceCollectionExtensions.cs`) are
confirmed **unchanged** between the tag and current HEAD
(`git diff --name-status` empty for those three paths).

### 3. 14-package manifest and remote-restore inventory

Manifest at both tags: SHA-256 `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`
(byte-identical to the historical `3.88.0`/`3.90.0` manifests — same 14 IDs,
unchanged package list; this is source-inventory evidence only, not consumption
proof).

Remote-consumption proof (selected `v3.102.0` tuple only, per contract scope):
a disposable console project referencing the 13 library package IDs, restored
via `dotnet restore <project> --configfile <nuget.org-only config>
--force --no-cache --disable-parallel --verbosity minimal
-p:RestorePackagesPath=<brand-new empty dir>`, resolved all 13 at exactly
`3.102.0` from `nuget.org` only (exit `0`). The 14th ID,
`Hexalith.EventStore.Admin.Cli`, is published as a `DotnetTool`-typed package
(a plain `PackageReference` to it correctly fails `NU1212`, retained as the
honest first result) and was separately proven via
`dotnet tool install Hexalith.EventStore.Admin.Cli --tool-path <fresh dir>
--version 3.102.0 --configfile <same nuget.org-only config>` (exit `0`).
Combined, all 14 release-manifest IDs are proven to resolve remotely at exactly
`3.102.0`, no local feeds, no fallback folders, no floating versions, brand-new
caches each time.

### 4. Seven-API blob comparison

| API/type | Rollback `f13f9925…` blob | `v3.88.0` blob | Selected `4ae9cee1…` blob | Result |
| --- | --- | --- | --- | --- |
| `IAsyncDomainProjectionHandler` | `99ca7b0b…8933e` | `99ca7b0b…8933e` | `99ca7b0b…8933e` | Byte-identical |
| `IReadModelStore` | `fb077899…c75` | `fb077899…c75` | `fb077899…c75` | Byte-identical |
| `IReadModelBatchStore` | `bf8952b3…3011` | `bf8952b3…3011` | `bf8952b3…3011` | Byte-identical |
| `ReadModelWritePolicy` | `ff0d5d14…d2f3` | `ff0d5d14…d2f3` | `ff0d5d14…d2f3` | Byte-identical |
| `IDomainQueryHandler` | `693ff04e…1f08` | `693ff04e…1f08` | `693ff04e…1f08` | Byte-identical |
| `IQueryCursorCodec` | `18e67dc0…b6f7` | `18e67dc0…b6f7` | `18e67dc0…b6f7` | Byte-identical |
| `QueryCursorScope` | `15d65af7…0292f` | `53065de4…bebee` | `53065de4…bebee` | Additive-compatible: `v3.88.0` and selected both add `AddProjectionWatermark(long?)` over rollback; no removed/changed member |

Selected/package APIs are identical (same commit). Result: **PASS** — matches
the previously accepted additive-only disposition; no new removed or changed
surface.

### 5. Timestamped EventStore source/package behavior ledger

**Selected coordinate `4ae9cee1e9abe050402fd1405a9abd54892ba13f` (worktree
`wt-eventstore-3102`, isolated, detached, clean before/after):**

| Lane | Rows | Result | Root cause |
| --- | --- | --- | --- |
| Worktree clean-state (pre/post `status`/`HEAD`/`describe`) | 2 | PASS | — |
| Remote-package restore (13 `PackageReference` + `Hexalith.EventStore.Admin.Cli` via `dotnet tool install`) | 3 | PASS | — |
| Source-mode (`UseHexalithProjectReferences=true`, Debug: restore, build, 4× test) | 6 | **FAIL** | `NU1010` on restore, `MSB4019` on build/test |
| Package-source (`UseHexalithProjectReferences=false`, Release: restore, build, 4× test) | 6 | **FAIL** | same |

**Root cause (blocking, applies to both build lanes)**: `Directory.Packages.props`
imports central package version pins from
`references/Hexalith.Builds/Props/Directory.Packages.props`. `Hexalith.Builds`
is declared in `Hexalith.EventStore`'s **own** `.gitmodules` as a submodule
pinned, at this tag, to commit `e0e069468b29ce3fe85082b7bf0eb0d1952ce77c` — a
different commit than either the `Hexalith.Projects` umbrella's root-declared
`Hexalith.Builds` pin (`e81e62770bcc72bc3d6722aefe6844775b82b6cf`) or
`Hexalith.Builds`' own current `main` HEAD. Initializing it inside the isolated
qualification worktree is a nested-submodule initialization relative to the
`Hexalith.Projects` umbrella this whole task runs under, which this task's
frozen boundary ("Never ... init nested submodules") and the repository
CLAUDE.md's umbrella-workspace submodule policy both prohibit without explicit
authorization. Without it, `dotnet restore` fails `NU1010` (every
`PackageVersion` unresolved) and `dotnet build`/`dotnet test --no-restore` then
fail `MSB4019` (missing import) — deterministic and immediate (each command
completes in seconds), not a stall, sandbox artifact, or cache issue; confirmed
identical across both `UseHexalithProjectReferences` modes since both import
the same file.

**Rollback coordinate `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` (worktree
`wt-eventstore-3701`):** an initial execution pass hit the identical `NU1010`
blocker, then ran `git submodule update --init` inside the worktree (7
submodules, including `Hexalith.Builds` pinned at this tag to
`cfafcbf1e904138b435b63ba4fd79f86b8dda069` — again a different commit than the
umbrella's root pin), after which all 12 source-mode/package-source rows
genuinely exited `0` (708+673+143+2651 tests × 2 modes, 0 failures). **On
review this submodule initialization is judged the same nested-submodule
boundary violation as above** (these submodules are declared by
`Hexalith.EventStore`'s own `.gitmodules`, nested relative to the
`Hexalith.Projects` umbrella, and pinned to commits that diverge from the
umbrella's own root-declared submodules) — it was reverted
(`git submodule deinit --all -f`; worktree confirmed `git status --porcelain=v1`
empty again immediately after). **The resulting 12 PASS rows are therefore NOT
retained as qualifying evidence.** They are recorded here only for
transparency (the code itself was proven to build and pass cleanly once the
dependency was present), with the same structural blocker as the selected
coordinate applying once the submodule is correctly absent. Separately, the
exact `global.json`-pinned SDK `10.0.302` (the host only had `10.0.400`) was
installed into an isolated scratch directory rather than roll-forward-weakened
— that correction is retained and is not itself a boundary concern.

**Blocking issue for EventStore Owner / Solution Architect**: rule on whether
initializing `Hexalith.EventStore`'s own declared, non-recursive
`Hexalith.Builds` submodule (pinned exactly to the commit each EventStore
coordinate names) is in-scope, authorized tooling for isolated-worktree
source/package-mode qualification. If authorized, both the selected and
rollback source-mode/package-source lanes must be re-run with that submodule
present and the results re-validated. If not authorized, the source-mode and
package-source lanes are structurally un-runnable from a fully isolated,
single-repository `Hexalith.EventStore` worktree under the current boundary,
and an alternative qualification harness (for example, running the lane from
inside a worktree nested under the `Hexalith.Projects` umbrella instead of a
free-standing scratch worktree) needs to be authorized instead.

### 6. Timestamped Builds alignment/runner/package ledger

**Candidate `3d16d3e090ae822bc9cdc64c4156d31c9acf1146`** (branch
`fix/p1r-3102-candidate`, worktree `wt-builds-3102`, based on `e81e62770bcc72bc3d6722aefe6844775b82b6cf`;
final `git status --porcelain=v1` empty, clean):

One commit: `fix(runner): align EventStore pin to 3.102.0` — commitlint `PASS`
before and after (`node_modules/.bin/commitlint`, exit `0` both times). Aligns
`SupportedPlatformPins.cs`, the module-manifest schema, positive fixtures, 15
negative fixtures (excluding the intentional `3.88.0`/`3.70.1` stale-pin
negatives, left untouched), evidence fixtures, and 7 coupled `artifact_sha256`
readiness-evidence expectations (recomputed from real fixture bytes via
`sha256sum`, never invented) from stale `3.90.0` to `3.102.0`. The catalog
itself needed no change — `Props/Directory.Packages.props` already read
`3.102.0` at the base revision.

| Row | Command | Exit | Result |
| --- | --- | --- | --- |
| restore | `dotnet restore Hexalith.Builds.slnx --disable-parallel --verbosity minimal` | 0 | PASS |
| build | `dotnet build Hexalith.Builds.slnx --configuration Release --no-restore -p:GeneratePackageOnBuild=false -m:1` | 0 | PASS (0 warnings, 0 errors) |
| Module.Tests (`dotnet test ... -m:1`, contract-exact) | — | 5 | **INCONCLUSIVE** — "Zero tests ran"; Microsoft.Testing.Platform `--server dotnettestcli` named-pipe handshake failure isolated to `-m:1`, reproduced 4×, sandbox on/off, before/after the fix — offset by direct-assembly fallback (`114/114`) and the G-4 gate's own internal re-run (`114/114`) |
| Evidence.Tests (`dotnet test ... -m:1`, contract-exact) | — | 5 | **INCONCLUSIVE** — identical blocker, offset by fallback (`31/31`) and G-4 internal re-run (`31/31`) |
| `test-authoritative-package-catalog.ps1` | — | 0 | PASS — 50 identities, 3 shared versions |
| `validate-package-version-audit.ps1` | — | 0 | PASS — 286 packages, 141 families, 1 source, zero mismatches; no regeneration needed |
| `test-g4-tool-package-contracts.ps1 -Version 0.0.0-p1r3102.1 -RequireControls -RetainPackageDirectory` | — | 0 | PASS — 38s, no stall; `sourceTree.revision=3d16d3e0…`, `sourceTree.clean=true`, `releaseEligible=true`, `ineligibilityReasons=[]` |

Static-alignment verification (all quoted with exact evidence in
`builds-3102-candidate-result.md`): 13 catalog rows resolve `3.102.0`;
`SupportedPlatformPins`/schema/positive fixtures/evidence/coupled hashes all
`3.102.0`; `superseded-platform-pin.json` (`3.88.0`) and
`tampered-platform-pin.json` (`3.70.1`) both rejected with exactly `HXM016`;
`invalid-profile.json` still `HXM009`; the deliberate evidence hash-mismatch
control still invalid.

Packaged artifacts (retained under the session scratch directory, see §8):
`Hexalith.Builds.Module.Cli.0.0.0-p1r3102.1.nupkg`/`.snupkg`,
`Hexalith.Builds.Evidence.Cli.0.0.0-p1r3102.1.nupkg`/`.snupkg`.

### 7. Reciprocal rollback ledger

**Rollback `6ea4c27ade695964ee3c95b1d28e0058f4e1430e`** (branch
`fix/p1r-3701-rollback`, worktree `wt-builds-3701`, based on
`e81e62770bcc72bc3d6722aefe6844775b82b6cf`; final `git status --porcelain=v1`
empty, clean):

One commit: `fix(runner): bind EventStore pin to 3.70.1 rollback` — commitlint
`PASS` before and after. Rebinds `Props/Directory.Packages.props`
(`HexalithEventStoreVersion=3.70.1`), `SupportedPlatformPins.cs`, schema,
positive fixtures, 15 negative fixtures, evidence fixtures, and coupled hashes
to `3.70.1`. `superseded-platform-pin.json` stays `3.88.0` (still stale
regardless of the accepted pin). `tampered-platform-pin.json` is **repointed
from `3.70.1` to `3.102.0`** — the required reciprocal-rejection fixture, since
`3.70.1` is now the accepted positive pin.

| Row | Command | Exit | Result |
| --- | --- | --- | --- |
| restore | `dotnet restore Hexalith.Builds.slnx --disable-parallel --verbosity minimal` | 0 | PASS |
| build | `dotnet build Hexalith.Builds.slnx --configuration Release --no-restore -p:GeneratePackageOnBuild=false -m:1` | 0 | PASS (0 warnings, 0 errors) |
| Module.Tests (`-m:1`, contract-exact) | — | 5 | **FAIL** — same MTP/`-m:1` handshake blocker as the candidate lane; sanctioned fallback (`dotnet test` without `-m:1`, still fully serialized) PASS `114/114` |
| Evidence.Tests (`-m:1`, contract-exact) | — | 5 | **FAIL** — same blocker; fallback PASS `31/31` |
| `test-authoritative-package-catalog.ps1` | — | 0 | PASS — 50 identities, 3 shared versions |
| `validate-package-version-audit.ps1` | — | 1 | **FAIL** — anti-downgrade-floor error: "Internal package 'Hexalith.EventStore.*' cannot downgrade accepted version floor '3.102.0' to catalog version '3.70.1'" for all 12 catalog `Hexalith.EventStore.*` rows. Root cause: the sanctioned generator (`Tools/audit-central-package-versions.ps1`) refuses to run against an uncommitted catalog, and folding a regenerated audit into the same commit via `git commit --amend` breaks its own `generatedFromRevision` ancestor self-check (the pre-amend hash is unreachable from the new HEAD) — a true single-commit self-reference is a SHA-1 preimage impossibility. A correct audit refresh needs **two commits**: the catalog/runner change, then a child commit adding the regenerated audit — matching the base repository's own historical two-commit pattern (`308e392` then `e81e627`). Given the explicit "exactly one commit" boundary for this rollback, this is recorded as an honest FAIL rather than resolved by a second, unauthorized commit or a hand-edited audit. |
| `test-g4-tool-package-contracts.ps1 -Version 0.0.0-p1r3701.1 -RequireControls -RetainPackageDirectory` | — | 0 | PASS — 38s, no stall; `releaseEligible=true` |

**Reciprocal rejection matrix (Builds side; EventStore side non-qualifying per §5):**

| Worktree | Positive pin | Rejected pin(s) required | Result |
| --- | --- | --- | --- |
| Selected candidate (Builds) | `3.102.0` | `3.88.0`, `3.70.1` | Both reject with exactly `HXM016` — **PASS** (Builds side only) |
| Rollback (Builds) | `3.70.1` | `3.102.0` | Rejects with exactly `HXM016` — **PASS** (Builds side only); `3.88.0` (unchanged `superseded-platform-pin.json`) also still rejects `HXM016` |

The Builds-side reciprocal `HXM016` proof is complete and genuine on both
worktrees. The overall reciprocal-rollback matrix gate is **not** fully met
because (a) the EventStore-side source/package lane for both tuples remains
blocked per §5, and (b) the rollback Builds revision's audit validator FAILs
per the row above.

### 8. Retained log, evidence, package, and manifest hashes

| Item | Value |
| --- | --- |
| EventStore selected/rollback manifest SHA-256 | `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r3102.1.nupkg` | `69c9c71a1dd3a89be397190d389813d8c0e0b7248160953600a481f6741ace45` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r3102.1.nupkg` | `1709c7194731ae8ad59643a881af5a0f7ee93db5c1eddb71ba991de5c350b904` |
| `Hexalith.Builds.Module.Cli.0.0.0-p1r3701.1.nupkg` | `2543a3da7c7246575dab659057ea239e47d13695a365840d84cd967c473c1c65` |
| `Hexalith.Builds.Evidence.Cli.0.0.0-p1r3701.1.nupkg` | `e544e22ae833fe76904a98352c413fe04cf4107cdef3ae4f4d75ec51948dbc1e` |
| Candidate `builds_qualifying_revision` | `3d16d3e090ae822bc9cdc64c4156d31c9acf1146` |
| Rollback `rollback_builds_revision` | `6ea4c27ade695964ee3c95b1d28e0058f4e1430e` |

**Evidence retention gap (recorded honestly, not concealed)**: all command
logs, per-row SHA-256 values, and the full result narratives for every lane
above are retained only under this session's ephemeral scratch directory
(`eventstore-3102-selected-result.md`, `eventstore-3701-rollback-result.md`,
`builds-3102-candidate-result.md`, `builds-3701-rollback-result.md`, and their
`logs/` subdirectories), **not** relocated into a durable, git-tracked
`qualification-evidence/` bundle the way prior attempts (`.8`, `.9`, `.13`,
`.18`, etc. above) were. That relocation did not happen in this attempt and is
an open task for whoever continues this work, before any of the above can
support owner acceptance.

### 9. Downstream non-closure assertions

- `6.1-P1R` remains `status: open` in `sprint-status.yaml` (untouched; only an
  append-only evidence-pointer comment was added, see that file).
- The Architecture Spine remains bound to EventStore `3.70.1` /
  `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` / Builds `HexalithEventStoreVersion=3.70.1`
  (re-read and confirmed unchanged; file not edited).
- `6.1-P0`, `6.1-P2`, `6.1-P3`, `6.1-P4`, Story 6.1, independent readiness, and
  the transitive Epic 7/8 gates all retain their own existing blockers
  (untouched).
- No push, publish, or release occurred anywhere. No gitlinks were moved in
  `Hexalith.Projects`, `Hexalith.EventStore`, or `Hexalith.Builds`. The two new
  Builds commits (`3d16d3e0…`, `6ea4c27a…`) exist only on local, unpushed,
  unmerged branches (`fix/p1r-3102-candidate`, `fix/p1r-3701-rollback`) in
  scratch worktrees; the tracked `Hexalith.Builds` checkout under
  `references/Hexalith.Builds` (and its gitlink in `Hexalith.Projects`) was
  never modified.
- The one nested-submodule initialization that occurred (EventStore rollback
  worktree, §5) was reverted before this record was written; no worktree or
  checkout in this attempt currently has any nested submodule initialized.
- No coordinate other than `v3.102.0`/`3.102.0` (candidate) or `v3.70.1`/`3.70.1`
  (rollback) was selected.

### 10. Four-owner acceptance table

| Role | Named approver | Decision | UTC date | Coordinates/evidence | Required next action |
| --- | --- | --- | --- | --- | --- |
| EventStore Owner | pending | pending | pending | Selected `4ae9cee1e9abe050402fd1405a9abd54892ba13f` / rollback `f13f9925fdca53efa2ab8c90d396ab106f91bb9c`; source/package-mode lanes blocked, see §5 | Rule on whether initializing `Hexalith.EventStore`'s own declared, non-recursive `Hexalith.Builds` submodule is authorized for isolated-worktree qualification; if yes, both tuples' build/test lanes must be re-run and re-validated, including against the actual 21-commit/11-file post-tag drift documented in §2 (not the 18-commit/4-file figure the originating spec assumed); if no, an alternative qualification harness must be authorized |
| Builds Owner | pending | pending | pending | Candidate `3d16d3e090ae822bc9cdc64c4156d31c9acf1146`; rollback `6ea4c27ade695964ee3c95b1d28e0058f4e1430e`; both unpushed/unmerged, static alignment and Builds-side reciprocal `HXM016` proof genuine per §6-§7 | Authorize a second, dependent audit-refresh commit on the rollback branch (or a tooling change to the ancestor self-consistency check) so `validate-package-version-audit.ps1` can pass at the rollback revision; relocate retained evidence into a durable `qualification-evidence/` bundle before acceptance |
| Solution Architect | pending | pending | pending | Architecture remains `3.70.1`; no multi-coordinate binding proposed | Make no rebinding decision until §5's EventStore-side blocker and §7's audit-validator FAIL both resolve; note the 21-commit/11-file post-tag drift discrepancy from the originating spec for G-1 wording purposes |
| Test Architect | pending | pending | pending | 12 `FAIL` EventStore rows (selected) + 12 non-qualifying reverted-boundary rows (rollback) + 2 `INCONCLUSIVE`/2 `FAIL` Builds `dotnet test -m:1` rows + 1 audit-validator `FAIL` (rollback) | Require a clean re-run of the EventStore source/package lanes once §5 is resolved, the audit-validator row once §6/§7's two-commit sequencing is authorized, and independently confirm the `dotnet test -m:1` Microsoft.Testing.Platform IPC blocker is an environment/tooling defect, not a candidate defect, before treating the fallback evidence as equivalent |

## Correction and evidence relocation — 2026-09-07

This append-only section corrects the "3.102.0 candidate / 3.70.1 rollback
revalidation attempt — 2026-09-05" section above **without altering one byte of
it**, and closes the evidence retention gap that section's §8 recorded honestly.

It changes no status field, selects nothing, qualifies nothing, runs no
qualification lane, moves no gitlink, initializes no submodule, and pushes and
publishes nothing. `6.1-P1R` remains `open`. All four owner decisions remain
pending. Every earlier section — `3.88.0`, `3.90.0`, the three stopped `3.97.0`
attempts, and the 2026-09-05 attempt — stands exactly as written.

Authority: `_bmad-output/implementation-artifacts/spec-6-1-p1r-requalify-with-sibling-materialization.md`
in `Hexalith.Projects` at baseline `dffdd677e0058240d5951f507cbd2b159b9643a5`.

### A. Evidence relocation — the 2026-09-05 §8 retention gap is closed

The 2026-09-05 §8 recorded that all command logs, per-row hashes, and result
narratives lived only in an ephemeral session scratch directory and had not been
relocated into a durable bundle. That scratch directory was still present and
readable on 2026-09-07, and its surviving contents have now been relocated
byte-for-byte to:

```
Hexalith.Projects@_bmad-output/implementation-artifacts/qualification-evidence/6-1-p1r-3102-20260905/
```

| Item | Value |
| --- | --- |
| Bundle manifest | `artifact-manifest.sha256`, 214 entries |
| Manifest SHA-256 | `da901517665b66606e3f03d270d72a992ca45afee85968efd39b3a38bb2fa107` |
| Re-hash verification | `sha256sum -c` — **214/214 OK, 0 failed**, recorded in `manifest-verification.txt` |
| Copy fidelity | 213/213 relocated files byte-identical to their scratch source; `diff -rq` clean for all four `logs/` trees and both `retained-packages/` trees |
| Artifacts absent | **none** — nothing was listed as absent, and nothing was reconstructed, re-derived, re-run, or reformatted |

The bundle carries the four lane result narratives, both commit messages, all
117 per-row log and `.meta` files across `logs/{eventstore-3102,eventstore-3701,builds-3102,builds-3701}/`,
both retained release manifests, `p1r-diff.txt`, the disposable remote-consumer
project and its nuget.org-only `NuGet.Config` bytes, and the retained G-4
packaged-command artifacts for both lanes under `retained-packages/`.

That last set independently corroborates this record's own §8 table: the four
retained `.nupkg` files re-hash on 2026-09-07 to exactly the four published
values (`69c9c71a…`, `1709c719…`, `2543a3da…`, `e544e22a…`). The relocated
release manifests hash to `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`,
matching §3.

Deliberately excluded and labelled as such in the bundle `README.md` — not
missing: the 630 MiB disposable restore cache (replaced by a derived
`packages-cache-inventory.txt` recording all 222 restored `<id>/<version>` pairs,
including the 13 `hexalith.eventstore.*` libraries at exactly `3.102.0` and the
`dotnet tool install` store path for the 14th ID), and roughly 8.5 GiB of
disposable Git worktrees, per-lane scoped environments, and the isolated
`global.json`-pinned SDK `10.0.302` install. Those are execution environments,
not artifacts.

The three prior stopped `3.97.0` bundles were re-verified unchanged on the same
date (`sha256sum -c` passes for `6-1-p1r-397-20260824`, `-attempt2`, and
`-attempt3`); none was modified.

### B. 2026-09-07 sibling-import verification — method and result

The 2026-09-05 §5 root-cause paragraph is factually wrong about the mechanism,
and this subsection states the method that shows it, so the finding can be
re-checked rather than taken on assertion.

`Hexalith.EventStore/Directory.Packages.props` defines **four** catalog import
candidates and tries them in order:

| # | Property | Path relative to the EventStore root | Guarded by `Exists()` |
| --- | --- | --- | --- |
| 1 | `Hexalith1BuildPackageProps` | `references/Hexalith.Builds/Props/Directory.Packages.props` (the nested submodule path) | yes |
| 2 | `Hexalith2BuildPackageProps` | `../references/Hexalith.Builds/Props/Directory.Packages.props` (**sibling fallback**) | yes |
| 3 | `Hexalith3BuildPackageProps` | `../../references/Hexalith.Builds/Props/Directory.Packages.props` | yes |
| 4 | `Hexalith4BuildPackageProps` | `../../references/Hexalith.Builds/Props/Directory.Packages.props` (same path as 3) | **no** |

Candidate 4 is unconditional. When 1–3 all miss, candidate 4 is the import that
raises `MSB4019`, and `Directory.Packages.props` never sets any `PackageVersion`,
which is what produces the `NU1010` wave on restore. This file is byte-identical
at `v3.102.0` and `v3.103.0` (SHA-256 `4fe25096ceefd11e8ffffe1ab6a1854e18ac36c853a6805fa72285433b03b638`)
and structurally identical at `v3.70.1` (`f3855b55b8e8184fcb05968b4f94bb40cc377131d7aaaecc3ba014cabca01130`).

**Method (read-only; no qualification lane, no restore, no build, no test).**
Two synthetic layouts were built in a disposable scratch directory:

- `flat/EventStore/` — the 2026-09-05 placement: an EventStore root with no
  sibling `references/` directory beside it.
- `lane/EventStore/` plus `lane/references/Hexalith.Builds/` — the attempt-3
  placement: an EventStore root with the Builds catalog as its sibling.

`Directory.Packages.props` was extracted read-only with
`git show 059f6a8917bfab26b85775be464840a1610dfdeb:Directory.Packages.props`
into both layouts. The Builds `Props/` directory was extracted read-only with
`git archive 071ef99733ba398362ae838e4696b4b1641ed07a Props`; the resulting
`Props/Directory.Packages.props` has SHA-256
`b50f9984247cf1cdc1daa9ef0f7345ea753bebcbd9228f3eecf975ec9c1df216` and imports
nothing further. A minimal `Microsoft.NET.Sdk` probe project was placed in each
EventStore root and **evaluated only**:

```text
dotnet msbuild Probe.csproj -getProperty:HexalithEventStoreVersion
```

**Results.**

| Case | Layout | Exit | Observed |
| --- | --- | ---: | --- |
| A | flat (2026-09-05) | `1` | `error MSB4019: The imported project ".../references/Hexalith.Builds/Props/Directory.Packages.props" was not found ... "$(Hexalith4BuildPackageProps)"` |
| B | sibling (attempt-3) | `0` | `HexalithEventStoreVersion` = `3.102.0`; `HexalithVersionsLoaded` = `true`; `ManagePackageVersionsCentrally` = `true` |

Case B resolved with **no nested submodule initialized anywhere**. Confirmed on
the same date in the umbrella checkout: `git -C references/Hexalith.EventStore
submodule status` reports all seven entries with the uninitialized `-` prefix,
and all seven `references/*` directories are empty (0 entries).

**Therefore**: the 2026-09-05 EventStore lanes did not fail because a
nested-submodule boundary made catalog import impossible. They failed because
their worktrees were placed **flat** (`<scratch>/wt-eventstore-3102`,
`<scratch>/wt-eventstore-3701`, and their `-source`/`-package` siblings), where
`<scratch>/references/Hexalith.Builds` never existed, so all four import
candidates missed. The `NU1010`/`MSB4019` rows now retained under
`qualification-evidence/6-1-p1r-3102-20260905/logs/eventstore-3102/` and
`logs/eventstore-3701/` are evidence of a **worktree-placement defect in the
harness**, not of a structural boundary blocker in the repository.

### C. Attempt-3 corroboration (2026-08-24)

This is not a new finding; it is a rediscovery of one this record already holds.
The "Stopped `3.97.0` supersession attempt 3 — 2026-08-24" section above, and
`Hexalith.Builds` branch `docs/p1r-397-attempt3-record` (`6b42f74`, now an
ancestor of local `main`), already document the same route end to end under the
name **repository-supported sibling materialization**: each detached EventStore
worktree at `<lane>/EventStore` with a detached Builds worktree at
`<lane>/references/Hexalith.Builds`. On that layout the exact mandated Debug
source-mode restore (`es-source-001-restore`) and the exact mandated serialized
Debug build (`es-source-002-build`, zero warnings, zero errors) both **passed**,
with no nested submodule initialized, no EventStore source edited, and no
gitlink moved.

Import and compilation through a sibling catalog are a solved problem, proven by
execution on 2026-08-24 and re-verified by evaluation on 2026-09-07.

### D. Restatement of the 2026-09-05 §5 open question

§5's closing paragraph asks the EventStore Owner and Solution Architect to rule
on whether initializing EventStore's own declared `Hexalith.Builds` submodule is
authorized for isolated-worktree qualification, and states that without such a
ruling the source-mode and package-source lanes are "structurally un-runnable".
**That question is moot as posed.** Sibling materialization already satisfies
MSBuild import and compilation without any nested submodule, so no owner ruling
is required to make the lanes runnable.

The real blocker sits one layer below, and attempt-3 reached it: EventStore's
repository-governance tests do not go through MSBuild. They read dependency
**bytes**, and Git **objects**, at `<eventstore-root>/references/<Name>`:

- `tests/Hexalith.EventStore.Contracts.Tests/Packaging/ReleasePackageManifestTests.cs`
  — `Live_sidecar_workflow_targets_live_project_outside_release_gate` and
  `Live_sidecar_workflow_guardrail_rejects_forbidden_mutations` both call
  `File.ReadAllText(Path.Combine(root, "references", "Hexalith.Builds", "Github", "dapr-init", "action.yml"))`.
- `tests/Hexalith.EventStore.Contracts.Tests/Packaging/ContainerPublishingGovernanceTests.cs`
  — runs `git ls-tree HEAD references/Hexalith.Builds` from the EventStore root
  to read the gitlink SHA, then runs `git cat-file -e <ApprovedBuildsReleaseSha>^{commit}`
  and `git merge-base --is-ancestor <ApprovedBuildsReleaseSha> <gitlinkSha>`
  **inside** `references/Hexalith.Builds`. That path must therefore be a real
  Git repository whose object store contains the approved release SHA — a plain
  file copy or a bare directory does not satisfy it.
- Attempt-3's 29 failures additionally named absent `Hexalith.Tenants` project
  content, the absent shared LLM instruction file, an unavailable Builds tool
  path at a pinned commit, and an unresolvable Git tree.

So the question owners should be asked is **not** "may nested submodules be
initialized so the catalog import works" — import is solved — but:

> How do repository-governance tests obtain real dependency bytes and real Git
> objects at `<eventstore-root>/references/<Name>`, at the exact SHAs the
> selected EventStore coordinate itself names, without initializing nested
> submodules under the umbrella and without moving any gitlink?

Feasibility observation for that question (2026-09-07, read-only, non-qualifying).
At `v3.103.0` EventStore's own gitlinks are:

| Dependency | Gitlink SHA at `v3.103.0` | Object available locally? |
| --- | --- | --- |
| `Hexalith.AI.Tools` | `5f93d2ec8239494852c97032c819cb1689939e36` | yes — umbrella module store |
| `Hexalith.Builds` | `071ef99733ba398362ae838e4696b4b1641ed07a` | yes — umbrella module store |
| `Hexalith.Commons` | `6da79aed2daa4e199689331ee3196f7872c0988a` | yes — umbrella module store |
| `Hexalith.FrontComposer` | `97a4e6b2d28fab470cad4870694fe3e2b012306a` | yes — umbrella module store |
| `Hexalith.Memories` | `7e9c2c387ee84b4d5c96c27f4cf613e13ff2c9e2` | yes — umbrella module store |
| `Hexalith.PolymorphicSerializations` | `8aeed1d27c9a050bc4bec6d89051aa00de306a69` | **no local repository** — needs a fetch from its declared URL |
| `Hexalith.Tenants` | `e7f366623733abdb64f173e9843edc7fc7d22193` | yes — umbrella module store |

Six of the seven are already resolvable from objects the umbrella holds. Note
also that `v3.103.0`'s `Hexalith.Builds` gitlink `071ef99…` is exactly the
`Hexalith.Builds` revision the umbrella working tree currently has checked out.

### E. Decisions taken 2026-09-07

Recorded here as decisions of record. They are not reopened by this section and
this section does not execute any of them.

1. **Selected coordinate moves to EventStore `v3.103.0`
   (`059f6a8917bfab26b85775be464840a1610dfdeb`)**, superseding the `3.102.0`
   candidate whose Builds alignment is `3d16d3e090ae822bc9cdc64c4156d31c9acf1146`.
   The 2026-09-05 `3.102.0` coordinate tables, ledgers, and evidence remain
   valid preserved history for the superseded candidate.
2. **The dependency-bytes route for repository-governance tests is read-only
   materialization at EventStore's own exact gitlink SHAs, inside disposable
   qualification worktrees**: no `git submodule update --init`, no gitlink
   movement, no EventStore source edit, and nothing written into any live
   checkout. This is recorded as an **explicit judgement call** against the
   umbrella's nested-submodule rule: the rule forbids initializing nested
   submodules in the workspace, and this route does not do that — it materializes
   dependency content and Git objects into throwaway lane directories at the
   SHAs the selected coordinate itself names. Whoever executes must keep that
   distinction demonstrable in the post-lane state capture, exactly as attempt-3
   did.
3. **The rollback audit gets a second, dependent child commit**, not a change to
   `Tools/validate-package-version-audit.ps1`. §7 above recorded the rollback
   audit `FAIL` and correctly diagnosed it as a single-commit self-reference
   impossibility in `generatedFromRevision`. The resolution is the base
   repository's own historical two-commit pattern (`308e392` then `e81e627`):
   commit the catalog/runner rebind, then a child commit adding the regenerated
   audit. The validator itself is not to be relaxed, hand-edited, or bypassed.

### F. Observed state changes since 2026-09-05 (observations only)

Recorded so that the 2026-09-05 statements above are read with their correct
as-of date. None of these is corrected in place, and none was caused by this
correction.

- §9 recorded that both new Builds commits were "unpushed, unmerged". That was
  true on 2026-09-05. Since then the candidate branch was merged by separate
  user work: `3d16d3e090ae822bc9cdc64c4156d31c9acf1146` is now an ancestor of
  local `main` via `aee36f40037b41d25c8bd8da6a0dd68671faa823`
  ("build: merge fix/p1r-3102-candidate into main via /pushall").
  `fix/p1r-3701-rollback` (`6ea4c27ade695964ee3c95b1d28e0058f4e1430e`) is still
  unmerged. This does not qualify or accept the candidate; alignment is not
  qualification.
- The `Hexalith.Builds` checkout under `Hexalith.Projects/references/Hexalith.Builds`
  was at `main` = `071ef99733ba398362ae838e4696b4b1641ed07a` when this section
  was written, one commit behind `origin/main` = `7b0b1837ce368e314b5e11b011b73603637a17e1`
  ("build(deps): update EventStore to 3.103.0 and Memories to 2.26.2"). This
  correction is committed on top of the local checkout's `071ef99…` and is
  therefore **not** a descendant of `origin/main`; it needs a merge or rebase
  before any push. Nothing was pushed.
- `7b0b183…` changes `Props/Directory.Packages.props` only. On `origin/main`,
  `SupportedPlatformPins.EventStoreVersion` still reads `3.102.0`. So under
  decision E-1 the catalog is already at `3.103.0` upstream while the runner
  pin, manifest schema, positive fixtures, negative fixtures, evidence fixtures,
  and coupled `artifact_sha256` values are **not** yet rebound to `3.103.0`.
  That rebind is outstanding work for the next authorized attempt; no such
  change was made here.
- EventStore `v3.103.0` (`059f6a89…`) is tagged as of 2026-09-07. Its
  `tools/release-packages.json` hashes to the same 14-ID manifest
  `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`. Source
  inventory observation only — not a remote listing, not consumption proof, and
  not a qualification of `3.103.0`.

### G. What this correction does not do

- No qualification lane was run — no restore, no build, no test, no runner, no
  packaged command. The only MSBuild invocation was an evaluation-only probe of
  a synthetic project in a disposable scratch directory (§B).
- No coordinate was qualified and no gate was closed. `6.1-P1R` stays `open`;
  `6.1-P0`, `6.1-P2`, `6.1-P3`, `6.1-P4`, Story 6.1, independent readiness, and
  the transitive Epic 7/8 gates keep their own blockers.
- The Architecture Spine is untouched and still binds EventStore `3.70.1` /
  `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` / Builds
  `HexalithEventStoreVersion=3.70.1`.
- No gitlink was moved, no submodule was initialized or deinitialized, nothing
  was pushed, published, or released, and no owner acceptance is inferred. §10's
  four-owner table stands with all four rows pending; §10's next-action text for
  the EventStore Owner should now be read through §D of this section.
- `sprint-status.yaml` received an append-only, non-status evidence-pointer
  comment only.

## Correction addendum — 2026-09-07 (second append, same date)

Review of the append above found four defects in it. This addendum corrects them
by appending, not by editing: every byte of every earlier section, including the
first 2026-09-07 append, stands unchanged. It changes no status, qualifies no
coordinate, runs no qualification lane, moves no gitlink, initializes no
submodule, and pushes nothing. `6.1-P1R` remains `open` with all four owner
decisions pending.

### H. §B attributed the import failures to a lane that never had one

§B closes by naming both `logs/eventstore-3102/` and `logs/eventstore-3701/` as
holding the `NU1010`/`MSB4019` rows. **Only `logs/eventstore-3102/` does.**
Counted in the relocated bundle:

| Lane log directory | Files containing `NU1010` or `MSB4019` | `.meta` rows |
| --- | ---: | --- |
| `logs/eventstore-3102/` | 12 — `src-01…06-*` and `pkg-01…06-*` | the failing rows §5 tabulates |
| `logs/eventstore-3701/` | **0** | 12 rows, `s1…s6` and `p1…p6`, **every one `EXIT=0`** |

So §B's conclusion — that flat worktree placement, not a boundary conflict,
produced the import failures — is correct for the **selected** `3.102.0` lane
and is evidenced by `logs/eventstore-3102/` alone. The rollback `3.70.1` lane
failed no import at all, for the reason in §I.

### I. Why the rollback lane passed, and what that does and does not prove

The relocated `logs/eventstore-3701/00b-submodule-init.log` is the missing piece
the first append passed over in silence. It records `git submodule update --init`
inside `wt-eventstore-3701` registering and cloning **all seven** of
`Hexalith.EventStore`'s declared submodules and checking each out at the exact
gitlink SHA `v3.70.1` names (`Hexalith.Builds` at
`cfafcbf1e904138b435b63ba4fd79f86b8dda069`, `Hexalith.Tenants` at
`211b4d4ae59d85cdbeb0cddf99bf3ad86b361f30`, and so on). With those bytes present
at `<eventstore-root>/references/<Name>`, that lane then passed **all twelve**
rows — six source-mode and six package-source — including
`s3-test-contracts` and `p3-test-contracts` at `708/708` passed, 0 failed.

**This is the reverted, non-qualifying route.** §5 above already records that the
initialization was judged a nested-submodule boundary violation, was reverted
with `git submodule deinit --all -f`, and that its 12 PASS rows are **not**
retained as qualifying evidence. Nothing here changes that. What was missing was
the reconciliation, so owners weigh decision 2 against retained evidence rather
than against silence:

**What the 3701 rows do support.** Dependency bytes materialized at EventStore's
own exact gitlink SHAs, at `<eventstore-root>/references/<Name>`, are sufficient
for that coordinate's repository-governance reads: at `v3.70.1` both
`ReleasePackageManifestTests` and `ContractsPackageDependencyTests` reference
`references/Hexalith.Builds`, and both passed. Decision 2 differs from what
produced these rows only in **how** the bytes arrive — read-only materialization
into disposable lane directories versus `git submodule update --init` inside the
worktree — not in **what** ends up on disk. The end state decision 2 asks for is
therefore a state this project has already observed working, once.

**What they do not support.** They are not evidence for the selected coordinate's
governance surface, which is materially larger:

| | `v3.70.1` (`f13f9925…`) | `v3.103.0` (`059f6a89…`) |
| --- | ---: | ---: |
| `Hexalith.EventStore.Contracts.Tests` `.cs` files | 54 | 70 |
| `Packaging/*.cs` files | 3 | 19 |
| test files referencing `Hexalith.Builds` | 2 | 8 |
| `ContainerPublishingGovernanceTests.cs` present | **no** | **yes** |

`ContainerPublishingGovernanceTests` is the test that does not merely read files
but runs `git ls-tree HEAD references/Hexalith.Builds` from the EventStore root
and then `git cat-file -e` and `git merge-base --is-ancestor` **inside**
`references/Hexalith.Builds`. It did not exist at `v3.70.1`, so the 3701 rows say
nothing about it. Conversely, attempt-3 (2026-08-24, `v3.97.0`, sibling catalog
only, **no** materialization at `<eventstore-root>/references/`) failed 29
contracts tests whose earliest causes are exactly these absent dependency bytes.

Read together: sibling-only materialization is known to fail this surface, full
materialization at the coordinate's own gitlink SHAs is known to satisfy the
older, smaller surface, and no evidence yet exists either way for
`v3.103.0`'s `ContainerPublishingGovernanceTests`. That is the honest state
decision 2 must be judged against.

### J. Two harness layouts both work, through different import candidates

§B verified one placement. The deferred-work entry that carries this work
(DW-68, extending DW-35) describes a different one. Both resolve the catalog
import with no nested submodule, but **through different candidates** of the
four §B tabulates, and an executor who "fixes" one into the other can break a
working lane. Both were verified on 2026-09-07 by evaluation-only MSBuild probes:

| Layout | EventStore root | Builds at | Resolves through | Missing |
| --- | --- | --- | --- | --- |
| DW-68 / umbrella-shaped | `<lane>/references/Hexalith.EventStore` | `<lane>/references/Hexalith.Builds` | **candidate 3**, `../../references/Hexalith.Builds` | candidate 2 path does not exist |
| Attempt-3 / §B | `<lane>/EventStore` | `<lane>/references/Hexalith.Builds` | **candidate 2**, `../references/Hexalith.Builds` | — |

Both probes evaluated `HexalithEventStoreVersion` to `3.102.0` from the Builds
catalog at `071ef99733ba398362ae838e4696b4b1641ed07a`, exit `0`, with zero
nested submodules initialized. Neither layout is preferred by this record; what
matters is that the executor records which candidate their layout resolves
through, so a later reader can tell a working lane from a coincidence.

### K. Superseded values from the first append, and the bundle's tracking state

Acting on this review changed the bundle: it gained a `.gitattributes` pinning
`-text` (the umbrella `.gitattributes` sets only `* whitespace=cr-at-eol` and
pins no text or eol handling, so a clone with `core.autocrlf=true` would rewrite
line endings and break every text entry in the manifest), its `.gitignore`
negations were extended and its cited umbrella rules corrected, and its
`README.md` counts were corrected. The manifest was regenerated. **The following
values in §A are superseded by this addendum**; §A's text stands as written.

| Item | §A value (superseded) | Current value |
| --- | --- | --- |
| Manifest entries | 214 | **215** |
| Manifest SHA-256 | `da901517665b66606e3f03d270d72a992ca45afee85968efd39b3a38bb2fa107` | **`de3b59d4f4d84b2e568f56a9f1aa0f41119bfaa09beedb7412e1e8b7c89cf423`** |
| Re-hash verification | 214/214 OK | **215/215 OK, 0 failed** |
| Copy fidelity | "213/213 relocated files" | **211/211 relocated files byte-identical** |
| `manifest-verification.txt` SHA-256 | not recorded | **`497bcaf44db6a39b6f198bf1fc6939affa172080497698526167b3b92ca2a9f7`**, now recorded in the bundle `README.md` |

The bundle holds 217 files: 211 relocated byte-for-byte, 1 derived
(`packages-cache-inventory.txt`), and 5 authored (`README.md`, `.gitignore`,
`.gitattributes`, `artifact-manifest.sha256`, `manifest-verification.txt`). The
manifest covers 215 — all but itself and `manifest-verification.txt`, whose
SHA-256 the `README.md` records, so no bundle file is unattested. The manifest
deliberately does not appear inside `manifest-verification.txt`, and the
`README.md` deliberately does not quote the manifest hash: a manifest covering a
README that quotes that manifest's own hash is the same unsatisfiable
self-reference §7 records for `generatedFromRevision`. The manifest hash is
attested here and in the `sprint-status.yaml` evidence pointer instead.

**Tracking state.** §A calls the bundle tracked. It is not tracked yet:
`git ls-files` over it returns zero. The umbrella working-tree changes for this
correction — the bundle, the `qualification-evidence/README.md` index rows, the
`sprint-status.yaml` comment, and the deferred-work entry — are deliberately
left uncommitted for the user to review and commit. The bundle-local
`.gitignore` and `.gitattributes` mean all 217 files are trackable the moment
that commit happens (verified: `git add -n` lists 217 of 217, `git check-ignore`
matches none, `git diff --check` reports none). Until then the bundle is durable
against scratch pruning but not against `git clean -xdf`.

### L. What this addendum does not do

No qualification lane was run; the only MSBuild invocations were evaluation-only
probes of synthetic projects in a disposable scratch directory. No coordinate was
qualified, no gate closed, no gitlink moved, no submodule initialized, nothing
pushed or published, and no owner acceptance inferred. The Architecture Spine
remains bound to EventStore `3.70.1`. §10's four-owner table stands with all four
rows pending.

## Stopped `3.106.0` supersession attempt — 2026-09-21

This append-only section preserves every earlier candidate and correction. It
records a stopped, non-qualifying execution of the contract adopted by
`spec-6-1-p1r.md`; it does not accept a coordinate, change Architecture or
planning state, initialize a nested submodule, move a gitlink, push, publish,
or release anything.

### Selected coordinates and Builds alignment

| Field | Exact value | Disposition |
| --- | --- | --- |
| `eventstore_source_revision` | `76051c70cbf868c40edc00ca0344fa5bd8879b69` | Selected immutable tag commit |
| `eventstore_source_describe` | `v3.106.0` | Exact and clean |
| `eventstore_package_version` / tag | `3.106.0` / `v3.106.0` | Selected published coordinate |
| `eventstore_package_source_revision` | `76051c70cbf868c40edc00ca0344fa5bd8879b69` | Same commit as source |
| `source_package_equivalent` | `true` | Source/package diff is empty |
| `eventstore_release_manifest_sha256` | `6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae` | Same exact 14-ID inventory retained in the attempt-3 bundle |
| `builds_catalog_introducing_revision` | `dae84d5f96911517eb1e6f97e75eb88e759e6d8a` | Introduces catalog `3.106.0`; child `000abf867abc3a99cfa74d39b6e73af05c78a602` records its audit |
| `builds_qualifying_revision` | `5730f5a60c0e106131498126cbee323e24db5f4e` | Immutable local candidate; alignment passed, qualification did not complete |
| `architecture_pre_acceptance_revision` | `8dc7e5695aa496b4ff3ec27f0477c466f6ae45eb` | Projects revision before this run; Architecture remains at accepted `3.70.1` |
| `rollback_eventstore_revision` | `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` | Contract-fixed rollback coordinate |
| `rollback_builds_revision` | pending | Phase 5 was forbidden after the Phase-3 failure |

The Builds candidate is one commit on local branch
`fix/p1r-3106-candidate`, based on
`2fba3497043fe5ffcfe4dc44c51a09eae9b950ab`. Its exact commit message,
`fix(runner): align EventStore pin to 3.106.0`, passed repository-pinned
commitlint before and after commit. It aligns 61 active runner, schema, test,
fixture, serialized-evidence, package-control, and coupled-hash files. It
preserves the `3.88.0` and `3.70.1` exact-pin negatives and the historical
`3.102.0` bundle documentation.

Static parity passed all 13 catalog rows at `3.106.0`, the authoritative
catalog passed, and the complete package audit passed at the same candidate.

### Source comparison and seven APIs

Selected source and package source are the same commit. Current unselected
EventStore HEAD `ffb6901a5b840af7be030ff246435b4bad00b79c` remains
`v3.106.0-29-gffb6901a` and changes 18 `src/` paths after the selected tag:

- `src/Hexalith.EventStore.Client/Gateway/EventStoreGatewayClient.cs`
- `src/Hexalith.EventStore.Client/Gateway/IEventStoreGatewayClient.cs`
- `src/Hexalith.EventStore.Contracts/Commands/CommandStatusQueryResponse.cs`
- `src/Hexalith.EventStore.Gateway/Hexalith.EventStore.Gateway.csproj`
- `src/Hexalith.EventStore.PayloadProtection/Base64UrlCodec.cs`
- `src/Hexalith.EventStore.PayloadProtection/BoundedJsonDocument.cs`
- `src/Hexalith.EventStore.PayloadProtection/CoreProtectionResult.cs`
- `src/Hexalith.EventStore.PayloadProtection/CoreUnprotectionResult.cs`
- `src/Hexalith.EventStore.PayloadProtection/EnvelopeCodec.cs`
- `src/Hexalith.EventStore.PayloadProtection/PayloadCryptography.cs`
- `src/Hexalith.EventStore.PayloadProtection/PayloadProtectionCore.cs`
- `src/Hexalith.EventStore.PayloadProtection/PayloadProtectionDiagnostics.cs`
- `src/Hexalith.EventStore.PayloadProtection/PayloadProtectionWireFormat.cs`
- `src/Hexalith.EventStore.Server/Commands/ArchivedCommandExtensions.cs`
- `src/Hexalith.EventStore/Authorization/ITrustedCommandExtensionPolicy.cs`
- `src/Hexalith.EventStore/Controllers/CommandsController.cs`
- `src/Hexalith.EventStore/Queries/DaprDomainQueryInvoker.cs`
- `src/Hexalith.EventStore/Validation/ExtensionMetadataSanitizer.cs`

Untagged HEAD remains unselected. The seven-API comparison passed: selected
source and package blobs are identical; the first six APIs also match rollback
and `v3.88.0`; `QueryCursorScope` retains only the previously accepted additive
`AddProjectionWatermark(long?)` difference over rollback. Exact blob IDs are
retained in `seven-api-blobs.tsv`.

### Exact dependency materialization

The disposable selected EventStore worktree materialized all seven declared
dependencies as separate detached worktrees at the tag's exact gitlink SHAs:

| Dependency | Exact revision |
| --- | --- |
| `Hexalith.AI.Tools` | `5f93d2ec8239494852c97032c819cb1689939e36` |
| `Hexalith.Builds` | `aee01323579adeda952bf41b2f3e0e61785075c6` |
| `Hexalith.Commons` | `19d7d4d6b21160557b7449f55a0ad0f55e6d7dc6` |
| `Hexalith.FrontComposer` | `0122c53afb5ac2993ca2e4d6a6e828600c960687` |
| `Hexalith.Memories` | `5829db422522b79c0d164df85adde53a996ba114` |
| `Hexalith.PolymorphicSerializations` | `8aeed1d27c9a050bc4bec6d89051aa00de306a69` |
| `Hexalith.Tenants` | `c150d5b1af4f911ff3b2a7ab7901d94838be09d7` |

No `git submodule update --init` ran. The post-failure state row verified the
EventStore worktree and every dependency worktree remained clean at those
revisions.

### Attempt ledger and first failure

Two earlier bundles preserve harness-only stops. Attempt 1 stopped because its
static row asserted an incorrect abbreviated `git describe`; attempt 2 stopped
because its catalog regex lost the literal-dollar escape. Neither advanced
past static alignment.

Attempt 3 passed coordinate capture, commitlint, static parity, authoritative
catalog, complete audit, seven-API comparison, the exact Debug source restore,
and the exact serialized Debug source build. The first test row then stopped
the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `es-source-003-contracts-test` | exit `5`; zero tests ran | **FAIL** |

The exact mandated command ends in `-m:1`. With the repository's `global.json`
opting into Microsoft.Testing.Platform under SDK `10.0.401`, `dotnet test`
does not expose `-m`/`-m:1`; it completed with the platform's zero-tests exit
instead of executing the assembly. This is the same command-shape blocker
recorded by the 2026-09-05 candidate, now reproduced at the selected
`v3.106.0` coordinate. It cannot be relabeled or offset by a fallback because
the contract requires every named row itself to be `PASS`.

Per the stop-first rule, the remaining source tests, package-source lane,
14-package remote restore, Builds restore/build/test/package lane, reciprocal
rollback commits and execution, finite completion record, and owner acceptance
did not start.

### Durable evidence and owner state

| Bundle | Manifest entries | Manifest SHA-256 | Verification SHA-256 |
| --- | ---: | --- | --- |
| `qualification-evidence/6-1-p1r-3106-20260921-attempt1/` | 10 | `f34a5da060e6448befa626c251156ad7c31a73d39fe7af1706246e0d606f0474` | `90cce12c8e59a601a6f8b210d1558b71b6ee87f9dc7645f549d659989426555f` |
| `qualification-evidence/6-1-p1r-3106-20260921-attempt2/` | 10 | `d7c040f908364356fa72e8ca471c6f73ce4f536a58db45f0fbfff8b68cc5c8c7` | `90cce12c8e59a601a6f8b210d1558b71b6ee87f9dc7645f549d659989426555f` |
| `qualification-evidence/6-1-p1r-3106-20260921-attempt3/` | 25 | `ece7ece87821f651a6d7ee96abad114aea1132d0ef531284092c3e37f9765495` | `8161bdefd516ae29b238ea11320069d78d1afa16eb8d95f50d314418a843719e` |

Each `sha256sum -c artifact-manifest.sha256` verification reports every entry
`OK`. The manifests exclude themselves and their verification transcript; the
table above attests both exact hashes.

| Role | Named approver | Decision | UTC date | Required next action |
| --- | --- | --- | --- | --- |
| EventStore Owner | Jérôme Piquot | pending | pending | Approve a corrected contract command only through explicit contract renegotiation; then require a fresh run from coordinate capture |
| Builds Owner | Jérôme Piquot | pending | pending | Retain candidate `5730f5a60c0e106131498126cbee323e24db5f4e`; do not call it qualified |
| Solution Architect | Jérôme Piquot | pending | pending | Keep Architecture at `3.70.1` until all phases pass |
| Test Architect | Jérôme Piquot | pending | pending | Reject acceptance while the required test row is `FAIL` and later phases are absent |

P1R, P0, P2, P3, P4, Story 6.1, readiness, and the transitive Epic 7/8 gates
retain their existing blockers. No acceptance is inferred from the named-role
authorization in the spec because the finite record is incomplete.

### Evidence tracking correction — 2026-09-21

The three bundle-local `.gitignore` files were added after the table above was
written so the umbrella's `[Ll]ogs/` rule cannot leave retained first-result
logs untracked. The manifests were regenerated and re-verified after that
addition. This append supersedes only the three manifest rows above:

| Bundle | Total files | Manifest entries | Manifest SHA-256 | Verification SHA-256 |
| --- | ---: | ---: | --- | --- |
| `qualification-evidence/6-1-p1r-3106-20260921-attempt1/` | 13 | 11 | `8b24457fa83c6df7b3399388034a29f09a4463ac1a4987e420fc97b6e18b18b7` | `01f22f30664ac7946ef37329248b1a14d5256de12e74c948bbf9f3d7d883dbb8` |
| `qualification-evidence/6-1-p1r-3106-20260921-attempt2/` | 13 | 11 | `55a519d8592a17827fe249b164141f88814d58c040783cc7d6f0a4fa016f3a69` | `01f22f30664ac7946ef37329248b1a14d5256de12e74c948bbf9f3d7d883dbb8` |
| `qualification-evidence/6-1-p1r-3106-20260921-attempt3/` | 28 | 26 | `bb67de53f3d4a6bf13e7abb8a01f917b220d9ddf5184b22b9538f05c828603a2` | `7d84c753edd2002f34c1ead20289da65f9a77b804ee5a9732604a320e109048e` |

All 16 retained `.log` files are now visible to `git status
--untracked-files=all`; each manifest verification reports every covered entry
`OK`. No command was rerun and no disposition changed.

## Renegotiated `3.106.0` attempt 4 — 2026-09-21

The human explicitly renegotiated only the qualification contract's
`dotnet test` serialization option from `-m:1` to
`--max-parallel-test-modules 1`. Every `dotnet build -m:1` row and the
approved intent, boundaries, decisions, and I/O matrix remained unchanged.
Attempts 1–3 and their manifests were verified before attempt 4 restarted from
coordinate capture.

Attempt 4 used fresh detached worktrees and fresh caches. It reproduced the
selected `v3.106.0` / `76051c70cbf868c40edc00ca0344fa5bd8879b69`
coordinate, exact 14-package release manifest, Builds candidate
`5730f5a60c0e106131498126cbee323e24db5f4e`, seven API result, and all seven
dependency revisions recorded above. Coordinate capture, prior-manifest
verification, candidate commitlint/static/catalog/audit, the seven-API
comparison, source restore, source build, and the renegotiated Contracts test
all passed.

The next required row stopped the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `es-source-004-client-test` | exit `1`; compiler `CS1704` | **FAIL** |

The compiler found two assemblies named `Hexalith.Commons.UniqueIds`: remote
package `2.30.0` from the restored assets and version `1.0.0` from the exact
dependency project. Diagnostic evaluation after the retained failure showed
`HexalithCommonsFromSource=true` and the exact Commons project path during
build, while
`src/Hexalith.EventStore.Contracts/obj/project.assets.json` retained
`Hexalith.Commons.UniqueIds/2.30.0` as a package and contained no project
reference. This is a distinct source-mode restore/build graph inconsistency;
the renegotiated test option itself was accepted by the runner and the
Contracts test executed successfully.

The post-failure state assertion passed: the EventStore worktree and all seven
exact dependency worktrees remained clean at their recorded revisions. Per
the stop-first rule, no package-source, remote-package, Builds serialized/G-4,
reciprocal rollback, or acceptance phase ran.

Attempt 4 retains 32 files. Its 30-entry
`artifact-manifest.sha256` has SHA-256
`a1b6750f82e0e6f767898d19160c0d1a3339c09d6257cfcba83e3a252721563a`;
`manifest-verification.txt` has SHA-256
`18028f3306becee10dcdec767d52d6f34b8de8a585192050e3ff310cf6465eac`.
Every manifest entry verifies `OK`.

All four owner decisions remain `pending`. Architecture remains bound to
EventStore `3.70.1`; P1R and every downstream gate remain open with their
prior blockers. A qualifying continuation requires new authority to change
the source-mode graph/contract or a new immutable EventStore package release;
this stopped run cannot be accepted as the finite record.

## Renegotiated `3.106.0` attempt 5 — 2026-09-21

The human next authorized a contract-only change adding `--no-build` to all
ten `dotnet test` rows while retaining `--no-restore` and
`--max-parallel-test-modules 1`. All three `dotnet build -m:1` rows,
production source, the approved frozen intent, and attempts 1–4 remained
unchanged. Attempt 5 used new detached worktrees and caches and restarted from
coordinate capture.

Preservation of all four earlier manifests, selected-coordinate capture,
candidate commitlint/static/catalog/audit, the seven-API comparison, source
restore, and the serialized source build passed. The first test row stopped
the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `es-source-003-contracts-test` | exit `2`; 2,022 passed and 1 failed of 2,023 | **FAIL** |

The exact renegotiated command used `--no-build --no-restore
-p:UseHexalithProjectReferences=true --max-parallel-test-modules 1`. The
failing test was
`ContainerPublishingGovernanceTests.ReleaseSourcePreflightFailsClosedAndEmitsSelectedSuccessfulPushProof(bypassValidation: "true", expectedSourceWorkflow: "commitlint.yml")`.
Its source-preflight process returned exit `126` instead of the required `0`.
The retained log has SHA-256
`4f692254c9b8f9923daed3fdd482508990612e82f47da17f03451b6a5c6ff4c8`.

The post-failure state assertion passed for EventStore and all seven exact
dependency worktrees. Per the explicit stop-first instruction, no later
source, package-source, remote-restore, Builds runner/G-4, reciprocal rollback,
or acceptance row ran.

Attempt 5 retains 30 files. Its 28-entry manifest has SHA-256
`091678120a13c0ecb8e19ad1c7e4ab28e00886978aa161c862db5cdd52c7831e`;
the verification transcript has SHA-256
`d41f9a94e7e6bc41947a8b53d78c8328ada443844aabaf2aa51d21ea440cb348`.
Every covered file verifies `OK`. All four owner decisions remain pending,
Architecture remains bound to `3.70.1`, and P1R plus every downstream gate
remain open.

## Clean serialized attempt 6 — 2026-09-21

Attempt 6 preserved attempt 5's first failure and used the contract's clean
serialized contention-rerun allowance. The attempt-5 contract was unchanged;
no production source changed. New detached source, package-source, exact
dependency, and Builds candidate worktrees used unique caches and no
overlapping qualification work.

Preservation of attempts 1–5, coordinate capture, candidate
commitlint/static/catalog/audit, and seven-API comparison passed. The complete
source lane passed, including the clean rerun of all 2,023 Contracts tests and
the Client, DomainService, and Server tests. That result supersedes only the
retained attempt-5 contention result. The complete package-source
restore/build/four-test lane also passed, including 2,023/2,023 Contracts
tests.

The next row stopped the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `remote-001-restore` | exit `1`; `NU1008` before package resolution | **FAIL** |

The retained consumer project declared exact `3.106.0` versions for thirteen
library `PackageReference` items and an exact `[3.106.0]` `PackageDownload`
for the DotnetTool package. Because that project was retained below the
umbrella repository tree, it inherited root central package management, which
forbids inline `PackageReference` versions. The first restore therefore failed
before consuming the remote packages. Per the explicit stop-first instruction,
the consumer was not corrected or rerun.

The post-failure assertion passed for both EventStore worktrees, all fourteen
exact dependency worktrees, and candidate Builds revision
`5730f5a60c0e106131498126cbee323e24db5f4e`. No Builds serialized/G-4,
reciprocal rollback, owner-acceptance, or propagation row ran.

Attempt 6 retains 57 files. Its 55-entry manifest has SHA-256
`609416be33bc297f3d6c077d90a4851fdf35151ccff60a032ae45d8ee5411e6a`;
the verification transcript has SHA-256
`abb4ff4667349ac667ffa18a61bbc71a33c5542cdc6f014165d976fea61e6bc9`.
Every covered file verifies `OK`. All four owner decisions remain pending,
Architecture remains bound to `3.70.1`, and P1R plus downstream gates remain
open.

## External-consumer attempt 7 — 2026-09-21

Attempt 7 preserved attempts 1–6 and corrected only the execution-harness
location: the remote consumer and NuGet configuration were created under the
qualification `/tmp` tree, outside repositories and ancestor build
configuration. Contract, source, and candidate bytes were unchanged.

Coordinate/static/API rows and both complete EventStore source and
package-source lanes passed. Before remote restore, a new effective-property
proof row was intended to attest that central package management was not
inherited. Its generated shell command contained an unmatched quote and
exited `2` before invoking MSBuild. The exact command and log are retained;
under the explicit stop-first rule it was not repaired or rerun. Remote
restore, Builds runner/G-4, rollback, acceptance, and propagation did not run.

The external consumer and NuGet configuration bytes were copied into the
bundle. Post-failure state passed for both EventStore lanes, all fourteen
dependency worktrees, and the Builds candidate. Attempt 7 retains 52 files;
its 50-entry manifest SHA-256 is
`7c3a978c1b2e9e84e5d0ae2aeeddd2022490112a699986195d2b50d352b2d0d1`
and its verification SHA-256 is
`d7fa8ee5ed016ab22345e4f6409fce1bfb9060895b9deb09848417a08e9dd5f0`.
Every covered file verifies `OK`. Owner decisions remain pending and no
planning state changed.

## Disk-exhausted attempt 8 — 2026-09-21

Attempt 8 preserved attempts 1–7 and restarted unchanged to correct only the
attempt-7 isolation-proof harness. Preservation, coordinate, candidate,
catalog/audit, API, and source restore rows passed. The serialized source build
then exited `1` after `/tmp` filled. Its log retains 93 build errors including
`MSB3021`, `MSB3027`, and `MSB3491`, each reporting `No space left on device`.

No test, package-source, remote-consumer, Builds runner/G-4, rollback,
acceptance, or propagation row ran. No preserved attempt directory/cache was
deleted and no rerun occurred. Disk state and clean checkout assertions passed
and are retained.

Attempt 8 retains 29 files. Its 27-entry manifest SHA-256 is
`b941f549ecb37380ddb5cb99e7362800cb889a0926c69be1f7ee14878c445a82`;
verification SHA-256 is
`178beb4c67a8a0a4d1733324f205087769e4cde546ad5fe4878aa6fcdbf60dfd`.
Every covered file verifies `OK`; owner decisions remain pending.

## Clean external-consumer and candidate attempt 9 — 2026-09-21

After the task-created attempt-8 worktrees and caches were safely removed,
attempt 9 restarted from coordinate capture under a new unique temporary root.
It preserved the eight sealed earlier bundles and used the unchanged contract,
EventStore tag, exact dependency revisions, and Builds candidate. Qualification
ran serially with no overlapping work.

Preservation, coordinates, candidate commitlint/static/catalog/audit, and the
seven-API comparison passed. The complete EventStore source and package-source
lanes passed, including both 2,023-test Contracts runs and all six other test
projects. The corrected direct-command CPM proof passed outside all repository
and ancestor build configuration, showing
`ManagePackageVersionsCentrally != true` and the exact external project path.
Official-only restore from an empty qualification cache then passed. The
retained assets and inventory verify all fourteen release-manifest package IDs
at `3.106.0`.

The Builds candidate restore, serialized build, and Module tests passed. The
next required row stopped the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `bld-candidate-004-evidence-test` | exit `2`; 59 passed and 9 failed of 68 | **FAIL** |

The nine failures consistently introduced `HXE152` evidence-binding
diagnostics. Both positive-readiness assertions failed; multiple negative
snapshots gained an unexpected `HXE152`; and the no-executed-tests assertions
observed `HXE152` where `HXE153` was required. The retained first-result log
has SHA-256
`98864eca855cf1aa4142d696a563540f12b16e9feadf0cc4289f8e2132d97061`.
No later catalog, audit, packaged-command, reciprocal rollback, acceptance, or
propagation row ran.

The post-failure state assertion passed for candidate Builds, both EventStore
worktrees, and all fourteen exact dependency worktrees. Attempt 9 contains 76
files. Its 74-entry manifest has SHA-256
`048e3a6df48f509c3c8cb2e0719db683a03a60dfc3ab165511396e9306a2499a`;
the verification transcript has SHA-256
`2fa1bf3d6e860b6e71cbe77f84b3abb5eedb605ae31c3766953fe47e1f299dac`.
All 74 covered entries verify `OK`; the separate eight-file external-consumer
hash list also verifies `OK`. The ledger contains 27 rows: 26 `PASS`, one
`FAIL`, and no `INCONCLUSIVE` result.

All four owner decisions remain pending. Architecture remains bound to
EventStore `3.70.1`; sprint status, deferred work, P1R, and every downstream
gate remain unchanged and open.

### Attempt 9 retention correction

Before final handoff and disposal of the temporary root, the exact executed
seven-API comparison script referenced by `es-002-api-blobs` was copied into
the bundle as `proofs/api-compare.sh`. `cmp` and SHA-256 both prove it is
byte-identical to the executed script; its SHA-256 is
`be0e53798afb90b621d9fb155cd1e6e7b1414d829e4ad2c01fa76028753d9fb3`.
The bundle was then resealed and fully reverified. This supersedes only the
attempt-9 file-count and seal values above: 77 total files, 75 manifest
entries, manifest SHA-256
`96a19ee04ff27c9ab235eb3e460a92f328d8644e71585d0eca9cb98b0f44e321`,
and verification-transcript SHA-256
`359de629afc63c852387479d5b353d389150c013ecda6e21eb906c84ac969205`.
No command was rerun and no disposition, owner state, or planning state
changed.

## Corrected-candidate and G-4 attempt 10 — 2026-09-21

Attempt 10 preserved attempts 1–9 and restarted from coordinate capture with
corrected Builds candidate
`ad52f350a2f0bc47849179ae17b4594dafff5363`. Candidate
`5730f5a60c0e106131498126cbee323e24db5f4e` is superseded only by the second
commit's fixture-binding correction: otherwise-valid retained module-run
artifacts now embed the SHA-256 of the aligned positive manifest, and their
referencing YAML hashes were refreshed. Contract, selected EventStore
coordinate, production source, and package coordinate were unchanged.

Preservation, coordinate capture, candidate commitlint/static/catalog/audit,
and seven-API comparison passed. Both complete EventStore matrices passed,
including 2,023/2,023 Contracts tests in source mode and package-source mode
plus every Client, DomainService, and Server row. The corrected direct-command
external-consumer CPM proof, official-only restore from an empty cache, and
all-fourteen-package inventory also passed.

Candidate restore, serialized Release build, Module tests (117/117), and
Evidence tests (68/68) passed. The official packaged G-4 row ran without
`-SkipSourceValidation`, executed required controls, and passed. Its retained
inventory uses unique disposable version `0.0.0-p1r3106-attempt10.1`, records
`releaseEligible=true` for the clean disposable candidate worktree, and
contains both CLI packages, both symbol packages, and 53 qualification-
evidence artifacts. The inventory SHA-256 is
`1a4b4942f3e730bee67f84879635d170d8357a7f81e8cc106a7cdbd5d47c46f0`.

The reciprocal rollback was then constructed from the corrected candidate as
the required two local commits:

| Role | Revision | Result before qualification |
| --- | --- | --- |
| Rebind catalog/runner/schema/fixtures/evidence/hashes to `3.70.1`; retain `3.88.0` and reject selected `3.106.0` | `44bd5ebcb1cac36f25b67ce5ba1a54a2772ca73b` | committed |
| Incrementally refresh the EventStore audit family against the committed rollback catalog | `caf49c58bc18f5226733e7879b4f71e99213fffd` | audit generation and deterministic validation passed (299 packages, 145 families, one source) |

The first rollback qualification row stopped the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `rollback-bld-000-commitlint` | exit `1`; both commit bodies violate `body-max-line-length` because each was authored as one line longer than 200 characters | **FAIL** |

The retained failure log SHA-256 is
`fa78f36b9ee860d97ebb99c9c564d8c7d2db976f0923a203f8eac9b663735642`.
The commits were not amended or rewritten. Under the stop-first rule, no
rollback static/catalog/audit qualification, restore/build/test/G-4, reciprocal
`HXM016` execution, or EventStore `v3.70.1` compatibility row ran. The clean
post-failure assertion passed for both Builds worktrees, both EventStore
worktrees, all fourteen exact dependency worktrees, and both live repositories.

Attempt 10 retains 142 files. Its 140-entry manifest has SHA-256
`f8fdf2c215c8a759596472dd5a5f0b0e6156bb139769fd1b44fa44ba91e50cb5`;
the verification transcript has SHA-256
`5d964ef6dd60d89d95bd444d46ac726a9d14ad75c9107da3b0f2a2e067988e80`.
Every covered file verifies `OK`. The ledger contains 30 rows: 29 `PASS`, one
`FAIL`, and no `INCONCLUSIVE` result.

All four owner decisions remain `pending`; a failed rollback commitlint row
prevents this attempt from being the complete finite record. Architecture
remains bound to EventStore `3.70.1`; sprint status, DW-35, DW-68, P1R, and
every downstream gate remain unchanged and open. No push, merge, publication,
release, or live-checkout mutation occurred.

## Tree-equivalent rollback attempt 11 — 2026-09-21

Attempt 11 preserved the failed attempt-10 branch and created distinct branch
`fix/p1r-3106-rollback-candidate` from corrected candidate
`ad52f350a2f0bc47849179ae17b4594dafff5363`. The two rollback changes were
reapplied with newly wrapped Conventional Commit messages:

| Role | New revision | Exact tree equivalence |
| --- | --- | --- |
| Rebind catalog/runner/schema/fixtures/evidence/hashes to `3.70.1` | `38bac7906bd8aa3887bdec4dd7c31470a10dd5ed` | tree `b404b1624b239446bfbc4a99127f8a342a062e8a`, equal to `44bd5eb...^{tree}` |
| Add the incremental EventStore-family audit | `1f46bc373d82fb266c8396759cc3a57c62e6ae7c` | tree `d53a53615774e127ebddc0688e9ad459e2de017c`, equal to `caf49c5...^{tree}` |

Both new messages passed the repository-pinned commitlint command. The failed
branch still resolves to `caf49c58bc18f5226733e7879b4f71e99213fffd`
with the same two tree identities; it was not amended, reset, or deleted.

Attempt 10's 140-entry manifest reverified, and attempt 11 adopted all 27
immutable passing phase 1–4 rows. It separately rechecked the selected
candidate packaged controls proving `3.88.0` and rollback `3.70.1` are each
rejected with `HXM016`. Rollback coordinate/tree proof, static alignment,
restore, serialized Release build, Module tests (117/117), Evidence tests
(68/68), and the authoritative 50-identity/three-shared-version catalog check
all passed.

The next row stopped the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `rollback-bld-009-audit` | exit `1`; audit `generatedFromRevision` `44bd5ebcb1cac36f25b67ce5ba1a54a2772ca73b` is not an ancestor of new head `1f46bc373d82fb266c8396759cc3a57c62e6ae7c` | **FAIL** |

This failure follows directly from the exact-tree constraint. Copying the
attempt-10 audit bytes preserves `generatedFromRevision=44bd5eb...`; changing
that field to the new tree-equivalent commit `38bac790...` would change the
new head tree, so it could no longer equal `caf49c5...^{tree}`. The retained
failure log SHA-256 is
`378b23a299031df38bc9a5b9a35ab26438a42668de5dd05aa28767c534f013ae`.

Per stop-first, no rollback G-4, packaged-command, `v3.70.1` EventStore
source/package/API, finite-record completion, or owner-acceptance row ran.
The post-failure state row passed for the rollback and live repositories.

Attempt 11 retains 41 files. Its 39-entry manifest has SHA-256
`8160f13b71756fbbc089f988599c20cf8225af6ce6e76ed582c323918f66c29a`;
the verification transcript has SHA-256
`841ae7b513ea7f9e47b339c7ace6c44e4e48822c6985eae2dcce2b3855164ad2`.
Every covered file verifies `OK`. The ledger contains 12 rows: 11 `PASS`, one
`FAIL`, and no `INCONCLUSIVE` result.

All four owner decisions remain pending. Architecture remains bound to
EventStore `3.70.1`; sprint status, DW-35, DW-68, P1R, and every downstream
gate remain unchanged and open. No push, merge, publication, release, or live-
checkout mutation occurred.

## Audit-provenance repair and stopped attempt 12 — 2026-09-21

Attempt 12 appended, rather than amended, provenance-repair commit
`7af20f8bafbfe561df6f7705913a0800603090b5` to
`fix/p1r-3106-rollback-candidate`. The prior rollback commits remain
`38bac7906bd8aa3887bdec4dd7c31470a10dd5ed` and
`1f46bc373d82fb266c8396759cc3a57c62e6ae7c`; the failed attempt-10 branch
still resolves to `caf49c58bc18f5226733e7879b4f71e99213fffd`.

The repository-authoritative family-scoped audit generator changed only
`Tools/package-version-audit.json`. Its top-level `generatedFromRevision` and
current Hexalith EventStore family origin both name parent `1f46bc3...`, which
is a proven ancestor of the new head. Pre- and post-commit audit validation
passed for 299 packages, 145 families, and one source. The authoritative
catalog passed 50 identities and three shared versions, and all three rollback
commit messages passed repository-pinned commitlint.

The rollback continuation itself stopped at its first row:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `adopt-001-prior-attempts` | exit `1`; after both prior bundle manifests verified, the proof compared the frozen block to stale expected SHA-256 `94a818e0...`; unchanged working/current-root-HEAD bytes hash to `d933172d...` | **FAIL** |

The retained first-result log SHA-256 is
`3770566d33b8554f05766f54ef6f55b0a697a1b0660e8df0710cd3ad2b59b88a`.
This is a proof-harness failure and does not represent a frozen-block edit.

The command wrapper's displayed output omitted that nested exit status, so one
additional row was inadvertently launched before the stop was recognized:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `rollback-bld-001-coordinate` | exit `1`; proof expected rollback commit 1 to change 61 paths, while the immutable commit changes 63 | **FAIL** |

That empty first-result log has SHA-256
`e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
No restore, build, test, catalog, audit, G-4, reciprocal-control, EventStore
`v3.70.1`, finite-record, or acceptance row ran after the failures.

Attempt 12 retains 43 files. Its 41-entry manifest has SHA-256
`e8b857740a3d8e78c6c99fd2e84b7494245a6ae1f8889f3b62bc3d218ce2ed98`;
the verification transcript has SHA-256
`4a86a7ab4b1de8dfa074e203b37d46bb213e5d1263c97de6bd53d955467d26d2`.
Every covered file verifies `OK`. The ledger contains 13 rows: 11 `PASS`, two
`FAIL`, and no `INCONCLUSIVE` result. The post-failure clean-state assertion
passed for the detached rollback worktree and both live repositories.

All four owner decisions remain explicitly pending. Architecture remains
bound to EventStore `3.70.1`; sprint status, DW-35, DW-68, P1R, and every
downstream gate remain unchanged and open. No push, merge, publication,
release, or live-checkout mutation occurred.

## Disk-backed rollback attempt 13 — 2026-09-21

Attempt 13 preserved rollback head
`7af20f8bafbfe561df6f7705913a0800603090b5` and corrected only the two
attempt-12 harness proofs. It used disk-backed qualification root
`/home/administrator/.local/state/hexalith-qualification/p1r-3106-attempt13-20260921`
on `/dev/sdd` (`ext4`), registered cleanup before materialization, used the
ordinary shared NuGet cache and version-keyed Playwright store, and proved the
ledger wrapper propagated a synthetic nested exit `7` before the ledger began.

Adoption dynamically captured root revision
`6e6b03969d9d526bc1c913e25e67f14d1a9c0126`; working and root-HEAD frozen
bytes were identical at SHA-256
`d933172da048d272daf9154740f606567ac8858807815931e06a7306259e6a92`.
Attempts 10–12 fully reverified and all 27 immutable passing phase 1–4 rows
from attempt 10 were adopted.

Rollback coordinate/static proof verified the three-commit chain, tree
identities, audit provenance ancestry, clean state, all `3.70.1` bindings,
`3.88.0` and selected `3.106.0` reciprocal `HXM016` controls, `HXM009`, and
the deliberate evidence-hash mismatch. Commit 1's immutable name-status diff
was retained as 63 rows without a magic-count assertion; its SHA-256 is
`f593f4b77d94b3fa4a482e1263b1c196a4551f6175c39804cce9b4edce9ce193`.

All three rollback messages passed commitlint. Builds restore, serialized
Release build, Module tests (117/117), Evidence tests (68/68), authoritative
catalog validation (50 identities, three shared versions), and full audit
validation (299 packages, 145 families, one source) passed.

The next required row stopped the run:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `rollback-bld-009-g4` | exit `1`; the harness had pre-created the requested retained-package directory, while the authoritative G-4 script requires each unique destination not to exist | **FAIL** |

The retained first-result log SHA-256 is
`884ab53627852da0a9b103163c3a6f49a2cab89b61801831b39678ae7143238f`.
The directory was empty and no package was produced. The wrapper propagated
the nonzero exit; no EventStore `v3.70.1`, finite-record, or owner-acceptance
row ran.

Attempt 13 retains 59 files. Its 57-entry manifest has SHA-256
`813befc4102f47cf7a3f207a42f0773bf16035d39aa2992992832f1797963b64`;
the verification transcript has SHA-256
`3e7ea9319da68e77556fa04c7c5ef0c142276e3a3c7ee25689afa93c520f648f`.
Every covered file verifies `OK`. The ledger contains 12 rows: 11 `PASS`, one
`FAIL`, and no `INCONCLUSIVE` result. Post-failure state passed, and cleanup
removed the qualification root and worktree registration. Cleanup diagnostics
for an absent optional PolymorphicSerializations repository related only to
never-created EventStore dependency worktrees and left no disposable state.

All four owner decisions remain explicitly pending. Architecture remains
bound to EventStore `3.70.1`; sprint status, DW-35, DW-68, P1R, and every
downstream gate remain unchanged and open. No push, merge, publication,
release, or live-checkout mutation occurred.

## Corrected G-4 and stopped rollback attempt 14 — 2026-09-21

Attempt 14 continued at unchanged rollback Builds revision
`7af20f8bafbfe561df6f7705913a0800603090b5`. It verified attempts 10–13,
adopted the 27 immutable passing phase-1–4 rows from attempt 10, and adopted
the eight same-revision attempt-13 rollback rows through audit. The current
contract SHA-256 was
`ccb22f3c19bf98082eb37a17ea217f5193099d0145437a725bc0aff454128586`;
working frozen bytes remained identical to root revision
`6e6b03969d9d526bc1c913e25e67f14d1a9c0126` at SHA-256
`d933172da048d272daf9154740f606567ac8858807815931e06a7306259e6a92`.

The G-4 harness created only the retention parent and proved the unique
`retained-objects/rollback` leaf was absent immediately before the
authoritative command. The command created the leaf and passed without
`-SkipSourceValidation` under unique version
`0.0.0-p1r3106-attempt14-rollback.1`. Its inventory records the exact clean
rollback source revision, two CLI packages, two symbol packages, 53
qualification artifacts, passing source validation, executed required
controls, and `releaseEligible=true`. All 58 retained files and inventory
hashes verify. The positive `3.70.1` control passed, while `3.88.0` and
selected `3.106.0` each failed with `HXM016`; invalid profile retained
`HXM009`, and the deliberate evidence-hash mismatch retained `HXE147`.

Fresh source and package EventStore worktrees at rollback revision
`f13f9925fdca53efa2ab8c90d396ab106f91bb9c` were materialized with these
exact dependency worktrees, without nested-submodule initialization:

| Dependency | Revision |
| --- | --- |
| Hexalith.AI.Tools | `991e8ea1b39bfb8170aea9a6857c25c7c69176c1` |
| Hexalith.Builds | `cfafcbf1e904138b435b63ba4fd79f86b8dda069` |
| Hexalith.Commons | `05f0d9f4f6360bef9c33f14396b0c0c74cbd6864` |
| Hexalith.FrontComposer | `0e6abe9ec33e11e8725ea764d4596169a4354269` |
| Hexalith.Memories | `ce66d4cad9e36eaddba0708e12551863d2b57a9d` |
| Hexalith.PolymorphicSerializations | `30312d8cc7b15adf477bf5fbab3e852393086ec0` |
| Hexalith.Tenants | `211b4d4ae59d85cdbeb0cddf99bf3ad86b361f30` |

The seven-API comparison passed. Six APIs have identical blobs at rollback,
superseded `v3.88.0`, selected package, and selected source coordinates. The
sole difference is the already-classified compatible additive
`QueryCursorScope.AddProjectionWatermark(long?)` method: the selected revision
adds 21 lines and removes zero.

Rollback source restore passed using the normal shared package cache. The
serialized Debug solution build passed with zero warnings and zero errors.
The next row stopped the run before test discovery:

| Row | Exact result | Disposition |
| --- | --- | --- |
| `rollback-es-source-003-contracts-test` | exit `1`; EventStore `v3.70.1` dispatches through historical VSTest/MSBuild, which rejects contract-required `--max-parallel-test-modules 1` as an unknown switch (`MSB1001`) | **FAIL** |

The retained first-result log SHA-256 is
`f7198ffb7f141986a5dc88396726e6f2753316945baeef6ffc9b4a276be7b61b`.
Per stop-first, no Client, DomainService, Server, package-mode, finite-record,
acceptance, or propagation row started. Post-failure capture retained 54
resolved asset hashes and 67 Contracts dependency content-hash rows; every
owned worktree and both live repositories were clean.

Attempt 14 retains 131 files. Its 129-entry manifest has SHA-256
`d4bcb684542a1594422bb5a7b99742da8bb007b843c3418761b6b7b91d72ff04`;
the verification transcript has SHA-256
`85624af19000821939216a1844a86191a7ebce6b2dfbf5a83b3f5c26a0e2f63d`.
Every covered file verifies `OK`. The ledger contains 12 rows: 11 `PASS`, one
`FAIL`, and no `INCONCLUSIVE` result. Final cleanup removed the qualification
root and every attempt worktree registration.

| Role | Named approver | Decision | UTC date | Evidence |
| --- | --- | --- | --- | --- |
| EventStore Owner | Jérôme Piquot | `pending` | `pending` | attempt 14 stopped before rollback completion |
| Builds Owner | Jérôme Piquot | `pending` | `pending` | attempt 14 stopped before rollback completion |
| Solution Architect | Jérôme Piquot | `pending` | `pending` | attempt 14 stopped before rollback completion |
| Test Architect | Jérôme Piquot | `pending` | `pending` | attempt 14 stopped before rollback completion |

Architecture remains bound to EventStore `3.70.1`; sprint status, DW-35,
DW-68, P1R, and every downstream gate remain unchanged and open. No push,
merge, publication, release, or live-checkout mutation occurred.

## Complete technical qualification and finite record — attempt 17 — 2026-09-21

Attempt 17 verified attempts 10–16 and completed the earliest-invalid rollback
rows without changing any candidate, contract, production source, frozen
intent, or prior evidence. Fresh EventStore package prerequisites at
`f13f9925fdca53efa2ab8c90d396ab106f91bb9c` restored and built with zero
warnings/errors. Semantic preflight normalized trailing whitespace and proved
the two retained scripts had the exact approved 13-token command, correct
worktree, task-local environment, and no extras. DomainService passed 143/143;
Server passed 2,626 with 25 skipped.

### 1. Coordinates

| Coordinate | Version/reference | Revision |
| --- | --- | --- |
| Selected EventStore source/package | `v3.106.0` / `3.106.0` | `76051c70cbf868c40edc00ca0344fa5bd8879b69` |
| Selected Builds | `fix/p1r-3106-candidate` | `ad52f350a2f0bc47849179ae17b4594dafff5363` |
| Rollback EventStore source/package | `v3.70.1` / `3.70.1` | `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` |
| Rollback Builds | `fix/p1r-3106-rollback-candidate` | `7af20f8bafbfe561df6f7705913a0800603090b5` |
| Superseded comparison | `v3.88.0` | `4843b492dff7c16a4bc74db67509263f969c78c6` |

### 2. Source/package divergence

Selected source and package bind to the same tag/revision, so the selected
tuple has no divergence. Untagged EventStore HEAD `ffb6901...` is unselected;
its post-tag production diff spans 18 `src/` paths, +206/-52 lines.

### 3. Remote package consumption

The official-only empty-cache consumer restored all 14 release-manifest IDs at
exact version `3.106.0`. Manifest SHA-256 is
`6b0b70b856839d4117bcd969f6a2de0093c477c109cb79f3f2882b1f05effcae`;
resolved inventory SHA-256 is
`aa40e4323acb0539731fcf08fbc00277751e0c6a5c9148e5e5475f821df8f7e2`.

### 4. Seven APIs

Six APIs are byte-identical across rollback, superseded, selected package, and
selected source coordinates. `QueryCursorScope.cs` differs only by the
compatible additive 21-line `AddProjectionWatermark(long?)`; zero lines are
removed. Table SHA-256 is
`13c74c84aac570fee95edfd6ead73bcea16c22a23b546d6344088b159e49f5c9`.

### 5. EventStore behavior

Selected source and package lanes both passed restore/build and Contracts
2,023/2,023, Client 801/801, DomainService 156/156, and Server 3,319 passed/25
skipped. Rollback source and package lanes both passed restore/build and
Contracts 708/708, Client 673/673, DomainService 143/143, and Server 2,626
passed/25 skipped. The explicitly approved rollback-test `-m:1` runner spelling
left the contract unchanged.

### 6. Builds qualification

Selected and rollback Builds each passed static alignment, commitlint,
restore/build, Module 117/117, Evidence 68/68, catalog, complete audit, and the
official required-control G-4. Candidate inventory SHA-256 is
`1a4b4942f3e730bee67f84879635d170d8357a7f81e8cc106a7cdbd5d47c46f0`;
rollback inventory SHA-256 is
`bcf78ce8b1c3a0db323064e60f3b998dccec123c73d9102c53a76d094d499be5`.

### 7. Reciprocal rollback

The selected runner accepts `3.106.0` and rejects `3.88.0` and `3.70.1` with
`HXM016`. The rollback runner accepts `3.70.1` and rejects `3.88.0` and
`3.106.0` with `HXM016`. Both preserve invalid-profile `HXM009` and deliberate
artifact-hash mismatch `HXE147`.

### 8. Content-addressed evidence

The finite 57-row migrated ledger contains only unique `PASS` rows and has
SHA-256
`0c2000dfd94923f7b1e6eeb2e0078f90f114ce4201aaa1e6f7cf5b3f4d0184a1`.
The retained completion bundle is
`qualification-evidence/6-1-p1r-3106-20260921-attempt17`, manifest SHA-256
`cae83ed1d9dc5fda0c27ca411f51620b0a71ac0ca51bbefde73870de77c72355`.
It references, without copying, the verified attempt-10, 13, 14, and 15
manifests and retained packages/evidence.

### 9. Preflight and cleanup

The disk-backed ext4 root passed space, immutable-cleanup, and nested-exit
preflight. Final cleanup removed the exact attempt root and all registrations;
both live repositories are clean. Cleanup verification SHA-256 is
`08f371a2bada3af97839356a7859bb4a133c2fdbbe032406dfaae548e2f91186`.

### 10. Downstream non-closure

Technical qualification does not authorize propagation. Architecture remains
`3.70.1`; P1R and P0 Stage 1 remain open; DW-35/DW-68, every P0 acceptance and
publication gate, P2/P3/P4, Story 6.1, readiness, and Epic 7/8 blockers remain
unchanged.

### 11. Owner acceptance

| Role | Named approver | Decision | UTC date | Evidence |
| --- | --- | --- | --- | --- |
| EventStore Owner | Jérôme Piquot | `pending` | `not-issued—pending` | exact tuple and finite record above |
| Builds Owner | Jérôme Piquot | `pending` | `not-issued—pending` | exact tuple and retained G-4 evidence above |
| Solution Architect | Jérôme Piquot | `pending` | `not-issued—pending` | exact tuple; Architecture unchanged |
| Test Architect | Jérôme Piquot | `pending` | `not-issued—pending` | manifests, ledger, controls, and cleanup proof above |

Technical qualification is **PASS**; acceptance is **0/4 and pending**. No
owner decision is inferred, and no push, merge, publication, release,
Architecture update, or planning propagation occurred.

## Four-owner acceptance and bounded propagation — 2026-09-21

At `2026-09-21T10:48:17Z`, Jérôme Piquot explicitly issued `accept` against
the exact attempt-17 finite record in all four required roles. The accepted
pre-decision record SHA-256 is
`900e3442c0b67910af0dd13a38545725e7e5806dbcc640931a3e250a81c386c9`.
After recording the decisions, the 79-entry attempt-17 manifest verifies at
SHA-256
`e806099bddfceac0cf4a145dbc4706da1159e442e391971a6c752c2d14d3f3fe`.

| Role | Named approver | Decision | UTC date | Accepted coordinates and evidence |
| --- | --- | --- | --- | --- |
| EventStore Owner | Jérôme Piquot | `accept` | `2026-09-21T10:48:17Z` | selected source/package `3.106.0` / `76051c70...`; rollback `3.70.1` / `f13f9925...`; finite record and 57-row ledger |
| Builds Owner | Jérôme Piquot | `accept` | `2026-09-21T10:48:17Z` | selected Builds `ad52f350...`; rollback Builds `7af20f8...`; retained G-4 inventories |
| Solution Architect | Jérôme Piquot | `accept` | `2026-09-21T10:48:17Z` | complete selected/rollback tuple; Architecture binding and G-1 non-selection wording |
| Test Architect | Jérôme Piquot | `accept` | `2026-09-21T10:48:17Z` | finite record, manifests, ledgers, controls, and cleanup proof |

Acceptance is **4/4 and satisfied**. The Architecture Spine is rebound to the
selected multi-coordinate tuple, P1R is `done`, P0 Stage 1 is complete, and
DW-35/DW-68 are resolved. P0 supported composition, publication, consumer pin,
persisted qualification, acceptance record, and owner acceptance remain open;
P2, P3, P4, Story 6.1, readiness, and transitive Epic 7/8 gates remain blocked.
P1R does not select Durable Task or Confirmation Artifact capability. No push,
publication, release, source dependency update, or live-checkout mutation was
authorized or performed.

## Post-acceptance review invalidation — 2026-09-21

Independent review established that attempt 17 is not acceptance-grade. Its
27 reconstructed attempt-10 binding hashes omit the contract-required
toolchain, dependency revisions, relevant configuration, resolved assets, and
prerequisite-build binding. Its finite ledger replaces each row's artifact
association with a bundle-wide checksum list that has no logical IDs or byte
sizes, and it omits the attempt-15 package build rows that produced the
Contracts/Client `--no-build` test binaries. The exact pre-decision record
bytes named by the four decisions were not retained as a file.

The four `accept` decisions and commit `ba7592c...` remain immutable historical
evidence, but the gate and bounded propagation are invalidated. Architecture
remains `3.70.1`; P1R, P0 Stage 1, DW-35, and DW-68 remain open. A new run must
start at every incompletely bound row, produce row-specific structured
manifests with durable relative paths and prerequisite-build linkage, retain
the exact pre-decision finite record, and obtain four fresh decisions before
propagation.

## Remediated four-owner acceptance and bounded propagation — attempt 23 — 2026-09-21

Attempt 21 reran the complete phase 1–5 matrix without migrating technical
results. All 58 rows are unique `PASS` results with complete bindings,
row-specific structured artifact manifests, actual prerequisite-build
bindings for every `--no-build` row, reciprocal controls, final-state proof,
and cleanup. Attempt 23 preserves that evidence in place, corrects the
nonexistent catalog-introducing SHA recorded by attempt 22, and proves all six
full record revisions as commit objects in their owning repositories.

The accepted pre-decision record is retained at
`Hexalith.Projects:_bmad-output/implementation-artifacts/qualification-evidence/6-1-p1r-3106-20260921-attempt23/finite-record-pre-decision.md`
with SHA-256
`477949fc59d0cdd3a00d0c46de7aada46164074675c16c253b2015db369c0bab`.
The catalog-introducing revision is
`dae84d5f96911517eb1e6f97e75eb88e759e6d8a`; it changes the catalog from
`3.104.0` to `3.106.0` and is an ancestor of qualifying Builds revision
`ad52f350a2f0bc47849179ae17b4594dafff5363` and rollback Builds revision
`7af20f8bafbfe561df6f7705913a0800603090b5`.

At `2026-09-21T13:03:11Z`, Jérôme Piquot explicitly issued `accept` against
those exact pre-decision bytes in all four required roles.

| Role | Named approver | Decision | UTC date | Accepted scope |
| --- | --- | --- | --- | --- |
| EventStore Owner | Jérôme Piquot | `accept` | `2026-09-21T13:03:11Z` | EventStore source/package `3.106.0` at `76051c70...`, 14-package restore, behavior/API evidence, and EventStore rollback `f13f9925...` |
| Builds Owner | Jérôme Piquot | `accept` | `2026-09-21T13:03:11Z` | Catalog introduction `dae84d5f...`, qualifying Builds `ad52f350...`, complete runner/G-4 evidence, and rollback Builds `7af20f8...` |
| Solution Architect | Jérôme Piquot | `accept` | `2026-09-21T13:03:11Z` | Exact multi-coordinate binding, executable rollback, Architecture update, and G-1 non-selection wording |
| Test Architect | Jérôme Piquot | `accept` | `2026-09-21T13:03:11Z` | Fresh 58-row ledger, complete bindings, structured artifacts, negative controls, remote restore, reciprocal rollback, and cleanup |

Acceptance is **4/4 and satisfied**. The Architecture Spine is rebound to the
accepted EventStore `3.106.0` source/package and Builds `ad52f350...` runner
tuple, P1R is `done`, P0 Stage 1 is complete, and DW-35/DW-68 are resolved.
P0 supported composition, publication, consumer pin, persisted qualification,
acceptance record, and owner acceptance remain open; P2, P3, P4, Story 6.1,
readiness, and transitive Epic 7/8 gates remain blocked. P1R does not select
Durable Task or Confirmation Artifact capability. No push, publication,
release, source dependency update, or live-checkout mutation was authorized or
performed.

## Attempt-23 post-acceptance review invalidation — 2026-09-21

Review iteration 2 invalidates the attempt-23 acceptance and bounded
propagation recorded above. The decisions remain immutable historical
evidence, but the accepted record is non-qualifying for four independent
reasons:

1. attempt 21 retains a known-false catalog-introducing revision in a PASS
   coordinate row, and that row's binding does not retain the function body
   that produced the value;
2. the official-remote consumer does not prove evaluated direct-ID equality
   against all 14 manifest IDs and uses non-singleton `PackageReference`
   ranges;
3. attempt 23 does not retain all internal-binding, cache-root, required-row,
   final-worktree, and dependency-registration checks it claims; and
4. the short pre-decision document omits mandatory finite-record sections,
   while each owner row omits its exact coordinate tuple and evidence
   reference.

Hexalith.Projects restored Architecture to the accepted `3.70.1` baseline and
reopened P1R, P0 Stage 1, DW-35, and DW-68. Attempts 21--23 and pre-decision
SHA-256
`477949fc59d0cdd3a00d0c46de7aada46164074675c16c253b2015db369c0bab`
remain preserved as invalidated history. Remediation must formally supersede
the false coordinate row, rerun the remote proof with singleton-exact direct
declarations and a missing-direct negative, retain complete verification, and
render the full finite record before four fresh decisions. No push,
publication, release, dependency update, or live-checkout mutation is
authorized.

## Review-2 remediation technical record — attempts 24–27 — 2026-09-21

Attempts 24–26 retain three stop-first harness boundaries without rewriting
prior evidence. Attempt 24 stopped before its first ledger row because the
shared qualification cache root did not yet exist. Attempt 25 passed the
root/cache preflight and stopped before its first worktree because a retained
standalone command lacked the exported worktree-plan path. Attempt 26 passed
preflight, all 18 exact-revision worktrees, formal coordinate supersession,
the 14-declaration positive check, and the 13-declaration missing-Contracts
negative check. Its official-only restore exited zero, then a postcondition
stopped because NuGet records `PackageDownload` under framework
`downloadDependencies`, not the `libraries` map. Every failed attempt cleaned
its run root and registrations; none contributed a reused row.

Attempt 27 corrected only that evidence-shape assertion. It formally
supersedes attempt 21's known-false `coordinates-001-selected` PASS row with
fully retained command implementation bytes and actual catalog-introducing
revision `dae84d5f96911517eb1e6f97e75eb88e759e6d8a`. The evaluated external
consumer contains exactly the 14 release-manifest IDs and every declaration is
singleton-exact `[3.106.0]`. A retained negative removes direct
`Hexalith.EventStore.Contracts`; the exact-set checker fails as required while
a separate official-only empty-cache restore still resolves Contracts
transitively. The Admin CLI `PackageDownload` is verified through its evaluated
download dependency and retained package SHA-256/SHA-512 metadata.

The authoritative ledger contains 65 unique PASS rows. Its explicit phase
contract contains 63 exact row IDs: 51 unchanged attempt-21 rows admitted only
after all 58 original binding inputs were rehashed, plus 12 superseding or new
proof rows. The record validates both canonical disk-backed roots, exact
Playwright `1.62.0` browser identity, the Architecture prestate's semantic
`3.70.1` binding, clean final status/HEAD/describe for all 18 repository and
dependency worktrees, and absence from every owning repository's worktree
registration list after cleanup.

The complete eleven-section pre-decision record is retained at
`Hexalith.Projects:_bmad-output/implementation-artifacts/qualification-evidence/6-1-p1r-3106-20260921-attempt27/finite-record-pre-decision.md`
with SHA-256
`cd7549186cee5414b4cf6ac64bc96419ebdfd59188131d3d0d620da2bf9a93bd`.
This corrected pre-decision seal expands the selected EventStore, introducing
and qualifying Builds, rollback EventStore, and rollback Builds revisions to
their full 40-character values in every pending owner row, with an explicit
reference to `finite-record-pre-decision.md`. Technical evidence is unchanged.
The authoritative ledger SHA-256 is
`e6ae33908f5b1a3df51486f09f90ad21824e7061d54b0677f0c1be9770275451`;
the exact phase ledger SHA-256 is
`5aee549e07bcc0299185cf76bb9c878bef55e4ed133deb2a12999ede002f45c7`;
and the attempt-27 artifact-manifest SHA-256 is
`21ef32c0d2e04059666fa6aa4daea3662cbcb1494dac8dfec95c5398bc4585b1`.

Technical qualification is **PASS**. Four fresh owner decisions are
**pending (0/4)** and must name the exact pre-decision SHA-256 above. Therefore
Architecture remains on accepted EventStore `3.70.1`; P1R, P0 Stage 1,
DW-35, and DW-68 remain open; all downstream gates retain their blockers. No
push, publication, release, dependency update, live-checkout mutation,
acceptance inference, or propagation occurred.
