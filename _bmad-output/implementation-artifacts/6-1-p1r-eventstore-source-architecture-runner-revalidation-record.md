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
