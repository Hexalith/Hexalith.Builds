# Initialize Dapr

Composite action that installs the Dapr CLI and runs `dapr init` with retry.

Factors out the Dapr bootstrap block that was previously duplicated across CI jobs and
workflows in consuming repositories (e.g. `Hexalith.Tenants`).

## Inputs

| Input     | Required | Default    | Description                          |
|-----------|----------|------------|--------------------------------------|
| `version` | No       | `1.18.0`   | Dapr runtime/CLI version to install. |

## Usage

```yaml
- name: Install and initialize Dapr
  uses: Hexalith/Hexalith.Builds/Github/dapr-init@main
  with:
    version: '1.18.0'
```

Use `@main` for Hexalith.Builds action references so consuming repositories run
the latest shared Dapr bootstrap logic.

This action installs the Dapr CLI with a shell step and uses
`nick-fields/retry@v4.0.0` internally for `dapr init`. Review `action.yml` if
your repository requires every nested third-party action to be pinned by commit
SHA.
