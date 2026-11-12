# Story 4-1 perimeter contract and library results

## Scope and source identity

This change adds the closed `hexalith.perimeter.v1` schema, its strict stdlib
bytes/file loader and one Git-backed comparison/application engine. It adds no
rendering, delivery, guard/restore job, release drift integration or local check.
The sample's empty placeholder and inactive enforcement seam remain for story
5-1. The Python workflow change only clarifies its existing step label.

The canonical CiCd story is
`_bmad-output/specs/spec-cicd/stories/4-1-perimeter-contract-and-library-in-builds.md`.
Its CiCd baseline is `811b04fa570bf575d1cb42f8b661ea6c05c25f61`, its Builds
implementation baseline is `3238aa2742dd69ec75144a6f523fd1f86c3f3174`, and the
previously integrated upstream revision is
`3c2f5944aaa28dc7854a00a8a9df171aa810b0c7`. Review the Builds content diff from
the implementation baseline alongside the CiCd story/gitlink diff. The earlier
planning companion remains historical evidence.

## Behavior and executable evidence

`Github/lib/tests/fixtures/perimeter-contract.json` supplies 128 shared cases:
26 valid and 102 invalid. Both the draft-2020-12 validator with regex format
checking and the independent stdlib loader consume the same corpus. It includes
an explicit AD-4 manifest covering every kind, root configuration patterns,
root/deep response files and skill paths, all four package dependency groups,
scoped package names and commitlint hook lines. Serialized-input controls also
reject duplicate keys, non-JSON constants, bad UTF-8/BOM and deeply nested input,
and run the loader with `python -I -S` from an untrusted current directory.

Real disposable Git repositories verify all nine entry kinds through index
object identities and blob contents. Every successful repair is compared
again, applied again with no index change, and checked against unchanged
working-tree bytes, untracked files and a submodule checkout. Controls include
malformed whole JSON, expected modes, symlink/gitlink replacement, annotated
tag peeling, CRLF no-ops, missing/malformed partial files, duplicate markers and
Gitmodules sections, foreign JSON values/allow lists/denies, Git syntax and
continuations, JSON pointers through arrays and escaped property names,
Git wildmatch classes against a Git glob-pathspec oracle, literal special
filenames, sparse indexes/outside-cone deletion and caller-selected indexes.

The engine rejects ownership/source/tag/selector/unmerged-index and
file/directory conflicts before mutation. It stages through a private candidate
index under the real index lock; failures also preserve the original index.
Attribute lookup uses a Git tree built directly from the selected index's
`.gitattributes` blobs. This avoids dirty working-tree attributes and Git index
refresh paths that may invoke their filters. Candidate indexes use expanded
sparse entries for cacheinfo updates. A final comparison rejects even a
successful clean filter if its output violates the selected contract.

Independent implementation checks reproduced and closed four edge cases:
JSON pointer array preservation, expected whole-directory membership versus
absence conflicts, Gitmodules comments ending in backslashes, and Git's POSIX
and negated bracket classes. A further 54 Gitmodules syntax/preservation
combinations and composed JSON repairs passed; comments-only Gitmodules
preservation has its own regression control.

## Validation

Local prerequisites were verified with Node 24.22.1, the repository-selected
.NET SDK 11.0.100, PowerShell 7.6.2, Python 3.12 and captured runner Python
3.14.4. `npm ci --ignore-scripts` passed with zero vulnerabilities;
`npm audit signatures` verified all 318 registry signatures and 58 attestations.
`Tools/ci/lint_workflows.py --install-only` verified the committed SHA-256 pins
for actionlint 1.7.12, zizmor 1.32.0 and uv 0.13.7.

The focused Python 3.12 behavior/parity suite passes. The complete locked
Python 3.12 runner passed all 14 retained groups, including the discovered
perimeter fixtures; subsequent perimeter-only changes also passed the focused
suite. Authenticated
`python3 Tools/ci/lint_workflows.py` passed: actionlint was clean and zizmor
reported 10 exact frozen composite findings and zero new findings. No frozen
runtime file, catalog/package pin or live release setting changed.

Pinned `@commitlint/cli@21.2.4` validated the exact implementation message
`feat(cicd): add the shared perimeter contract and Git library` with
`node_modules/.bin/commitlint --edit /tmp/cicd-story-4-1-implementation-message.txt --verbose`:
exit 0, `found 0 problems, 0 warnings`. The CiCd-agent profile is
`/home/administrator/.config/gh-cicd-agent`; token override variables were
absent. Fetch/divergence checks confirmed `main` equaled `origin/main` before
the implementation commit.

The full captured-runner pass, committed-object API result and hosted
exact-source run identities will be recorded after their completion.

## Publication boundary

The implementation session never dispatches release, publishes a draft, or
changes CiCd's `builds-version`. The parent interactive CiCd session owns fresh
release prerequisites, dispatch on main after exact-source green CI, original
artifact/outcome correlation and acceptable-publication verification. The
canonical story remains incomplete until a published immutable bot-authored
tag at an ancestor of Builds main contains this implementation and is adopted.
The retained qualification draft is preserved.
