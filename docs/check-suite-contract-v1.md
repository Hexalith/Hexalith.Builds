# Check suite contract v1

The three shipped, closed JSON schemas are `hexalith.check-suite.v1`,
`hexalith.check-invocation.v1` and `hexalith.check-result.v1`. Consumers select
these exact versions; unknown versions, properties, duplicate JSON keys, empty
suites, duplicate check IDs and invalid semantics fail closed. Validation never
loads or executes payload code. A valid package is not publication eligibility
and is not hosted E2E evidence.

## Owned descriptors and package layout

A descriptor names an exact numeric `major.minor.patch` suite version, module,
source path, CODEOWNERS owner, Python runtime, entrypoint and complete payload
inventory. Stable check IDs name a surface, positive/denial assertion, E2E/smoke
kind, environments, integer timeout (1–3600 seconds), effect and synthetic tenant
mode. Smokes also declare a finite cadence (60–86400 seconds). A production
check must be a read smoke against an existing synthetic tenant. Production
never creates/deletes tenants, grants administration or causes external effects.
Staging writes require a declared run-scoped synthetic tenant and separately
admitted authorization. An authorization-denial check never covers a positive
flow.

`hexalith-evidence check-suite package` produces an uncompressed directory:

```text
<module>/<exact-suite-version>/
  descriptor.json          # exact original UTF-8 bytes
  package.json             # index, not an executable
  payload/<declared-path>  # exact executable/support bytes
```

Only these files may exist. Relative paths use ASCII letters, numbers, dots,
underscores and hyphens separated by `/`; absolute/traversal paths, duplicate
case-insensitive names, symlinks/reparse points and linked ancestors are refused.
Each file/document is bounded to 4 MiB and each inventory to 256 entries. A
package destination must not already exist. Build output and local cache files
are excluded by the explicit inventory, not by opportunistic directory scans.

All digests use lowercase `sha256:` plus 64 hex digits. Compute each exact file's
SHA-256, including whitespace and line endings. `package.json` has exactly
`schema`, `suiteDigest` and `content`. `schema` is `hexalith.check-package.v1`.
`content` is the following canonical JSON, encoded UTF-8 without BOM, whitespace
or trailing newline, in this exact property order:

```json
{"descriptor":{"sha256":"sha256:<descriptor hash>","size":123},"payload":[{"path":"runner.py","sha256":"sha256:<payload hash>","size":456}]}
```

Payload entries are sorted by ordinal path, with each entry's keys in
`path,sha256,size` order. Sizes are decimal integer byte counts. Compute
`suiteDigest = SHA256(content bytes)`. This computation is nonrecursive:
`content` includes neither `suiteDigest` nor `package.json`. The descriptor digest
is its exact byte digest. Both identities are retained in invocation/results.
The validator recomputes everything and demands the canonical `content` bytes,
rejecting payload, descriptor or index tampering and unexpected files. Directory
metadata/timestamps do not enter the digest. Consumers select an exact retained
suite version and digest bound by the signed release record; floating versions,
rebuilding at execution time and descriptor-only identity are forbidden.

## Trusted invocations and evidence admission

An executor constructs and retains the original invocation before starting the
sandbox. It binds invocation ID, stable check ID, attempt ID, environment,
execution mode, served release, suite and descriptor digests, profile and
configuration digests. A staging invocation also retains the working
baseline's terminal attempt, release, profile and configuration identities when
a baseline exists. The required `workingBaseline: null` explicitly labels a
fresh install when no baseline exists; only the trusted executor decides this.
An absent field or malformed baseline object is invalid.
Every retry has a new invocation ID and retains its own result; retries never
overwrite or convert earlier failed/skipped/timed-out/incomplete attempts.

The invocation includes only nonsecret context: one verification endpoint,
synthetic tenant/mode, fixture party/case identities, external-effects-disabled
requirement, executor-issued start and absolute deadline. HTTPS is mandatory for
hosted execution. Controlled local tests may use loopback HTTP and must carry
`executionMode: controlled`; they cannot qualify hosted evidence. Reject
endpoint credentials, query/fragment components and redirects to other endpoints.
The executor chooses fixture identities and admits them as synthetic; arbitrary
suite-selected tenant names confer no authority. `externalEffectsDisabled: true`
is a required context claim backed by the executor's verified configuration,
not a mechanism that disables effects in the application.

Run the retained entrypoint inside an executor sandbox. The declared verification
endpoint and short-lived synthetic-client token are its only authority-bearing
inputs. Supply the token through `HEXALITH_CHECK_TOKEN`, never arguments,
retained invocation, descriptor, registry, result, diagnostics or raw process
logs. McpCli receives the same token through its supported `EVENTSTORE_TOKEN`
environment input. The sandbox mounts the exact release-bound McpCli candidate
at `/candidate/hexalith`, with no user profiles or inherited credentials. Limit
network access to the declared endpoint, subprocesses, filesystem, memory and
wall-clock time; kill the entire sandbox at the executor deadline. Story 4.20
implements this enforcement. A Python deadline aid is not a security boundary.

The executor owns expected release/profile/configuration context, authenticates
the release actually serving the endpoint, and checks the deadline independently.
A module self-report cannot certify its own package, candidate, release or
identity. `EvaluateResult` accepts the original trusted invocation, retained
package, untrusted result, separately authenticated observed release digest and
executor reception time. It checks every identity including the staging
baseline, exact declared check, allowed environment/tenancy and finite check
budget. Reception and completion must be within deadline, and timestamps cannot
precede the executor start or follow reception. Only a complete, matching,
timely hosted `passed` result satisfies a check. A correct schema alone is never
evidence admission. Qualification must also verify the sandbox's external
enforcement, entitled executor signature, retained attempt lineage and release
bindings; those integrations follow in the executor/publication stories.

Results preserve `passed`, `failed`, `skipped`, `timed-out`, `incomplete`, plus
`complete`, start/completion timestamps and bounded fixed diagnostic codes.
A passed result requires `complete: true` and no diagnostics. Missing output,
runner crash and partial output are incomplete; skip stays skipped; timeout
always fails admission, even if a late process subsequently prints success.
The executor retains its own timeout/missing-output observation alongside any
original self-report. The runner returns exit 0 only for a complete pass, 1 for
nonpassing outcomes and 2 for invalid invocation/entry setup. Never retain raw
response bodies or exception strings as diagnostics.

## Static validation and publication handoff

```text
hexalith-evidence check-suite package <descriptor> --payload-root <source-dir> --destination <new-dir> --output json
hexalith-evidence check-suite validate <package-dir> --output json
hexalith-evidence check-suite validate <document> --kind suite|invocation|result --output json
hexalith-evidence check-suite coverage <registry> --repository <source-root> --packages <retained-root> --output json
```

Static success exits 0; contract/package/coverage refusal exits 6; invalid command
usage exits 1 using the existing HXC001 parser diagnostic. Reports contain only
`valid`, sorted stable `diagnostics` and optional `suiteDigest`. No input values,
paths, tokens or exception messages are reflected.

| Rule | Meaning |
| --- | --- |
| HXS001 | Closed schema, parse, duplicate-key or unavailable-document refusal |
| HXS002 | Descriptor/invocation/result semantic refusal |
| HXS003 | Unsafe, missing, changed or noncanonical package bytes/layout |
| HXS004 | Missing, nonpassing, late or identity-mismatched evidence |
| HXS005 | Missing/invalid owner, source, retained package, module/check or source link |
| HXS006 | Explicit unresolved flow or smoke publication blocker |

Platform's `hexalith.reference-check-registry.v1` is a closed registry with
`schema` and `modules`. Entries have exactly `module,owner,source,sourceKind,suite,
descriptor,package,criticalFlows,smokeSurfaces`. Each flow/smoke mapping has
`name,surface,checks,status,reason`; resolved mappings use `status: resolved`
and `reason: none`. Blocked mappings retain their names/surfaces, use empty
checks and a stable owned reason. The source inventory must include all five
reference modules. Descriptor ownership/source/module/suite and exact packaged
descriptor bytes must agree with the registry; packages use `<module>/<version>`.
Every declared critical flow is present, and existing `e2eChecks` and smoke
`suite` links are preserved and resolved. Positive staging E2E checks cover
critical flows; positive production smokes cover smoke mappings. Explicit
blockers and missing ownership/source/check mappings refuse publication.
Story 5.3 owns enforcement across all modules.

Story 4.12 must run package byte validation and complete source-aware reference
coverage validation before signing/publishing retained releases, bind all suite
and descriptor digests into release records, and refuse publication while
coverage blockers remain. This contract supplies callable validation; it does
not claim publication integration, hosted qualification or sandbox enforcement
is implemented.
