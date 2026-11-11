# Publish original release artifacts

Invoke this remote composite as the sole step of the caller's publish job,
after the reusable release returns `publish == 'true'`. The caller owns
environment `hexalith-release`, allows deployment from `main`, disables workflow
caches, and grants `contents: write`, `actions: read`, `id-token: write`.
The NuGet Trusted Publishing policy must name that caller workflow and
environment. No signed provenance or attestation permission is used.

| Input | Meaning |
| --- | --- |
| `publish` | Literal `true` authorization from the validated reusable handoff |
| `version`, `sha` | Stable unprefixed version and full original built commit |
| `last-tag` | Captured reachable stable base tag, or empty; never recomputed |
| `draft-id` | Bot-authored draft with the five authenticated trailers |
| `artifact-id`, `artifact-digest`, `artifact-run` | Original `release` artifact, `sha256:` ZIP digest and producing run |
| `nuget-user` | nuget.org account for the caller's Trusted Publishing policy |
| `registry-username`, `registry-password` | Scoped push pair for the artifact's images at `registry.hexalith.com` |

The composite authenticates producer workflow path, manual dispatch on main,
source, artifact metadata/expiry/digest and downloaded bytes. It validates the
exact ZIP member set, NuGet identity/version/commit and complete OCI graph
before exposing original files in a private directory outside the workspace.
It never checks out or executes module source, reads module configuration,
restores, builds or repacks. Package commands use an empty private directory
and private CLI home, explicit nuget.org source and `--skip-duplicate`.

Every live version tag is peeled through annotated tags. A different commit or
non-commit target fails with `tag vX.Y.Z does not point at the built commit`.
All container roots are checked before NuGet login or any publication write;
each is checked again immediately before copy. Official version/source/digest
pinned Skopeo uses `copy --all --preserve-digests`. Auth is restricted to the
registry endpoint, private authfiles are removed on all exits, and remote root,
platform manifests, configs and layers are checked by digest and size.

After package/image success, fresh main must still descend from the built
source. A difference anywhere in `.github/workflows/` produces successful
`ready`, preserving the draft for the nightly App. Otherwise one draft PATCH
sets `tag_name`, full `target_commitish` and `draft: false`, creating the tag
last. Its response and an independent live peeled tag must both match before
`published`. API, authorization and network failures remain failures.

The `outcome` output is `published`, `ready` or `failed`. An always-run finalizer
uploads exactly one `hexalith-release-outcome` artifact, with `overwrite: true`,
containing schema-valid `outcome.json`. It covers failed preflight, nested
NuGet login and push steps. Malformed handoffs report the trusted dispatch SHA;
authenticated handoffs report the built SHA/version. If the private publisher
state is missing or invalid when the finalizer runs, it reports a `failed`
outcome with the trusted dispatch SHA and no version. `ready` includes `draftId`
and the captured `lastTag`. The reusable reporter relinquishes ownership only
for a successful validated handoff and must not upload a second outcome.

Retries consume identical authenticated original bytes and skip duplicate
NuGet versions. Successful package commands emit only fixed published,
skipped-duplicate or completed statuses; raw command output and temporary keys
are never logged. External races can fail after partial publication; retain the
draft and replay the same handoff. Runtime never moves or deletes Git refs and
never rolls back package/image writes. Empty inventories permit GitHub-only
publication.

The reusable release now supplies the same handoff in complete mode after
independently authenticating the selected draft's original artifact. This
composite repeats its own checks and duplicate-safe publication from those
same bytes. Recovery receives only pull credentials; publisher push credentials
and OIDC stay in this caller job. If the original artifact is gone, recovery
can delete only an unchanged marked draft whose entire saved destination
inventory is definitely absent; an existing or uncertain destination fails
and retains the draft. That branch yields one reusable `nothing` outcome,
without invoking this publisher. Rendered callers and nightly/App publication
remain downstream work; production callers and switches are unchanged.

[Story 3-4 qualification](../../docs/cicd-3-4-publish-composite-results.md)
records an isolated hosted package/image publication that ended `ready`, then
a duplicate-safe `published` retry from the same original producer run,
artifact ID/digest, source and version. The temporary caller used its own
Trusted Publishing policy; no production caller or API-key fallback was used.


[Story 3-5 recovery verification](../../docs/cicd-3-5-release-complete-mode-results.md)
records the original handoff and its failure/deletion boundaries. Its hosted
probe omits the publisher and makes no publication claim.
