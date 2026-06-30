# Initialize Dapr

Composite action that installs the Dapr CLI and runs `dapr init` with retry.

Factors out the Dapr bootstrap block that was previously duplicated across CI jobs and
workflows in consuming repositories (e.g. `Hexalith.Tenants`).

## Inputs

| Input     | Required | Default    | Description                          |
|-----------|----------|------------|--------------------------------------|
| `version` | No       | `1.17.0`   | Dapr runtime/CLI version to install. |

## Usage

```yaml
- name: Install and initialize Dapr
  uses: Hexalith/Hexalith.Builds/Github/dapr-init@main
  with:
    version: '1.17.0'
```

Consumers that pin third-party actions by SHA should pin this action to a commit SHA
rather than `@main`:

```yaml
  uses: Hexalith/Hexalith.Builds/Github/dapr-init@<commit-sha>
```

The pinned third-party actions used internally (`dapr/setup-dapr`, `nick-fields/retry`)
are themselves pinned by SHA inside this action, preserving supply-chain posture.
