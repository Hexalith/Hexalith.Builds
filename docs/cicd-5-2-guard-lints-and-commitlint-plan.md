---
story: '5-2'
created: '2026-11-13'
status: ready-for-dev
planning_only: true
baseline_revision: abb9e11576bff5f1407696acc481224ea0424aae
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: 7db81f4f19bef5e5337d241abc4bbff98ef62c4c
---

# Guard lints and commitlint plan

Story 5-2 extends the existing first-stage guard under CAP-9/14 and
AD-8/11/19/20. This delivery contains planning documentation only. The canonical
story in the enclosing CiCd repository is
`_bmad-output/specs/spec-cicd/stories/5-2-guard-lints-and-commitlint.md`.
It contains the complete intent, matrix, Code Map, tasks and acceptance criteria.

## Implementation

1. Add a minimal trusted `Github/guard/package.json` and lock with exact
   commitlint CLI/config-conventional 21.2.4 and js-yaml 4.3.2. Both versions
   already exist in Builds' root lock. Keep lint policy in Python and JavaScript
   limited to commitlint configuration/bridge. Preserve every effective current
   rule: the ten existing types, header/body 200, inherited footer 100,
   defaultIgnores false and the remaining conventional rules. Make root
   commitlint validation share the new skip-marker policy.
2. Add `Github/lib/guard_tools.py`: install the trusted guard package outside the
   module, using scripts-disabled npm ci and npm audit signatures. Sanitize
   npm/Node/Git/token configuration; run explicit trusted tool/config paths with
   stdin and argument arrays. Install once per guard invocation after perimeter
   comparison succeeds; share tools between YAML parsing and commitlint. No
   module package/config/plugin discovery or runtime Python dependency.
3. Add `Github/lib/guard_actions.py`. Read every committed workspace-root
   `.github/actions/**/action.yml` and `action.yaml`, including dormant actions.
   Use the pinned external YAML CLI as a data parser, then Python policy:
   require composite actions and actual step uses references to normalized
   local `.github/actions/` directories or full-SHA third-party actions, never
   Builds. Validate local targets without dereferencing symlinks/gitlinks.
   Apply enforcement.md's exact text deny list; preserve explicit-project
   dotnet build/test commands. The current sample actions already conform.
4. Add `Github/lib/audit_lint.py`. Use the module snapshot, including only the
   sample subtree in Builds. Detect every MSBuild Project XML root independent
   of extension, encoding or namespace; inspect element/attribute declarations
   under every parent, including PackageReference metadata. Reject blocking
   NoWarn codes NU1900/3/4/5, NuGetAuditSuppress, MSBuildWarningsAsMessages,
   NuGetAudit-prefixed TreatAsLocalProperty tokens, auditSources,
   TestingPlatformCommandLineArguments and VSTestTestCaseFilter. Cover
   case-insensitive response files and NuGet configs, including custom-named
   configs identified by XML structure. Preserve NU1901/2 and unrelated source
   files; fail malformed identified inputs without interpreting imports.
5. Add `Github/lib/guard_commitlint.py` and `commit_policy.py`, with the guard
   config/bridge. Read frozen Git objects: PR base..head plus title, push
   before..after, dispatch HEAD. A zero-before push validates all reachable
   after commits; missing nonzero endpoints fail rather than narrowing to last.
   Preserve a reusable explicit-range seam for the later local check. Reject
   all five GitHub bracketed skip forms case-insensitively and any skip-checks
   trailer key, including markers in message bodies and merge commits.
6. Extend `Github/lib/guard.py` and `Github/guard/action.yml`, and acquire complete
   history in reusable `.github/workflows/ci.yml`. Keep guard first, comparison
   before module configuration parsing/new lint work, and existing enabled-action
   checks. Configure pinned explicit Node 24 without package-manager cache or
   implicit module setup. Preserve false drift for every lint-only failure and
   existing true drift only for actual perimeter differences. Validate fixture
   controls before Builds-root bypass. Builds root runs no new lint/tool install;
   sample uses root actions and Builds-wide commit messages while auditing only
   its module subtree.
7. Add focused real-Git/parser/policy/event tests under `Github/lib/tests` and
   extend existing guard/adapter/shared-CI harnesses. Replace opaque invalid YAML
   success fixtures with valid composites; retain malformed negative controls.
   Exercise actual composite conditions, Node mappings and failure boundaries,
   rather than concatenating run steps or skipping new uses steps. Verify dirty,
   staged and sparse independence; poison module config/Node/npm inputs; ensure
   failure stops prepare/SDK/build/tests/hooks. Separate real pinned-tool evidence
   from command doubles. Existing dual-interpreter discovery includes new files.
8. Immediately before caller retirement, read Builds Protect main ruleset
   19196298 again and confirm story 1's cut. After executable replacement sample
   guard coverage, delete `.github/workflows/commitlint-caller.yml` in that same
   implementation change, remove its `Hexalith.Builds.slnx` entry and any live
   readers/text-assertion tests, and preserve PR title edited coverage in
   `.github/workflows/builds-ci.yml`. Keep frozen `commitlint.yml` and its
   meaningful behavioral tests unchanged. Update `Tools/ci/README.md`, sample
   README and later implementation results separately.

## Boundaries and validation

Story 1 and the inventory record the completed ruleset cut. A fresh scoped-agent
API read on 2026-11-13 confirmed Protect main is active on the default branch,
with exactly deletion/non_fast_forward and no bypass actors. That read changes
no settings and does not replace the pre-deletion check during implementation.
If a future read is denied, the prerequisite remains unverified; retain the
caller and report the exact blocker.

Preserve the existing perimeter engine, strict loader, read-only job permissions,
fixed ownership diagnostics and root/sample/Windows topology. Audit lint closes
the NoWarn waiver gap explicitly deferred from story 3-3; it does not modify or
duplicate release audit flags, vulnerability scanning or release drift resolve.
Builds' trusted Props waivers and intentional root test fixtures remain outside
the sample audit. No restore/delivery/adoption/admin work belongs to this story.
Frozen module-consumed runtime, unrelated dependency/SDK/catalog pins and root
legacy release configuration remain unchanged. No workflow-text assertions.

Action parsing supports ordinary YAML aliases/flow/quoted values without a
handwritten restricted parser. The js-yaml CLI converts JSON input in the reverse
direction; prefix a harmless YAML comment to force YAML-to-JSON without adding a
document. Planning probes confirmed flow/aliases parse and duplicate keys/unsafe
tags fail. Require a single mapping document. Keep deny matching textual, accept
its occasional false positives, and do not claim general shell or transitive
script analysis. Obfuscated audit expressions/computed imports remain accepted
residuals. Commitlint cannot run a workflow already suppressed by GitHub; the
skip rule catches messages whenever a guard invocation includes them.

During implementation run focused guard/audit/policy suites, the complete
locked Python runner under Python 3.12 and runner Python, pinned actionlint/zizmor
with online audits and signatures, public-API and frozen-file/whitespace checks,
and normal Builds CI for the exact implementation commit. Retain all root/sample,
hook, Linux/Windows and both interpreter results plus independent review. Story
5-1's former verification exception does not apply; it was ultimately unused.
No release or real module adoption is required for qualification.

This planning delivery claims contract/code investigation, ruleset readback,
parser probes, documentation/metadata/whitespace checks and exact-message pinned
commitlint only. It makes no implementation/test/dependency/workflow change and
leaves `commitlint-caller.yml` intact.

Planning verification on 2026-11-13 passed metadata/full-baseline identity and
Given/When/Then acceptance checks, both repositories' whitespace checks, and
parser controls for JSON-style input, an existing YAML document marker and
multiple-document rejection. The exact proposed message
`docs(cicd): plan guard lints and commitlint` passed
`node node_modules/@commitlint/cli/cli.js --config commitlint.config.mjs --edit <message-file> --verbose`
using @commitlint/cli 21.2.4: zero problems, zero warnings, exit 0.

## Planning delivery

Commit and push this plan in Builds after validating the exact message with the
repository's pinned commitlint CLI. Stage the canonical story and the pushed
Builds pointer together in CiCd; leave the superproject commit/push to the caller.
Halt at ready-for-dev. Review later implementation using both the full Builds
diff from implementation_baseline_revision and the CiCd diff from
baseline_revision, preserving the canonical full identifiers above.
