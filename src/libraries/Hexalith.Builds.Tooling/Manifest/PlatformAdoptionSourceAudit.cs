// <copyright file="PlatformAdoptionSourceAudit.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>Bounds known source patterns; unsupported languages and indirection require owner review.</summary>
internal static class PlatformAdoptionSourceAudit
{
    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Inspects tracked module-owned build, source, configuration and helper files.</summary>
    /// <param name="root">The module root.</param>
    /// <param name="tracked">Tracked module-owned paths.</param>
    /// <param name="frozen">Exact verified hosting directories.</param>
    /// <param name="requirement">The audit requirement.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Failures with file and reason; no raw source is emitted.</returns>
    internal static IReadOnlyList<PlatformAdoptionRow> Scan(string root, IReadOnlyList<string> tracked, HashSet<string> frozen, string requirement, CancellationToken cancellationToken)
    {
        List<PlatformAdoptionRow> rows = [];
        foreach (string path in tracked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToUpperInvariant();
            if (!new[] { ".CS", ".CSPROJ", ".PROPS", ".TARGETS", ".PS1", ".SH", ".PY", ".JSON", ".YAML", ".YML" }.Contains(extension, StringComparer.Ordinal)
                || frozen.Any(directory => path.StartsWith(directory + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (!PlatformAdoptionValidator.IsLocalFile(root, path))
            {
                rows.Add(new PlatformAdoptionRow(requirement, false, path, "Tracked audit file is missing, unreadable or physically escapes the active workspace."));
                continue;
            }

            try
            {
                FileInfo info = new(Path.Combine(root, path));
                if (info.Length > 1_048_576)
                {
                    rows.Add(new PlatformAdoptionRow(requirement, false, path, "File exceeds the bounded 1 MiB source audit; owner review is required."));
                    continue;
                }

                string content = File.ReadAllText(info.FullName);
                if (requirement == "dependency-selectors")
                {
                    if (extension is ".CSPROJ" or ".PROPS" or ".TARGETS")
                    {
                        InspectBuild(content, path, rows);
                    }
                    else if (extension is ".CS" or ".PS1" or ".SH" or ".PY")
                    {
                        bool helper = Match(content, @"(?:Resolve|Find|Get)[-_]?(?:Hexalith|Sibling|Repository|Source)\w*(?:Root|Path)|(?:sibling|nested|package)[-_]?(?:fallback|root|checkout)");
                        bool discovery = Match(content, @"(?:File\s*\.\s*Exists|Directory\s*\.\s*Exists|Test-Path|os\.path\.(?:exists|isdir)|Path|\[\s*-[def]\s)[^\r\n]*(?:Hexalith\.|UseNuGetDeps|UseHexalithProjectReferences|FromSource|Root)|(?:UseNuGetDeps|UseHexalithProjectReferences|FromSource|\w+Root)[^\r\n]*(?:Exists|Test-Path|exists|isdir)");
                        if (helper || discovery || Match(content, @"(?:\.\.[/\\])+(?:references[/\\])?Hexalith\.|references[/\\]Hexalith\.[^\s""']+[/\\]references[/\\]"))
                        {
                            rows.Add(new PlatformAdoptionRow("dependency-selectors", false, path, "Known sibling/nested/package-fallback helper or file-based dependency/mode selection; use only the tool-resolved mapping."));
                        }
                    }
                }
                else if (extension == ".CS")
                {
                    if (Match(content, @"\b(?:WithTopic|MapSubscribeHandler|MapDaprSubscribeHandler|SubscribeTopic|AddTopicEventHandler)\s*\(|\[\s*(?:Dapr\s*\.\s*)?Topic(?:Attribute)?\s*\(|[""']/dapr/subscribe[""']"))
                    {
                        rows.Add(new PlatformAdoptionRow("dapr-subscriptions", false, path, "Programmatic subscription discovery is forbidden; declare delivery routes and dead-letter policy in the module declaration."));
                    }

                    if (Match(content, @"\b(?:GetStateAsync|GetBulkStateAsync|GetStateEntryAsync|SaveStateAsync|DeleteStateAsync|ExecuteStateTransactionAsync|PublishEventAsync|GetSecretAsync|GetBulkSecretAsync)\s*(?:<[^>\r\n]+>)?\s*\(\s*""[^""]+""|\b(?:StoreName|StateStoreName|PubSubName|PubsubName|SecretStoreName|ComponentName|storeName|pubsubName)\s*[:=]\s*""[^""]+"""))
                    {
                        rows.Add(new PlatformAdoptionRow("dapr-subscriptions", false, path, "Literal Dapr component name; obtain the name from the declared configuration key."));
                    }
                }
                else if (requirement == "dapr-subscriptions" && extension is ".JSON" or ".YAML" or ".YML"
                    && Match(content, @"[""']?(?:StoreName|StateStoreName|PubSubName|SecretStoreName|ComponentName)[""']?\s*:\s*[""']?[A-Za-z0-9][A-Za-z0-9._-]*"))
                {
                    rows.Add(new PlatformAdoptionRow("dapr-subscriptions", false, path, "Literal Dapr component default in configuration; Platform injects the declared configuration key."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or RegexMatchTimeoutException)
            {
                rows.Add(new PlatformAdoptionRow(requirement, false, path, "Cannot complete the bounded source inspection; correct unreadable/malformed input or obtain owner review."));
            }
        }

        return rows;
    }

    private static void InspectBuild(string content, string path, List<PlatformAdoptionRow> rows)
    {
        XDocument document = XDocument.Parse(content);
        foreach (XElement element in document.Descendants())
        {
            string name = element.Name.LocalName;
            string condition = string.Join(" ", element.AncestorsAndSelf().Attributes("Condition").Select(attribute => attribute.Value));
            string value = element.HasElements ? string.Empty : element.Value;
            string include = element.Attribute("Include")?.Value ?? element.Attribute("Project")?.Value ?? string.Empty;
            bool dependency = name.Contains("Hexalith", StringComparison.OrdinalIgnoreCase) || name is "UseNuGetDeps" or "UseHexalithProjectReferences" || name.EndsWith("FromSource", StringComparison.Ordinal) || include.Contains("Hexalith", StringComparison.OrdinalIgnoreCase);
            bool selector = name is "ProjectReference" or "PackageReference" || name.EndsWith("Root", StringComparison.Ordinal) || name.EndsWith("FromSource", StringComparison.Ordinal) || name is "UseNuGetDeps" or "UseHexalithProjectReferences";
            if ((dependency && selector && Match(condition, @"\bExists\s*\("))
                || (dependency && Match(value + " " + include, @"(?:\.\.[/\\])+(?:references[/\\])?Hexalith\.|references[/\\]Hexalith\.[^\s""']+[/\\]references[/\\]")))
            {
                rows.Add(new PlatformAdoptionRow("dependency-selectors", false, path, "File-based dependency selection or sibling/nested fallback; consume only tool-mapped roots and project/package pairs."));
                return;
            }

            if (name is "UseNuGetDeps" or "UseHexalithProjectReferences" || (name.EndsWith("Root", StringComparison.Ordinal) && dependency))
            {
                rows.Add(new PlatformAdoptionRow("dependency-selectors", false, path, "Module build files assign a tool-owned dependency root or mode property; leave selection to the pinned tool."));
                return;
            }
        }
    }

    private static bool Match(string content, string pattern) => Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _regexTimeout);
}
