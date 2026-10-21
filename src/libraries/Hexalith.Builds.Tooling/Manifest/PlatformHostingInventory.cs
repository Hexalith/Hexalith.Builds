// <copyright file="PlatformHostingInventory.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json;

/// <summary>Checks exact frozen hosting project directories and their pending retirement gates.</summary>
internal static class PlatformHostingInventory
{
    private static readonly string[] _kinds = ["AppHost", "Aspire", "ServiceDefaults"];

    /// <summary>Validates inventory completeness; it grants no retirement authority.</summary>
    /// <param name="root">The active module root.</param>
    /// <param name="tracked">Tracked module-owned paths.</param>
    /// <returns>Only exact, verified frozen hosting project directories.</returns>
    internal static HashSet<string> Validate(string root, IReadOnlyList<string> tracked)
    {
        const string path = "module/hosting-inventory.json";
        PlatformAdoptionValidator.Require(PlatformAdoptionValidator.IsLocalFile(root, path), "Supply module/hosting-inventory.json; use an empty projects array only when no hosting projects exist.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)));
        JsonElement inventory = document.RootElement;
        PlatformAdoptionValidator.Require(inventory.ValueKind == JsonValueKind.Object && inventory.TryGetProperty("schemaVersion", out JsonElement version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int major) && major == 1, "Use hosting inventory schemaVersion 1.");
        PlatformAdoptionValidator.Require(inventory.TryGetProperty("projects", out JsonElement projects) && projects.ValueKind == JsonValueKind.Array, "Supply the hosting projects array.");
        RequireProperties(inventory, ["schemaVersion", "projects"]);
        HashSet<string> recorded = new(StringComparer.Ordinal);
        HashSet<string> frozen = new(StringComparer.Ordinal);
        foreach (JsonElement project in projects.EnumerateArray())
        {
            RequireProperties(project, ["path", "kind", "owner", "consumers", "state", "replacementPlan", "retirementPlan", "gates"]);
            string projectPath = Text(project, "path");
            string kind = Text(project, "kind");
            string? actualKind = Kind(projectPath);
            PlatformAdoptionValidator.Require(actualKind is not null && actualKind == kind && tracked.Contains(projectPath, StringComparer.Ordinal), "Inventory path must name a tracked AppHost, Aspire or ServiceDefaults project with its matching kind: " + projectPath + ".");
            PlatformAdoptionValidator.Require(PlatformAdoptionValidator.IsLocalFile(root, projectPath), "A frozen hosting project must exist physically inside the active workspace: " + projectPath + ".");
            PlatformAdoptionValidator.Require(recorded.Add(projectPath), "Remove duplicate hosting record: " + projectPath + ".");
            PlatformAdoptionValidator.Require(!Pending(Text(project, "owner")), "Replace the pending hosting owner for " + projectPath + ".");
            PlatformAdoptionValidator.Require(project.TryGetProperty("consumers", out JsonElement consumers) && consumers.ValueKind == JsonValueKind.Array && consumers.GetArrayLength() > 0 && consumers.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) && !Pending(value.GetString()!)), "List identified owning consumers (or explicitly 'none') for " + projectPath + ".");
            PlatformAdoptionValidator.Require(Text(project, "state") == "frozen", "Hosting projects must remain frozen: " + projectPath + ".");
            _ = Text(project, "replacementPlan");
            _ = Text(project, "retirementPlan");
            PlatformAdoptionValidator.Require(project.TryGetProperty("gates", out JsonElement gates) && gates.ValueKind == JsonValueKind.Object, "Record pending retirement gates for " + projectPath + ".");
            RequireProperties(gates, ["producerAdoption", "ownerReviewedSourcePackageDeployedParity", "eventStoreAd22ExactSubjectRemovalAuthority"]);
            foreach (string gate in new[] { "producerAdoption", "ownerReviewedSourcePackageDeployedParity", "eventStoreAd22ExactSubjectRemovalAuthority" })
            {
                _ = Text(gates, gate);
            }

            PlatformAdoptionValidator.Require(projectPath.Contains('/', StringComparison.Ordinal), "A frozen project must have its own directory.");
            string directory = projectPath[..projectPath.LastIndexOf('/')];
            PlatformAdoptionValidator.Require(directory is not "src" and not "test" and not "tests", "Only an exact hosting-project directory can be frozen; never root build files.");
            PlatformAdoptionValidator.Require(!tracked.Any(file => file.StartsWith(directory + "/", StringComparison.Ordinal) && file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && file != projectPath), "A frozen directory cannot contain another project: " + directory + ".");
            _ = frozen.Add(directory);
        }

        string[] missing = [.. tracked.Where(path => Kind(path) is not null && !recorded.Contains(path))];
        PlatformAdoptionValidator.Require(missing.Length == 0, "Inventory every module-owned hosting project: " + string.Join(", ", missing) + ".");
        return frozen;
    }

    private static void RequireProperties(JsonElement parent, string[] names) =>
        PlatformAdoptionValidator.Require(parent.ValueKind == JsonValueKind.Object && parent.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal)), "Remove unknown inventory fields and arbitrary exclusions; only exact frozen hosting-project directories are permitted.");

    private static string Text(JsonElement parent, string name)
    {
        PlatformAdoptionValidator.Require(parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()), "Supply a nonblank hosting inventory field: " + name + ".");
        return parent.GetProperty(name).GetString()!;
    }

    private static bool Pending(string value) => value.TrimStart().StartsWith("pending", StringComparison.OrdinalIgnoreCase);

    private static string? Kind(string path) => _kinds.FirstOrDefault(kind => path.EndsWith("." + kind + ".csproj", StringComparison.OrdinalIgnoreCase));
}
