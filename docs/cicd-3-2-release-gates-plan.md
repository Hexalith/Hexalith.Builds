---
story: '3-2'
created: '2026-11-10'
status: ready-for-dev
planning_only: true
baseline_revision: 8c43e667064d3c8452fc5612e93d822d07114a2d
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: ec97b6be56b46795ffd454067b72dc0ff4b14b3a
---

# Release gates and outcome plan

Story 3-2 adds the reusable release gate jobs and the outcome contract. This
delivery contains the plan only. Runtime work starts on a later dispatch.
The canonical story lives in the enclosing CiCd repository at
`_bmad-output/specs/spec-cicd/stories/3-2-release-gate-jobs-and-the-outcome.md`.
Its intent contract, acceptance criteria and architecture AD-16/17/18 govern
implementation; this document is the Builds-local execution companion.

## Existing implementation to reuse

- [The strict config loader](../Github/lib/ci_config.py) is stdlib only and
  already understands CodeQL languages and the fixed nightly hook. Read the
  dispatch commit's complete config through this loader, including completion
  of an older draft. Never read the old draft's config.
- [Linux prepare](../Github/prepare/action.yml) already checks out the event
  SHA without persisted credentials or recursive submodules, initializes only
  configured direct dependencies, selects the module SDK, restores/builds once,
  and runs the shared environment setup. Nightly calls `$/Github/prepare`, then
  literal `./.github/actions/nightly`. No prepare rewrite or duplicated setup.
- [Shared pipeline helpers](../Github/lib/ci_pipeline.py) provide preparation,
  build flags and safe outputs. Do not invoke restore/build inside resolve.
- [The spike helper](../Github/lib/publish_spike.py) demonstrates authenticated
  urllib requests without credential-forwarding redirects. Do not copy its
  spike repository identities into production helpers.
- [Quality commands](../Tools/ci/README.md) already discover new lib tests on
  Python 3.12 and runner Python and run real lint/API checks. Preserve the
  completed shared CI, sample module and the legacy runtime freeze.

## Required runtime behavior

Resolve runs only trusted stdlib Python and git, with exactly `contents: write`
and `actions: read`. No third-party Python, npm, uv, module scripts or publish
credentials run in this job. Module content is data; trusted helper source comes
from the reusable workflow's own `$/` action and `GITHUB_ACTION_PATH`.

Reject any ref except `refs/heads/main`. Select the release SHA once: one valid
marked draft selects complete mode and its SHA; none selects create mode and
dispatch `github.sha`; multiple marked drafts fail. A marked draft must have bot
authorship, matching target/trailer full SHA, ancestry to main and a stable
version above the highest reachable stable tag excluding its own tag. Bot
drafts with invalid release markers fail by name. Ordinary unmarked drafts are
never modified. Share the reachable-tag calculation with later release stages;
no reachable tag permits only the first version `1.0.0`.

The switch accepts exactly `true` or `false`. False logs `publishing disabled`;
unset/other values fail with
`HEXALITH_RELEASE_PUBLISH_ENABLED must be exactly true or false`. Create mode
then requires the latest exact-SHA `ci.yml` run with push/manual event to be
successful. Wait at most 30 minutes for an unfinished run, polling every minute;
otherwise fail `main is red, nothing released`. Only Builds can override the
CI identity to `builds-ci.yml`. Complete mode skips this CI check.

Publish current-SHA CodeQL/hook settings before switch/CI failures so the
nightly jobs can still run. Use `always()` scheduling and intentional-disable
conditions. They run at dispatch `github.sha`, including complete mode and
disabled publishing. CodeQL is a matrix over the validated languages, skipped
for an empty list, with `build-mode: none`, category
`hexalith-release/<language>`, `contents: read` and `security-events: write`.
SHA-pin architecture version v4.39.3, wait for processing, and query alerts.
Open high/critical findings whose most recent instance category starts with
`hexalith-release/` block; unrelated, closed and lower findings do not. Failed
analysis, unavailable alert data or a failed hook makes the run fail. The hook
job has `contents: read` and only explicit pull-registry credentials, provided
to prepare as `REGISTRY_PULL_USERNAME` / `REGISTRY_PULL_PASSWORD`.

All new workflows use empty top-level permissions, per-job grants,
`cache-mode: none`, credential-free checkouts and no expressions in `run:`.
Only top-level callers own concurrency. No publisher/OIDC/attestation grant
belongs to these jobs.

## Ordered implementation files

1. Add `Github/lib/release_state.py` for module-agnostic API/Git data access,
   marked drafts, shared reachable stable tags, frozen SHA/mode, switch and
   bounded exact-source CI. Add `Github/release-resolve/action.yml` as trusted
   adapter. Expose current-SHA language/hook settings independently of selected
   release SHA, plus `sha`, `mode`, `enabled` and known draft metadata.
2. Add `Github/lib/release_security.py` and
   `Github/release-security/action.yml` for the paginated post-analysis alert
   gate. Treat transport/parsing/analysis failures as failures.
3. Add `schemas/hexalith.release-outcome.v1.json`,
   `Github/lib/release_outcome.py` and `Github/release-outcome/action.yml`.
   Use the closed draft-2020-12 schema and prescribed Hexalith `$id`; required
   document fields are `schema: "hexalith.release-outcome.v1"`, full `sha`,
   and `outcome`. Outcomes are
   `published`, `ready`, `nothing`, `disabled`, `failed`; include stable,
   unprefixed `version` whenever known. Ready additionally requires positive
   integer `draftId` and `lastTag` (stable tag, or empty when none).
4. Add `.github/workflows/release.yml` with `workflow_call`, guarded
   `ci-workflow`, the `delivered-sha` seam for 5-1, explicit optional pull
   secrets, resolve, CodeQL, prepare/nightly-hook and the final outcome job.
   Surface resolved SHA/mode and string `publish`, currently `false`.
5. Add `Github/lib/tests/test_release_{state,security,outcome}.py` and
   `Github/lib/tests/fixtures/release-outcome.json`. Exercise real disposable
   Git histories,
   paginated HTTP controls, bounded clocks, actual adapters, config source
   selection, schema/stdlib agreement and failure precedence. Assert executed
   effects and outcomes, never workflow source text.
6. During implementation only, add a manual
   `.github/workflows/release-gates-probe.yml`, minimal root
   `.github/actions/nightly/action.yml` and
   `Tools/ci/probe_release_gates.py`. Use guarded test-only
   `module-root: Samples/Hexalith.Sample`, temporarily enabling nightly in the
   sample config and matching expected-source config; restore both afterward.
   The temporary hook can read a probe dispatch input to force failure.
   Do not change production config/callers or the repository publishing switch.
   Prove current-SHA prepare/hook, reporting after failure, and real CodeQL
   processing/alert reads through hosted run conclusions, logs and artifacts
   under the existing switch. Exercise other switch states, red CI, complete
   mode and deterministic draft/alert/errors through executable local controls;
   label hosted branch coverage only where actually observed.
   Retain sanitized evidence in `docs/cicd-3-2-release-gates-results.md`, then
   remove the probe files. Do not seed vulnerable live findings or publish.
7. Update `Tools/ci/README.md` and the results document with runtime interfaces,
   caller permission union, downstream handoff and exact-source verification.
   Validate/review both repository deltas; commit/push Builds and stage the
   canonical CiCd story with its updated gitlink.

## Outcome ownership and incremental integration

When publish does not run, the reusable final `if: always()` job writes one
`hexalith-release-outcome` artifact containing only `outcome.json`, with
`overwrite: true`. Its upload still runs after a failing decision while the
job/run remains failed. When later draft work authorizes publication, the
reusable reporter does not upload: the later publish composite alone owns
`published`, `ready` or publishing failure. Overwrite supports reruns, not two
competing writers.

Any failed/cancelled prerequisite wins over disabled/no-op success. Disabled
requires a valid false switch and successful or intentionally skipped nightly
jobs. Nothing requires an explicit no-release version decision or confirmed
stale-draft deletion. Before SHA selection, failure reporting uses dispatch
SHA; afterward it uses the selected SHA. Cancellation preventing upload remains
unsuccessful to the scheduler.

Version/build/draft and publication are later work. Do not manufacture
`nothing to release` because those stages are absent: the enabled, all-green
intermediate path keeps `publish=false` and fails explicitly with
`release publication stages are not installed`. Keep production callers
unchanged until their replacement story. Integration controls can exercise the
future explicit no-change and publisher-handoff decisions without inventing a
successful production release.

The drift check joins resolve in 5-1. Leave that seam clear without claiming
perimeter protection now. Preserve `builds-release.yml`, frozen `codeql.yml`,
Builds CI's existing CodeQL call and package/release configuration. Do not
revisit 3-1's original-artifact, nested NuGet/login or provenance decisions.

## Verification and delivery

Implementation requires focused release tests, the existing locked full suite
on both interpreters, pinned online actionlint/zizmor, committed-source public
API checks, frozen-file comparison and hosted probe evidence. The local
composite harness cannot establish Actions failure/always scheduling, so do
not substitute it for the hosted job graph. Record run/attempt/full source
revision, job conclusions and downloaded outcome separately from local controls.

This planning delivery validates frontmatter, local links, full Git baselines,
unchecked runtime tasks, documentation-only scope and whitespace; validates the
exact commit message with pinned commitlint 21.2.4; fetches/inspects divergence;
and commits/pushes this file only. Stage the canonical story and updated Builds
gitlink in CiCd without a CiCd commit or push. Halt after planning; no runtime
tests, probe, release workflow or publication is executed by this invocation.

## Primary-source implementation references

- [GitHub workflow-run REST API](https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-workflow)
  supports workflow-file identity, exact head SHA, events and pagination.
- [GitHub code-scanning alert REST API](https://docs.github.com/en/rest/code-scanning/code-scanning#list-code-scanning-alerts-for-a-repository)
  documents alert state/severity and most-recent-instance semantics.
- [CodeQL v4.39.3 analyze metadata](https://github.com/github/codeql-action/blob/v4.39.3/analyze/action.yml)
  provides category, source ref/SHA and processing-wait inputs.
- [Artifact overwrite behavior](https://github.com/actions/upload-artifact#overwriting-an-artifact)
  describes replacement, which gives an artifact a new identity.
