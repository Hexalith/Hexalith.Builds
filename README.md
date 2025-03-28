# Hexalith.Builds

Common project for building Hexalith applications, modules and libraries.

## Overview

The Hexalith.Builds repository centralizes build configurations, property definitions, package management, and code style settings for the Hexalith ecosystem. It ensures consistency across all Hexalith repositories by providing standard build files that can be imported into any Hexalith project.

This repository:
- Centralizes version management
- Maintains consistent package dependencies and versions
- Enforces uniform code style and analysis rules
- Standardizes build configurations
- Provides common build properties for development environments
- Ensures consistent AI assistant rules and commit message formats

## Repository Structure

### Version Control
- `Hexalith.Version.props`: Defines the current version based on the latest git tag
- Current: [![Version](https://img.shields.io/github/v/tag/Hexalith/Hexalith.Builds?filter=v*)](https://github.com/Hexalith/Hexalith.Builds/tags)

### Build Configuration
- `Hexalith.Build.props`: Main build properties file
- `Environment.Build.props`: Defines build environment variables
- `Framework.Build.props`: Specifies the default target framework (currently `net9.0`)
- `Hexalith.Package.props`: Defines common package metadata for NuGet packages
- `Hexalith.Builds.sln`: Solution file

### Package Management
- `Directory.Packages.props`: Centralizes package version management
- `package.json`: Node.js package configuration for release management

### Code Style and Analysis
- `Hexalith.globalconfig`: Global configuration file for C# code style
- `stylecop.json`: StyleCop configuration settings

### AI Assistant Rules
- `.clinerules`: Rules for the Cline AI assistant
- `.cursorrules`: Rules for the Cursor AI assistant
- `ai-assistant-instructions.md`: Common instructions for AI assistants
- `ai-commit-prompt.md`: Prompt for generating commit messages
- `.github/copilot-instructions.md`: Instructions for GitHub Copilot

### Tools and Templates
- [`Tools/`](Tools/README.md): Tools for repository management
  - `builds-submodule-init.ps1`: Script for initializing the Git submodule
- [`Github/`](Github/): Directory containing GitHub workflow templates:
  - [`build-packages/`](Github/build-packages/README.md): Templates for building .NET packages
  - [`create-release/`](Github/create-release/README.md): Templates for creating releases with semantic versioning
  - [`initialize-build/`](Github/initialize-build/README.md): Templates for initializing builds
  - [`initialize-dotnet/`](Github/initialize-dotnet/README.md): Templates for initializing .NET projects
  - [`publish-packages/`](Github/publish-packages/README.md): Templates for publishing packages
  - [`unit-tests/`](Github/unit-tests/README.md): Templates for running unit tests
  - [`version/`](Github/version/README.md): Templates for versioning

### Workflows
- `.github/workflows/build-release.yml`: Builds packages and creates releases
- `.github/workflows/copy-ai-assistant-instructions.yml`: Syncs AI assistant instructions

## Usage

### Importing Build Properties

To use the standardized build properties:

```xml
<Import Project="$(MSBuildThisFileDirectory)..\Hexalith.Builds\Hexalith.Build.props" />
```

For projects to be packaged as NuGet packages:

```xml
<Import Project="$(MSBuildThisFileDirectory)..\Hexalith.Builds\Hexalith.Package.props" />
```

### Adding as a Git Submodule

Add this repository as a Git submodule:

```powershell
# From your repository root:
.\Hexalith.Builds\Tools\builds-submodule-init.ps1
```

This script initializes and configures the Hexalith.Builds Git submodule.

### Environment Detection

The build system automatically detects different environments:
- `CIBuild`: Set to `true` in GitHub Actions or Azure DevOps
- `IDEBuild`: Set to `true` in Visual Studio, ReSharper, VS Code, or Cursor

### Project References vs Package References

For local development, use project references instead of package references:

```xml
<UseProjectReference>true</UseProjectReference>
```

This is automatically set when `IDEBuild` is `true` and `CIBuild` is not `true`.

## Version Management

Versions are managed centrally in `Hexalith.Version.props` and derived from git tags.

For non-release builds, a suffix is added:
- GitHub builds: `preview-{GITHUB_RUN_NUMBER}`
- Local builds: Timestamp in format `yyyyMMddHHmmss`

To create a new version:
1. Create and push a tag with format `v*.*.*` (e.g., `v1.2.3`)
2. GitHub Actions will update `Hexalith.Version.props`
3. All referencing projects will use the new version

## GitHub Workflow Templates

The repository provides reusable workflow templates in the `Github` directory for:
- Building .NET packages
- Creating releases with semantic versioning
- Initializing builds and .NET projects
- Publishing packages
- Running unit tests
- Versioning

Use these templates by referencing them in your own workflow files.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
