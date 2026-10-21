---
work_package_id: 6.1-P0
artifact_kind: tool-runtime-tuple-proposal
created: 2026-10-20
status: proposed-awaiting-owner-selection
repository_authority: Hexalith/Hexalith.Builds
live_qualification_authorized: false
acceptance_claimed: false
consumer_adoption_authorized: false
---

# P0 tool/runtime tuple proposal and matching G-6

Recommend preparing a new lockstep tool build for the current catalog, retaining
the existing P0 v1 acceptance contract. Proposed local qualification version:
**`4.33.2-p0.20261020.1`** for both tools, EventStore **`3.126.0`**, FrontComposer
**`4.7.0`**, SDK **`10.0.402`**, Aspire **`13.6.1`**, Toolkit
**`13.6.1-beta.935`**, Dapr CLI/runtime/.NET **`1.18.0`/`1.18.2`/`1.18.10`**.
This is an explicit proposal, not a selection, published version, completed
qualification, or authorization to start live work.

There is **no complete, qualified current delivery tuple available**. The exact
repair base below exists, but v1 contract/catalog disagreement requires source
work before the final delivery SHA can be named. Approval of the target versions
can authorize that preparation; live qualification additionally needs the final
reviewed SHA, matching usable P1R, and matching G-6. Do not substitute a future
`main` or invent a final SHA. A repair producing different bytes supersedes the
base tuple and must be bound and reviewed before live execution.

The current catalog is suitable as a concrete **P0 infrastructure target**. It
does not contain every API required by resolution prerequisites. Completing P0
at this baseline and later adopting a capable successor incurs another affected
qualification. If owners require one final resolution-capable package tuple,
defer final selection until the missing owner contracts and EventStore release
exist; a truthful exact version for that release cannot be supplied today.

## Evidence and scope

The [static observation bundle](../../evidence/g4/tuple-proposal-20261020/observations.json)
records fresh NuGet indexes/nuspec provenance, two downloaded tool archive hashes,
embedded catalogs, checkout identities, and hashes of prerequisite records.
[API inspection](../../evidence/g4/tuple-proposal-20261020/api-inspection.json)
contains nine successful `dotnet-inspect` queries, including a published API
comparison. [Source comparisons](../../evidence/g4/tuple-proposal-20261020/source-comparison.json)
separate tagged production source from newer source repairs. These are static
[Runtime archive observations](../../evidence/g4/tuple-proposal-20261020/runtime-archive-observations.json)
also bind SHA-256 and signed-ZIP SHA-512 for the seven proposed EventStore
packages. These hash kinds do not stand in for NuGet restore content hashes.
No restored/locked/loaded application graph, signature validation,
container launch, live qualification, publication, approval, or adoption is claimed.

All changes in this dispatch are proposal/evidence documentation in Builds.
Projects has concurrent authority-validator work; EventStore has concurrent
qualification work. Preserve both. The bundle records the inspected checkout
state; it is not evidence of clean final delivery.

## Historical, measured, and available tuples

| Coordinate | Exact identity | Evidence and applicability |
| --- | --- | --- |
| Historical tools | Both `Hexalith.Builds.Module.Cli` and `Hexalith.Builds.Evidence.Cli` `4.29.1`; source `21ce044ab465ccb2adab58b3d66e394ffbecf3c2` | [Published controls](../../evidence/g4/published-4.29.1-20261001/README.md): 97 synthetic controls, remote restore, six help contracts, 233 fixture bindings. Full P0 acceptance absent; original unsigned qualified archives versus signed remote payload equivalence remains unproven. |
| Historical EventStore | `3.110.0`, tag `v3.110.0`, source `27279fe6431925a6ea046c3f89af61487185c7de`; accepted Builds `21ce044ab465ccb2adab58b3d66e394ffbecf3c2` | [P1R record](../../../projects/_bmad-output/implementation-artifacts/6-1-p1r-acceptance.json), dated `2026-10-01T06:17:01Z`, is an exact historical acceptance. It does not establish current P1R usability, current G-6, or P0. |
| Measured candidate | EventStore `3.125.1`, tag/source `55432c50d4b6f1666b4bd6d4a60bfc263b2d0e0b`; tag-construction Builds `4629ff2d3bd66376b29a553663c241d3a7b87853` | [Canonical V4 index](../../../eventstore/_bmad-output/implementation-artifacts/evidence/6-1-p1r-31251-published-run/final-attempt/canonical-v4-index.json) and [independent audit](../../../eventstore/_bmad-output/implementation-artifacts/evidence/6-1-p1r-31251-published-run/reconciliation/selected-final-parent-audit-9895165b155e48f2852f55b91c9bea35.json): 19 lanes/163 cases; 4,804 assertions, 4,795 passed, nine failed. Source snapshot-tail checks account for six failures, post-upgrade restore for three; separate restore 10/12, cleanup 7/7, Reminder 149/149, evolution 193/193. Command/audit exit zero is not qualification. `qualified=false`, `p1r_usable=false`. |
| `3.125.1` provenance roles | Observed package checkout Builds `4629ff2d3bd66376b29a553663c241d3a7b87853`; CI workflow Builds `13a3316ad0b93e25bf273efca00be36154b327c2`; release execution Builds `22a578b576a515d2af214fe81859447fffc97981` | [Projects handoff](../../../projects/_bmad-output/implementation-artifacts/spec-6-1-p1r-31251-published-qualification-handoff.md) distinguishes these observations. Its earlier draft hold predates execution; use the canonical V4 results for measured outcomes. Do not collapse construction, verifier, source, or execution identities. |
| Latest observed published tools | Both `4.33.1`, tag/nuspec source `c6f56c011ce3a83cdf6100c322352daf125c6af9` | Fresh indexes and nuspecs agree. Both downloaded tooling assemblies embed EventStore `3.125.1`, FrontComposer `4.6.0`, Aspire `13.6.1`, Toolkit `13.6.1-beta.935`. Updating a checkout catalog cannot rewrite these archives. |
| Current catalog EventStore | `3.126.0`, tag/nuspec source `fcf78d6cafb6c0c9f52dc350a9a09aff54ded288`; tag-construction Builds `1dfc39e3a3e6ab1c8ce95138490b39d76e7feb2c` | Fresh Client/Contracts/DomainService/ServiceDefaults/Gateway/Server/Aspire nuspecs agree on source. This is publication metadata, not a new P1R result. Do not inherit `3.125.1` qualification, enrollment, or support dispositions. |

The `3.125.1` [whole-host preservation observation](../../../eventstore/_bmad-output/implementation-artifacts/evidence/6-1-p1r-31251-published-run/final-attempt/outer-shared-after-canonical-v4.json)
also remains nonpassing. Owned-resource cleanup does not erase unrelated-host
changes. Support-boundary disposition, four-role acceptance and same-baseline
conformance remain outstanding. Do not claim that all 19 measured lanes passed
or offer owner signatures as a substitute for resolving their technical findings.

Fresh downloaded `4.33.1` signed ZIP SHA-256 observations are:

- Module: `8689159ad33dd597f723c19567c6aee78b0585cffaf0d0f8d345fb857d49e530`.
- Evidence: `d8c8cb574307c729eff7b4c679b4150da8bd8a8653949b1d5b3e8c65d06fd271`.

These are archive identities only. NuGet signatures were not verified in this
dispatch; hashes do not establish source/package equivalence or acceptance.

## Proposed exact preparation tuple

| Role | Proposed coordinate |
| --- | --- |
| Tool IDs/version | `Hexalith.Builds.Module.Cli` = `Hexalith.Builds.Evidence.Cli` = `4.33.2-p0.20261020.1`, local qualification build only; not present in the observed indexes |
| Tool source repair base | Builds `5d03f1db9c38db34ecac7fe6048937478ffb82cc`, already checked out at the Projects root-declared Builds gitlink; freeze it as the repair base, not the uncreated repaired delivery commit |
| Owning documentation checkout | Builds `b435f2e3b3892e03f69322d24de6003b0baf7a7d`; earlier than the Projects Builds checkout; this dispatch does not move either checkout |
| Runtime package family | Client, Contracts, DomainService, ServiceDefaults, Gateway, Server, Aspire `3.126.0`; source/tag `fcf78d6cafb6c0c9f52dc350a9a09aff54ded288`; qualify actual transitive EventStore closure at that version |
| UI packages | FrontComposer Shell/Contracts `4.7.0`, release source `cf5fe8da100bd5224a0d7f597fc92e1ebb98a5f4` |
| SDK and orchestration | .NET SDK `10.0.402`; Aspire.AppHost.Sdk/Aspire.Hosting/Aspire.Cli `13.6.1`; CommunityToolkit.Aspire.Hosting.Dapr `13.6.1-beta.935` |
| Managed runtime/OS | Microsoft.NETCore.App and Microsoft.AspNetCore.App `10.0.13`, Linux x64; observed host Ubuntu `26.04`. Verify actual child runtime selection in both lanes; the SDK pin alone does not bind it. |
| Dapr | CLI `1.18.0`; runtime/placement/scheduler `1.18.2`; selected Dapr .NET packages `1.18.10` |
| Dapr control-plane image | `daprio/dapr@sha256:1146cf75f6a900f098489f995bcb2145bf9b040f87ab70eb0de2db21000d01bb`, Linux amd64 manifest observed for `1.18.2`; multi-platform index `sha256:9ec89d30076155d2376c06f98028b6920f31fac5df0eb40e0b8b94cc88dd5b59` |
| Other G-6 controlled packages | Fluent UI Components/Icons `5.0.1`; NSubstitute `6.2.0`; Fluxor/Fluxor.Blazor.Web `6.11.0` |
| P0 persistence | Existing runner-owned Redis `docker.io/library/redis:7.4-alpine@sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf`; preserve the existing two-module persisted fixture |
| G-6/P1R persistence | Existing isolated PostgreSQL fixture image `postgres@sha256:a02db8cac496f15b094798a38254f14d6e00741f709360e5e00bb6668ea31636`; PostgreSQL proof does not replace the runner's Redis proof |
| P0 contracts | Existing `hexalith.module-manifest.v1`, `hexalith.module-run-evidence.v1`, `hexalith.readiness-evidence.v1`, `hexalith.g4-p0-acceptance.v1`; proposed current runtime constants require the compatibility repair below |
| Feed/promotion | First qualify exact local Release-built packages. Any prerelease publication follows the existing GitHub Packages policy; stable publication uses NuGet.org and the actual semantic-release result. The local qualification label reserves no stable version. |

The exact local label prevents “latest” restore and fits the observed Platform
range `[4.33.1, 5.0.0-0)`. It is a proposed test artifact identity, not an assertion
that semantic-release will publish `4.33.2`. Freeze the final source commit,
signed/unsigned archive hashes and actual published lockstep version at delivery.
Do not relabel rebuilt bytes with an already published version.

Keep these additional identities separate:

- Projects observed HEAD: `76844f4eb38c6e3d2938ba025fab7d94080db871`.
- Projects EventStore source gitlink: `6c56c27bc1a8d63c83f9631f7162cf79fdde0a1e`;
  this contains newer production source than the `3.126.0` package source.
- Platform observed gitlink: `bb81fc9e98533246cfeed43ffcce08866a2a57f6`;
  its own construction Builds pin: `24322f56177b8fe86a78b2be7125f873c0a682bd`.
  Read that git object for catalog attribution; do not initialize a nested Builds.
- Projects CI workflow execution pin: `51af786cf156d2a3396dbd49f5e4898222e55c12`;
  release execution pin: `a07078ad74d3727bc5a6b6d85d47d56a6e5c9fec`.
  Neither becomes the proposed tool source by implication. Review/align the
  intended qualification execution source through its owning change.

All twelve current Projects direct gitlinks are retained in the static audit.
The final clean Projects qualification commit will differ after authorized
policy/adoption changes; capture its full hash then, rather than predicting it.

## Compatibility repairs and the resolution API boundary

| Boundary | Demonstrated availability/gap | Required treatment |
| --- | --- | --- |
| v1 manifest versus runtime/acceptance | `ModuleManifestLoader`, v1 schema and fixtures retain EventStore `3.120.0`/FrontComposer `4.6.0`. `SupportedPlatformPins` uses the build-embedded catalog; `G4P0AcceptanceValidator` compares against that catalog. Published `4.33.1` embeds `3.125.1`/`4.6.0`; a current rebuild embeds `3.126.0`/`4.7.0`. | Reconcile current executable fixture, schema, loader, evidence and acceptance checks to the selected tuple. Preserve old schema/fixture evidence as historical bytes. Add a control rejecting disagreement; regenerate only new candidate evidence. Final source SHA follows this repair. Simply rebuilding is insufficient. |
| v1 versus Platform v2 | `4.33.1` contains both routes. v2 uses the pinned Platform model and `ModuleTestSelection`; `--filter` is refused with `HXC002`. P0 acceptance v1 still binds the v1 manifest/native/persisted contract. | Recommendation: finish existing P0 using its v1 contract; no implicit switch to v2. A v2 P0 migration is a separate owner contract decision and must preserve all P0 assertions. Projects may use only a separately qualified profile/manifest pairing. |
| Existing query transport | Published `3.125.1` and `3.126.0` provide `DelegatedOwnerQueryClient.InvokeAsync(SubmitQueryRequest, CancellationToken) -> Task<QueryResult>`. `VerifiedQueryCaller` exposes Query, OriginalQuery, Principal. | Reuse the SDK transport; do not add a Projects transport or hydrate owner detail to simulate metadata. |
| Production digest/verified origin (D1) | Published `3.126.0` still emits digest-only query bindings, has no `OriginatingQueryContext` API and no `VerifiedQueryCaller.OriginatingContext`. Current EventStore source `6c56c27bc1a8d63c83f9631f7162cf79fdde0a1e` adds Tenant/domain bindings, `WorkloadAuthorityGrant.AllowQueryAuthorityDigest`, origin context and its SDK accessor. | Source implementation is present; published delivery, production-path proof, native mapping/enrollment and owner acceptance are not established here. Qualify and release these repairs under their owner; pin that actual release before resolution consumer adoption. Source-mode success cannot qualify the `3.126.0` archive. |
| Aggregate proof bound (D4) | Published `3.126.0` `BoundOwnerQueryProof.Scope` rejects aggregate/principal IDs over 200. Newer source raises aggregate only to 256, retaining principal 200. | Require the tested released repair and 200/201/256/257 controls; do not silently widen principals or use a surrogate ID. |
| Conversations metadata (D5/D6) | Catalog Contracts/Client `1.2.0`, source `7f67adb8bb67b668dd37d64e9d4795e37dcfb758`. Published contracts contain no `ConversationResolutionMetadata*` types. Current source still lacks the proposed narrow read. | Deliver the content-free key/read/handler and native resource/permission freshness under the existing Conversations owner work. Full `GetConversationAsync` detail is unsuitable. A new published version is not yet nameable. |
| Folders/File evidence (D2/D6) | Contracts `1.3.0`, source `efb98b1e5215ad73b4591f2a2a8ff00f8b8c794a`; `FolderOwnerReadResult` has Outcome only. No published `FolderFileOwnerRead*` contract. | Name the actual stable File provider/mapping and deliver compatible optional evidence or an additive version, with native revisions and independent resource authorization. Do not equate Projects FileReferenceId with an owner File ID. |
| RTI (D3) | Published Client `3.126.0` supplies `IReadModelStore.GetAsync<T>(storeName,key,token) -> Task<ReadModelEntry<T>>`, including ETag. This is storage, not a complete RTI feed/head certificate. | Projects must identify owner source/checkpoint contracts, initialized-empty and complete-generation proof and implement the sole RTI. Query execution performs no ingestion/repair. |

The published Contracts query API diff `3.125.1..3.126.0` reports no API changes;
it does not establish equivalent behavior. The existing
[D1–D6 decision record](../../../projects/_bmad-output/implementation-artifacts/6-4-prerequisite-decisions-2026-10-20.md)
already selects the designs. Do not request those decisions again. The missing
File/feed contracts, implementation, publication and measurements are delivery
work, not six new approval questions.

## Matching G-6 proposal and execution gates

Use the exact proposed SDK/Aspire/Toolkit/Dapr/Fluent UI/NSubstitute/Fluxor tuple
above in a new reviewed current-policy disposition. The existing approved
`Tools/g6-current-policy.json` instead specifies SDK `10.0.401`, Aspire SDK/CLI
`13.6.0`, Toolkit `13.6.0-preview.1.261001-0243`, Fluent UI `5.0.0`. Its dated
approval cannot be copied onto the proposed tuple.

Fresh read-only command, from this Builds checkout:

```text
python3 Tools/g6_current.py audit --workspace /home/administrator/projects/hexalith/projects --policy /home/administrator/projects/hexalith/projects/references/Hexalith.Builds/Tools/g6-current-policy.json --out /tmp/p0-tuple-g6-static-audit-20261020.json
```

Exit `0` means the audit emitted data. [Actual result](../../evidence/g4/tuple-proposal-20261020/g6-static-audit.json):
**`tupleApproved=false`, 56 issues**. SDK is `10.0.402`, Toolkit is `.935`, UI is
`5.0.1`; Projects AppHost still pins Aspire `13.6.0`, so the audit's effective
Aspire SDK/CLI remains `13.6.0` while the catalog, other consumers and Platform
use `13.6.1`. Missing restored graphs and incompatible resolved/effective pins
remain recorded. The globally installed Aspire CLI is `13.5.3`; future execution
must use isolated `13.6.1`. No restore or live run was started by this audit.

Preparation must align the Projects AppHost/CLI execution and the policy with
the chosen `13.6.1` target, review all eleven controlled consumers and their
restored graphs, and capture clean source/gitlink closure. Audit the catalog's
conditional **Folders.Aspire Toolkit `13.0.0`** separately; it must not replace
`.935` in the runner's graph. Retain exact preview dependencies such as
`Aspire.Hosting.Keycloak` `13.6.1-preview.1.26506.6` in the graph; dependency
resolution does not qualify Keycloak runtime or deployment.

Named Builds, Platform and FrontComposer/Web owners must explicitly dispose
the `.935` prerelease and the Dapr `1.18.2`/`.NET 1.18.10` support exception for
this tuple. Keep `Dapr.Workflow` unselected and `reuseEnabled=false`. Preserve
historical policies/packets; never reseal old evidence as current.

After tuple/source authorization, execute the existing Projects
`tools/qualification/run_g6_current.py` with a fresh output directory and the
reviewed policy; validate through `Tools/g6_current.py validate` against that
same clean workspace. Require actual isolated PostgreSQL two-sidecar
stop/restart **1/1**, **33/33** deterministic cases over the **21** selectors,
strict EventStore capture validation, command/report hashes and owned cleanup.
Capture Dapr binary hashes, registry manifest/repository digests and actual
loaded/restored versions; do not treat a mutable image tag or image config ID
as an immutable registry digest. The [read-only registry observation](../../evidence/g4/tuple-proposal-20261020/dapr-registry-manifest.json)
binds the proposed Linux amd64 control-plane manifest; using it in the existing
qualifier requires replacing its tag-only selection in the reviewed preparation.
Use run-owned control planes and the existing
private SQLite resolver containment; never swap shared control planes. A named
decision accepts the resulting exact packet/result hash after inspection.

G-6 is source/toolchain evidence only. P1R must separately qualify the actual
selected published EventStore archives and source comparison. `3.126.0` requires
explicit finite enrollment, source/construction/verifier Builds bindings and
the full applicable contract, including recovery/evolution additions; existing
`3.125.1` enrollment is not permission to substitute versions. Keep all
observed `3.125.1` failures. Neither prerequisite is satisfied by a passing P0
unit suite, a current catalog audit, or a production-source change alone.

## Qualification, acceptance and Projects adoption after authorization

1. Complete the compatibility repairs, freeze the actual delivery SHA and
   candidate inventory, and review that exact closure. Obtain usable P1R and
   matching accepted G-6 before P0 live qualification. A changed controlled
   version/source/fixture requires affected evidence again.
2. Continue existing Stages 6–7; retain Stages 1–5 as historical completed work.
   Run the existing package contract gate with `-RequireControls` and the exact
   proposed local version, individual test projects, clean Debug/source and
   Release/package lanes, all declared AD-25 orchestration classes and blocking
   negatives. Preserve failed attempts; zero/all-skipped/missing reports cannot
   pass. `full` and `full-mtp` must exercise actual persisted native reports.
3. Through installed tools, prove authenticated write/event/projection sequence,
   restart/rehydration, retry/idempotency, two-instance and cross-Tenant behavior,
   stale/wrong-sequence/missing persistence controls, cancellation and causal
   failures. Run identity-bound `down` twice and compare actual owned/shared
   resources before/after; retain metadata-only evidence and hashes.
4. Qualify and publish under existing release controls. Freeze the actual
   published version, construction/execution source identities and inventories;
   remotely restore both exact tools in an empty-cache disposable consumer.
   Verify signatures, compare qualified payloads independently of NuGet signing
   transformations, and prove duplicate/partial publication recovery. Repeat
   affected installed-command parity checks on the delivered bytes.
5. Exercise rollback below. Build the real `evidence/g4/6.1-p0-acceptance.json`
   only from actual artifacts and dated Builds Owner, Platform Owner and named
   Test Architect decisions. Validate it using the exact installed evidence
   tool. Synthetic acceptance fixtures never become owner evidence.
6. After accepted P0, implement Projects consumer adoption: exact lockstep
   `.config/dotnet-tools.json`, thin `module/hexalith-projects.module.json`, and
   approved reads/native profiles plus explicit CI package mode. Qualify that
   consumer's actual source/package tuple and revalidate the handoff guard.
   Do not copy a runner, remove its AppHost, change routing, or claim P4/Story
   6.1/6.4 acceptance from P0 alone. Resolution adoption additionally requires
   the released owner/API/RTI and exact-current P1R/P2/P3/P4 gates.

The proposed v1 reads command shape is `dotnet tool run hexalith-module test
--manifest module/hexalith-projects.module.json --profile reads --mode package`,
only after that real manifest/profile is delivered and accepted. Do not put
`--filter` on a Platform-v2 invocation; no command against a nonexistent reads
profile is evidence. The existing owner's package-gate script is an internal
qualification tool, not a second consumer entry point.

## Rollback and decisions still required

**P0 adoption rollback:** use the recorded run identity to execute idempotent
`down`, retain failed evidence, remove/revert only the disposable or authorized
consumer's tool/manifest adoption, and keep dependent work blocked. P0 has no
accepted previous tool release. `4.29.1` and `4.33.1` are not invented known-good
rollback pins. Retain the greenfield boundary `v4.19.2` /
`8e0e2da5e1eff07468b41d85d97979c96c2ac975`; never reset a developer checkout.

**Runtime/data recovery:** proposed capable downgrade rollback remains **null**.
Historical `3.70.1` / `f13f9925fdca53efa2ab8c90d396ab106f91bb9c` with Builds
`7af20f8bafbfe561df6f7705913a0800603090b5`, and historical accepted `3.110.0`,
are comparison evidence, not verified downgrade targets for new state. Preserve
the selected AD-17 drain/freeze/forward recovery direction: disable new consumer
operations, stop incompatible writers, retain state/evidence and recover forward
until a capable rollback is independently qualified. Tool rollback does not
roll back persisted EventStore formats.

| Genuine decision | Accountable owners | Concrete decision/evidence still needed |
| --- | --- | --- |
| Target and timing | Builds + EventStore/Platform + Projects/Solution owner | Authorize the explicit current-catalog preparation tuple and v1 compatibility repair, accepting later affected qualification for the released resolution APIs; or defer final tuple until those releases exist. Do not select `3.125.1` merely because it was measured. |
| Exact source and prerequisite scope | Builds + EventStore + Solution/Test owners | After repairs, approve the full delivery/verifier/execution revisions and actual archive identities; authorize new `3.126.0` P1R enrollment/qualification or select a genuinely capable later release. Resolve measured recovery/source/host-preservation findings and support boundary; supply genuine same-baseline conformance. |
| G-6 tuple/exceptions | Named Builds, Platform, FrontComposer/Web owners | Approve the exact proposed tuple, `.935` prerelease and Dapr support exception, isolated execution scope and source/graph alignment. After execution, decide the exact result hash; tuple approval is not result acceptance. |
| Final P0 acceptance | Builds Owner, Platform Owner, named Test Architect | Assess actual clean source/package/native/persisted/negative/cleanup/publication/rollback evidence, then date and sign the exact record. This decision cannot honestly be issued before results exist. |

No D1–D6 redesign vote, routine command approval, nested-submodule permission,
or permission to duplicate topology is needed. Missing API/provider/feed work
remains accountable delivery work. The user's instruction expressly requires
tuple approval before gated live qualification, so this proposal stops before
that gate while leaving qualification, acceptance and consumer adoption open.
