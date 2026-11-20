# Initialize Dapr

Composite action that installs the Dapr CLI and runs full `dapr init` with
retry-safe cleanup.

Factors out the Dapr bootstrap block that was previously duplicated across CI jobs and
workflows in consuming repositories (e.g. `Hexalith.Tenants`).

## Inputs

| Input             | Required | Default  | Description |
|-------------------|----------|----------|-------------|
| `version`         | No       | `1.19.1` | Dapr CLI version to install and the legacy runtime fallback. |
| `runtime-version` | No       | `1.19.2` | Dapr runtime version used by `dapr init`. An explicit `version` alone still selects both. |

## Behavior

- Installs the Dapr CLI with the official `dapr/setup-dapr` action.
- Runs full `dapr init --runtime-version <runtime-version>` so the runtime does
  not drift to Dapr's latest patch release. Existing callers that supply only
  `version` keep the previous shared CLI/runtime behavior.
- Sets `DAPR_DEFAULT_IMAGE_REGISTRY=ghcr`, matching the Dapr action test
  workflow and avoiding Docker Hub image pulls where possible.
- Cleans partial Dapr installs before each retry so a failed first attempt does
  not leave `~/.dapr/bin/daprd` or Dapr containers behind.
- Waits for Dapr init ports `58080`, `58081`, `50005`, and `50006` to become free
  on every interface before calling `dapr init`. The scheduler port `50006` can
  overlap Linux ephemeral connections, so each retry waits for it to clear.

## Usage

```yaml
- name: Install and initialize Dapr
  uses: Hexalith/Hexalith.Builds/Github/dapr-init@main
  with:
    version: '1.19.1'
    runtime-version: '1.19.2'
```

Omit both inputs to use the default CLI and runtime. Supply only `version` to use that same release for both.

Use `@main` for Hexalith.Builds action references so consuming repositories run
the latest shared Dapr bootstrap logic.

This action uses `dapr/setup-dapr` for CLI installation and
`nick-fields/retry@v4.0.0` internally for initialization. Review `action.yml`
if your repository requires every nested third-party action to be pinned by
commit SHA.
