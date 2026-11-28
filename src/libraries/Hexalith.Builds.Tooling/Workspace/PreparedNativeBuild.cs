// <copyright file="PreparedNativeBuild.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Identifies only verified files copied into the owner's private run workspace.</summary>
/// <param name="ReceiptSha256">The independently selected producer receipt digest.</param>
/// <param name="Components">The immutable component inventories.</param>
/// <param name="Profiles">The admitted native selections.</param>
public sealed record PreparedNativeBuild(
    string ReceiptSha256,
    IReadOnlyDictionary<string, NativeBuildComponent> Components,
    IReadOnlyDictionary<string, string> Profiles)
{
    /// <summary>Gets the Platform model's fixed component role.</summary>
    public const string ModelRole = "platform-model";

    /// <summary>Gets the native test graph's profile-scoped role.</summary>
    /// <param name="profile">The declared test profile.</param>
    /// <returns>The component role.</returns>
    public static string TestRole(string profile) => "test." + profile;

    /// <summary>Gets a composition dependency's profile-scoped role.</summary>
    /// <param name="profile">The declared test profile.</param>
    /// <param name="dependency">The dependency purpose assigned by the Platform model.</param>
    /// <returns>The component role.</returns>
    public static string CompositionRole(string profile, string dependency) => "composition." + profile + "." + dependency;

    /// <summary>Resolves and checks one admitted component in this private workspace.</summary>
    /// <param name="workspace">The owner workspace.</param>
    /// <param name="role">The required component role.</param>
    /// <returns>The verified entry assembly.</returns>
    public string VerifyEntry(string workspace, string role)
    {
        NativeBuildComponent component = Require(role);
        string directory = DirectoryFor(workspace, role);
        _ = NativeBuildInventory.VerifyDirectory(directory, component.Files, null, CancellationToken.None);
        return NativeBuildInventory.Resolve(directory, component.Entry);
    }

    /// <summary>Constructs the private workspace's component path from its validated role.</summary>
    /// <param name="workspace">The owner or producer workspace.</param>
    /// <param name="role">The component role.</param>
    /// <returns>The component root.</returns>
    internal static string DirectoryFor(string workspace, string role)
        => NativeBuildInventory.Resolve(workspace, "native-build/" + role);

    /// <summary>Identifies a declared profile without using run-specific clocks or credentials.</summary>
    /// <param name="selection">The selected native entry.</param>
    /// <returns>The stable profile identity.</returns>
    internal static string ProfileIdentity(ModuleTestSelection selection)
        => NativeBuildInventory.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            selection.Name,
            selection.ModuleId,
            selection.Mode,
            selection.ManifestHash,
            selection.Entry,
            selection.EntryHash,
            selection.Executor,
        }));

    /// <summary>Checks that the owner admitted a specific declaration binding.</summary>
    /// <param name="selection">The selected native entry.</param>
    /// <returns>True when the same profile, executor, manifest and project were measured.</returns>
    internal bool Admits(ModuleTestSelection selection)
        => Profiles.TryGetValue(selection.Name, out string? identity) && identity == ProfileIdentity(selection);

    /// <summary>Copies an admitted test output into an execution-private directory.</summary>
    /// <param name="ownerWorkspace">The owner workspace holding the run's original bytes.</param>
    /// <param name="privateOutput">The destination unique to this test execution.</param>
    /// <param name="selection">The admitted native selection.</param>
    /// <param name="cancellationToken">The execution cancellation token.</param>
    /// <returns>The private verified entry assembly.</returns>
    /// <exception cref="InvalidDataException">The declared entry does not belong to this verified inventory.</exception>
    internal string CopyTest(string ownerWorkspace, string privateOutput, ModuleTestSelection selection, CancellationToken cancellationToken)
    {
        if (!Admits(selection))
        {
            throw new InvalidDataException("Prepared native build does not cover the selected profile and project.");
        }

        string role = TestRole(selection.Name);
        NativeBuildComponent component = Require(role);
        _ = NativeBuildInventory.VerifyDirectory(DirectoryFor(ownerWorkspace, role), component.Files, privateOutput, cancellationToken);
        return NativeBuildInventory.Resolve(privateOutput, component.Entry);
    }

    private NativeBuildComponent Require(string role)
        => Components.TryGetValue(role, out NativeBuildComponent? component) && component.Role == role
            ? component
            : throw new InvalidDataException("Prepared native build is missing a required composition component.");
}
