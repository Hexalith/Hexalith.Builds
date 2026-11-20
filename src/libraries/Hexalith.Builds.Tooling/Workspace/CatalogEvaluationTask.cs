// <copyright file="CatalogEvaluationTask.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Text.Json;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;

/// <summary>Evaluates the pinned catalog for multiple consumers in one isolated SDK process.</summary>
public sealed class CatalogEvaluationTask : Microsoft.Build.Utilities.Task
{
    /// <summary>Gets or sets the generated, consumer-named wrapper projects.</summary>
    [Required]
    public ITaskItem[] Projects { get; set; } = [];

    /// <inheritdoc/>
    public override bool Execute()
    {
        using ProjectCollection collection = new();
        foreach (ITaskItem item in Projects)
        {
            string path = item.GetMetadata("FullPath");
            Project project = collection.LoadProject(path);
            Dictionary<string, string> versions = [with(StringComparer.OrdinalIgnoreCase)];
            foreach (ProjectItem package in project.GetItems("PackageVersion"))
            {
                string id = package.EvaluatedInclude;
                string version = package.GetMetadataValue("Version");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                {
                    Log.LogError($"Empty package ID or catalog version for '{id}' in '{path}'.");
                    return false;
                }

                if (!versions.TryAdd(id, version))
                {
                    Log.LogError($"Duplicate catalog package '{id}' in '{path}'.");
                    return false;
                }
            }

            using FileStream output = File.Create(path + ".json");
            JsonSerializer.Serialize(output, versions);
            collection.UnloadProject(project);
        }

        return true;
    }
}
