# Verify Action

Builds and tests a Hexalith .NET project **without publishing**. Use it as the
pull-request / CI gate that runs before the release job.

## Inputs

- `project-name` (required): The name of the project (used to locate the test project).

## Steps

1. **Checkout** (`actions/checkout@v5`, `fetch-depth: 0`).
2. **Initialize build submodules** (`Github/initialize-build`).
3. **Initialize .NET** (`Github/initialize-dotnet`).
4. **Run unit tests** (`Github/unit-tests`) - compiles the libraries (as test
   dependencies) and runs the test suite.

## Example Usage

```yaml
jobs:
  verify:
    runs-on: ubuntu-latest
    name: Verify
    steps:
    - name: Verify build and tests
      uses: Hexalith/Hexalith.Builds/Github/verify@main
      with:
        project-name: ${{ github.event.repository.name }}
```
