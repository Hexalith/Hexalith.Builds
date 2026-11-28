// <copyright file="NativeBuildPreparationService.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>Seals audited Release outputs and verifies the same selection at the public runtime boundary.</summary>
public static class NativeBuildPreparationService
{
    private const string _schema = "hexalith.native-build.v1";
    private const string _receiptName = "receipt.json";

    private static readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>Seals original, already audited Release outputs without rebuilding them.</summary>
    /// <param name="manifestPath">The active module declaration.</param>
    /// <param name="inputsPath">The producer-owned evaluated component locations.</param>
    /// <param name="workspace">A fresh producer workspace beneath a real directory.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The producer receipt digest on success.</returns>
    /// <exception cref="InvalidDataException">The prepared input or output selection is invalid.</exception>
    public static async Task<ToolCommandResult> PrepareAsync(string manifestPath, string inputsPath, string workspace, CancellationToken cancellationToken)
    {
        string? staging = null;
        try
        {
            string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            string parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Prepared native build output must have an existing parent.");
            NativeBuildInventory.RequireReal(parent, directory: true);
            if (Path.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null)
            {
                throw new InvalidDataException("Prepared native build output must be fresh.");
            }

            PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([manifestPath], cancellationToken);
            if (!validation.IsValid)
            {
                throw new InvalidDataException("Prepared native build requires a valid v2 module declaration.");
            }

            SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifestPath, WorkspaceMode.Package, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> source = await NativeBuildSource.CaptureAsync(mapping, manifestPath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ModuleTestSelection> selections = DeclaredNativeProfiles(validation.Declarations!, manifestPath);
            NativeBuildInput[] inputs = JsonSerializer.Deserialize<NativeBuildInput[]>(ReadMetadata(Path.GetFullPath(inputsPath)), _options)
                ?? throw new InvalidDataException("Prepared native build inputs are empty.");
            if (inputs.Length == 0 || selections.Count == 0)
            {
                throw new InvalidDataException("Prepared native build requires declared native package profiles and their original outputs.");
            }

            staging = Path.Combine(parent, ".hexalith-native-" + CompositionRunPlanFactory.NewRunId());
            CompositionWorkspace.CreatePrivateDirectory(staging);
            List<NativeBuildInput> originalInputs = [.. inputs];
            bool buildsModel = inputs.All(input => input.Role != PreparedNativeBuild.ModelRole);
            if (buildsModel)
            {
                string modelWorkspace = Path.Combine(staging, "model-build");
                CompositionWorkspace.CreatePrivateDirectory(modelWorkspace);
                await SourceMappingMaterializer.WriteAsync(mapping, modelWorkspace, cancellationToken).ConfigureAwait(false);
                string assembly = await PlatformManifestCommand.BuildModelAsync(mapping, modelWorkspace, cancellationToken).ConfigureAwait(false);
                string assets = Path.Combine(modelWorkspace, "build", "platform-model", "obj", "Hexalith.Platform.Composition", "project.assets.json");
                originalInputs.Add(new NativeBuildInput(PreparedNativeBuild.ModelRole, assembly, assets, NativeBuildSource.ModelProject(mapping)));
            }

            NativeBuildInput model = originalInputs.Single(input => input.Role == PreparedNativeBuild.ModelRole);
            RequireReleaseAssembly(model.Entry, model: true);
            if (buildsModel)
            {
                await PrepareCompanionsAsync(originalInputs, model.Entry, mapping, manifestPath, selections, staging, cancellationToken).ConfigureAwait(false);
            }

            SortedDictionary<string, NativeBuildComponent> components = [];
            foreach (NativeBuildInput input in originalInputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string componentDirectory = PreparedNativeBuild.DirectoryFor(staging, input.Role);
                if (components.ContainsKey(input.Role)
                    || !Path.IsPathFullyQualified(input.Entry) || !Path.IsPathFullyQualified(input.Assets) || !Path.IsPathFullyQualified(input.Project)
                    || input.Entry != Path.GetFullPath(input.Entry) || input.Assets != Path.GetFullPath(input.Assets) || input.Project != Path.GetFullPath(input.Project))
                {
                    throw new InvalidDataException("Prepared native build has duplicate roles or noncanonical input paths.");
                }

                string original = Path.GetDirectoryName(input.Entry)!;
                _ = NativeBuildInventory.VerifyDirectory(original, null, componentDirectory, cancellationToken);
                string entry = Path.GetFileName(input.Entry);
                string privateEntry = NativeBuildInventory.Resolve(componentDirectory, entry);
                bool isModel = input.Role == PreparedNativeBuild.ModelRole;
                RequireReleaseAssembly(privateEntry, isModel);
                if (isModel && NativeBuildInventory.HashFile(Path.Combine(componentDirectory, "Hexalith.Builds.Tooling.dll"), cancellationToken) != source["tooling-sha256"])
                {
                    throw new InvalidDataException("Prepared native build model references different Tooling bytes.");
                }

                (string framework, string rid, string assetsDigest, IReadOnlyDictionary<string, NativeBuildPackageIdentity> packages) = NativeBuildPackageGraph.Measure(
                    input with { Entry = privateEntry }, mapping, componentDirectory, cancellationToken);
                IReadOnlyDictionary<string, string> files = NativeBuildInventory.VerifyDirectory(componentDirectory, null, null, cancellationToken);
                NativeBuildComponent component = new(input.Role, entry, input.Project, NativeBuildInventory.HashFile(input.Project, cancellationToken), framework, rid, assetsDigest, packages, files);
                NativeBuildPackageGraph.VerifyRuntime(privateEntry, component);
                components.Add(input.Role, component);
            }

            SortedDictionary<string, string> profiles = new(selections.ToDictionary(selection => selection.Name, PreparedNativeBuild.ProfileIdentity), StringComparer.Ordinal);
            NativeBuildReceipt receipt = new(_schema, "Release", source, profiles, components);
            ValidateSelection(receipt, mapping, selections, cancellationToken);
            if (!Same(source, await NativeBuildSource.CaptureAsync(mapping, manifestPath, cancellationToken).ConfigureAwait(false)))
            {
                throw new InvalidDataException("Prepared native build source changed during sealing.");
            }

            foreach (string name in new[] { "model-build", "companions" })
            {
                string generated = Path.Combine(staging, name);
                if (Directory.Exists(generated) && !await CompositionWorkspace.TryDeleteAsync(generated, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("Prepared native build could not remove temporary composition build intermediates.");
                }
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, _options);
            await File.WriteAllBytesAsync(Path.Combine(staging, _receiptName), bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            staging = null;
            return new ToolCommandResult("prepared", ToolOutcome.Passed(), []) { NativeBuildReceiptSha256 = NativeBuildInventory.Hash(bytes) };
        }
        catch (OperationCanceledException)
        {
            return Refusal("Prepared native build was cancelled.", cancelled: true);
        }
        catch (WorkspaceMappingException exception)
        {
            return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(exception.Diagnostics[0].Phase, exception.Diagnostics[0].Category, exception.Diagnostics[0].RuleId, ToolExitCode.PrerequisiteUnavailable), exception.Diagnostics);
        }
        catch (Exception exception) when (IsRefusal(exception))
        {
            return Refusal(RefusalMessage(exception));
        }
        finally
        {
            if (staging is not null)
            {
                _ = await CompositionWorkspace.TryDeleteAsync(staging, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads the independently authenticated receipt before any resource or run workspace is allocated.</summary>
    /// <param name="handoff">The user's explicit producer receipt and digest.</param>
    /// <param name="mapping">The production-selected package boundary.</param>
    /// <param name="manifestPath">The selected module declaration.</param>
    /// <param name="selections">Every selected batch entry.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>The exact verified output selection.</returns>
    /// <exception cref="InvalidDataException">The selected source or files differ from the independently identified producer receipt.</exception>
    internal static async Task<PreparedNativeBuild> VerifyAsync(NativeBuildHandoff handoff, SourceMapping mapping, string manifestPath, IReadOnlyList<ModuleTestSelection> selections, CancellationToken cancellationToken)
    {
        string receiptPath = Path.GetFullPath(handoff.ReceiptPath);
        if (Path.GetFileName(receiptPath) != _receiptName || !IsSha256(handoff.Sha256))
        {
            throw new InvalidDataException("Prepared native build requires receipt.json and an independent lowercase SHA-256 digest.");
        }

        byte[] bytes = ReadMetadata(receiptPath);
        if (NativeBuildInventory.Hash(bytes) != handoff.Sha256)
        {
            throw new InvalidDataException("Prepared native build receipt SHA-256 differs from the independent digest.");
        }

        NativeBuildReceipt receipt = JsonSerializer.Deserialize<NativeBuildReceipt>(bytes, _options)
            ?? throw new InvalidDataException("Prepared native build receipt is empty.");
        IReadOnlyDictionary<string, string> current = await NativeBuildSource.CaptureAsync(mapping, manifestPath, cancellationToken).ConfigureAwait(false);
        if (!Same(receipt.Source, current))
        {
            throw new InvalidDataException("Prepared native build source, catalog, SDK, runtime or Tooling identity differs.");
        }

        ValidateSelection(receipt, mapping, selections, cancellationToken);
        string producer = Path.GetDirectoryName(receiptPath)!;
        string payload = Path.Combine(producer, "native-build");
        NativeBuildInventory.RequireReal(payload, directory: true);
        if (!Directory.EnumerateFileSystemEntries(payload).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal).SetEquals(receipt.Components.Keys))
        {
            throw new InvalidDataException("Prepared native build component inventory differs from the producer receipt.");
        }

        foreach (NativeBuildComponent component in receipt.Components.Values)
        {
            string directory = PreparedNativeBuild.DirectoryFor(producer, component.Role);
            _ = NativeBuildInventory.VerifyDirectory(directory, component.Files, null, cancellationToken);
            string entry = NativeBuildInventory.Resolve(directory, component.Entry);
            RequireReleaseAssembly(entry, component.Role == PreparedNativeBuild.ModelRole);
            NativeBuildPackageGraph.VerifyRuntime(entry, component);
        }

        string tooling = Path.Combine(PreparedNativeBuild.DirectoryFor(producer, PreparedNativeBuild.ModelRole), "Hexalith.Builds.Tooling.dll");
        return NativeBuildInventory.HashFile(tooling, cancellationToken) == current["tooling-sha256"]
            ? new PreparedNativeBuild(handoff.Sha256, receipt.Components, receipt.Profiles)
            : throw new InvalidDataException("Prepared native build model references different Tooling bytes.");
    }

    /// <summary>Copies the verified components to run-owned paths and rechecks every copied byte.</summary>
    /// <param name="handoff">The previously verified producer receipt path and digest.</param>
    /// <param name="prepared">The admitted receipt inventories.</param>
    /// <param name="workspace">The owner workspace.</param>
    /// <param name="cancellationToken">The operation token.</param>
    internal static void Materialize(NativeBuildHandoff handoff, PreparedNativeBuild prepared, string workspace, CancellationToken cancellationToken)
    {
        string producer = Path.GetDirectoryName(Path.GetFullPath(handoff.ReceiptPath))!;
        foreach (NativeBuildComponent component in prepared.Components.Values)
        {
            _ = NativeBuildInventory.VerifyDirectory(
                PreparedNativeBuild.DirectoryFor(producer, component.Role),
                component.Files,
                PreparedNativeBuild.DirectoryFor(workspace, component.Role),
                cancellationToken);
        }
    }

    /// <summary>Identifies integrations eligible to be populated from a normal build.</summary>
    /// <param name="declarations">The validated manifest.</param>
    /// <param name="manifestPath">The selected declaration.</param>
    /// <returns>The module's declared native package selections.</returns>
    /// <exception cref="InvalidDataException">A declared native package profile is ambiguous.</exception>
    internal static IReadOnlyList<ModuleTestSelection> DeclaredNativeProfiles(IReadOnlyList<PlatformModuleDeclaration> declarations, string manifestPath)
    {
        DateTimeOffset timestamp = DateTimeOffset.UnixEpoch;
        List<ModuleTestSelection> selections = [];
        foreach (PlatformModuleDeclaration declaration in declarations)
        {
            if (!declaration.Declaration.TryGetProperty("tests", out JsonElement tests))
            {
                continue;
            }

            foreach (JsonElement test in tests.EnumerateArray().Where(test => test.GetProperty("mode").GetString() == "package"
                && test.GetProperty("executor").GetString() is PersistedProfileNativeTests.MicrosoftTestingPlatform or PersistedProfileNativeTests.VsTest))
            {
                selections.Add(ModuleTestSelection.Select(declarations, test.GetProperty("name").GetString(), manifestPath, timestamp)
                    ?? throw new InvalidDataException("Prepared native build could not bind a declared package profile."));
            }
        }

        return selections;
    }

    /// <summary>Identifies a safe prepared-build refusal without relaying process output.</summary>
    /// <param name="exception">The refusal.</param>
    /// <returns>Whether it is a supported preparation or validation failure.</returns>
    internal static bool IsRefusal(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException or BadImageFormatException;

    /// <summary>Formats only fixed diagnostics from the prepared-build boundary.</summary>
    /// <param name="exception">The underlying refusal.</param>
    /// <returns>A credential-free message.</returns>
    internal static string RefusalMessage(Exception exception) =>
        exception is InvalidDataException && exception.Message.StartsWith("Prepared native build ", StringComparison.Ordinal)
            ? exception.Message
            : "Prepared native build receipt or output metadata could not be read.";

    private static async Task PrepareCompanionsAsync(List<NativeBuildInput> inputs, string model, SourceMapping mapping, string manifestPath, IReadOnlyList<ModuleTestSelection> selections, string staging, CancellationToken cancellationToken)
    {
        foreach (ModuleTestSelection selection in selections)
        {
            string rolePrefix = PreparedNativeBuild.CompositionRole(selection.Name, string.Empty);
            string workspace = NativeBuildInventory.Resolve(Path.Combine(staging, "companions"), selection.Name);
            CompositionWorkspace.CreatePrivateDirectory(workspace);
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, cancellationToken).ConfigureAwait(false);
            CompositionRunPorts unusedPorts = new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, [], 14, 15, 16, []);
            string manifest = Path.GetFullPath(manifestPath);
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(CompositionRunPlanFactory.NewRunId(), workspace, workspace, unusedPorts, [], [], mapping, [manifest], selection.ModuleId, model) with
            {
                PlatformDeclarationHash = selection.ManifestHash,
                SelectedTest = selection,
                SelectedTests = [selection],
                StartupDeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(20),
            };
            string planPath = Path.Combine(workspace, "plan.json");
            await CompositionDocumentStore.WriteAsync(planPath, plan, cancellationToken).ConfigureAwait(false);
            CompositionProcessResult result = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo("dotnet", [model, "--prepare-native-companion", planPath], mapping.Root, null),
                TimeSpan.FromMinutes(20),
                cancellationToken).ConfigureAwait(false);
            if (!result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0)
            {
                throw new InvalidDataException("Prepared native build Platform companion could not produce the audited reader and owner closure.");
            }

            NativeBuildInput[] produced = JsonSerializer.Deserialize<NativeBuildInput[]>(result.Output, _options)
                ?? throw new InvalidDataException("Prepared native build Platform companion did not return output identities.");
            foreach (NativeBuildInput input in produced.Where(input => inputs.All(existing => existing.Role != input.Role)))
            {
                if (!input.Role.StartsWith(rolePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Prepared native build Platform companion returned another profile's output.");
                }

                inputs.Add(input);
            }
        }
    }

    private static void ValidateSelection(NativeBuildReceipt receipt, SourceMapping mapping, IReadOnlyList<ModuleTestSelection> selections, CancellationToken cancellationToken)
    {
        if (receipt.Schema != _schema || receipt.Configuration != "Release" || receipt.Components.Count is < 2 or > 128)
        {
            throw new InvalidDataException("Prepared native build receipt schema, Release configuration or component count is invalid.");
        }

        if (!receipt.Components.TryGetValue(PreparedNativeBuild.ModelRole, out NativeBuildComponent? model)
            || model.Project != NativeBuildSource.ModelProject(mapping) || model.Entry != "Hexalith.Platform.Composition.dll"
            || model.ProjectSha256 != NativeBuildInventory.HashFile(model.Project, cancellationToken))
        {
            throw new InvalidDataException("Prepared native build model differs from the selected Platform identity.");
        }

        foreach (ModuleTestSelection selection in selections)
        {
            string rolePrefix = PreparedNativeBuild.CompositionRole(selection.Name, string.Empty);
            if (selection.Mode != "package" || selection.Executor is not (PersistedProfileNativeTests.MicrosoftTestingPlatform or PersistedProfileNativeTests.VsTest)
                || !receipt.Profiles.TryGetValue(selection.Name, out string? identity) || identity != PreparedNativeBuild.ProfileIdentity(selection)
                || !receipt.Components.TryGetValue(PreparedNativeBuild.TestRole(selection.Name), out NativeBuildComponent? test)
                || test.Project != Path.GetFullPath(Path.Combine(mapping.Root, selection.Entry)) || !string.Equals(test.ProjectSha256, selection.EntryHash, StringComparison.OrdinalIgnoreCase)
                || !receipt.Components.Keys.Any(role => role.Length > rolePrefix.Length && role.StartsWith(rolePrefix, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Prepared native build selected profile, executor, project or composition closure differs.");
            }
        }

        Dictionary<string, (string Package, NativeBuildPackageIdentity Identity)> cohort = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (KeyValuePair<string, NativeBuildComponent> binding in receipt.Components)
        {
            NativeBuildComponent component = binding.Value;
            if (binding.Key != component.Role || component.Role.Length > 240
                || component.Role.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))
                || !IsSha256(component.ProjectSha256) || !IsSha256(component.AssetsSha256)
                || !component.Files.ContainsKey(component.Entry)
                || !component.Files.ContainsKey(Path.ChangeExtension(component.Entry, ".deps.json"))
                || !component.Files.ContainsKey(Path.ChangeExtension(component.Entry, ".runtimeconfig.json"))
                || !component.Files.ContainsKey("restore-evidence.json")
                || !component.Framework.StartsWith("net", StringComparison.Ordinal)
                || (component.RuntimeIdentifier.Length > 0 && component.RuntimeIdentifier != receipt.Source["runtime"])
                || component.Files.Values.Any(hash => !IsSha256(hash)))
            {
                throw new InvalidDataException("Prepared native build component framework, runtime or runnable inventory is invalid.");
            }

            foreach (KeyValuePair<string, NativeBuildPackageIdentity> package in component.Packages.Where(package => package.Key.StartsWith("Hexalith.", StringComparison.OrdinalIgnoreCase)))
            {
                string family = package.Key.Split('/')[0];
                if (cohort.TryGetValue(family, out (string Package, NativeBuildPackageIdentity Identity) prior)
                    && (!prior.Package.Equals(package.Key, StringComparison.OrdinalIgnoreCase)
                        || (prior.Identity != package.Value && !family.StartsWith(mapping.ActiveModule + ".", StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidDataException("Prepared native build resolved package cohort differs between runnable outputs.");
                }

                _ = cohort.TryAdd(family, (package.Key, package.Value));
            }
        }
    }

    private static void RequireReleaseAssembly(string entry, bool model)
    {
        using FileStream input = NativeBuildInventory.OpenFile(entry);
        using PEReader pe = new(input);
        MetadataReader metadata = pe.GetMetadataReader();
        bool release = false;
        bool preparedEntrypoint = false;
        foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            EntityHandle type = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (type.Kind == HandleKind.TypeReference
                && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)type).Name) == "AssemblyConfigurationAttribute")
            {
                BlobReader value = metadata.GetBlobReader(attribute.Value);
                release = value.ReadUInt16() == 1 && value.ReadSerializedString() == "Release";
            }
        }

        if (model)
        {
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                if (metadata.GetString(type.Name) == "PlatformAppModel" && metadata.GetString(type.Namespace) == "Hexalith.Platform.Composition")
                {
                    preparedEntrypoint = type.GetMethods().Any(method => metadata.GetString(metadata.GetMethodDefinition(method).Name) == "ComposePreparedRun");
                }
            }
        }

        if (!release || (model && !preparedEntrypoint))
        {
            throw new InvalidDataException("Prepared native build requires Release assembly metadata and a prepared-compatible Platform model.");
        }
    }

    private static byte[] ReadMetadata(string path)
    {
        using FileStream input = NativeBuildInventory.OpenFile(path);
        if (input.Length is <= 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Prepared native build receipt/input metadata is absent or exceeds its size limit.");
        }

        byte[] bytes = new byte[input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static bool Same(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
        => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out string? value) && value == pair.Value);

    private static bool IsSha256(string value)
        => value is { Length: 64 } && value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static ToolCommandResult Refusal(string message, bool cancelled = false)
    {
        ToolFailureCategory category = cancelled ? ToolFailureCategory.Cancelled : ToolFailureCategory.PrerequisiteUnavailable;
        ToolDiagnostic diagnostic = new(cancelled ? "HXC130" : "HXW013", ToolPhase.Prerequisite, category, message, "preparedNativeBuild");
        ToolExitCode exitCode = cancelled ? ToolExitCode.Cancelled : ToolExitCode.PrerequisiteUnavailable;
        return new ToolCommandResult(cancelled ? "cancelled" : "failed", ToolOutcome.Passed().Fail(diagnostic.Phase, category, diagnostic.RuleId, exitCode), [diagnostic]);
    }
}
