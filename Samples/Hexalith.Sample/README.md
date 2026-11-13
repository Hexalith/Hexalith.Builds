# Hexalith Sample

This consumer builds against the current Builds tree through the same central
build, package and version imports used by modules. It has its own SDK pin and
nested solution, with no nested Builds checkout and no repository-root props
import. The product is deliberately small so failures identify the consumer
surface rather than infrastructure.

Builds CI calls shared CI with `module-root: Samples/Hexalith.Sample` and
`expected-source: Samples/Hexalith.Sample/expected-source`. It restores and builds
once after guard succeeds, runs unit tests, the enabled root `after-unit-tests`
action, and integration tests. A dependent Windows job prepares a separate
Release build and runs the
root `sample-windows` action. Both actions execute built test projects only.
The configuration delegates those projects through `tests.exclude`, so they do
not also run in shared stages. Only this configuration enables the sample actions.

`buildsTag` records an exact released contract version; the sample intentionally
imports the Builds checkout under test rather than that older release.

The expected-source fixture has the delivered layout `rendered/Hexalith.Sample/`
and a sibling `Hexalith.Sample.perimeter.json`. Its v1 perimeter has one `whole`
entry owning `.github/hexalith-ci.json`. Stage 1 compares the committed sample
subtree with that committed expected configuration before prepare parses JSON
or initializes dependencies. An owned edit fails with the ownership diagnostic.
Dirty/staged content and sparse working-tree omissions do not affect this Git
object comparison.

After a match, guard validates committed CI configuration through the shared
loader and checks every enabled hook/job action at the workspace root.
Symlink/gitlink action metadata and missing regular `action.yml` files fail
without authorizing future restore. Both fixture inputs are required together,
and fixture controls are rejected outside Builds. Builds' separate root call
skips delivered-state lookup entirely. Create-mode sample release checks use
the same paired fixture controls; complete mode skips expected-state reads.
Commit format remains covered by the separate Builds commitlint caller.
