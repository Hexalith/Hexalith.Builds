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

## Repository Structure

The repository contains the following key files:

### Version Control

- `Hexalith.Version.props`: Defines the current version of Hexalith (currently `1.56.0`), used across all projects

### Build Configuration

- `Hexalith.Build.props`: Main build properties file, imports environment and version properties
- `Environment.Build.props`: Defines build environment variables to detect CI/CD pipelines and IDE environments
- `Framework.Build.props`: Specifies the default target framework (currently `net9.0`)
- `Hexalith.Package.props`: Defines common package metadata for NuGet packages

### Package Management

- `Directory.Packages.props`: Centralizes package version management for all dependencies using the central package management feature

### Code Style and Analysis

- `Stylecop.Build.props`: Configures StyleCop analyzers for code style enforcement
- `.stylecop.json`: Contains StyleCop configuration settings like indentation and documentation rules
- `.stylecop.ruleset`: Defines the rule set for StyleCop analyzers

### Workflows

- `.github/workflows/update-version.yml`: GitHub Actions workflow that automatically updates the version in `Hexalith.Version.props` when a new version tag is pushed

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

The version of all Hexalith components is managed centrally in the `Hexalith.Version.props` file. When a new version is tagged in GitHub (with format `v*.*.*`), the GitHub Actions workflow will automatically update this version.

For non-release builds, a suffix is added to the version number:
- For GitHub builds: `preview-{GITHUB_RUN_NUMBER}`
- For local builds: A timestamp in the format `yyyyMMddHHmmss`

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
