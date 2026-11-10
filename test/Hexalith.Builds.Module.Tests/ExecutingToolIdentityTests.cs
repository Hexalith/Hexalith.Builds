// <copyright file="ExecutingToolIdentityTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies the actual executor digest and fail-closed preallocation behavior.</summary>
public sealed class ExecutingToolIdentityTests
{
    /// <summary>A self-declared matching digest cannot classify arbitrary persisted content as a binary manifest.</summary>
    /// <param name="fault">The malformed or secret-bearing persisted manifest.</param>
    /// <returns>The refusal before any diagnostic bytes are retained.</returns>
    [Theory]
    [InlineData("secret")]
    [InlineData("name")]
    [InlineData("digest")]
    [InlineData("canonical")]
    [InlineData("duplicate")]
    public async Task ReviewPersistedManifestMustHaveExactCanonicalShapeAsync(string fault)
    {
        using PlatformManifestTestWorkspace workspace = new();
        ExecutingToolIdentity original = ExecutingToolIdentity.Capture();
        string changed = fault switch
        {
            "secret" => "{\"password\":\"fixture-secret-never-retained\"}",
            "name" => original.Manifest.Replace("Hexalith.Builds.Tooling.dll", "private-token.dll", StringComparison.Ordinal),
            "digest" => original.Manifest.Replace("sha256:", "sha512:", StringComparison.Ordinal),
            "canonical" => original.Manifest + " ",
            _ => original.Manifest.Replace("\"algorithm\":\"sha256\"", "\"algorithm\":\"sha256\",\"algorithm\":\"sha256\"", StringComparison.Ordinal),
        };
        ExecutingToolIdentity persisted = original with { Manifest = changed, ContentIdentity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(changed))) };
        InvalidDataException refusal = await Should.ThrowAsync<InvalidDataException>(() => persisted.RetainAsync(workspace.Root, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Message.ShouldNotContain("fixture-secret");
        Directory.GetFiles(workspace.Root, "executing-tool-*.json").ShouldBeEmpty();
    }

    /// <summary>Valid original manifest retention never hashes later binary files.</summary>
    /// <returns>The retained original bytes after both original files disappear.</returns>
    [Fact]
    public async Task ReviewValidPersistedManifestNeedsNoCleanerRehashAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string cli = Path.Combine(workspace.Root, "Hexalith.Builds.Module.Cli.dll");
        string tooling = Path.Combine(workspace.Root, "Hexalith.Builds.Tooling.dll");
        await File.WriteAllTextAsync(cli, "original-cli", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(tooling, "original-tooling", TestContext.Current.CancellationToken).ConfigureAwait(true);
        ExecutingToolIdentity identity = ExecutingToolIdentity.Capture("1.0.0", cli, tooling);
        File.Delete(cli);
        File.Delete(tooling);
        _ = await identity.RetainAsync(workspace.Root, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string retained = Directory.GetFiles(workspace.Root, "executing-tool-*.json").Single();
        (await File.ReadAllTextAsync(retained, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(identity.Manifest);
    }

    /// <summary>Actual CLI and Tooling bytes, rather than a package pin, supply the digest.</summary>
    [Fact]
    public void CaptureUsesActualVersionAndTwoNamedExecutingBinaries()
    {
        ExecutingToolIdentity identity = ExecutingToolIdentity.Capture();
        identity.Version.ShouldBe(PlatformIdentityResolver.ToolVersion);
        identity.ContentIdentity.ShouldBe("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Manifest))));
        using JsonDocument manifest = JsonDocument.Parse(identity.Manifest);
        manifest.RootElement.GetProperty("algorithm").GetString().ShouldBe("sha256");
        manifest.RootElement.GetProperty("binaries").EnumerateArray().Select(binary => binary.GetProperty("name").GetString()).ShouldBe(["Hexalith.Builds.Module.Cli.dll", "Hexalith.Builds.Tooling.dll"]);
        identity.Manifest.ShouldNotContain(Path.GetDirectoryName(typeof(ExecutingToolIdentity).Assembly.Location)!);
        using JsonDocument tool = JsonDocument.Parse(JsonSerializer.Serialize(identity.ToContract()));
        foreach (string qualification in new[] { "buildsAdoption", "platformAcceptance", "integrationQualification" })
        {
            tool.RootElement.GetProperty(qualification).GetProperty("state").GetString().ShouldBe("not-yet-populated");
        }
    }

    /// <summary>Both binary bytes influence the identity while paths and repeated capture do not.</summary>
    /// <param name="changed">The binary whose bytes change.</param>
    /// <returns>The deterministic hash assertions.</returns>
    [Theory]
    [InlineData("Hexalith.Builds.Module.Cli.dll")]
    [InlineData("Hexalith.Builds.Tooling.dll")]
    public async Task ChangedBinaryBytesChangeManifestIdentityAsync(string changed)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string cli = Path.Combine(workspace.Root, "Hexalith.Builds.Module.Cli.dll");
        string tooling = Path.Combine(workspace.Root, "Hexalith.Builds.Tooling.dll");
        await File.WriteAllTextAsync(cli, "original-cli", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(tooling, "original-tooling", TestContext.Current.CancellationToken).ConfigureAwait(true);
        ExecutingToolIdentity first = ExecutingToolIdentity.Capture("1.2.3", cli, tooling);
        ExecutingToolIdentity.Capture("1.2.3", cli, tooling).ShouldBe(first);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, changed), "changed-binary-bytes", TestContext.Current.CancellationToken).ConfigureAwait(true);
        ExecutingToolIdentity.Capture("1.2.3", cli, tooling).ContentIdentity.ShouldNotBe(first.ContentIdentity);
        using JsonDocument original = JsonDocument.Parse(first.Manifest);
        original.RootElement.GetProperty("binaries")[0].GetProperty("contentIdentity").GetString().ShouldBe("sha256:" + Convert.ToHexStringLower(SHA256.HashData("original-cli"u8)));
    }

    /// <summary>Hash failure refuses a fresh command before any state, workspace or receipt allocation.</summary>
    /// <returns>The failure and absent-allocation assertions.</returns>
    [Fact]
    public async Task HashFailureRefusesBeforeProvisioningAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            ExecutingToolCaptureOverride = () => throw new IOException("controlled binary hash failure"),
            CiOwnerReceiptPath = Path.Combine(workspace.Root, "receipt.json"),
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, workspace.Save(), "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        exit.ShouldNotBe(0);
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
        Directory.Exists(options.StateDirectory).ShouldBeFalse();
        File.Exists(options.CiOwnerReceiptPath).ShouldBeFalse();
    }
}
