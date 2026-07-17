# P0 persisted-fixture contract assets

These files are deterministic, metadata-only contract assets for the
`hexalith.module-manifest.v1` loader and the future Builds-owned runner.
They deliberately do not contain an executable module host, credentials,
ports, Dapr configuration, or any claimed runtime result.

`p0-two-module.manifest.json` declares the minimum two-module composition.
Its descriptor files are declarative fixture metadata only: they are not
loadable .NET assemblies and cannot be used as persisted-runtime evidence.
The runner must replace this metadata-only fixture boundary with its approved
descriptor ABI before a live lane can pass.

The `controls` directory defines non-passing live-runner controls. The
`negative` directory contains manifest-validation controls. Every fixture has
a sibling `<name>.expected.json` record with its expected exit code and stable
rule ID.
