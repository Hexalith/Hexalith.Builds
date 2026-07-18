---
work_package_id: 6.1-P1
artifact_kind: normalization-record
created: 2026-07-18
authorized_by: Jerome (EventStore Owner, Builds Owner, Solution Architect)
status: accepted
repository_authority: "EventStore and Builds repositories; Architecture Spine through Solution-Architect approval"
---

# 6.1-P1 Normalization Record: EventStore Source, Architecture Spine, and Central Package Version

This is the finite, owner-approved normalization record required by Story 6.1-P1
(`Hexalith.Projects/_bmad-output/implementation-artifacts/6-1-p1-normalize-eventstore-source-architecture-central-package-versions.md`).
It resolves the three-way EventStore version disagreement (Architecture Spine
`3.67.3`, Builds `HexalithEventStoreVersion` `3.70.0`, checked-out EventStore
submodule past `3.70.0`) into one agreed source revision, package version, and
Architecture Spine pin.

## Accepted baseline

| Item | Value |
| --- | --- |
| EventStore source revision | `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` (tag `v3.70.1`) |
| EventStore package version | `3.70.1` (published to NuGet.org and GitHub Packages) |
| Builds `HexalithEventStoreVersion` | `3.70.1` (`Hexalith.Builds` commit `c074d0d`) |
| Architecture Spine `Stack` table entry | `3.70.1`, updated 2026-07-18 |
| Architecture Spine `G-1` gate text | Updated to reference `3.70.1` API evidence |
| 6.1-P0 G-4 runner manifest agreement | Confirmed: 6.1-P0 already pins Builds `HexalithEventStoreVersion=3.70.1`-line (no runner edit required) |

## Why 3.70.1, not the previously recorded 3.70.0 or 3.67.3

- The last actual EventStore release was `v3.70.0` (commit `97437cd6`), which
  the Builds central property already declared. However, that exact tagged
  revision fails to restore its own source tree: a duplicate/then-missing
  `PackageVersion` declaration for `Microsoft.Extensions.TimeProvider.Testing`
  produces `NU1506`/`NU1010` depending on the paired `Hexalith.Builds`
  submodule revision. `main` had been CI-red for 5 commits past `3.70.0` as a
  result, with no new release cut.
- Two additional CI failures on `main` were unrelated stale-test-vs-baseline
  mismatches: `CommitMessagePolicyTests` hard-asserted the old
  `.github/copilot-instructions.md` markdown-link format for
  `hexalith-llm-instructions.md`, which the new location-independent shared
  baseline (rolled out repository-wide 2026-07-17) no longer uses;
  `ReleasePackageManifestTests` checked `AGENTS.md`/`CLAUDE.md` for the
  package-inventory count, which the new baseline intentionally excludes from
  those universal entry points.
- Both were fixed in `Hexalith.EventStore` commit `650faf05` (pushed to
  `main`), CI went green, and semantic-release cut `v3.70.1` from that
  revision — the first revision that is simultaneously a real, published,
  tagged release **and** restores/builds/tests cleanly from a fresh checkout.
- No `latest`, floating branch, local patch, prerelease, or uncommitted
  working-tree checkout is used anywhere in this pin; `v3.70.1` is an
  immutable, published, root-declared submodule tag.

## Compatibility evidence (AD-14 read/query seams, AC3)

The exact public symbols/signatures Story 6.1 (FR-2, FR-5) depends on were
diffed across the full `v3.67.3..v3.70.1` range and are **byte-identical**:

- `Hexalith.EventStore.DomainService.IAsyncDomainProjectionHandler`
- `Hexalith.EventStore.Client.Projections.IReadModelStore`
- `Hexalith.EventStore.Client.Projections.IReadModelBatchStore`
- `Hexalith.EventStore.Client.Projections.ReadModelWritePolicy`
- `Hexalith.EventStore.DomainService.IDomainQueryHandler`
- `Hexalith.EventStore.Client.Queries.IQueryCursorCodec`
- `Hexalith.EventStore.Client.Queries.QueryCursorScope`

Everything else that changed in `Projections`/`Queries`/`DomainService` across
that range is additive (new `IReadModelBatchStagingStore`, extended
`ReadModelBatchProtocol`, dispatcher enhancements) — consistent with the
additive/serialization-tolerant contract rule; no removal or signature change
touches the AD-14 seams.

`Hexalith.EventStore.Contracts.Tests` (708 tests) passed locally at `v3.70.1`
with both root-declared submodules (`references/Hexalith.AI.Tools`,
`references/Hexalith.Builds`) initialized at their pinned revisions. EventStore
CI run [29611932848](https://github.com/Hexalith/Hexalith.EventStore/actions/runs/29611932848)
passed in full (`tenants-source-mode` + `build-and-test`, including Tier 1/2
unit and integration tests) at commit `650faf05`. Release run
[29612449076](https://github.com/Hexalith/Hexalith.EventStore/actions/runs/29612449076)
published all 14 manifest packages at `3.70.1` to NuGet.org and GitHub.

## Transitive package graph (AC4)

`Hexalith.Projects.Infrastructure.csproj` (the actual EventStore-consuming,
AD-14-seam-using project in the Story 6.1 read path) restores cleanly against
the updated Builds central property with no `NU1506`/`NU1010`/downgrade
warnings. A pre-existing, unrelated `NU1506` in `Hexalith.Memories`
(duplicate `Microsoft.Extensions.TimeProvider.Testing` /
`ModelContextProtocol.AspNetCore` `PackageVersion` entries, surfaced only when
restoring the full `Hexalith.Projects.Server.csproj` graph) is explicitly out
of scope per AC4 ("unrelated central catalog entries... remain explicitly out
of scope and are not silently touched") and is not addressed by this record.

## Rollback pin and regression evidence (AC8)

- **Rollback pin**: Architecture Spine `3.67.3` + `Hexalith.Builds` commit
  `12aaed6c` (`HexalithEventStoreVersion=3.67.3`) + EventStore tag `v3.67.3`
  — the last mutually-agreeing triple before this normalization.
- **Regression check**: the same byte-identical AD-14 diff above is symmetric
  evidence that no consumer of the prior `3.67.3`-pinned API surface breaks
  under `3.70.1` — every symbol/signature Story 6.1 (or any other 6.1-P1-era
  consumer) could have called against `3.67.3` still resolves identically
  against `3.70.1`.

## G-4 runner manifest agreement (AC6)

6.1-P0's frontmatter already records `latest_stable_at_authorization: 4.19.2`
and pins its own implementation to Builds baseline `edbaeaed`; as of this
record, `Hexalith.Builds` main is at post-`4.20.0` release commit `a14c776`
with `HexalithEventStoreVersion=3.70.1` (commit `c074d0d`). The runner
manifest itself is untouched by P1; this record only confirms the runner's
Builds baseline and this normalization's accepted EventStore version now
agree on `3.70.1`.

## Owner approvals

Jerome, acting as the named EventStore Owner, Builds Owner, and Solution
Architect, authorized this normalization on 2026-07-18 (superseding the
"cannot self-approve" blocker in the Projects-side 6.1-P1 handoff, which
required exactly this named-owner action before any task could begin).

## Completion boundary (AC9)

This record satisfies 6.1-P1 in full. It does **not** implement G-1/G-2
capability, the G-4 runner, non-EventStore G-6 prerelease gates, self-approve
6.1-P2/P3/P4, or mark Story 6.1 ready. The Projects-side 6.1-P1 handoff story
and `sprint-status.yaml` are updated to reference this record as the accepted
evidence for 6.1-P4 to consume.
