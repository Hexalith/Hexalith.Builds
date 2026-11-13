---
story: '5-3'
created: '2026-11-14'
status: ready-for-dev
planning_only: true
baseline_revision: 06a3998be613e31affac5b69a755840e6a4bbcf3
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: 2dc16bba3053c1efc871a221a4e782a8c3da72ab
---

# Restore job plan

Story 5-3 implements CAP-8's drift repair under AD-4/7/8/9/11/19/20. This delivery
contains planning documentation only. The canonical story in the enclosing CiCd
repository is `_bmad-output/specs/spec-cicd/stories/5-3-restore-job.md`; it holds
the full intent, scenario matrix, Code Map, tasks and acceptance criteria.

## Implementation

1. Factor accepted-state acquisition in `Github/lib/expected_state.py` into a
   context manager shared with restore. Current `compare_repository` destroys
   its temporary delivered/Builds clones before returning its materialized
   `Comparison`; restore needs live expected snapshots until application ends.
   Preserve existing guard/release APIs, diagnostics, ancestor checks and cleanup.
2. Add stdlib `Github/lib/restore.py` and `Github/restore/action.yml`. The
   composite runs through `$/Github/restore` at the reusable workflow's own
   commit and invokes `python3 -I` through its trusted action path. Revalidate
   event, repository, ref, drift and fixture restrictions in the script. Accept
   nothing from the CI job except its exact true drift flag.
3. Independently acquire latest module main, latest accepted delivered branch,
   perimeter, expected files and exact Builds tag objects. Use fixed trusted
   HTTPS identities, anonymous no-checkout source acquisition and one pinned
   full main commit as the repair parent. Bound in-memory JSON/HTTP reads at
   4 MiB before parsing. Missing/unavailable/invalid data fails closed.
4. Build a private index in a fresh bare `--template=` repository, retaining no
   module Git configuration. Reuse the strict perimeter loader and
   `perimeter.apply(selected=<entry indices>)`; never implement another set of
   ownership semantics. Preserve sanitized writer environments through every
   internal candidate reader and hashing operation. Honor committed text
   attributes without enabling module filters, hooks, LFS or submodule execution.
5. Never repair workflows. Permit the Builds gitlink and its `.gitmodules`
   section only when the accepted perimeter tag equals the running workflow's
   exact tag. CI config additionally requires an exact committed workflow
   directory match, including extra/missing files and modes. Obtain native
   `job.workflow_ref`, `job.workflow_sha` and `job.workflow_repository` inside
   the executing composite step; validate Builds source and tag-object identity.
   Branch/SHA refs authorize no lockstep/config repair. Module config and gitlink
   never determine the running tag. Protect workflow and skipped lockstep paths
   from broad absent-glob footprints and ancestor/descendant removals as well;
   omit conflicting entries or fail before publication, and validate candidate
   tree invariants before commit. A `.gitmodules` repair must preserve an
   ineligible Builds section while allowing other independently owned edits.
6. Produce one `build(cicd): restore owned CI/CD files` commit with `commit-tree`
   and fixed bot identity, then one ordinary push to main. No force/rebase/retry
   after rejection. Supply the token only to the push subprocess through an
   HTTPS-GitHub-scoped header; never persist it or expose it to anonymous reads.
   Dispatch `ci.yml` on `main` through bounded, fixed-endpoint stdlib HTTP after
   push. A dispatch failure leaves the successful repair and fails visibly;
   rerunning against fully clean main can dispatch without another commit.
   Excluded-only drift makes no empty commit, push or futile dispatch.
7. Add a separate restore job in reusable `.github/workflows/ci.yml` with
   failure-aware, noncancelled gating: push to main, exact true guard drift,
   empty module-root, and non-Builds repository. It gets only Contents/Actions
   write and runs no module code or checkout. Keep the original CI failure
   visible. Grant the necessary caller permission union to the two reusable
   callers in Builds' `builds-ci.yml` and CiCd's
   `templates/.github/workflows/ci.yml.j2`; inner CI/custom-action/Windows jobs
   remain read-only. This is a caller-permission API change. Re-render CiCd
   with its template cause; the current registry is empty, so no adoption is
   involved. Frozen legacy workflows remain unchanged.
8. Add real-Git transaction/race tests in `Github/lib/tests/test_restore.py`,
   executable composite/job coverage in `test_restore_adapter.py`, and focused
   acquisition/perimeter/guard/harness regressions. Cover mixed product edits,
   malformed whole/partial files, both tag-transition directions, source/tag
   spoofing, workflow extra/missing/mode and indirect deletion controls,
   private-index atomicity, poisoned executable surfaces/environment, no-op,
   non-fast-forward and post-push dispatch recovery. Verify remote tree/commit,
   parent, dispatch payload and untouched worktrees, not workflow YAML strings.
9. Update `Tools/ci/README.md` and an implementation results document; run focused
   suites, complete locked Python 3.12/runner-Python suites, pinned online
   actionlint/zizmor, public API, CiCd render and frozen-file/whitespace checks,
   plus exact-source normal Builds CI. Preserve full revision/run identities.

## Boundaries and verification

`guard.run` emits false drift for unavailable accepted state and all lint-only
failures; only ownership drift authorizes restore. Keep that distinction.
Builds itself has no delivered state, and sample mode must never restore.
The production module registry is empty and delivery writers are not built yet.
Use local temporary remotes and injected Python transports for actual repair
proof, then normal hosted Builds CI for native runner/root/sample/Windows/lint
and permission validation. A skipped Builds restore job cannot prove a module
write. No release, module adoption or repository administration is required.

The perimeter engine supports selection but can remove indirectly matched
paths. In particular `**/bmad-testarch-ci/**` can intersect workflows; excluding
only the workflow whole-dir entry is insufficient. Enforce final-tree workflow
equality and skipped lockstep/partial-section invariants before publication.
Keep product content and parseable foreign partial-entry data outside the repair.
An unparseable partial-entry file is rewritten to its owned part, per AD-4.

Native workflow identity is documented in the
[GitHub job context](https://docs.github.com/en/actions/reference/workflows-and-actions/contexts#job-context).
It identifies the called reusable workflow and is available within execution
steps. The caller's `github` workflow identity cannot supply the running Builds
tag. No additional identity-discovery/OIDC grant is required.

Planning loaded the canonical spec and all companions, all 19 prior story
specs' continuity, applicable repository guidance and relevant runtime/tests.
All earlier execution checklists are complete. The investigation found no
unresolved restore intent gap. Full baselines above were obtained directly
from each repository's `git rev-parse HEAD` before writing this plan.

The planning-only checks are document metadata, full-baseline identity,
Given/When/Then acceptance, exact file scope, whitespace and pinned commitlint.
The exact message `docs(cicd): plan drift restore job` passed
`node node_modules/@commitlint/cli/cli.js --config commitlint.config.mjs --edit /tmp/cicd-story-5-3-planning/commit-message.txt --verbose`
with pinned CLI 21.2.4: zero problems, zero warnings, exit 0. No implementation
tests or hosted restore run are claimed for this documentation delivery.

The readiness gate passed, including an independent subagent check. Metadata,
both full starting revisions, the intent block, eight unchecked execution tasks,
seven Given/When/Then acceptance criteria and whitespace were verified. The
illustrative restore commit message also passed pinned commitlint 21.2.4 with
zero problems/warnings. The canonical story is `ready-for-dev` and carries the
required `oversized` warning.

## Planning delivery

Commit and push this document in Builds. Stage the canonical story and pushed
Builds gitlink together in CiCd, leaving the superproject commit/push to the
caller. Halt at `ready-for-dev`; all implementation tasks remain unchecked.
Review later implementation using both the Builds diff from
`implementation_baseline_revision` and the CiCd diff from `baseline_revision`.
