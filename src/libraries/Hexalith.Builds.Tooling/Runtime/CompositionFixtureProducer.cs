// <copyright file="CompositionFixtureProducer.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>Authenticates the Platform-owned signed extension producer for one package test.</summary>
public sealed class CompositionFixtureProducer
{
    private const string _module = "platformtestextension";
    private const string _profile = "eventstore-extension-package";
    private const string _consumer = "Hexalith.Platform.EventStoreHost";
    private const string _package = "Hexalith.Platform.TestExtension";
    private const string _library = "lib/net11.0/" + _package + ".dll";
    private const string _archiveName = "platform-fixture.nupkg";
    private const string _manifest = "tests/fixtures/Hexalith.Platform.TestExtension/hexalith.module-manifest.v2.json";
    private const string _project = "tests/Hexalith.Platform.TestExtension.Qualification.Tests/Hexalith.Platform.TestExtension.Qualification.Tests.csproj";

    /// <summary>The fixture version verified against publication 44902658431 and the public NuGet signature.</summary>
    private const string _version = "2.2.5";
    private const string _releaseSource = "0aacf13244fa6b1d9e08d627149d72cf12229167";
    private const string _archiveSha256 = "7c3b432aba236b9cd102b6fad98a15ed629b3dcdfb9bad9ee6a3d105f63d04b4";
    private const string _librarySha256 = "650d37d4aa1ae6de6c4633c1ecafa763349ed8c29f1c7e24d257ec0853d3f004";
    private const string _nuspecSha256 = "7d140d679c8e926b7455e941e7405d63a42a8bcfeb60cf9443e60a903bc2afa5";
    private const int _limit = 2_000_000;
    private const string _refusal = "The signed Platform fixture producer does not match this package test or its owned inputs.";

    private static readonly Dictionary<string, (string Member, string Sha256)> _bodies = new(StringComparer.Ordinal)
    {
        [_manifest] = ("build/hexalith.module-manifest.v2.json", "6807b0212f29d5a0f7f82ffdb77bf1b85f3b12b0a29c0073e307ba99f8bc9e24"),
        ["tests/fixtures/Hexalith.Platform.TestExtension/fixture-producer.json"] = ("build/fixture-producer.json", "f8ef5ea9bd70e7446dc449e64214df045f0e28563be8b3fbbf851d16eb381914"),
        [_project] = ("build/qualification/Hexalith.Platform.TestExtension.Qualification.Tests.csproj", "35ebee634b9edb008115684ac5bd4ea90bff32fa6aa4e87a8d392767200f6f76"),
        ["tests/Hexalith.Platform.TestExtension.Qualification.Tests/ReadinessTests.cs"] = ("build/qualification/ReadinessTests.cs", "79f99abe1a100e560c007d6b7b4fe831bf15b7e64bbc0b59bb0a963c6ac823a3"),
        ["eng/test-extension-release/qualify-host.py"] = ("build/qualification/qualify-host.py", "af7fa849d069fbe2b7b4b36fc80a6434bca12bc03d9816b425ac9e061e0fb511"),
    };

    private static readonly Dictionary<string, string> _platformImports = new(StringComparer.Ordinal)
    {
        ["Directory.Build.props"] = "66cd6d69549948840bd6e896f6a52f4390d6d0691f705b946042f8fd45a81df2",
        ["Directory.Build.targets"] = "e9bf3c872c8d6b5a308f1f6c047391880917144ecb750bfa0ccfd710b12de9a6",
        ["Directory.Packages.props"] = "6ce2c6efcca3b4e4297db9e22bd262524d14d9a4801c8627bd41c247b8a0b56a",
        ["eng/HistoricalPackageSecurity.props"] = "86318249efc90336fe9fd395d8e3742dc940d10e7b425936d6c23d1b7962a81b",
        ["eng/SourceRestore.props"] = "2e433f7e5d30eb94beb7030f22fe0ddee7798ece8177eaec045084040851d100",
        ["global.json"] = "5feaf50426a6c5554654e326bedd2f6c6273f13b948b1ec6e11e12cae68f9f05",
        ["nuget.config"] = "eae1b6dcb61e63c93b4146f0903414053f5bad17a6513de07ce63ada3382979c",
    };

    private static readonly string[] _sourceMappingImports =
    [
        "SourceMapping.before-targets.props", "SourceMapping.final.targets", "SourceMapping.late.props", "SourceMapping.props", "SourceMapping.targets",
        "SourceMapping.values.props", "SourceMapping.values.targets",
    ];

    private static readonly string[] _nestedImports = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "Directory.Build.rsp", "NuGet.Config", "nuget.config"];

    private static readonly HttpClient _nuget = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    private CompositionFixtureProducer(string runId)
    {
        RunId = runId;
        PackageId = _package;
        Version = _version;
        SourceCommit = _releaseSource;
        ArchiveSha256 = _archiveSha256;
        LibrarySha256 = _librarySha256;
    }

    /// <summary>Gets the owning test run.</summary>
    public string RunId { get; }

    /// <summary>Gets the admitted fixture package ID.</summary>
    public string PackageId { get; }

    /// <summary>Gets the owning catalog's admitted release version.</summary>
    public string Version { get; }

    /// <summary>Gets the Platform source revision published in the fixture archive.</summary>
    public string SourceCommit { get; }

    /// <summary>Gets the SHA-256 of the verified signed fixture archive.</summary>
    public string ArchiveSha256 { get; }

    /// <summary>Gets the SHA-256 of the package's runtime assembly.</summary>
    public string LibrarySha256 { get; }

    private static string ArchiveFileName => _package.ToLowerInvariant() + "." + _version + ".nupkg";

    private static string FlatContainerPath => "/v3-flatcontainer/" + _package.ToLowerInvariant() + "/" + _version + "/" + ArchiveFileName;

    /// <summary>Checks whether the given plan requires the Platform fixture producer.</summary>
    /// <param name="plan">The owning run plan.</param>
    /// <returns>Whether producer authentication is mandatory for the plan.</returns>
    public static bool RequiresProof(CompositionRunPlan plan) => RequiresProof(plan.SourceMapping, plan.PlatformActiveModule, plan.SelectedTest);

    /// <summary>Verifies the private runner authentication and the currently selected package, catalog and source.</summary>
    /// <param name="plan">The owning run plan.</param>
    /// <param name="signingKey">The runner's private per-run signing key.</param>
    /// <param name="required">Whether the parent launch requires this fixture producer.</param>
    /// <returns>The verified producer for this test, or null for a different test.</returns>
    /// <exception cref="InvalidDataException">The producer binding or one of its inputs differs.</exception>
    public static CompositionFixtureProducer? Verify(CompositionRunPlan plan, string? signingKey, bool required = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!required && !RequiresProof(plan) && plan.FixtureProducerBinding is null)
        {
            return null;
        }

        if (signingKey is null || plan.FixtureProducerBinding is not { Length: 64 } binding || !binding.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException(_refusal);
        }

        byte[] expected = Tag(plan, signingKey);
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(binding)) ? new CompositionFixtureProducer(plan.RunId) : throw new InvalidDataException(_refusal);
    }

    /// <summary>Determines whether a package-mode selection names the finite producer.</summary>
    /// <param name="mapping">The resolved workspace mapping.</param>
    /// <param name="activeModule">The declaration's active module.</param>
    /// <param name="selected">The selected entry, if applicable.</param>
    /// <returns>Whether producer authentication is required.</returns>
    internal static bool RequiresProof(SourceMapping? mapping, string? activeModule, ModuleTestSelection? selected)
        => mapping?.Mode == WorkspaceMode.Package && (activeModule == _module || selected?.ModuleId == _module || selected?.Name == _profile);

    /// <summary>Gets whether a selected suite needs the actual fixture producer's fresh owner.</summary>
    /// <param name="selected">The selected suite.</param>
    /// <returns>Whether the suite cannot reuse another owned run.</returns>
    internal static bool IsFixtureTest(ModuleTestSelection selected)
        => selected is { ModuleId: _module, Name: _profile, Mode: "package" };

    /// <summary>Checks the real Platform checkout, selected native sources and exact closure before startup.</summary>
    /// <param name="mapping">The resolved workspace mapping.</param>
    /// <param name="activeModule">The declaration's active module.</param>
    /// <param name="selected">The selected native entry.</param>
    /// <param name="selections">The requested native entries.</param>
    /// <param name="manifestPath">The active manifest path.</param>
    /// <param name="closure">The authenticated declaration closure.</param>
    /// <param name="cancellationToken">The original startup deadline.</param>
    /// <returns>A task that checks the selected committed inputs.</returns>
    /// <exception cref="InvalidDataException">The selected source or closure differs from the fixture producer.</exception>
    internal static Task ValidateBeforeStartupAsync(
        SourceMapping? mapping,
        string? activeModule,
        ModuleTestSelection? selected,
        IReadOnlyList<ModuleTestSelection> selections,
        string manifestPath,
        IReadOnlyList<PlatformModuleDeclaration>? closure,
        CancellationToken cancellationToken)
    {
        if (!RequiresProof(mapping, activeModule, selected))
        {
            return Task.CompletedTask;
        }

        string root = RequireSource(mapping, activeModule, selected, selections, [manifestPath]);
        string[] modules = closure is null ? [] : [.. closure.Select(item => CompositionDeclarationClosure.Id(item.Declaration)).Order(StringComparer.Ordinal)];
        return modules.SequenceEqual(["eventstore", "memories", _module, "tenants"], StringComparer.Ordinal)
            ? RequireTrackedFilesAsync(mapping!, root, manifestPath, cancellationToken)
            : throw new InvalidDataException(_refusal);
    }

    /// <summary>Requires the host-specific pin and retains the exact signed public archive in the owner's workspace.</summary>
    /// <param name="mapping">The resolved workspace mapping.</param>
    /// <param name="workspace">The private run workspace.</param>
    /// <param name="cancellationToken">The original startup deadline.</param>
    /// <returns>A task that verifies the signed package used by the producer.</returns>
    /// <exception cref="InvalidDataException">The official signed archive is unavailable or changed.</exception>
    internal static async Task RetrievePackageAsync(SourceMapping mapping, string workspace, CancellationToken cancellationToken)
    {
        _ = RequireCatalog(mapping, workspace);
        string archivePath = Path.Combine(workspace, _archiveName);
        if (File.Exists(archivePath))
        {
            ValidateArchive(archivePath);
            return;
        }

        Uri packageUri = new("https://api.nuget.org" + FlatContainerPath, UriKind.Absolute);
        using HttpResponseMessage initial = await _nuget.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage? redirected = IsRedirect(initial.StatusCode) && initial.Headers.Location is { } location
            && Uri.TryCreate(packageUri, location, out Uri? resolved) && IsOfficialPackageRedirect(resolved)
                ? await _nuget.GetAsync(resolved, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
                : null;
        HttpResponseMessage response = redirected ?? initial;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException($"The signed Platform fixture package is unavailable from NuGet ({(int)response.StatusCode}).");
        }

        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using MemoryStream body = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > _limit)
            {
                throw new InvalidDataException(_refusal);
            }

            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        byte[] bytes = body.ToArray();
        ValidateArchiveBytes(bytes);
        await File.WriteAllBytesAsync(archivePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Seals the verified producer context with the actual runner's private signing key.</summary>
    /// <param name="plan">The owning run plan.</param>
    /// <param name="signingKey">The runner's private per-run key.</param>
    /// <returns>The authenticated plan or the unchanged plan for an unrelated profile.</returns>
    /// <exception cref="InvalidDataException">The plan already contains a producer tag.</exception>
    internal static CompositionRunPlan Bind(CompositionRunPlan plan, string signingKey)
    {
        if (plan.FixtureProducerBinding is not null)
        {
            throw new InvalidDataException(_refusal);
        }

        bool required = RequiresProof(plan);
        return required ? plan with { FixtureProducerBinding = Convert.ToHexStringLower(Tag(plan, signingKey)) } : plan;
    }

    /// <summary>Checks that an official package redirect keeps the exact approved package destination.</summary>
    /// <param name="location">The fully resolved redirect target.</param>
    /// <returns>Whether the exact package can be requested there.</returns>
    internal static bool IsOfficialPackageRedirect(Uri location)
        => location is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0, Fragment.Length: 0 }
            && ((location.Host == "api.nuget.org" && location.AbsolutePath == FlatContainerPath)
                || (location.Host == "globalcdn.nuget.org" && location.AbsolutePath == "/packages/" + ArchiveFileName));

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string RequireSource(
        SourceMapping? mapping,
        string? activeModule,
        ModuleTestSelection? selected,
        IReadOnlyList<ModuleTestSelection> selections,
        IReadOnlyList<string> manifests)
    {
        bool valid = mapping is { Mode: WorkspaceMode.Package, HasGit: true, PlatformIdentity: { IsPlatformRepository: true, CheckoutCommit: not null } platform }
            && mapping.ManagedAuthority is null && mapping.IdentityFindings.Count == 0
            && mapping.Root == platform.PlatformPath && new DirectoryInfo(mapping.Root).LinkTarget is null
            && mapping.Entries.Any(entry => entry.Identity == mapping.ActiveModule && entry.Origin == "source" && entry.Path == mapping.Root)
            && activeModule == _module && selected is { ModuleId: _module, Name: _profile, Mode: "package", Executor: "mtp", Entry: _project }
            && selections.Count == 1 && selections[0] == selected
            && manifests.Count == 1 && string.Equals(manifests[0], Path.GetFullPath(Path.Combine(mapping.Root, _manifest)), FilesystemPathRules.Comparison(mapping.Root));
        return valid ? mapping!.Root : throw new InvalidDataException(_refusal);
    }

    private static CatalogVersionSnapshot RequireCatalog(SourceMapping mapping, string workspace)
    {
        string catalogFile = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        CatalogVersionSnapshot catalog = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, catalogFile);
        string? pin = new CompositionDeclarationOptions(mapping, []) { Workspace = workspace }.CatalogPackageVersion(_package, catalogFile, _consumer);
        return pin == _version ? catalog : throw new InvalidDataException(_refusal);
    }

    private static async Task RequireTrackedFilesAsync(SourceMapping mapping, string root, string manifestPath, CancellationToken cancellationToken)
    {
        SourceMapping resolved = await WorkspaceRootResolver.ResolveAsync(manifestPath, WorkspaceMode.Package, cancellationToken).ConfigureAwait(false);
        if (resolved.ContentHash != mapping.ContentHash)
        {
            throw new InvalidDataException(_refusal);
        }

        string commit = resolved.PlatformIdentity!.CheckoutCommit!;
        string[] paths = [.. _bodies.Keys, .. _platformImports.Keys];
        if (paths.Any(path => !ModuleTestSelection.IsOwnedEntry(root, path)))
        {
            throw new InvalidDataException(_refusal);
        }

        CompositionProcessResult tree = await GitWorkspaceProcess.RunAsync(root, cancellationToken, ["ls-tree", "-rz", commit, "--", .. paths]).ConfigureAwait(false);
        CompositionProcessResult checkout = await GitWorkspaceProcess.RunAsync(root, cancellationToken, ["hash-object", "--no-filters", "--", .. paths]).ConfigureAwait(false);
        string[] hashes = checkout.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!tree.Started || tree.TimedOut || tree.OutputTruncated || tree.ExitCode != 0
            || !checkout.Started || checkout.TimedOut || checkout.OutputTruncated || checkout.ExitCode != 0 || hashes.Length != paths.Length)
        {
            throw new InvalidDataException(_refusal);
        }

        HashSet<string> tracked = [.. tree.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        if (paths.Where((path, index) => !tracked.Contains($"100644 blob {hashes[index]}\t{path}") && !tracked.Contains($"100755 blob {hashes[index]}\t{path}")).Any())
        {
            throw new InvalidDataException(_refusal);
        }

        _ = SourceBodies(root);
    }

    private static IReadOnlyList<string> SourceBodies(string root)
    {
        DirectoryInfo projectRoot = new(Path.GetDirectoryName(Path.Combine(root, _project))!);
        DirectoryInfo testsRoot = new(Path.Combine(root, "tests"));
        if (!projectRoot.Exists || !testsRoot.Exists || new DirectoryInfo(root).EnumerateFileSystemInfos("Directory.Build.rsp").Any()
            || _nestedImports.Any(name => projectRoot.EnumerateFileSystemInfos(name).Any() || testsRoot.EnumerateFileSystemInfos(name).Any())
            || projectRoot.EnumerateDirectories().Any(directory => directory.Name is not ("bin" or "obj"))
            || projectRoot.EnumerateFiles("*.cs").Any(file => file.Name != "ReadinessTests.cs"))
        {
            throw new InvalidDataException(_refusal);
        }

        bool signedSourcesMatch = _bodies.All(pair => ModuleTestSelection.IsOwnedEntry(root, pair.Key) && HashFile(Path.Combine(root, pair.Key)) == pair.Value.Sha256);
        bool buildInputsMatch = _platformImports.All(pair => ModuleTestSelection.IsOwnedEntry(root, pair.Key) && HashFile(Path.Combine(root, pair.Key)) == pair.Value);
        return signedSourcesMatch && buildInputsMatch ? [.. _platformImports.Values] : throw new InvalidDataException(_refusal);
    }

    private static byte[] Tag(CompositionRunPlan plan, string signingKey)
    {
        if (!RequiresProof(plan) || !plan.IsPlatformComposition || plan.CandidatePolicy is not null || plan.QualificationOwners is { Count: > 0 }
            || !CompositionRunPlanFactory.IsRunId(plan.RunId) || !CompositionRunPlanFactory.IsRunId(plan.AttemptId) || !CompositionRunPlanFactory.IsRunId(plan.EnvironmentId)
            || string.IsNullOrWhiteSpace(plan.CompositionContentIdentity) || plan.PlatformDeclarationHash != plan.SelectedTest?.ManifestHash)
        {
            throw new InvalidDataException(_refusal);
        }

        string root = RequireSource(plan.SourceMapping, plan.PlatformActiveModule, plan.SelectedTest, plan.SelectedTests, plan.PlatformDeclarationPaths);
        IReadOnlyList<string> source = SourceBodies(root);
        CatalogVersionSnapshot snapshot = RequireCatalog(plan.SourceMapping!, plan.Workspace);
        string[] imports = [.. _sourceMappingImports.Select(path => HashFile(Path.Combine(plan.Workspace, "source-mapping", path)))];
        ValidateArchive(Path.Combine(plan.Workspace, _archiveName));
        Span<byte> key = stackalloc byte[48];
        if (!Convert.TryFromBase64String(signingKey, key, out int written) || written != key.Length)
        {
            throw new InvalidDataException(_refusal);
        }

        try
        {
            return HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(new
            {
                purpose = "hexalith.platform-fixture-producer.v1",
                plan.Schema,
                plan.RunId,
                plan.AttemptId,
                plan.EnvironmentId,
                plan.PlatformIdentity,
                plan.SourceMappingHash,
                plan.CompositionContentIdentity,
                plan.PlatformDeclarationHash,
                plan.PlatformDeclarationPaths,
                plan.SelectedTest,
                snapshot.CatalogCommit,
                snapshot.Hash,
                source,
                imports,
                archive = _archiveSha256,
                release = _releaseSource,
                library = _librarySha256,
            }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string HashFile(string path)
        => new FileInfo(path) is { Exists: true, LinkTarget: null, Length: > 0 and <= _limit }
            ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
            : throw new InvalidDataException(_refusal);

    private static void ValidateArchive(string path)
    {
        if (new FileInfo(path) is not { Exists: true, LinkTarget: null, Length: > 0 and <= _limit })
        {
            throw new InvalidDataException(_refusal);
        }

        ValidateArchiveBytes(File.ReadAllBytes(path));
    }

    private static void ValidateArchiveBytes(byte[] bytes)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != _archiveSha256)
        {
            throw new InvalidDataException(_refusal);
        }

        using MemoryStream body = new(bytes, writable: false);
        using ZipArchive archive = new(body);
        if (archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count || archive.GetEntry(".signature.p7s") is null)
        {
            throw new InvalidDataException(_refusal);
        }

        foreach ((string member, string hash) in _bodies.Values.Append((_library, _librarySha256)).Append((_package + ".nuspec", _nuspecSha256)))
        {
            using Stream entry = (archive.GetEntry(member) ?? throw new InvalidDataException(_refusal)).Open();
            if (Convert.ToHexStringLower(SHA256.HashData(entry)) != hash)
            {
                throw new InvalidDataException(_refusal);
            }
        }
    }
}
