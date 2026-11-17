// <copyright file="ConsumerBuildsIdentity.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Json.Schema;

/// <summary>Resolves an adopted consumer's committed single-version catalog authority.</summary>
internal static partial class ConsumerBuildsIdentity
{
    private const string _config = ".github/hexalith-ci.json";
    private const string _builds = "references/Hexalith.Builds";

    /// <summary>Returns the adopted source, or null only when no owned configuration exists.</summary>
    /// <param name="root">The active consumer Git root.</param>
    /// <param name="recorded">The already inspected index gitlinks.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="executingIdentityOverride">An internal complete-identity seam for isolated producer tests.</param>
    /// <returns>The committed and tagged catalog source.</returns>
    /// <exception cref="WorkspaceMappingException">The present owned producer cannot be authenticated.</exception>
    internal static async Task<string?> ResolveAsync(string root, IReadOnlyDictionary<string, string> recorded, CancellationToken cancellationToken, string? executingIdentityOverride = null)
    {
        CompositionProcessResult entry = await ReadAsync(root, cancellationToken, "ls-tree", "-z", "HEAD", "--", _config).ConfigureAwait(false);
        if (entry.Output.Length == 0 && !Path.Exists(Path.Combine(root, _config)) && new FileInfo(Path.Combine(root, _config)).LinkTarget is null)
        {
            return null;
        }

        if (!PlatformIdentityResolver.IsRegularBlob(entry.Output, _config))
        {
            throw Refuse(root, "The present owned CI configuration must be a committed regular blob.");
        }

        CompositionProcessResult staged = await ReadAsync(root, cancellationToken, "diff", "--cached", "--name-only", "HEAD", "--", _config).ConfigureAwait(false);
        CompositionProcessResult difference = await ReadAsync(root, cancellationToken, "diff", "--name-only", "--", _config).ConfigureAwait(false);
        if (staged.Output.Length != 0 || difference.Output.Length != 0)
        {
            throw Refuse(root, "The owned CI configuration differs from HEAD or its index.");
        }

        string content = (await ReadAsync(root, cancellationToken, "show", "HEAD:" + _config).ConfigureAwait(false)).Output;
        string path = Path.Combine(root, _config);
        try
        {
            string ancestor = root;
            foreach (string part in _config.Split('/'))
            {
                ancestor = Path.Combine(ancestor, part);
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                {
                    throw Refuse(root, "The actual owned CI configuration must be a canonical regular file.");
                }
            }

            // FileAttributes cannot identify Unix FIFOs/devices. The operating
            // system's fixed regular-file predicate is bounded and performs no
            // read, so special entries cannot block the later content read.
            if (!OperatingSystem.IsWindows())
            {
                string test = File.Exists("/usr/bin/test") ? "/usr/bin/test" : "/bin/test";
                CompositionProcessResult regular = await CompositionProcess.RunAsync(
                    CompositionProcess.CreateStartInfo(test, ["-f", path], root, null),
                    TimeSpan.FromSeconds(5),
                    1024,
                    cancellationToken).ConfigureAwait(false);
                if (!regular.Started || regular.ExitCode != 0 || regular.TimedOut || regular.OutputTruncated)
                {
                    throw Refuse(root, "The actual owned CI configuration must be a canonical regular file.");
                }
            }

            if (!File.Exists(path) || Directory.Exists(path) || new FileInfo(path).Length > 65_536)
            {
                throw Refuse(root, "The actual owned CI configuration must be a bounded regular file.");
            }

            byte[] actual = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            byte[] committedBytes = Encoding.UTF8.GetBytes(content);
            if (!actual.AsSpan().SequenceEqual(committedBytes))
            {
                string eol = (await ReadAsync(root, cancellationToken, "check-attr", "eol", "--", _config).ConfigureAwait(false)).Output.Trim();
                string autocrlf = (await ReadAsync(root, cancellationToken, "config", "--default", "false", "--get", "core.autocrlf").ConfigureAwait(false)).Output.Trim();
                bool expectedCrLf = eol.EndsWith(": crlf", StringComparison.Ordinal)
                    || (eol.EndsWith(": unspecified", StringComparison.Ordinal) && autocrlf == "true");
                if (!expectedCrLf || content.Contains('\r') || !actual.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(content.Replace("\n", "\r\n", StringComparison.Ordinal))))
                {
                    throw Refuse(root, "The actual owned CI configuration bytes differ from the committed blob.");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Refuse(root, "The actual owned CI configuration is missing or unreadable as a regular file.");
        }

        string tag = ReadTag(content, root);
        string? committed = PlatformIdentityResolver.TreeGitlink(
            (await ReadAsync(root, cancellationToken, "ls-tree", "-z", "HEAD", "--", _builds).ConfigureAwait(false)).Output, _builds);
        if (committed is null || recorded.GetValueOrDefault(_builds) != committed)
        {
            throw Refuse(root, "The owned Builds committed/index gitlinks differ or are unavailable.");
        }

        string builds = Path.Combine(root, _builds);
        string tagged = (await ReadAsync(builds, cancellationToken, "rev-parse", "--verify", "refs/tags/" + tag + "^{commit}").ConfigureAwait(false)).Output.Trim();
        if (tagged != committed)
        {
            throw Refuse(root, "The owned Builds stable tag differs from its committed source.");
        }

        string? executing = executingIdentityOverride ?? typeof(ConsumerBuildsIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return executing == tag[1..] + "+" + committed
            ? committed
            : throw Refuse(root, "The executing Builds Tooling version/source differs from the owned producer.");
    }

    private static string ReadTag(string content, string root)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement value = document.RootElement;
            using Stream stream = typeof(ConsumerBuildsIdentity).Assembly.GetManifestResourceStream("Hexalith.Builds.Tooling.Workspace.hexalith.ci.v1.json")
                ?? throw Refuse(root, "The shared CI contract is unavailable.");
            using StreamReader reader = new(stream);
            JsonSchema schema = JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions
            {
                SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => throw new InvalidOperationException("Remote schemas are forbidden.") },
            });
            if (JsonDuplicatePropertyValidator.FindDuplicateProperty(value) is not null || !schema.Evaluate(value).IsValid
                || !value.TryGetProperty("buildsTag", out JsonElement selected) || selected.ValueKind != JsonValueKind.String
                || selected.GetString() is not { } tag || !StableTag().IsMatch(tag))
            {
                throw Refuse(root, "The complete owned CI configuration or exact stable Builds tag is invalid.");
            }

            HashSet<string> enabled =
            [
                .. value.GetProperty("hooks").EnumerateArray().Select(item => item.GetString()!),
                .. value.GetProperty("jobs").EnumerateObject().Select(item => item.Name),
            ];
            return value.GetProperty("tests").GetProperty("exclude").EnumerateObject().Any(item => !enabled.Contains(item.Value.GetString()!))
                ? throw Refuse(root, "The owned CI configuration excludes a project without an enabled action.")
                : tag;
        }
        catch (JsonException)
        {
            throw Refuse(root, "The owned CI configuration must contain strict JSON.");
        }
    }

    private static async Task<CompositionProcessResult> ReadAsync(string root, CancellationToken cancellationToken, params string[] arguments)
    {
        CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, arguments).ConfigureAwait(false);
        return result.Started && result.ExitCode == 0 && !result.TimedOut && !result.OutputTruncated
            ? result
            : throw Refuse(root, "Local owned Builds configuration/tag inspection failed.");
    }

    private static WorkspaceMappingException Refuse(string root, string message) => new(new ToolDiagnostic(
        "HXW009", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, message, Path.Combine(root, _config)));

    [GeneratedRegex("^v(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)$")]
    private static partial Regex StableTag();
}
