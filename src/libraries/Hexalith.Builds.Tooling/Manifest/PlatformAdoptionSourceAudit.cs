// <copyright file="PlatformAdoptionSourceAudit.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using YamlDotNet.RepresentationModel;

/// <summary>Bounds known source patterns; unsupported languages and indirection require owner review.</summary>
internal static class PlatformAdoptionSourceAudit
{
    private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Inspects tracked module-owned build, source, configuration and helper files.</summary>
    /// <param name="root">The module root.</param>
    /// <param name="tracked">Tracked module-owned paths.</param>
    /// <param name="frozen">Exact verified hosting directories.</param>
    /// <param name="configurationKeys">The declared Dapr configuration keys.</param>
    /// <param name="requirement">The audit requirement.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Failures with file and reason; no raw source is emitted.</returns>
    internal static IReadOnlyList<PlatformAdoptionRow> Scan(string root, IReadOnlyList<string> tracked, HashSet<string> frozen, HashSet<string> configurationKeys, string requirement, CancellationToken cancellationToken)
    {
        List<PlatformAdoptionRow> rows = [];
        foreach (string path in tracked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToUpperInvariant();
            bool configuration = extension is ".JSON" or ".YAML" or ".YML";
            bool supported = new[] { ".CS", ".CSPROJ", ".PROPS", ".TARGETS", ".PS1", ".SH", ".PY" }.Contains(extension, StringComparer.Ordinal)
                || (requirement == "dapr-subscriptions" && configuration && Path.GetFileName(path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
            if (!supported
                || (requirement == "dapr-subscriptions" && frozen.Any(directory => path.StartsWith(directory + "/", StringComparison.Ordinal))))
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
                        InspectBuild(root, content, path, rows);
                    }
                    else if (extension is ".CS" or ".PS1" or ".SH" or ".PY")
                    {
                        string commentsMasked = MaskNonExecutable(content, maskStrings: false, hashComments: extension != ".CS");
                        const string dependencyTarget = @"references[/\\]Hexalith\.|(?:\.\.[/\\])+Hexalith\.|UseNuGetDeps|UseHexalithProjectReferences|Hexalith\w*(?:FromSource|Root)|HexalithTenantsBasePath|HexalithSourceMapping\w*";
                        string executable = MaskNonExecutable(content, hashComments: extension != ".CS");
                        bool discovery = HasDependencyProbe(commentsMasked, executable, dependencyTarget);
                        bool helper = MatchCode(commentsMasked, executable, @"(?:Resolve|Find|Get)[-_]?(?:Hexalith|Sibling)\w*(?:Root|Path)|(?:sibling|nested|package)[-_]?fallback")
                            || MatchCode(commentsMasked, executable, @"(?:Resolve|Find|Get)[-_]?(?:Repository|Source)\w*(?:Root|Path)\s*\([^()\r\n]*(?:" + dependencyTarget + ")");
                        if (helper || discovery)
                        {
                            rows.Add(new PlatformAdoptionRow("dependency-selectors", false, path, "Known sibling/nested/package-fallback helper or file-based dependency/mode selection; use only the tool-resolved mapping."));
                        }
                    }
                }
                else if (extension == ".CS")
                {
                    string executable = MaskNonExecutable(content);
                    string syntax = MaskNonExecutable(content, maskStrings: false);
                    if (MatchCode(syntax, executable, @"\b(?:WithTopic|MapSubscribeHandler|MapDaprSubscribeHandler|SubscribeTopic|AddTopicEventHandler)\s*\(|(?:\[\s*|,\s*)(?:global\s*::\s*)?(?:[A-Za-z_]\w*\s*\.\s*)*Topic(?:Attribute)?\s*\(|\b(?:Map(?:Get|Post|Methods)|Http(?:Get|Post))\s*\(\s*@?""/?dapr/subscribe"""))
                    {
                        rows.Add(new PlatformAdoptionRow("dapr-subscriptions", false, path, "Programmatic subscription discovery is forbidden; declare delivery routes and dead-letter policy in the module declaration."));
                    }

                    if (MatchCode(syntax, executable, @"\b(?:GetStateAsync|GetBulkStateAsync|GetStateEntryAsync|SaveStateAsync|TrySaveStateAsync|QueryStateAsync|DeleteStateAsync|ExecuteStateTransactionAsync|PublishEventAsync|PublishBulkEventAsync|InvokeBindingAsync|GetSecretAsync|GetBulkSecretAsync)\s*(?:<[^>\r\n]+>)?\s*\(\s*@?""[^""]+""|\b(?:StoreName|StateStoreName|PubSubName|PubsubName|SecretStoreName|ComponentName|storeName|pubsubName)\s*[:=]\s*@?""[^""]+"""))
                    {
                        rows.Add(new PlatformAdoptionRow("dapr-subscriptions", false, path, "Literal Dapr component name; obtain the name from the declared configuration key."));
                    }
                }
                else if (extension is ".JSON" or ".YAML" or ".YML" && HasPhysicalComponentDefault(content, extension, configurationKeys))
                {
                    rows.Add(new PlatformAdoptionRow("dapr-subscriptions", false, path, "Literal Dapr component default in configuration; Platform injects the declared configuration key. Bounded static check only."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or RegexMatchTimeoutException or JsonException or YamlDotNet.Core.YamlException)
            {
                rows.Add(new PlatformAdoptionRow(requirement, false, path, "Cannot complete the bounded source inspection; correct unreadable/malformed input or obtain owner review."));
            }
        }

        return rows;
    }

    private static void InspectBuild(string root, string content, string path, List<PlatformAdoptionRow> rows)
    {
        XDocument document = XDocument.Parse(content);
        foreach (XElement element in document.Descendants())
        {
            string name = element.Name.LocalName;
            string condition = string.Join(" ", element.AncestorsAndSelf().Attributes("Condition").Select(attribute => attribute.Value));
            string value = element.HasElements ? string.Empty : element.Value;
            string include = element.Attribute("Include")?.Value ?? element.Attribute("Project")?.Value ?? string.Empty;
            if (name.Equals("HexalithBuildsRoot", StringComparison.OrdinalIgnoreCase)
                && Regex.Replace(condition, @"\s+", string.Empty, RegexOptions.CultureInvariant, _regexTimeout).Equals("'$(HexalithBuildsRoot)'==''", StringComparison.OrdinalIgnoreCase)
                && Regex.Replace(value.Replace('\\', '/'), @"\s+", string.Empty, RegexOptions.CultureInvariant, _regexTimeout) == "$(MSBuildThisFileDirectory)references/Hexalith.Builds")
            {
                continue;
            }

            bool projectReference = name.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase);
            bool mode = name.Equals("UseNuGetDeps", StringComparison.OrdinalIgnoreCase) || name.Equals("UseHexalithProjectReferences", StringComparison.OrdinalIgnoreCase);
            bool generated = name.StartsWith("HexalithSourceMapping", StringComparison.OrdinalIgnoreCase)
                || name.Equals("HexalithTenantsBasePath", StringComparison.OrdinalIgnoreCase)
                || (name.StartsWith("Hexalith", StringComparison.OrdinalIgnoreCase)
                    && (name.EndsWith("Root", StringComparison.OrdinalIgnoreCase) || name.EndsWith("FromSource", StringComparison.OrdinalIgnoreCase)));
            bool pathReference = projectReference || name.Equals("Import", StringComparison.OrdinalIgnoreCase);
            bool mappedPath = pathReference && Match(include, @"\$\((?:Hexalith\w*Root|HexalithTenantsBasePath)\)");
            bool crossModulePath = pathReference && IsCrossModulePath(root, path, include);
            bool package = name.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) && include.StartsWith("Hexalith.", StringComparison.OrdinalIgnoreCase);
            bool dependency = generated || mode || mappedPath || crossModulePath || package;
            if (crossModulePath || (dependency && Match(condition, @"\bExists\s*\(")))
            {
                rows.Add(new PlatformAdoptionRow("dependency-selectors", false, path, "File-based dependency selection or sibling/nested fallback; consume only tool-mapped roots and project/package pairs."));
                return;
            }

            if (mode || generated)
            {
                rows.Add(new PlatformAdoptionRow("dependency-selectors", false, path, "Module build files assign a tool-owned dependency root or mode property; leave selection to the pinned tool."));
                return;
            }
        }
    }

    private static bool IsCrossModulePath(string root, string path, string include)
    {
        string directory = Path.GetDirectoryName(Path.Combine(root, path))!;
        string local = include.Replace('\\', '/').Replace("$(MSBuildThisFileDirectory)", directory.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase);
        if (local.Length == 0 || local.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        string relative = Path.GetRelativePath(root, Path.GetFullPath(local, directory)).Replace('\\', '/');
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || Match(relative, @"^references/Hexalith\.[^/]+(?:/|$)");
    }

    private static bool HasDependencyProbe(string syntax, string executable, string dependencyTarget)
    {
        foreach (Match probe in Regex.Matches(executable, @"\b(?:File\s*\.\s*Exists|Directory\s*\.\s*Exists|os\s*\.\s*path\s*\.\s*(?:exists|isdir)|Path)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _regexTimeout))
        {
            int start = probe.Index + probe.Length;
            int end = ProbeArgumentEnd(executable, start, call: true);
            if (Match(probe.Value, @"^Path\s*\(") && !Match(executable[end..], @"^\)\s*\.\s*(?:exists|is_dir|is_file)\s*\("))
            {
                continue;
            }

            if (Match(syntax[start..end], dependencyTarget))
            {
                return true;
            }
        }

        foreach (Match probe in Regex.Matches(executable, @"\bTest-Path\b|\btest\s+-[def]\b|\[\s*-[def]\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _regexTimeout))
        {
            int start = probe.Index + probe.Length;
            if (Match(syntax[start..ProbeArgumentEnd(executable, start, call: false)], dependencyTarget))
            {
                return true;
            }
        }

        return false;
    }

    private static int ProbeArgumentEnd(string executable, int start, bool call)
    {
        int depth = call ? 1 : 0;
        for (int index = start; index < executable.Length; index++)
        {
            char current = executable[index];
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth <= 0)
            {
                return index;
            }
            else if (current is ';' or '{' or '}' || (!call && depth == 0 && current is ']' or '|' or '&'))
            {
                return index;
            }
            else if (!call && depth == 0 && current is '\r' or '\n')
            {
                int previous = index - 1;
                while (previous >= start && executable[previous] is ' ' or '\t' or '\r')
                {
                    previous--;
                }

                if (previous < start || executable[previous] is not '\\' and not '`')
                {
                    return index;
                }
            }
        }

        return executable.Length;
    }

    private static bool MatchCode(string content, string executable, string pattern) =>
        Regex.Matches(content, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _regexTimeout)
            .Any(match => !char.IsWhiteSpace(executable[match.Index]));

    private static string MaskNonExecutable(string content, bool maskStrings = true, bool hashComments = false)
    {
        char[] executable = content.ToCharArray();
        int index = 0;
        while (index < content.Length)
        {
            int start = index;
            bool mask = true;
            if ((hashComments && content[index] == '#')
                || (content[index] == '/' && index + 1 < content.Length && content[index + 1] == '/'))
            {
                index = content.IndexOf('\n', index);
                if (index < 0)
                {
                    index = content.Length;
                }
            }
            else if (content[index] == '/' && index + 1 < content.Length && content[index + 1] == '*')
            {
                int end = content.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? content.Length : end + 2;
            }
            else if (content[index] is '"' or '\'')
            {
                mask = maskStrings;
                char quote = content[index++];
                bool verbatim = start > 0 && content[start - 1] == '@';
                int quotes = 1;
                while (index < content.Length && content[index] == quote && quote == '"')
                {
                    quotes++;
                    index++;
                }

                if (quotes >= 3)
                {
                    int end = content.IndexOf(new string(quote, quotes), index, StringComparison.Ordinal);
                    index = end < 0 ? content.Length : end + quotes;
                }
                else if (quotes == 1)
                {
                    while (index < content.Length)
                    {
                        char current = content[index++];
                        if (current == quote)
                        {
                            if (verbatim && index < content.Length && content[index] == quote)
                            {
                                index++;
                                continue;
                            }

                            break;
                        }

                        if (!verbatim && current == '\\' && index < content.Length)
                        {
                            index++;
                        }
                    }
                }
            }
            else
            {
                index++;
                continue;
            }

            for (int position = start; mask && position < index; position++)
            {
                if (executable[position] is not '\r' and not '\n')
                {
                    executable[position] = ' ';
                }
            }
        }

        return new string(executable);
    }

    private static bool HasPhysicalComponentDefault(string content, string extension, HashSet<string> configurationKeys)
    {
        if (extension == ".JSON")
        {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return InspectJson(document.RootElement, string.Empty);
        }

        YamlStream yaml = [];
        using StringReader reader = new(content);
        yaml.Load(reader);
        return yaml.Documents.Any(document => InspectYaml(document.RootNode, string.Empty));

        bool InspectJson(JsonElement value, string prefix) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(property => InspectJson(property.Value, Join(prefix, property.Name))),
            JsonValueKind.Array => value.EnumerateArray().Any(item => InspectJson(item, prefix)),
            JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => IsComponentKey(prefix) && Physical(value.ValueKind == JsonValueKind.Null ? null : value.ToString()),
            _ => false,
        };

        bool InspectYaml(YamlNode value, string prefix) => value switch
        {
            YamlMappingNode mapping => mapping.Children.Any(pair => pair.Key is YamlScalarNode key && InspectYaml(pair.Value, Join(prefix, key.Value ?? string.Empty))),
            YamlSequenceNode sequence => sequence.Children.Any(item => InspectYaml(item, prefix)),
            YamlScalarNode scalar => IsComponentKey(prefix) && Physical(scalar.Value),
            _ => false,
        };

        bool IsComponentKey(string key) => configurationKeys.Contains(key)
            || Match(key, "(?:^|:)(?:StoreName|StateStoreName|PubSubName|SecretStoreName|ComponentName)$");
    }

    private static string Join(string prefix, string name) => prefix.Length == 0 ? name : prefix + ":" + name;

    private static bool Physical(string? value) => !string.IsNullOrWhiteSpace(value)
        && value is not "null" and not "~"
        && !Match(value, @"^(?:\$\{[A-Za-z_][A-Za-z0-9_]*\}|\$\([A-Za-z_][A-Za-z0-9_:]*\)|\{\{\s*[A-Za-z_][A-Za-z0-9_.:]*\s*\}\})$");

    private static bool Match(string content, string pattern) => Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _regexTimeout);
}
