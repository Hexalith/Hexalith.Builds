// <copyright file="ManagedBuildPreparationService.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;

/// <summary>Prepares explicit managed package builds without changing any checkout.</summary>
public static partial class ManagedBuildPreparationService
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Authenticates accepted delivery, then prepares runtime-free producer imports.</summary>
    /// <param name="manifestPath">The real module declaration.</param>
    /// <param name="authorityPath">The closed, revision-bound authority receipt.</param>
    /// <param name="workspace">A fresh private producer output.</param>
    /// <param name="qualification">Whether this is explicitly local-only qualification.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The structured preparation result without resources or run identity.</returns>
    public static Task<ToolCommandResult> PrepareAsync(string manifestPath, string authorityPath, string workspace, bool qualification, CancellationToken cancellationToken)
        => BuildPreparationService.PrepareCoreAsync(manifestPath, WorkspaceMode.Package, workspace, cancellationToken, () => ResolveAsync(manifestPath, authorityPath, qualification, cancellationToken));

    /// <summary>Reads and binds all independently observable authority inputs before evaluation.</summary>
    /// <param name="manifestPath">The real module declaration.</param>
    /// <param name="authorityPath">The authority receipt.</param>
    /// <param name="qualification">Whether only candidate authority is accepted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="verifyAccepted">Internal behavioral-test seam; production uses the packaged verifier.</param>
    /// <param name="toolVersionOverride">Internal source-test version seam; production uses the executing assembly.</param>
    /// <returns>The validated package mapping.</returns>
    /// <exception cref="InvalidDataException">An authority input is invalid and becomes a structured refusal.</exception>
    /// <exception cref="WorkspaceMappingException">The checked authority or dependency identity is unavailable.</exception>
    internal static async Task<SourceMapping> ResolveAsync(string manifestPath, string authorityPath, bool qualification, CancellationToken cancellationToken, Func<ManagedBuildAuthority, string, CancellationToken, Task>? verifyAccepted = null, string? toolVersionOverride = null)
    {
        try
        {
            string[] forbiddenEnvironment = ["GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "CustomBeforeDirectoryBuildProps", "CustomAfterMicrosoftCommonProps", "CustomBeforeDirectoryBuildTargets", "CustomAfterDirectoryBuildTargets", "AfterMicrosoftNETSdkTargets"];
            if (forbiddenEnvironment.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                || (Environment.GetEnvironmentVariable("Configuration") is { Length: > 0 } configurationSelection && configurationSelection != "Release"))
            {
                throw new InvalidDataException("Ambient Git selectors, producer import overrides and non-Release preparation are forbidden.");
            }

            RejectReparseTraversal(authorityPath);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllBytesAsync(authorityPath, cancellationToken).ConfigureAwait(false));
            RejectDuplicates(json.RootElement);
            ManagedBuildAuthority authority = json.RootElement.Deserialize<ManagedBuildAuthority>(_options)
                ?? throw new InvalidDataException("Managed authority is empty.");
            string kind = qualification ? "candidate-qualification" : "accepted-delivery";
            if (authority.Schema != "hexalith.managed-build.v1" || authority.Kind != kind
                || !Repository().IsMatch(authority.Repository) || !StableTag().IsMatch(authority.BuildsTag)
                || !Hash(authority.ConfigurationDigest) || !Oid(authority.ModuleHead) || !Oid(authority.BuildsCommit)
                || !Oid(authority.OriginalBuildsGitlink) || !Oid(authority.PlatformCommit) || !Oid(authority.PlatformCatalogCommit)
                || string.IsNullOrWhiteSpace(authority.PlatformReleaseTag) || string.IsNullOrWhiteSpace(authority.ProducerPin))
            {
                throw new InvalidDataException("Missing, malformed or differently scoped managed authority.");
            }

            string root = Path.GetFullPath(authority.ModuleRoot);
            string builds = Path.GetFullPath(authority.BuildsRoot);
            if (root != authority.ModuleRoot || builds != authority.BuildsRoot)
            {
                throw new InvalidDataException("Authority roots must be canonical absolute paths.");
            }

            RejectReparseTraversal(root);
            RejectReparseTraversal(builds);
            string origin = await GitAsync(root, cancellationToken, "config", "--get", "remote.origin.url").ConfigureAwait(false);
            string expectedOrigin = "https://github.com/" + authority.Repository;
            if (!RepositoryPathResolver.TryResolveExistingFile(root, Path.GetRelativePath(root, Path.GetFullPath(manifestPath)), out _)
                || !SamePath(await GitAsync(root, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false), root)
                || (await GitAsync(root, cancellationToken, "rev-parse", "--show-superproject-working-tree").ConfigureAwait(false)).Length != 0
                || await GitAsync(root, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false) != authority.ModuleHead
                || await GitAsync(root, cancellationToken, "write-tree").ConfigureAwait(false) != await GitAsync(root, cancellationToken, "rev-parse", "HEAD^{tree}").ConfigureAwait(false)
                || (await GitAsync(root, cancellationToken, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all").ConfigureAwait(false)).Length != 0
                || (origin != expectedOrigin && origin != expectedOrigin + ".git"))
            {
                throw new InvalidDataException("Managed preparation requires the exact clean standalone module revision and origin.");
            }

            string recorded = await GitAsync(root, cancellationToken, "ls-tree", "HEAD", "--", "references/Hexalith.Builds").ConfigureAwait(false);
            if (recorded != "160000 commit " + authority.OriginalBuildsGitlink + "\treferences/Hexalith.Builds"
                || !SamePath(await GitAsync(builds, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false), builds)
                || await GitAsync(builds, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false) != authority.BuildsCommit
                || await GitAsync(builds, cancellationToken, "rev-parse", "--verify", "refs/tags/" + authority.BuildsTag + "^{commit}").ConfigureAwait(false) != authority.BuildsCommit
                || (await GitAsync(builds, cancellationToken, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all").ConfigureAwait(false)).Length != 0)
            {
                throw new InvalidDataException("Recorded Builds identity or active clean released checkout differs from authority.");
            }

            string toolText = await File.ReadAllTextAsync(Path.Combine(root, ".config", "dotnet-tools.json"), cancellationToken).ConfigureAwait(false);
            using JsonDocument tools = JsonDocument.Parse(toolText);
            RejectDuplicates(tools.RootElement);
            JsonElement tool = tools.RootElement.GetProperty("tools").GetProperty("hexalith.builds.module.cli");
            if (tools.RootElement.GetProperty("isRoot").ValueKind != JsonValueKind.True
                || tool.GetProperty("version").GetString() != authority.ProducerPin
                || authority.ProducerPin != (toolVersionOverride ?? PlatformIdentityResolver.ToolVersion)
                || !tool.TryGetProperty("rollForward", out JsonElement rollForward) || rollForward.ValueKind != JsonValueKind.False
                || !StableVersion().IsMatch(authority.ProducerPin))
            {
                throw new InvalidDataException("The exact local manifest pin must equal the executing released producer without roll-forward.");
            }

            if (qualification)
            {
                await VerifyQualificationAsync(authority, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (authority.Qualification is not null || !Oid(authority.DeliveredSha) || !Hash(authority.PerimeterDigest)
                    || authority.OriginalBuildsGitlink != authority.BuildsCommit
                    || builds != Path.Combine(root, "references", "Hexalith.Builds"))
                {
                    throw new InvalidDataException("Production preparation requires accepted delivery and its actual recorded Builds catalog.");
                }

                await (verifyAccepted ?? ManagedBuildAuthorityVerifier.VerifyAsync)(authority, authorityPath, cancellationToken).ConfigureAwait(false);
            }

            Runtime.CompositionProcessResult configuration = qualification
                ? await GitWorkspaceProcess.RunAsync(authority.Qualification!.CiCdRoot, cancellationToken, "show", authority.Qualification.CiCdCandidate + ":" + authority.Qualification.RenderedConfigPath).ConfigureAwait(false)
                : await GitWorkspaceProcess.RunAsync(root, cancellationToken, "show", "HEAD:.github/hexalith-ci.json").ConfigureAwait(false);
            using JsonDocument selectedConfig = JsonDocument.Parse(configuration.Output);
            if (!configuration.Started || configuration.TimedOut || configuration.OutputTruncated || configuration.ExitCode != 0
                || Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configuration.Output))) != authority.ConfigurationDigest
                || selectedConfig.RootElement.GetProperty("moduleManifest").GetString() != Path.GetRelativePath(root, Path.GetFullPath(manifestPath)).Replace('\\', '/'))
            {
                throw new InvalidDataException("The selected authority configuration does not bind this exact module manifest.");
            }

            SourceMapping mapping = await WorkspaceRootResolver.ResolveManagedAsync(manifestPath, authority, cancellationToken).ConfigureAwait(false);
            PlatformIdentity platform = mapping.PlatformIdentity ?? throw new InvalidDataException("Actual Platform identity is unavailable.");
            bool platformMatches = platform.Commit == authority.PlatformCommit && platform.CheckoutCommit == authority.PlatformCommit
                && platform.CatalogCommit == authority.PlatformCatalogCommit && platform.ReleaseTag == authority.PlatformReleaseTag;
            return platformMatches ? mapping : throw new InvalidDataException("Actual Platform identity, release or truthful historical catalog differs from authority.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new WorkspaceMappingException(new ToolDiagnostic("HXW012", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "Managed build authority refused: " + exception.Message, "managedAuthority"));
        }
    }

    private static async Task VerifyQualificationAsync(ManagedBuildAuthority authority, CancellationToken cancellationToken)
    {
        CandidateBuildQualification candidate = authority.Qualification ?? throw new InvalidDataException("Candidate qualification bindings are missing.");
        if (authority.DeliveredSha is not null || authority.PerimeterDigest is not null
            || !Oid(candidate.BaselineHead) || !Oid(candidate.CiCdCandidate) || !Oid(candidate.CandidateTree)
            || !Oid(candidate.RenderedConfigBlob) || candidate.ProducerSourceCommit != authority.BuildsCommit
            || !Hash(candidate.OwnedBaselineDigest) || candidate.OwnedBaselineDigest != candidate.OwnedProductDigest
            || !Hash(candidate.ProductDiffDigest) || Path.GetFullPath(candidate.CiCdRoot) != candidate.CiCdRoot
            || RepositoryPathResolver.TryResolvePathWithinRoot(authority.ModuleRoot, Path.GetRelativePath(authority.ModuleRoot, authority.BuildsRoot), out _)
            || candidate.RenderedConfigPath != "rendered/" + authority.Repository.Split('/')[1] + "/.github/hexalith-ci.json")
        {
            throw new InvalidDataException("Candidate qualification is incomplete or attempts to impersonate accepted delivery.");
        }

        RejectReparseTraversal(candidate.CiCdRoot);
        if (await GitAsync(candidate.CiCdRoot, cancellationToken, "rev-parse", candidate.CiCdCandidate + "^{tree}").ConfigureAwait(false) != candidate.CandidateTree
            || !SamePath(await GitAsync(candidate.CiCdRoot, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false), candidate.CiCdRoot)
            || await GitAsync(candidate.CiCdRoot, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false) != candidate.CiCdCandidate
            || await GitAsync(candidate.CiCdRoot, cancellationToken, "rev-parse", candidate.CiCdCandidate + ":" + candidate.RenderedConfigPath).ConfigureAwait(false) != candidate.RenderedConfigBlob
            || await GitAsync(candidate.CiCdRoot, cancellationToken, "ls-tree", candidate.CiCdCandidate, "--", "references/" + authority.Repository.Split('/')[1]).ConfigureAwait(false) != "160000 commit " + candidate.BaselineHead + "\treferences/" + authority.Repository.Split('/')[1]
            || await GitAsync(authority.ModuleRoot, cancellationToken, "rev-parse", "refs/remotes/origin/main").ConfigureAwait(false) != candidate.BaselineHead)
        {
            throw new InvalidDataException("Candidate tree, rendered blob or observed remote baseline changed.");
        }

        string config = await GitAsync(candidate.CiCdRoot, cancellationToken, "show", candidate.CiCdCandidate + ":" + candidate.RenderedConfigPath).ConfigureAwait(false);

        // Read raw bytes for the digest: trimming Git command output is never a content normalization.
        Runtime.CompositionProcessResult raw = await GitWorkspaceProcess.RunAsync(candidate.CiCdRoot, cancellationToken, "show", candidate.CiCdCandidate + ":" + candidate.RenderedConfigPath).ConfigureAwait(false);
        using JsonDocument prospective = JsonDocument.Parse(config);
        if (!raw.Started || raw.TimedOut || raw.OutputTruncated || raw.ExitCode != 0
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw.Output))), authority.ConfigurationDigest, StringComparison.Ordinal)
            || prospective.RootElement.GetProperty("buildsTag").GetString() != authority.BuildsTag)
        {
            throw new InvalidDataException("Candidate configuration or selected released catalog changed.");
        }
    }

    private static void RejectReparseTraversal(string path)
    {
        for (FileSystemInfo? item = File.Exists(path) ? new FileInfo(Path.GetFullPath(path)) : new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Managed authority paths must not traverse reparse points.");
            }
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = [with(StringComparer.OrdinalIgnoreCase)];
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Duplicate authority or tool-manifest key.");
                }

                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicates(item);
            }
        }
    }

    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), FilesystemPathRules.Comparison(second));

    private static bool Oid(string? value) => value is { Length: 40 or 64 } && value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool Hash(string? value) => value is { Length: 64 } && Oid(value);

    private static async Task<string> GitAsync(string root, CancellationToken cancellationToken, params string[] arguments)
    {
        Runtime.CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, arguments).ConfigureAwait(false);
        return !result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0
            ? throw new InvalidDataException("Exact local Git authority inspection failed: " + arguments[0])
            : result.Output.TrimEnd('\r', '\n');
    }

    [GeneratedRegex("\\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\\z")]
    private static partial Regex Repository();

    [GeneratedRegex("\\Av(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z")]
    private static partial Regex StableTag();

    [GeneratedRegex("\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z")]
    private static partial Regex StableVersion();
}
