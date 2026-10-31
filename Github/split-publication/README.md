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
