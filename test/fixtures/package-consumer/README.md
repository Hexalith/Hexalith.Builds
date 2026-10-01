# Published G-4 candidate consumer

This fixture pins both published tools to `4.29.1`, whose source revision is
`21ce044ab465ccb2adab58b3d66e394ffbecf3c2`. It is a candidate consumer,
not P0 acceptance or permission to start live qualification. The selected
EventStore `3.110.0` tuple has independently recorded P1R acceptance at
`2026-10-01T06:17:01Z`; fresh G-6 acceptance remains pending. The prior `3.106.0`
record is historical. P1R acceptance alone does not authorize live qualification.

Copy `.config/dotnet-tools.json` and `NuGet.Config` into a new disposable
consumer directory outside the source checkout. Set `NUGET_PACKAGES` and
`DOTNET_CLI_HOME` to new, invocation-owned directories, then run from that
consumer directory:

```text
dotnet tool restore --configfile NuGet.Config --no-http-cache
dotnet tool run hexalith-module -- --version
dotnet tool run hexalith-evidence -- --version
```

Both version commands must print
`4.29.1+21ce044ab465ccb2adab58b3d66e394ffbecf3c2`. Stable restore uses only
NuGet.org. No globally installed Hexalith tool or Builds source is needed.

The 2026-10-01 isolated consumer also exercised copied metadata-only
`test/fixtures/module/` and `test/fixtures/evidence/` controls, excluding
`module/executable/` and all build output. The synthetic acceptance positive
sample and its approvers are fabricated validator inputs. The metadata-only
module fixture returns `2/HXR003` for `run` and `test`; its repeated `down`
checks create no live resources. These are contract controls, not persisted
or live-cleanup proof. Results and retained nonpassing attempts are in
[`evidence/g4/published-4.29.1-20261001/`](../../../evidence/g4/published-4.29.1-20261001/README.md).

After matching P1R/G-6 acceptance, qualify the executable two-module fixture
through the installed `full` and `full-mtp` profiles and retain native reports,
event/projection sequences, required negative controls, cancellation, and
invocation-owned cleanup. Run `down` with the exact run identity twice. Exercise
rollback in an isolated adopted consumer: retain failed evidence, complete
idempotent cleanup, remove its local-tool/manifest adoption through the
authorized consumer change, and retain the previous Builds boundary
`v4.19.2` / `8e0e2da5e1eff07468b41d85d97979c96c2ac975`. Do not reset a
developer's source tree or invent a previous good tool pin.

Only an independently validated final acceptance record with dated Builds
Owner, Platform Owner, and named Test Architect decisions permits the P4
handoff. Publication and these controls supply neither those decisions nor
Story 6.1's remaining independent gates.
