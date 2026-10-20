---
title: '6.1-P0 published 4.35.0 tool-source proposal'
status: proposed
runtime_selection_unchanged: true
live_qualification_started: false
p0_accepted: false
---

# Published tool-source proposal — 2026-10-20

Recommend rebasing tool delivery to **both published tools 4.35.0**, source/tag
`fff4a212180d8cd1c5c0759655a9bc4fb6963c45`, while retaining the approved
EventStore **3.126.0** / FrontComposer **4.7.0** runtime preparation tuple.
This is a new tool-source decision. The existing approved local
`4.33.2-p0.20261020.1` build at `e40ed9c7110d56a2f38c9291627728aea2f205cb`
remains the active preparation target until this proposal is approved.
The original approved proposal and its bytes are preserved.

The catalog changed during qualification. Fresh NuGet indexes now list both tools
at 4.35.0; the Git tag and both nuspecs identify the full producer revision above.
Both downloaded archives have valid NuGet repository signatures. Their embedded
catalogs agree on every selected runtime field. This does not establish P0 acceptance.

| Role | Exact proposed identity |
| --- | --- |
| Hexalith.Builds.Module.Cli | 4.35.0; signed archive SHA-256 `2b2f0689f38c8a20efa823bd3dc277b4bbe785b03c62513878b9e4c261cb2880` |
| Hexalith.Builds.Evidence.Cli | 4.35.0; signed archive SHA-256 `cd860844beeaa58be034abbd198326cd1bb8abad86bdd74da03ba66a8346fa96` |
| Tool producer/tag source | `fff4a212180d8cd1c5c0759655a9bc4fb6963c45` |
| Proposed G-6 Projects source | `3f5ab317d1d1dbfafd6102308258818a84e0a322` in `/home/administrator/projects/hexalith/p0-4350-proposal-20261020` |
| Proposed G-6 Builds source/execution | `fff4a212180d8cd1c5c0759655a9bc4fb6963c45` |
| Proposed G-6 EventStore source/verifier | `51422dff60f7f1db06f5356efea2548c45c370d7`; production source unchanged from accepted G-6 verifier `47def45665312410b73fa12cb047c6bbaa431cf2` |
| Published EventStore runtime | 3.126.0; `fcf78d6cafb6c0c9f52dc350a9a09aff54ded288` |
| Runtime construction Builds | `1dfc39e3a3e6ab1c8ce95138490b39d76e7feb2c` |
| Runtime CI / release-action Builds | `5c04dab754e269f52306b9582cacd7cf1dc1540f` / `22a578b576a515d2af214fe81859447fffc97981` |
| Published FrontComposer runtime | 4.7.0; producer `cf5fe8da100bd5224a0d7f597fc92e1ebb98a5f4` |
| SDK / runtimes | SDK 10.0.402 (`a8dd0577c5c13f43a2f57fe33b4ff11b90f6371e`); .NET and ASP.NET Core 10.0.13 (`4099f83977f4d29604a9aeb971fb0a7bc1d5f699`) |
| Aspire | SDK/Hosting/CLI 13.6.1; CLI revision `3751e615ad660621f2a7aa4c390ef48e08a0eed8`; Toolkit 13.6.1-beta.935 |
| Dapr | CLI 1.18.0; runtime 1.18.2; .NET packages 1.18.10 |
| Other controlled packages | Fluent UI Components/Icons 5.0.1; NSubstitute 6.2.0; Fluxor 6.11.0 |
| Runtime images | Existing approved Dapr Linux/amd64 `sha256:1146cf75f6a900f098489f995bcb2145bf9b040f87ab70eb0de2db21000d01bb`; P0 Redis `sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf`; P1R PostgreSQL `sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636` |

The proposed root commits only two gitlink changes and the matching workflow
execution pins. All twelve declared dependencies are present at explicit revisions,
indexed in `published-4350-proposal-workspace.json`. Historical documents outside
the G-6 material scopes use sparse checkout; all 8,283 tracked material files are
hydrated. Git refs and indexes are independent; immutable object files share disk
storage with task-owned frozen clones. No nested dependency was initialized.
All eleven selected graphs restore, and the restored static audit reports the
approved runtime tuple with zero issues. No new G-6 live result exists.

G-6 retains its existing auxiliary pub/sub `redis:7.4` tag; it has no immutable
launch-digest proof and does not replace P0's pinned Redis proof. P1R retains its
separate pub/sub Redis `sha256:c35b83ce044bb6d148c484d36e059ad28e02d5714ba6731fb55b6421e2ed0ccf`
and private SDK 10.0.402 image
`sha256:3a48280a9a7c6276c3eeaba1efa6a08820f18c84fb9e6274c187719ecfaf9fbc`
(Ubuntu 24.04.5/glibc 2.39, .NET/ASP.NET Core 10.0.13). The outer host is Ubuntu
26.04/Linux x64. No helper image or support scope is silently substituted.

## Evidence and changed behavior

The [release](https://github.com/Hexalith/Hexalith.Builds/releases/tag/v4.35.0),
[exact-source CI](https://github.com/Hexalith/Hexalith.Builds/actions/runs/39426403061)
and [release workflow](https://github.com/Hexalith/Hexalith.Builds/actions/runs/39431435493)
all identify the observed 4.35.0 delivery. The release inventory SHA-256 is
`26c1251938fbebaae5cf7d72306980fc82cee340779adafa450c3554ca5d86ff`.
Its source tree is clean at the producer revision; source/package controls passed.
Its `releaseEligible` applies to that contract gate, not persisted P0 acceptance.

Actual release-job lines verify remote 4.35.0 archives against the qualified local
archives through the publisher's uncompressed-entry comparison, excluding the
NuGet signature. Qualified local ZIP hashes differ from signed remote ZIP hashes:
Module `48175e5669d1f9eb1c74ce0fff6a06de09e4ae24e6fc6741f98e2dfc2003d20b`,
Evidence `984443b718ebefb0e4e9e8daff9fc2f7d9ca15b9584d9b001e4f71296553a0a5`.
These producer gate observations do not replace independent empty-cache consumer,
loaded-identity, installed-command parity and persisted/native evidence.
Synthetic 9.8.x publisher-control lines in the same job are not real 4.35.0 proof.

Compared with qualified preparation source `e40ed9c7110d56a2f38c9291627728aea2f205cb`,
4.35.0 adds domain enrollment validation: eight production files, 631 added and
three removed lines, including `ModuleCommandApplication` changes and new platform
adoption/inventory validators. The embedded runtime tuple is unchanged. Different
source and assembly bytes require affected qualification; the earlier local package
gate is not inherited. Earlier published 4.34.0 at
`5d03f1db9c38db34ecac7fe6048937478ffb82cc` predates the v1 schema/alias repair,
and is not a qualified alternative to the approved preparation.

Machine evidence lives under [the continuation bundle](../../evidence/g4/approved-tuple-20261020/README.md):
`published-tools-4350-{observation,embedded-catalogs,signatures,tag-primary,release-primary,workflow-primary,release-inventory,release-log-identities}.json`,
`published-4350-proposal-{freeze,material-hydration,graph-restores,static-audit-restored}.json`.

## Compatibility gaps, rollback and decisions

EventStore 3.126.0 and its capabilities have not changed. Published D1 origin
context/dynamic binding and the aggregate 256 bound, Conversations narrow metadata,
File evidence/provider/native revision contracts and the RTI source/checkpoint/head
proof remain delivery work under the existing D1–D6 decisions. Folders source calls
projection endpoint helpers absent from published DomainService 3.126.0. Passing a
source AppHost does not establish that package compatibility. This tool-source
choice supplies no substitute APIs, no future runtime version, and no v2 migration.

The corrected 3.126.0 P1R campaign continues independently from full verifier source
`51422dff60f7f1db06f5356efea2548c45c370d7`, qualification Builds
`e40ed9c7110d56a2f38c9291627728aea2f205cb`, root
`a107d736c5991b2753655d5afd2a90198fd6dbe0`. These are verifier roles,
not the proposed tool-producer role. Its runtime archives/source are unchanged.
Original failed measurements remain preserved.

The user's accepted G-6 artifact `287663d2a1d5ae25acf64e27a9984780062f26aaa4b8e71a972c73ead4488a08`
remains bound to Projects `83c4802be3626560e53d4ef88aa78647e89df589` and Builds
`e40ed9c7110d56a2f38c9291627728aea2f205cb`. It does not qualify this proposed source.
Approval here authorizes the affected tool/G-6 qualification; the new exact G-6
result still needs the named Builds, Platform and FrontComposer/Web assessment.

There is no accepted prior P0 tool pin and no qualified capable data downgrade.
Keep the greenfield Builds boundary `8e0e2da5e1eff07468b41d85d97979c96c2ac975`
and Projects AD-17 drain/freeze/forward recovery. No old package is promoted to a
rollback target by this proposal.

The genuine decision now is whether to adopt this published tool-source tuple or
retain the approved local preparation and defer the final delivered source. Do not
repeat the approved runtime/SDK exceptions or D1–D6 design votes. P1R's measured
support disposition, four role decisions and same-baseline conformance follow its
completed audit; technical failures cannot be waived into a pass. Final P0 owner
acceptance follows actual installed/native/persisted/negative/cleanup/publication/
rollback evidence. Projects consumer adoption follows accepted P0 and the needed
released owner APIs. Neither final acceptance is supplied by this target choice.
