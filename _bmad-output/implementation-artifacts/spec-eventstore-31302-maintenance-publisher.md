---
title: Permit the exact EventStore 3.130.2 maintenance publication
status: in-progress
type: bugfix
route: dispatch
baseline_commit: 22a578b576a515d2af214fe81859447fffc97981
---

<frozen-after-approval reason="user authorizes separate qualified prerequisites and publication">

Publish the minimal SDK HTTP-verb reachability repair independently of newer
unqualified SDK features. Permit only repository Hexalith/Hexalith.EventStore,
branch 3.130.x, version 3.130.2, ci.yml and protected production. Preserve main
publication and every source, identity, inventory, authority, registry and phase
guard. Do not rewrite main, tags, generated publisher scripts or old receipts.

</frozen-after-approval>

The SDK's owning dispatch specification includes this canonical Builds change
as its approved immutable publisher prerequisite. Its three independent review
layers cover this diff jointly with the owning SDK workflow/wrapper changes.

The baseline is the SDK's already approved publisher execution commit. Apart
from publication_preflight.py, every executed publisher action/helper byte is
unchanged. No Builds product package release or original checkout change is
required. A focused normal push workflow executes the complete publisher Python
suite and the repository-pinned commitlint, without initializing submodules.

Code map: source policy and identity in
Github/publish-containers/publication_preflight.py; existing contract harness in
Github/publish-containers/tests/test_publication_preflight.py; consumer contract
in Github/publish-containers/README.md; focused exact-branch CI in
.github/workflows/eventstore-maintenance-publisher.yml.

Acceptance: the exact tuple survives verify/publish/container with a real
maintenance runtime identity and branch-specific CI proof. Wrong repository,
branch, version, prerelease, metadata, workflow, runtime ref or environment
refuses. Stale branch tips and main-only green receipts refuse. Existing main
and destination/authority/immutable-phase controls remain passing. Final owner
commit/push and exact-source focused CI must pass before SDK selects its new
publisher execution SHA. No new publication is claimed by local tests.

Local verification: complete container publisher suite passes135tests/0failures,
including finite tuple refusal, exact branch source proof, actual runtime ref,
production-only policy, and all three immutable publication phases. The joint
SDK/Builds independent review and exact-source remote CI remain pending.
