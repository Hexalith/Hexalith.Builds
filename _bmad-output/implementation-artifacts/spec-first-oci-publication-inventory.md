---
status: review
date: '2026-10-31'
baseline_commit: a22b55f0a4f6d2aaa3a5ccc505896064ac595364
---

# Admit an authoritative first OCI repository inventory

## Intent

The authorized Tenants6 release41211426470 passed exact-source CI, source-reference
integration and candidate preparation but stopped before tag creation/publication.
The shared release reader rejected the new tenants-api registry repository's
authenticated initial tag-list HTTP404 with OCI NAME_UNKNOWN. An existing
repository correctly returns HTTP200 with its tag inventory. The release must
understand first-publication absence without treating lookup failures as absence.

## File Scope

Allowed files for this story:

- `Github/publish-containers/publication_preflight.py`
- `Github/publish-containers/tests/test_publication_preflight.py`
- `Github/publish-containers/README.md`
- `_bmad-output/implementation-artifacts/spec-first-oci-publication-inventory.md`

## Contract

Only a configured authenticated initial tag-list HTTP404 whose bounded, valid,
duplicate-free JSON contains exactly one OCI NAME_UNKNOWN error contributes an
empty version inventory for that selected repository. A later paginated404,
generic/malformed404, missing authentication, authentication/authorization failure,
rate limit, server failure or transport failure remains blocking and support-safe.
Existing HTTP200 tag inventories and same-repository pagination remain unchanged.

An empty inventory does not replace exact destination-absence checks or alter
source, frozen identity, publication authority, version floors or overwrite refusal.
All code remains in the shared owning Builds reader. The consuming Tenants release
must adopt the exact reviewed execution SHA through its normal provenance gates.

## Verification

The original Builds checkout remains clean at a22b55f. Work uses a genuine isolated
detached worktree from origin/main and initializes no nested submodules.

The complete affected publication_preflight test file passes40/40 in0.121s.
Running the same40 tests against baseline source reproduces exactly the new
positive-control error; all39 other controls pass. Explicit cases cover401,403,
429,500,502,503, timeout/transport failures, malformed/generic/oversized/duplicate-key
404 bodies, missing authentication and a404 after a successful pagination page.
The existing200/pagination and source/identity/authority/collision gates pass.

A read-only actual registry observation with existing configured Docker credentials
proves tenants returns200/68 tags and tenants-api returns404/NAME_UNKNOWN. The new
reader accepts those inventories, its6.0.0 version-floor check passes and its
independent exact6.0.0 tag-absence checks pass for both repositories. No registry
write or secret disclosure occurs in this proof.

Evidence:

- `/tmp/builds-first-oci-publication-tests.log`
- `/tmp/builds-first-oci-baseline-regression.log`
- `/tmp/tenants-release-authenticated-registry-observations.json`
- `/tmp/builds-first-oci-actual-registry-proof.json`

Parent inspects the complete patch/proof before integration or push. Full owner CI
and the exact consuming release remain required after the owning commit.
