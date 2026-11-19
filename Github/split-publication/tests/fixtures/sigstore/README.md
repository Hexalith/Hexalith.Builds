# Sigstore verification fixtures

`github-reusable-workflow.json` is the public GitHub-issued bundle from the
[GitHub CLI test data](https://github.com/cli/cli/blob/acee710547dcf7dfb6490afea77972187e74b819/pkg/cmd/attestation/test/data/reusable-workflow-attestation.sigstore.json).
It exercises the shipped Public Good Instance root, Fulcio, SCT and Rekor
verification without network access.

The synthetic bundles use disposable P-256 signing, CA, certificate
transparency and Rekor keys. The matching synthetic trust root is used only
by the test's temporary copy of the shipped verifier. Each case has an SCT,
Rekor inclusion promise and a signed inclusion proof. The `invalid-signature`
case has valid log evidence for an invalid DSSE signature. The identity and
package mismatch cases are signed to exercise Builds validation after
successful cryptographic verification. The test root has no production trust.
