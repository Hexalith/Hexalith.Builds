# Split publication

Owner-controlled commands for the opt-in `split-publication-v1` mode of
`.github/workflows/domain-release.yml`.

`build-publication-candidate` is secretless and read-only. It may restore,
build, test, pack, and plan a release. It does not publish, mint an attestation,
or seal `hexalith.release-evidence.v4`.

`publish-publication-candidate` runs under the caller-selected protected
environment and executes only this action plus the literal-commit actions in
the reusable closure. It authenticates the raw archive before extraction.
Candidate bytes are data: the publisher does not check out the caller, run a
candidate command, or load a package hook.

The publication gate is deny-only. The publisher sets
`publication-started.json` immediately before the first NuGet or GitHub Release
mutation and not before.

FrontComposer still owns its policy file, caller workflow, post-release
ledger, incident-recovery workflow, and semantic catalog engine. This action
does not copy those sources.

## FrontComposer attestation verification

The FrontComposer-owned `release-publish.yml` can call this action at the
owner-accepted full Builds commit with `command: verify-attestation` before
`NuGet/login`. The command takes these inputs.

| Input | Source |
| --- | --- |
| `archive` | Raw publication-candidate archive for the Release attempt |
| `expected-sha256` | Authenticated Actions upload digest of the raw archive |
| `bundle` | `actions/attest` bundle path in the workspace or `RUNNER_TEMP` |
| `metadata` | Protected publisher's publication state containing `workflow_provenance` |
| `builds-execution-sha` | Full Builds commit accepted in the active Release action closure |

The command compares the upload digest, validates the archive inventory, then
verifies a single Sigstore v0.3 in-toto DSSE signature offline. The shipped
trust snapshot is for GitHub's public-repository attestations. The verifier
requires a trusted time, certificate chain, certificate transparency and Rekor
evidence. It supplies the verified Fulcio identity to the existing statement
validator, which binds the signer, source, Release attempt and all eight package
name/digest pairs. GitHub runner context supplies the expected Release candidate,
branch, repository IDs, run and attempt. A `refs/heads/main` signer alias requires
the separate candidate signer digest in the certificate. Builds executes at its
own pinned commit.

Use the GitHub-hosted Node 24 runtime and Python 3.11 or newer. This command
needs no credential input. Success reports the SHA-256 of the verified raw
bundle and exits zero. The FrontComposer branch of `release-authenticated`
rechecks the bundle when sealing the manifest. Builds-reusable callers retain
their existing attestation handling.

### Verifier package

`sigstore-verifier/dist.cjs` bundles `@sigstore/verify` 4.1.3 and its locked
runtime dependencies from [sigstore-js](https://github.com/sigstore/sigstore-js).
The bundled code is copyright The Sigstore Authors and is distributed under
the included Apache 2.0 license. `trusted-root.json` is the Sigstore Public
Good Instance TUF target `trusted_root.json`, retrieved with the Sigstore TUF
client and pinned at SHA-256
`6494e21ea73fa7ee769f85f57d5a3e6a08725eae1e38c755fc3517c9e6bc0b66`.
The action loads both files from the same pinned Builds revision and makes
no verification-time network requests. A verifier or trust snapshot change
requires a new reviewed Builds action revision.

Rebuild and compare the shipped verifier from the Builds repository root.

```bash
run-with-temp bash -euc '
  verifier="$PWD/Github/split-publication/sigstore-verifier"
  cp "$verifier"/package.json "$verifier"/package-lock.json "$verifier"/verify.cjs "$TMPDIR"
  cd "$TMPDIR"
  npm_config_cache="$TMPDIR/npm-cache" npm ci --ignore-scripts
  npm run build
  cmp dist.cjs "$verifier/dist.cjs"
'
run-with-temp env PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s Github/split-publication/tests
```
