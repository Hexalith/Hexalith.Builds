// <copyright file="CompositionDeclarationClosure.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;

using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>Validates authoritative source or release declarations and selects the composed dependency closure.</summary>
public static class CompositionDeclarationClosure
{
    private static readonly string[] _referenceOwners = ["eventstore", "tenants", "memories"];
    private static readonly string[] _completeOwners = ["eventstore", "tenants", "parties", "folders", "projects", "mcpcli", "memories"];

    [ThreadStatic]
    private static (CompositionRunPlan Plan, IReadOnlyList<PlatformModuleDeclaration> Declarations)? _verifiedModelCall;

    /// <summary>Loads the same owner declarations for startup coordination and the Platform model.</summary>
    /// <param name="options">The selected declaration inputs and release provenance.</param>
    /// <returns>The validated dependency closure.</returns>
    /// <exception cref="InvalidDataException">An owner declaration or its release provenance is unavailable or inconsistent.</exception>
    public static IReadOnlyList<PlatformModuleDeclaration> Load(CompositionDeclarationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _verifiedModelCall switch
        {
            { } verified when options.ActiveModule == PlatformManifestCommand.CompleteEnvironmentModule
                && ReferenceEquals(options.Mapping, verified.Plan.SourceMapping)
                && ReferenceEquals(options.DeclarationPaths, verified.Plan.PlatformDeclarationPaths)
                && string.Equals(options.Workspace, verified.Plan.Workspace, StringComparison.Ordinal) => verified.Declarations,
            _ => LoadCore(options),
        };
    }

    /// <summary>Loads declarations in the selected consumer context, materializing the verified pin privately for a cold package read.</summary>
    /// <param name="options">The selected declaration inputs, consumer and release provenance.</param>
    /// <param name="cancellationToken">The original command cancellation token.</param>
    /// <returns>The validated dependency closure, detached from any temporary catalog workspace.</returns>
    /// <exception cref="InvalidDataException">The selected consumer, pinned catalog or owner release provenance is unavailable or inconsistent.</exception>
    public static Task<IReadOnlyList<PlatformModuleDeclaration>> LoadForConsumerAsync(CompositionDeclarationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.ActiveModule == PlatformManifestCommand.CompleteEnvironmentModule
            || (options.Mapping.Mode == WorkspaceMode.Package && options.ConsumerProjectName is null && options.Workspace is null
                && HasCanonicalSelector(options, cancellationToken)))
        {
            options = options with
            {
                ActiveModule = PlatformManifestCommand.CompleteEnvironmentModule,
                ConsumerProjectName = CompositionDeclarationOptions.PlatformCompositionProjectName,
            };
        }

        return options.Mapping.Mode == WorkspaceMode.Package && options.ConsumerProjectName is not null && options.Workspace is null
            ? LoadColdConsumerAsync(options, cancellationToken)
            : Task.FromResult(LoadCore(options));
    }

    /// <summary>Returns the declared module identity.</summary>
    /// <param name="declaration">The validated module.</param>
    /// <returns>The module identity.</returns>
    public static string Id(JsonElement declaration) => declaration.GetProperty("identity").GetProperty("moduleId").GetString()!;

    /// <summary>Resolves task paths against their mapped owner repository or released declaration artifacts.</summary>
    /// <param name="mapping">The command-selected source/package mapping.</param>
    /// <param name="declaration">The authoritative declaring module.</param>
    /// <returns>The declaring repository or release-artifact root.</returns>
    public static string TaskRepositoryRoot(SourceMapping mapping, PlatformModuleDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(declaration);
        SourceMappingEntry? owner = mapping.Entries.SingleOrDefault(entry => entry.Identity.Equals(Repository(Id(declaration.Declaration)), StringComparison.OrdinalIgnoreCase));
        if (owner?.Path is { } root)
        {
            return root;
        }

        string source = declaration.FullPath ?? Path.GetFullPath(declaration.Source);
        return mapping.Mode == WorkspaceMode.Source
            ? ManifestPathValidator.FindRepositoryRoot(source)
            : Path.GetDirectoryName(source)!;
    }

    /// <summary>Returns the source repository identity for an owner.</summary>
    /// <param name="module">The module identity.</param>
    /// <returns>The repository identity.</returns>
    public static string Repository(string module) => module switch
    {
        "eventstore" => "Hexalith.EventStore",
        "tenants" => "Hexalith.Tenants",
        "memories" => "Hexalith.Memories",
        "mcpcli" => "Hexalith.McpCli",
        _ => "Hexalith." + char.ToUpperInvariant(module[0]) + module[1..],
    };

    /// <summary>Verifies the AppHost will use the complete closure selected by startup coordination.</summary>
    /// <param name="plan">The runner's plan including its selected declaration content identity.</param>
    /// <returns>The complete closure verified against the plan.</returns>
    /// <exception cref="InvalidDataException">The complete declaration set changed after startup coordination.</exception>
    internal static IReadOnlyList<PlatformModuleDeclaration> VerifyCompleteEnvironmentPlan(CompositionRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        SourceMapping mapping = plan.SourceMapping ?? throw new InvalidDataException("The complete Platform run has no verified source mapping.");
        IReadOnlyList<PlatformModuleDeclaration> closure = LoadCore(new CompositionDeclarationOptions(mapping, plan.PlatformDeclarationPaths)
        {
            ActiveModule = plan.PlatformActiveModule,
            QualificationOwners = plan.QualificationOwners ?? [],
            Workspace = plan.Workspace,
        });
        string identity = TestEnvironment.TestEnvironmentDescriptorWriter.CompositionIdentity(closure, plan.SelectedTest, plan.QualificationOwners);
        return string.Equals(identity, plan.CompositionContentIdentity, StringComparison.Ordinal)
            ? closure
            : throw new InvalidDataException("The complete Platform declaration closure changed after startup coordination.");
    }

    /// <summary>Supplies the verified declarations only to the synchronous call for this AppHost plan.</summary>
    /// <param name="plan">The verified run plan.</param>
    /// <param name="closure">The verified closure, or null for individual-module composition.</param>
    /// <param name="compose">The synchronous Platform composition call.</param>
    internal static void ComposeVerifiedModel(CompositionRunPlan plan, IReadOnlyList<PlatformModuleDeclaration>? closure, Action compose)
    {
        (CompositionRunPlan Plan, IReadOnlyList<PlatformModuleDeclaration> Declarations)? previous = _verifiedModelCall;
        try
        {
            _verifiedModelCall = closure is null ? null : (plan, closure);
            compose();
        }
        finally
        {
            _verifiedModelCall = previous;
        }
    }

    private static bool HasCanonicalSelector(CompositionDeclarationOptions options, CancellationToken cancellationToken) =>
        options.DeclarationPaths is [string path]
        && PlatformManifestValidator.Validate([path], cancellationToken).Declarations is [{ } declaration]
        && string.Equals(declaration.FullPath, CanonicalSelector(options), FilesystemPathRules.Comparison(options.Mapping.Root))
        && Id(declaration.Declaration) == PlatformManifestCommand.CompleteEnvironmentModule;

    private static string CanonicalSelector(CompositionDeclarationOptions options) =>
        Path.GetFullPath(Path.Combine(options.Mapping.Root, PlatformManifestCommand.OwnerManifestPath));

    private static IReadOnlyList<PlatformModuleDeclaration> LoadCore(CompositionDeclarationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return LoadDeclarations(options);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or XmlException)
        {
            throw new InvalidDataException($"{options.PlatformRoot}: declaration or release provenance is malformed: {exception.Message}", exception);
        }
    }

    private static async Task<IReadOnlyList<PlatformModuleDeclaration>> LoadColdConsumerAsync(CompositionDeclarationOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConsumerProjectName);
        if (string.IsNullOrWhiteSpace(options.Mapping.PlatformIdentity?.CatalogCommit))
        {
            throw new InvalidDataException($"{options.PlatformRoot}: consumer '{options.ConsumerProjectName}' requires a verified Builds catalog pin.");
        }

        string temporary = Path.Combine(Path.GetTempPath(), "hexalith-closure-catalog-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        IReadOnlyList<PlatformModuleDeclaration>? declarations = null;
        try
        {
            CompositionWorkspace.CreatePrivateDirectory(temporary);
            await CatalogVersionMaterializer.WriteAsync(options.Mapping, temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            declarations = LoadCore(options with { Workspace = temporary });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }

        if (!await CompositionWorkspace.TryDeleteAsync(temporary, CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidDataException($"Temporary pinned catalog workspace '{temporary}' could not be removed.", failure);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return declarations!;
    }

    private static IReadOnlyList<PlatformModuleDeclaration> LoadDeclarations(CompositionDeclarationOptions options)
    {
        IReadOnlyList<string> qualificationOwners = CompositionQualificationOwners.Normalize(options.QualificationOwners);
        IReadOnlyList<PlatformModuleDeclaration> suppliedDeclarations = options.DeclarationPaths.Count == 0
            ? []
            : ValidateDeclarations(options.DeclarationPaths);
        if (suppliedDeclarations.Any(declaration => Id(declaration.Declaration) == PlatformManifestCommand.CompleteEnvironmentModule)
            || options.ActiveModule == PlatformManifestCommand.CompleteEnvironmentModule)
        {
            return LoadCompleteEnvironment(options, suppliedDeclarations);
        }

        List<string> paths = [];
        StringComparer pathComparer = FilesystemPathRules.Comparison(options.Mapping.Root) == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        Dictionary<string, string> authenticatedReleaseHashes = [with(pathComparer)];
        Dictionary<string, PlatformModuleDeclaration> snapshots = [with(StringComparer.Ordinal)];
        foreach (string owner in _referenceOwners)
        {
            PlatformModuleDeclaration declaration = LoadOwner(options, owner, authenticatedReleaseHashes);
            paths.Add(declaration.Source);
            if (options.Mapping.Mode == WorkspaceMode.Package)
            {
                snapshots.Add(owner, declaration);
            }
        }

        // A supplied reference-owner file is already part of the source set or release snapshot.
        foreach (string suppliedPath in options.DeclarationPaths)
        {
            string path = Path.GetFullPath(suppliedPath);
            if (paths.Contains(path, pathComparer))
            {
                continue;
            }

            if (options.Mapping.Mode == WorkspaceMode.Package)
            {
                JsonElement[] supplied =
                [
                    .. suppliedDeclarations.Where(declaration => pathComparer.Equals(declaration.FullPath, path))
                        .Select(declaration => declaration.Declaration),
                ];
                for (int index = 0; index < supplied.Length; index++)
                {
                    JsonElement module = supplied[index];
                    if (snapshots.TryGetValue(Id(module), out PlatformModuleDeclaration? snapshot) && !JsonElement.DeepEquals(module, snapshot.Declaration))
                    {
                        throw new InvalidDataException($"{path}: {Id(module)}: modules[{index}]: supplied owner declaration conflicts with authoritative release snapshot '{snapshot.Source}'.");
                    }
                }

                if (supplied.All(module => snapshots.ContainsKey(Id(module))))
                {
                    continue;
                }

                // A mixed document can replace an equal snapshot, but never change its release content.
                foreach (JsonElement module in supplied)
                {
                    if (snapshots.TryGetValue(Id(module), out PlatformModuleDeclaration? snapshot))
                    {
                        _ = paths.Remove(snapshot.Source);
                    }
                }
            }

            paths.Add(path);
        }

        if (options.TimesheetsEnabled)
        {
            string candidate = Path.GetFullPath(Path.Combine(options.PlatformRoot, "eng", "timesheets-deployment", "hexalith.module-manifest.v2.json"));
            if (!paths.Contains(candidate, pathComparer))
            {
                paths.Add(candidate);
            }
        }

        IReadOnlyList<PlatformModuleDeclaration> declarations = ValidateDeclarations(paths, authenticatedReleaseHashes);
        Dictionary<string, PlatformModuleDeclaration> byId = declarations.ToDictionary(declaration => Id(declaration.Declaration), StringComparer.Ordinal);
        HashSet<string> selected = [with(StringComparer.Ordinal)];
        HashSet<string> visiting = [with(StringComparer.Ordinal)];
        void Select(string identity) => SelectDependency(
            identity,
            dependency => byId.GetValueOrDefault(dependency)
                ?? throw new InvalidDataException($"{dependency}: identity.dependencies: owner declaration is unavailable."),
            selected,
            visiting);

        if (options.ActiveModule is not null)
        {
            Select(options.ActiveModule);
        }

        foreach (string owner in qualificationOwners)
        {
            Select(owner);
        }

        if (options.TimesheetsEnabled)
        {
            Select("timesheets");
        }

        bool domain = options.DomainModule ?? (options.ActiveModule is not null && options.ActiveModule is not ("eventstore" or "memories" or "mcpcli" or "platform" or "builds"));
        if (options.ActiveModule is null || domain)
        {
            foreach (string owner in _referenceOwners)
            {
                Select(owner);
            }
        }

        return [.. declarations.Where(declaration => selected.Contains(Id(declaration.Declaration)))];
    }

    private static IReadOnlyList<PlatformModuleDeclaration> LoadCompleteEnvironment(
        CompositionDeclarationOptions options,
        IReadOnlyList<PlatformModuleDeclaration> supplied)
    {
        string manifest = CanonicalSelector(options);
        StringComparison comparison = FilesystemPathRules.Comparison(options.Mapping.Root);
        if (options.Mapping is not { HasGit: true, PlatformIdentity: { IsPlatformRepository: true, CatalogCommit.Length: > 0 } platform }
            || options.Mapping.IdentityFindings.Count != 0
            || !string.Equals(platform.PlatformPath, options.Mapping.Root, comparison)
            || !string.Equals(options.PlatformRoot, options.Mapping.Root, comparison)
            || supplied is not [{ } selector]
            || Id(selector.Declaration) != PlatformManifestCommand.CompleteEnvironmentModule
            || !string.Equals(selector.FullPath, manifest, comparison)
            || !RepositoryPathResolver.TryResolveExistingFile(options.Mapping.Root, PlatformManifestCommand.OwnerManifestPath, out string resolved)
            || !RepositoryPathResolver.TryResolvePathWithinRoot(options.Mapping.Root, ".", out string physicalRoot)
            || !string.Equals(Path.GetFullPath(Path.Combine(physicalRoot, PlatformManifestCommand.OwnerManifestPath)), resolved, comparison))
        {
            throw new InvalidDataException($"The complete 'platform' selector requires '{manifest}' in the verified Platform root.");
        }

        JsonElement identity = selector.Declaration.GetProperty("identity");
        if (identity.GetProperty("servers").GetArrayLength() > 0
            || !Dependencies(selector).ToHashSet(StringComparer.Ordinal).SetEquals(_completeOwners))
        {
            throw new InvalidDataException($"{manifest}: the 'platform' selector must be serverless and select {string.Join(", ", _completeOwners)}.");
        }

        // The complete selector always uses the same pinned consumer view as the Platform model.
        options = options with { ConsumerProjectName = CompositionDeclarationOptions.PlatformCompositionProjectName };
        Dictionary<string, string> authenticatedReleaseHashes = [with(StringComparer.Ordinal)];
        Dictionary<string, PlatformModuleDeclaration> byId = new(StringComparer.Ordinal)
        {
            [Id(selector.Declaration)] = selector with { Source = manifest },
        };
        HashSet<string> selected = [with(StringComparer.Ordinal)];
        HashSet<string> visiting = [with(StringComparer.Ordinal)];
        SelectDependency(Id(selector.Declaration), GetOwner, selected, visiting);
        IReadOnlyList<PlatformModuleDeclaration> validated = ValidateDeclarations(byId.Values.Select(declaration => declaration.Source), authenticatedReleaseHashes);
        bool unchanged = validated.Count == byId.Count && validated.All(declaration =>
            byId.TryGetValue(Id(declaration.Declaration), out PlatformModuleDeclaration? selectedOwner)
            && string.Equals(declaration.FullPath, selectedOwner.Source, comparison)
            && declaration.SourceHash == selectedOwner.SourceHash);
        return unchanged
            ? [.. validated.Select(declaration => declaration with { Source = declaration.FullPath! })]
            : throw new InvalidDataException("The complete Platform declarations changed during dependency selection.");

        PlatformModuleDeclaration GetOwner(string owner)
        {
            if (owner is not (PlatformManifestCommand.CompleteEnvironmentModule or "conversations") && !_completeOwners.Contains(owner, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"{owner}: identity.dependencies: no owner declaration is enrolled in the complete environment.");
            }

            if (!byId.TryGetValue(owner, out PlatformModuleDeclaration? declaration))
            {
                declaration = LoadOwner(options, owner, authenticatedReleaseHashes, requireDirect: true);
                if (owner == "projects" && !Dependencies(declaration).Contains("conversations", StringComparer.Ordinal))
                {
                    throw new InvalidDataException($"{Repository(owner)}: identity.dependencies: the complete environment requires Conversations through Projects.");
                }

                if (owner == PlatformManifestCommand.McpCliModule && declaration.Declaration.GetProperty("identity").GetProperty("servers").GetArrayLength() > 0)
                {
                    throw new InvalidDataException($"{Repository(owner)}: the run-scoped McpCli declaration must be serverless.");
                }

                byId.Add(owner, declaration);
            }

            return declaration;
        }
    }

    private static void SelectDependency(
        string owner,
        Func<string, PlatformModuleDeclaration> getOwner,
        HashSet<string> selected,
        HashSet<string> visiting)
    {
        if (selected.Contains(owner))
        {
            return;
        }

        PlatformModuleDeclaration declaration = getOwner(owner);
        if (!visiting.Add(owner))
        {
            throw new InvalidDataException($"{declaration.Source}: {owner}: identity.dependencies: dependency cycle is not supported.");
        }

        foreach (string dependency in Dependencies(declaration))
        {
            SelectDependency(dependency, getOwner, selected, visiting);
        }

        _ = visiting.Remove(owner);
        _ = selected.Add(owner);
    }

    private static IEnumerable<string> Dependencies(PlatformModuleDeclaration declaration) => declaration.Declaration
        .GetProperty("identity").GetProperty("dependencies").EnumerateArray().Select(dependency => dependency.GetString()!);

    private static PlatformModuleDeclaration LoadOwner(
        CompositionDeclarationOptions options,
        string owner,
        Dictionary<string, string> authenticatedReleaseHashes,
        bool requireDirect = false)
    {
        string repository = Repository(owner);
        string path;
        if (options.Mapping.Mode == WorkspaceMode.Source)
        {
            SourceMappingEntry? entry = options.Mapping.Entries.SingleOrDefault(e => e.Identity.Equals(repository, StringComparison.OrdinalIgnoreCase));
            string reference = Path.Combine("references", repository);
            if (entry is not { Origin: "source", Path: not null }
                || !RepositoryPathResolver.TryResolveExistingFile(entry.Path, PlatformManifestCommand.OwnerManifestPath, out _)
                || (requireDirect && (!string.Equals(entry.Path, Path.Combine(options.Mapping.Root, reference), FilesystemPathRules.Comparison(options.Mapping.Root))
                    || !RepositoryPathResolver.TryResolveExistingFile(options.Mapping.Root, Path.Combine(reference, PlatformManifestCommand.OwnerManifestPath), out _))))
            {
                string required = owner == PlatformManifestCommand.McpCliModule
                    ? $" Required tool project: '{PlatformManifestCommand.McpCliProjectPath(options.Mapping)}'."
                    : string.Empty;
                throw new InvalidDataException($"{repository}: identity.dependencies: mapped source declaration '{PlatformManifestCommand.OwnerManifestPath}' is unavailable.{required}");
            }

            path = Path.GetFullPath(Path.Combine(entry.Path, PlatformManifestCommand.OwnerManifestPath));
        }
        else
        {
            path = AuthenticateRelease(options, owner, repository, authenticatedReleaseHashes);
        }

        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([path], authenticatedReleaseHashes);
        IReadOnlyList<PlatformModuleDeclaration> declarations = result.IsValid
            ? result.Declarations!
            : throw new InvalidDataException($"{repository}: '{path}': {ValidationError(result)}");
        return declarations is [{ } declaration] && Id(declaration.Declaration) == owner
            ? declaration with { Source = path }
            : throw new InvalidDataException($"{repository}: '{path}': mapped declaration must identify owner '{owner}' exactly once.");
    }

    private static string AuthenticateRelease(
        CompositionDeclarationOptions options,
        string owner,
        string repository,
        Dictionary<string, string> authenticatedReleaseHashes)
    {
        string relativeDirectory = Path.Combine("src", "Hexalith.Platform.Composition", "Declarations");
        string snapshotRelative = Path.Combine(relativeDirectory, owner + ".json");
        string provenanceRelative = Path.Combine(relativeDirectory, "provenance.json");
        string snapshot = Path.Combine(options.PlatformRoot, snapshotRelative);
        string provenance = Path.Combine(options.PlatformRoot, provenanceRelative);
        if (!RepositoryPathResolver.TryResolveExistingFile(options.PlatformRoot, snapshotRelative, out _)
            || !RepositoryPathResolver.TryResolveExistingFile(options.PlatformRoot, provenanceRelative, out _))
        {
            throw new InvalidDataException($"{repository}: release snapshot '{snapshot}' or provenance '{provenance}' is unavailable.");
        }

        using JsonDocument sources = JsonDocument.Parse(File.ReadAllText(provenance));
        if (sources.RootElement.ValueKind != JsonValueKind.Object
            || !sources.RootElement.TryGetProperty(owner, out JsonElement source) || source.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{repository}: release snapshot provenance '{provenance}' has no entry for '{owner}'.");
        }

        foreach (string field in new[] { "repository", "commit", "source", "sha256", "version" })
        {
            if (!source.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new InvalidDataException($"{repository}: release snapshot provenance '{provenance}': {field}: source provenance is unavailable.");
            }
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(snapshot)));
        if (source.GetProperty("repository").GetString() != repository
            || source.GetProperty("source").GetString() != PlatformManifestCommand.OwnerManifestPath
            || source.GetProperty("sha256").GetString() != hash)
        {
            throw new InvalidDataException($"{repository}: release snapshot '{snapshot}' differs from its source provenance.");
        }

        string catalogVersion = options.CatalogVersion(owner, snapshot);
        string provenanceVersion = source.GetProperty("version").GetString()!;
        string provenanceCommit = source.GetProperty("commit").GetString()!;
        if (!options.ReleaseCommits.TryGetValue(owner + "/" + catalogVersion, out string? releaseCommit)
            || !options.ReleaseSnapshotHashes.TryGetValue(owner + "/" + catalogVersion, out string? releasedHash)
            || !string.Equals(provenanceVersion, catalogVersion, StringComparison.Ordinal)
            || !string.Equals(provenanceCommit, releaseCommit, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hash, releasedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{repository}: release snapshot '{snapshot}': provenance version '{provenanceVersion}' at commit '{provenanceCommit}' does not match Builds catalog version '{catalogVersion}', release commit '{releaseCommit ?? "unavailable"}', or its released v2 declaration (absent until the owner releases one).");
        }

        authenticatedReleaseHashes.Add(Path.GetFullPath(snapshot), hash);
        return snapshot;
    }

    private static IReadOnlyList<PlatformModuleDeclaration> ValidateDeclarations(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, string>? authenticatedReleaseHashes = null)
    {
        PlatformManifestValidationResult result = authenticatedReleaseHashes is null
            ? PlatformManifestValidator.Validate(paths)
            : PlatformManifestValidator.Validate(paths, authenticatedReleaseHashes);
        return result.IsValid
            ? result.Declarations!
            : throw new InvalidDataException(ValidationError(result));
    }

    private static string ValidationError(PlatformManifestValidationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Source}: {diagnostic.Field}: {diagnostic.Message}"));
}