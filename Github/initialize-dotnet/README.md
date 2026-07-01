# Initialize .NET GitHub Action

Sets up the .NET SDK for Hexalith workflows. The action can install the SDK
declared by a `global.json` file, or install an explicit SDK version when no
`global.json` path is provided. It can also install the Aspire workload.

## Inputs

| Input | Description | Required | Default |
|-------|-------------|----------|---------|
| `global-json-file` | Path to a `global.json` file that pins the SDK version. Takes precedence over `dotnet-version` when set. | No | `''` |
| `dotnet-version` | SDK version passed to `actions/setup-dotnet` when `global-json-file` is empty. | No | `10.0.300` |
| `aspire` | Install the Aspire workload when set to any non-empty value. | No | `''` |

## Steps

1. If `global-json-file` is set, run `actions/setup-dotnet@main` with that
   file.
2. Otherwise, run `actions/setup-dotnet@main` with `dotnet-version`.
3. If `aspire` is non-empty, run `dotnet workload install aspire`.

## Usage

### Use the default SDK

```yaml
- name: Initialize .NET
  uses: Hexalith/Hexalith.Builds/Github/initialize-dotnet@main
```

### Use global.json

```yaml
- name: Initialize .NET
  uses: Hexalith/Hexalith.Builds/Github/initialize-dotnet@main
  with:
    global-json-file: global.json
```

### Install Aspire workload

```yaml
- name: Initialize .NET with Aspire
  uses: Hexalith/Hexalith.Builds/Github/initialize-dotnet@main
  with:
    global-json-file: global.json
    aspire: 'true'
```

## Notes

- `global-json-file` is preferred for repositories that pin their SDK.
- `dotnet-version` is only used when `global-json-file` is empty.
- The action does not restore, build, or test the repository.
