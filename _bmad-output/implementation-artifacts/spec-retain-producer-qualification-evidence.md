---
title: 'Retain and publish producer qualification evidence'
type: 'bugfix'
created: '2026-10-31'
status: 'done'
route: 'oneshot'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

Resolve the producer publication gap identified by Conversations attempt
`attempt-20261031-4383-03` for Builds source
`d1931b1e803144048d927fff16cb8119afde541e`. Recover retained byte matches and
verify all 55 inventory rows, clearly distinguishing content equality from
authenticated original-run provenance. When complete original carriers are
unavailable, retain future CI qualification outputs and publish the actual
release's inventory and declared evidence together in a verified archive with
explicit source, version and GitHub run identity. Prepare a reviewable additive
successor handoff with exact remaining publication actions.

Preserve release assets, inventories, accepted evidence, tool pins, gitlinks and
checkout revisions. Do not regenerate original evidence, initialize nested
submodules, bypass authentication, dispatch CI or publish. Keep Conversations
Story 16.1 draft/backlog and its implementation hold ACTIVE; broad regression
completion remains a separate gate. This is a Builds producer correction, not
implementation or completion of the held Conversations story.

</frozen-after-approval>

## Implementation Notes

- Builds owns every planned change. No Conversations tracked file is changed.
- No unresolved intent choice or irreversible action is needed for the local
  correction. Publication and CI dispatch remain separately authorized actions.
- Reuse the existing qualifier and its retained actual-version directory. Add
  one archive helper and focused tests; wire CI retention, pre-tag archive
  preparation, release assets, and an always-run Actions retention step.
- Historical Conversations Story 10.3/10.4 boundary contracts and final-record
  generation are not applicable to this freeform Builds change. Exact path,
  hash, inventory and preservation checks apply directly to this handoff.
- Baseline preservation snapshot: `/tmp/builds-4383-preservation-before.json`.
- No commit, branch, push or external publication is part of this task.
- Recovery found 50 byte-identical rows and five unavailable rows. All 55
  original-run provenance outcomes remain unverified/unavailable; original
  qualification remains BLOCKED. Exact ledger and source/run observations are
  in `evidence/g4/producer-publication-4383-20261031/`.
- Independent blind review identified publication-boundary carrier validation,
  atomic creation, current-source cleanliness and metadata/retention checks.
  These were patched within the producer scope; existing qualification and
  publisher semantics remain enforced.
- The publisher regression harness now creates offline fixture carriers and
  rejects missing/truncated carriers before any mocked package push. Fixtures
  are confined to its existing temporary test repository.

## Review Triage Log

| Finding | Verdict and disposition |
| --- | --- |
| Partial ZIP at final filename | medium; patched with staged write, fsync, atomic exclusive link, cleanup, and injected disk-failure test |
| Clean source only trusted from inventory | high; patched with live Git status recheck and dirty-source fault test |
| Publisher could omit missing/changed carrier | high; patched with exact archive/member/byte/run verification before first package push and publisher rejection cases |
| Unsupported job/event/ref accepted | medium; patched with exact workflow job/event/ref combinations |
| Ambiguous duplicate JSON properties | medium; patched with rejecting JSON property parser and fault test |
| Untyped clean/version fields | medium; patched with explicit boolean and version checks |
| Symlinked package ancestor accepted | medium; patched with ancestor checks and fault test |
| Failed attempt raw bytes lack metadata | medium; patched with always-run diagnostic attempt records, explicit no qualification credit |
| Workflow test could match unrelated always condition | low; patched to bind assertions to each named upload step |
| Authenticated capture receipts missing | medium; patched with timestamped API/download commands, raw responses, statuses and hashes |
| Numeric job ID absent from runner environment | low; handoff clarifies workflow job key in carrier and required authenticated numeric-job mapping in receiver's packet |

## Verification

- Local correction and handoff only: complete, internally reviewed and tested.
- Original 4.38.3 qualification: BLOCKED; 50 matching-content recoveries and five
  missing files do not establish original-run provenance.
- Final local validation and exact changed-path/preservation evidence are recorded
  in `evidence/g4/producer-publication-4383-20261031/validation.json`.
- Conversations Story 16.1 lifecycle and all authority/hold state remain unchanged.
- No commit, CI dispatch, package/release publication or external message was made.
