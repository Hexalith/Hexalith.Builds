# Build Packages Action

This GitHub Action builds and publishes packages for Hexalith projects.

## Inputs

- `project-name` (required): The name of the project.

## Outputs

- `version`: The new release version.
- `major`: The new release major version.
- `minor`: The new release minor version.
- `patch`: The new release patch version.
- `published`: Whether a new release was published.

## Steps

1. **Checkout Repository**: Uses `actions/checkout@v4` to fetch the repository with full history.
2. **Get Version**: Uses `Hexalith/Hexalith.Builds/Github/version@main` to determine the new version.
3. **Initialize .NET**: Uses `Hexalith/Hexalith.Builds/Github/initialize-dotnet@main` to set up the .NET environment.
4. **Initialize Build**: Uses `Hexalith/Hexalith.Builds/Github/initialize-build@main` to prepare the build environment.
5. **Run Unit Tests**: Uses `Hexalith/Hexalith.Builds/Github/unit-tests@main` to run unit tests for the project.
6. **Build Packages**: Uses `Hexalith/Hexalith.Builds/Github/build-packages@main` to build the packages if a new release is published.
7. **Publish Packages**: Uses `Hexalith/Hexalith.Builds/Github/publish-packages@main` to publish the packages if a new release is published.
8. **Create Release**: Uses `Hexalith/Hexalith.Builds/Github/create-release@main` to create a new release on GitHub.

## Environment Variables

- `GITHUB_TOKEN`: GitHub token for authentication.
- `NUGET_API_KEY`: API key for publishing to NuGet.

## Example Usage
