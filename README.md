# Hexalith.Builds

Common project for building Hexalith applications, modules and libraries.

## Overview

The Hexalith.Builds repository centralizes all build configurations, property definitions, package management, and code style settings for the Hexalith ecosystem. It ensures consistency across all Hexalith repositories by providing standard build files that can be imported into any Hexalith project.

## Purpose

This repository serves several key purposes:

- Centralizing version management for all Hexalith projects
- Maintaining consistent package dependencies and versions
- Enforcing uniform code style and analysis rules
- Standardizing build configurations
- Providing common build properties for development environments
- Ensuring consistent AI assistant rules and commit message formats

## Repository Structure

The repository contains the following key files:

### Version Control

- `Hexalith.Version.props`: Defines the current version of all Hexalith projects based on the latest git tag in this repository.
- Current : [![Version](https://img.shields.io/github/v/tag/Hexalith/Hexalith.Builds?filter=v*)](https://github.com/Hexalith/Hexalith.Builds/tags)

### Build Configuration

- `Hexalith.Build.props`: Main build properties file, imports environment and version properties
- `Environment.Build.props`: Defines build environment variables to detect CI/CD pipelines and IDE environments
- `Framework.Build.props`: Specifies the default target framework (currently `net9.0`)
- `Hexalith.Package.props`: Defines common package metadata for NuGet packages
- `Hexalith.Builds.sln`: Solution file for the Hexalith.Builds project

### Package Management

- `Directory.Packages.props`: Centralizes package version management for all dependencies using the central package management feature
- `package.json`: Node.js package configuration for release management

### Code Style and Analysis

- `Hexalith.globalconfig`: Global configuration file for C# code style and formatting rules
- `stylecop.json`: Contains StyleCop configuration settings like indentation and documentation rules

### AI Assistant Rules

- `.clinerules`: Rules for the Cline AI assistant
- `.cursorrules`: Rules for the Cursor AI assistant
- `ai-assistant-instructions.md`: Common instructions for AI assistants
- `ai-commit-prompt.md`: Prompt for generating commit messages following the Conventional Commits specification
- `.github/copilot-instructions.md`: Instructions for GitHub Copilot

### Tools and Templates

- `Tools/builds-submodule-init.ps1`: PowerShell script for initializing the Hexalith.Builds Git submodule
- `Github/`: Directory containing GitHub workflow templates for building and deploying projects
  - `build-packages/`: Templates for building .NET packages
  - `create-release/`: Templates for creating releases with semantic versioning
  - `initialize-build/`: Templates for initializing builds
  - `initialize-dotnet/`: Templates for initializing .NET projects
  - `publish-packages/`: Templates for publishing packages
  - `unit-tests/`: Templates for running unit tests
  - `version/`: Templates for versioning

### Workflows

- `.github/workflows/build-release.yml`: GitHub Actions workflow that builds packages and creates releases when changes are pushed to specific branches
- `.github/workflows/copy-ai-assistant-instructions.yml`: GitHub Actions workflow that copies the content of `ai-assistant-instructions.md` to all AI assistant rule files when changes are made

## Usage

### Importing Build Properties

To use the standardized build properties in a Hexalith project, add the following to your project file:

```xml
<Import Project="$(MSBuildThisFileDirectory)..\Hexalith.Builds\Hexalith.Build.props" />
```

For projects that will be packaged and published as NuGet packages:

```xml
<Import Project="$(MSBuildThisFileDirectory)..\Hexalith.Builds\Hexalith.Package.props" />
```

### Adding as a Git Submodule

You can add this repository as a Git submodule to your project using the provided PowerShell script:

```powershell
# From your repository root:
.\Hexalith.Builds\Tools\builds-submodule-init.ps1
```

This script will:
1. Initialize the Hexalith.Builds Git submodule from the GitHub repository
2. Update the submodule to the latest commit referenced in your repository
3. Checkout the main branch in the Hexalith.Builds submodule

### Environment Detection

The build system automatically detects different build environments:

- `CIBuild`: Set to `true` when building in GitHub Actions or Azure DevOps
- `IDEBuild`: Set to `true` when building inside Visual Studio, ReSharper, Visual Studio Code, or Cursor

### Project References vs Package References

When developing locally in an IDE, the system can automatically use project references instead of package references if `UseProjectReference` is set to `true`:

```xml
<UseProjectReference>true</UseProjectReference>
```

This is automatically set when `IDEBuild` is `true` and `CIBuild` is not `true`.

## Version Management

The version of all Hexalith components is managed centrally in the `Hexalith.Version.props` file. The version number is derived from git tags in the repository.

When a new version is tagged in GitHub (with format `v*.*.*`), the GitHub Actions workflow will automatically update the `HexalithVersion` property in the `Hexalith.Version.props` file.

For non-release builds, a suffix is added to the version number:
- For GitHub builds: `preview-{GITHUB_RUN_NUMBER}`
- For local builds: A timestamp in the format `yyyyMMddHHmmss`

To create a new version:
1. Create and push a new tag with format `v*.*.*` (e.g., `v1.2.3`)
2. The GitHub workflow will automatically update the version in `Hexalith.Version.props`
3. All projects referencing this repository will use the new version

## GitHub Workflow Templates

The repository provides reusable GitHub workflow templates in the `Github` directory:

- [`build-packages`](Github/build-packages/README.md): Template for building .NET packages with specific version numbers
- [`create-release`](Github/create-release/README.md): Template for creating releases using semantic versioning
- [`initialize-build`](Github/initialize-build/README.md): Templates for initializing builds
- [`initialize-dotnet`](Github/initialize-dotnet/README.md): Templates for initializing .NET projects
- [`publish-packages`](Github/publish-packages/README.md): Templates for publishing packages
- [`unit-tests`](Github/unit-tests/README.md): Templates for running unit tests
- [`version`](Github/version/README.md): Templates for versioning

You can use these templates in your GitHub workflows by referencing them from your own workflow files.

## AI Assistant Rules

The repository includes rules and instructions for various AI assistants:

- `.clinerules` and `.cursorrules`: Rules for Cline and Cursor AI assistants
- `ai-assistant-instructions.md`: Common instructions for all AI assistants
- `ai-commit-prompt.md`: Prompt for generating commit messages following the Conventional Commits specification

These files ensure consistent coding style and practices across all Hexalith projects when using AI assistants.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
