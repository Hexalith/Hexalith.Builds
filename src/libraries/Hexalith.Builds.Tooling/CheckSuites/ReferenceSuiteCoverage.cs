// <copyright file="ReferenceSuiteCoverage.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CheckSuites;

using System.Text.Json;

/// <summary>Checks Platform-owned reference mappings against actual sources and retained packages.</summary>
public static class ReferenceSuiteCoverage
{
    private static readonly string[] _requiredModules = ["eventstore", "mcpcli", "memories", "parties", "tenants"];

    /// <summary>Refuses publication when ownership, sources, retained suites or declared mappings are unresolved.</summary>
    /// <param name="registryPath">The reference registry path.</param>
    /// <param name="repositoryRoot">The source repository containing declarations and CODEOWNERS.</param>
    /// <param name="packageRoot">The directory containing the registry's retained packages.</param>
    /// <returns>A sanitized deterministic publication-eligibility report, distinct from package validity.</returns>
    public static CheckSuiteReport Validate(string registryPath, string repositoryRoot, string packageRoot)
    {
        List<string> diagnostics = [];
        try
        {
            using JsonDocument registryDocument = CheckSuiteContract.Parse(CheckSuiteContract.ReadBounded(registryPath));
            JsonElement registry = registryDocument.RootElement;
            if (!CheckSuiteContract.HasProperties(registry, "schema", "modules") || CheckSuiteContract.Text(registry, "schema") != "hexalith.reference-check-registry.v1")
            {
                return CheckSuiteContract.Report("HXS005");
            }

            JsonElement[] modules = [.. registry.GetProperty("modules").EnumerateArray()];
            if (!modules.Select(module => CheckSuiteContract.Text(module, "module")).Order(StringComparer.Ordinal).SequenceEqual(_requiredModules))
            {
                diagnostics.Add("HXS005");
            }

            string[] ownership = File.ReadAllLines(CheckSuiteContract.OwnedPath(repositoryRoot, ".github/CODEOWNERS"));
            string[] owners = [.. ownership.Where(line => line.TrimStart().StartsWith("* ", StringComparison.Ordinal)).SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))];
            foreach (JsonElement entry in modules)
            {
                ValidateEntry(entry, repositoryRoot, packageRoot, owners, diagnostics);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            diagnostics.Add("HXS005");
        }

        return CheckSuiteContract.Report([.. diagnostics]);
    }

    private static void ValidateEntry(JsonElement entry, string repositoryRoot, string packageRoot, string[] owners, List<string> diagnostics)
    {
        string moduleId = CheckSuiteContract.Text(entry, "module");
        if (!CheckSuiteContract.HasProperties(entry, "module", "owner", "source", "sourceKind", "suite", "descriptor", "package", "criticalFlows", "smokeSurfaces")
            || !owners.Contains(CheckSuiteContract.Text(entry, "owner"), StringComparer.Ordinal))
        {
            diagnostics.Add("HXS005");
            return;
        }

        string sourcePath = CheckSuiteContract.OwnedPath(repositoryRoot, CheckSuiteContract.Text(entry, "source"));
        _ = CheckSuiteContract.ReadBounded(sourcePath);
        byte[] descriptor = CheckSuiteContract.ReadBounded(CheckSuiteContract.OwnedPath(repositoryRoot, CheckSuiteContract.Text(entry, "descriptor")));
        if (!CheckSuiteContract.ValidateDocument("suite", descriptor).Valid)
        {
            diagnostics.Add("HXS005");
            return;
        }

        using JsonDocument suiteDocument = CheckSuiteContract.Parse(descriptor);
        JsonElement suite = suiteDocument.RootElement;
        string retainedPackage = CheckSuiteContract.OwnedPath(packageRoot, CheckSuiteContract.Text(entry, "package"));
        CheckSuiteReport package = CheckSuiteContract.ValidatePackage(retainedPackage);
        if (!package.Valid || !descriptor.SequenceEqual(CheckSuiteContract.ReadBounded(Path.Combine(retainedPackage, "descriptor.json")))
            || CheckSuiteContract.Text(suite, "module") != moduleId || CheckSuiteContract.Text(suite, "owner") != CheckSuiteContract.Text(entry, "owner")
            || CheckSuiteContract.Text(suite, "source") != CheckSuiteContract.Text(entry, "source") || CheckSuiteContract.Text(suite, "suiteId") != CheckSuiteContract.Text(entry, "suite")
            || CheckSuiteContract.Text(entry, "package") != moduleId + "/" + CheckSuiteContract.Text(suite, "version"))
        {
            diagnostics.Add("HXS005");
        }

        JsonElement[] checks = [.. suite.GetProperty("checks").EnumerateArray()];
        JsonElement[] flows = [.. entry.GetProperty("criticalFlows").EnumerateArray()];
        JsonElement[] smokes = [.. entry.GetProperty("smokeSurfaces").EnumerateArray()];
        if (flows.Length == 0 || smokes.Length == 0 || flows.Select(flow => CheckSuiteContract.Text(flow, "name")).Distinct(StringComparer.Ordinal).Count() != flows.Length
            || smokes.Select(smoke => CheckSuiteContract.Text(smoke, "name")).Distinct(StringComparer.Ordinal).Count() != smokes.Length)
        {
            diagnostics.Add("HXS005");
        }

        foreach (JsonElement mapping in flows.Concat(smokes))
        {
            bool smoke = smokes.Contains(mapping);
            if (!CheckSuiteContract.HasProperties(mapping, "name", "surface", "checks", "status", "reason")
                || CheckSuiteContract.Text(mapping, "status") != "resolved" || CheckSuiteContract.Text(mapping, "reason") != "none")
            {
                diagnostics.Add("HXS006");
                continue;
            }

            string[] ids = CheckSuiteContract.Strings(mapping, "checks");
            if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !checks.Any(check => CheckSuiteContract.Text(check, "id") == id
                && CheckSuiteContract.Text(check, "surface") == CheckSuiteContract.Text(mapping, "surface")
                && CheckSuiteContract.Text(check, "kind") == (smoke ? "smoke" : "e2e")
                && CheckSuiteContract.Text(check, "assertion") == "positive"
                && CheckSuiteContract.Strings(check, "environments").Contains(smoke ? "production" : "staging", StringComparer.Ordinal))))
            {
                diagnostics.Add("HXS005");
            }
        }

        if (CheckSuiteContract.Text(entry, "sourceKind") == "manifest")
        {
            using JsonDocument source = CheckSuiteContract.Parse(CheckSuiteContract.ReadBounded(sourcePath));
            JsonElement[] declaredModules = [.. source.RootElement.GetProperty("modules").EnumerateArray().Where(module => CheckSuiteContract.Text(module.GetProperty("identity"), "moduleId") == moduleId)];
            if (declaredModules.Length != 1)
            {
                diagnostics.Add("HXS005");
                return;
            }

            JsonElement declared = declaredModules[0];
            JsonElement lifecycle = declared.GetProperty("lifecycle");
            JsonElement[] declaredFlows = [.. lifecycle.GetProperty("criticalFlows").EnumerateArray()];
            if (!declaredFlows.Select(flow => CheckSuiteContract.Text(flow, "name")).Order(StringComparer.Ordinal)
                .SequenceEqual(flows.Select(flow => CheckSuiteContract.Text(flow, "name")).Order(StringComparer.Ordinal)))
            {
                diagnostics.Add("HXS005");
            }

            foreach (JsonElement flow in declaredFlows)
            {
                JsonElement[] mappings = [.. flows.Where(mapping => CheckSuiteContract.Text(mapping, "name") == CheckSuiteContract.Text(flow, "name"))];
                if (mappings.Length != 1 || CheckSuiteContract.Text(mappings[0], "surface") != CheckSuiteContract.Text(flow, "surface")
                    || (flow.TryGetProperty("e2eChecks", out JsonElement links) && links.EnumerateArray().Any(link => !CheckSuiteContract.Strings(mappings[0], "checks").Contains(link.GetString(), StringComparer.Ordinal))))
                {
                    diagnostics.Add("HXS005");
                }
            }

            foreach (JsonElement surface in lifecycle.GetProperty("smokeSurfaces").EnumerateArray().Where(surface => !smokes.Any(mapping => CheckSuiteContract.Text(mapping, "surface") == CheckSuiteContract.Text(surface, "surface"))
                || CheckSuiteContract.Text(surface, "suite") != CheckSuiteContract.Text(suite, "suiteId")))
            {
                diagnostics.Add("HXS005");
            }

            string[] surfaces = [.. declared.GetProperty("surfaces").GetProperty("interfaces").EnumerateArray().Select(surface => CheckSuiteContract.Text(surface, "name"))];
            if (flows.Concat(smokes).Any(mapping => !surfaces.Contains(CheckSuiteContract.Text(mapping, "surface"), StringComparer.Ordinal)))
            {
                diagnostics.Add("HXS005");
            }
        }
        else if (CheckSuiteContract.Text(entry, "sourceKind") != "code" || moduleId != "mcpcli")
        {
            diagnostics.Add("HXS005");
        }
    }
}
