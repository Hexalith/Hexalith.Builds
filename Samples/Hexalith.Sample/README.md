# Hexalith Sample

This consumer builds against the current Builds tree through the same central
build, package and version imports used by modules. It has its own SDK pin and
nested solution, with no nested Builds checkout and no repository-root props
import. The product is deliberately small so failures identify the consumer
surface rather than infrastructure.

Builds CI calls shared CI with `module-root: Samples/Hexalith.Sample` and
`expected-source: Samples/Hexalith.Sample/expected-source`. It restores and builds
once, runs unit tests, the enabled root `after-unit-tests` action, and integration
tests. A dependent Windows job prepares a separate Release build and runs the
root `sample-windows` action. Both actions execute built test projects only.
The configuration delegates those projects through `tests.exclude`, so they do
not also run in shared stages. Only this configuration enables the sample actions.

`buildsTag` records an exact released contract version; the sample intentionally
imports the Builds checkout under test rather than that older release.

The expected-source fixture has the delivered layout `rendered/Hexalith.Sample/`
and a sibling `Hexalith.Sample.perimeter.json`. It currently contains the owned
sample configuration. Its empty perimeter object reserves the filename only;
story 4-1 supplies the shared perimeter schema and library, and story 5-1 owns
the sample's valid manifest and enforcement integration. The reserved stage-1 step
performs no guard or commit-format enforcement today.
