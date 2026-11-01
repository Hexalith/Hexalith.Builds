---
title: Permit the exact EventStore 3.130.2 maintenance publication
status: in-progress
type: bugfix
route: dispatch
baseline_commit: 22a578b576a515d2af214fe81859447fffc97981
review_loop_iteration: 1
---

<frozen-after-approval reason="user authorizes separate qualified prerequisites and publication">

Publish the minimal SDK HTTP-verb reachability repair independently of newer
unqualified SDK features. Permit only repository Hexalith/Hexalith.EventStore,
branch release/parties-sdk-3.130.2, version 3.130.2, ci.yml and protected production. Preserve main
publication and every source, identity, inventory, authority, registry and phase
guard. Do not rewrite main, tags, generated publisher scripts or old receipts.

</frozen-after-approval>

The SDK's owning dispatch specification includes this canonical Builds change
as its approved immutable publisher prerequisite. Its three independent review
layers cover this diff jointly with the owning SDK workflow/wrapper changes.

The baseline is the SDK's already approved publisher execution commit. Apart
from publication_preflight.py and the actual publish-containers.sh entry point,
every executed publisher action/helper byte is unchanged. No Builds product package release or original checkout change is
required. A focused normal push workflow executes the complete publisher Python
suite and the repository-pinned commitlint, without initializing submodules.

Code map: source policy and identity in
Github/publish-containers/publication_preflight.py; existing contract harness in
Github/publish-containers/tests/test_publication_preflight.py; consumer contract
in Github/publish-containers/README.md; focused exact-branch CI in
.github/workflows/eventstore-maintenance-publisher.yml.
The actual shell source guard and its existing executable fixture are in
Github/publish-containers/publish-containers.sh and
Github/publish-containers/tests/test_publish_script_contract.py.

Acceptance: the exact tuple survives verify/publish/container with a real
maintenance runtime identity and branch-specific CI proof. Wrong repository,
branch, version, prerelease, metadata, workflow, runtime ref or environment
refuses. Stale branch tips and main-only green receipts refuse. Existing main
and destination/authority/immutable-phase controls remain passing. Final owner
commit/push and exact-source focused CI must pass before SDK selects its new
publisher execution SHA. No new publication is claimed by local tests.

Local verification: complete container publisher suite passes138tests/0failures,
including finite tuple refusal, exact branch source proof, actual runtime ref,
production-only policy, and all three immutable publication phases. The joint
SDK/Builds independent re-review and exact-source remote CI remain pending.

## Spec Change Log

The first joint review found that a preflight-only exception leaves the actual
shell source guard main-only. The wrapper phase tests and unchanged-helper blob
assertion hid that failure. Re-derive the complete canonical entry point with
the same finite tuple, keep every other existing guard, and add the actual
shell positive/refusal cases. KEEP the passing main behavior, strict canonical
Python policy, current-source proof, immutable phases, destination/authority
controls and all other action/validator/smoke bytes. This corrects the non-frozen
code map; the authorized maintenance intent is unchanged.

The actual shell regression passes on the candidate and fails against a private
copy of baseline22a at its main-only guard. Receipt:
/tmp/builds-eventstore-maintenance-shell-counterfactual.json. No original source
or publication state changed. The SDK's joint triage contains individual B1–B3
rows and the remaining SDK findings; its new three-layer review covers both
canonical publisher corrections together.

The second review's B6 correction freezes all three maintenance CI, commitlint
and Integration receipts and rechecks each in every phase. A real three-phase
test removes lint or Integration after publish and proves container refusal.
Main's source-proof shape and single-CI contract are unchanged. B9 validates
every new22a-to-source commit with pinned commitlint; executing the actual
workflow block passes our range and rejects an existing older range containing
an invalid earlier message despite a valid tip. No invalid commit was created.
Proof: /tmp/builds-eventstore-maintenance-range-proof.json.
