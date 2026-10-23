// <copyright file="CheckSuiteContract.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CheckSuites;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Json.Schema;

/// <summary>Validates closed v1 documents and packages without executing their payload.</summary>
public static partial class CheckSuiteContract
{
    private const int _maximumBytes = 4 * 1024 * 1024;
    private static readonly string[] _identityFields = ["invocationId", "checkId", "attemptId", "environment", "executionMode", "servedReleaseDigest", "suiteDigest", "descriptorDigest", "profileDigest", "configurationDigest", "workingBaseline"];

    /// <summary>Validates a suite, invocation or result against its embedded schema and semantic rules.</summary>
    /// <param name="kind">The exact v1 document kind: suite, invocation or result.</param>
    /// <param name="bytes">The UTF-8 document bytes.</param>
    /// <returns>A sanitized report.</returns>
    public static CheckSuiteReport ValidateDocument(string kind, byte[] bytes)
    {
        try
        {
            if (kind is not ("suite" or "invocation" or "result") || bytes is null || bytes.Length > _maximumBytes)
            {
                return Report("HXS001");
            }

            using JsonDocument document = Parse(bytes);
            JsonElement root = document.RootElement;
            using Stream? stream = typeof(CheckSuiteContract).Assembly.GetManifestResourceStream($"Hexalith.Builds.Tooling.CheckSuites.hexalith.check-{kind}.v1.json");
            if (stream is null)
            {
                return Report("HXS001");
            }

            using StreamReader reader = new(stream, Encoding.UTF8);
            JsonSchema schema = JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions
            {
                SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => throw new InvalidOperationException("Remote schemas are forbidden.") },
            });
            if (!schema.Evaluate(root).IsValid)
            {
                return Report("HXS001");
            }

            if (kind == "suite")
            {
                JsonElement[] checks = [.. root.GetProperty("checks").EnumerateArray()];
                string[] payload = [.. root.GetProperty("payload").EnumerateArray().Select(item => item.GetString()!)];
                if (!SafePath(Text(root, "source")) || !payload.All(SafePath) || !payload.Contains(Text(root, "entrypoint"), StringComparer.Ordinal)
                    || payload.Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Length
                    || checks.Select(check => Text(check, "id")).Distinct(StringComparer.Ordinal).Count() != checks.Length
                    || checks.Any(check => Text(check, "effect") == "synthetic-write" && Text(check, "tenancy") != "run-scoped-synthetic")
                    || checks.Any(check => Strings(check, "environments").Contains("production", StringComparer.Ordinal)
                        && (Text(check, "kind") != "smoke" || Text(check, "effect") != "read" || Text(check, "tenancy") != "existing-synthetic")))
                {
                    return Report("HXS002");
                }
            }
            else if (kind == "invocation")
            {
                if (!Date(root, "startedAt", out DateTimeOffset started) || !Date(root, "deadlineAt", out DateTimeOffset deadline)
                    || deadline <= started || deadline - started > TimeSpan.FromHours(1)
                    || !Uri.TryCreate(Text(root, "verificationEndpoint"), UriKind.Absolute, out Uri? endpoint)
                    || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
                    || (endpoint.Scheme != "https" && !(Text(root, "executionMode") == "controlled" && endpoint.Scheme == "http" && endpoint.IsLoopback))
                    || (Text(root, "environment") == "production" && Text(root, "syntheticTenantMode") != "existing-synthetic"))
                {
                    return Report("HXS002");
                }
            }
            else if (!Date(root, "startedAt", out DateTimeOffset started) || !Date(root, "completedAt", out DateTimeOffset completed) || completed < started
                || (Text(root, "outcome") == "passed" && (!root.GetProperty("complete").GetBoolean() || root.GetProperty("diagnostics").GetArrayLength() != 0)))
            {
                return Report("HXS002");
            }

            return Report();
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Report("HXS001");
        }
    }

    /// <summary>Creates a deterministic directory package, refusing existing destinations or unsafe payloads.</summary>
    /// <param name="descriptorPath">The owned source descriptor.</param>
    /// <param name="payloadRoot">The directory containing its declared payload.</param>
    /// <param name="outputDirectory">A destination that must not exist.</param>
    /// <returns>A report containing the package digest.</returns>
    public static CheckSuiteReport Package(string descriptorPath, string payloadRoot, string outputDirectory)
    {
        string? temporary = null;
        try
        {
            string descriptorFullPath = Path.GetFullPath(descriptorPath);
            byte[] descriptor = ReadBounded(OwnedPath(Path.GetDirectoryName(descriptorFullPath)!, Path.GetFileName(descriptorFullPath)));
            CheckSuiteReport validation = ValidateDocument("suite", descriptor);
            if (!validation.Valid)
            {
                return validation;
            }

            using JsonDocument document = Parse(descriptor);
            JsonElement root = document.RootElement;
            List<(string Path, byte[] Bytes)> files = [];
            foreach (string path in Strings(root, "payload").Order(StringComparer.Ordinal))
            {
                files.Add((path, ReadBounded(OwnedPath(payloadRoot, path))));
            }

            string output = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(output) || File.Exists(output))
            {
                return Report("HXS003");
            }

            string parent = Path.GetDirectoryName(output)!;
            RequireUnlinkedAncestors(parent);
            _ = Directory.CreateDirectory(parent);
            temporary = Path.Combine(parent, ".check-suite-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            _ = Directory.CreateDirectory(temporary);
            File.WriteAllBytes(Path.Combine(temporary, "descriptor.json"), descriptor);
            foreach ((string path, byte[] bytes) in files)
            {
                string destination = Path.Combine(temporary, "payload", path);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }

            byte[] content = IndexContent(descriptor, files);
            string digest = Digest(content);
            using JsonDocument index = Parse(content);
            byte[] package = JsonSerializer.SerializeToUtf8Bytes(new { schema = "hexalith.check-package.v1", suiteDigest = digest, content = index.RootElement });
            File.WriteAllBytes(Path.Combine(temporary, "package.json"), package);
            Directory.Move(temporary, output);
            temporary = null;
            return new CheckSuiteReport(true, [], digest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            return Report("HXS003");
        }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            {
                Directory.Delete(temporary, true);
            }
        }
    }

    /// <summary>Recomputes descriptor and payload identities and rejects extra files, links and byte tampering.</summary>
    /// <param name="packageDirectory">The retained directory package.</param>
    /// <returns>A report containing the verified digest.</returns>
    public static CheckSuiteReport ValidatePackage(string packageDirectory)
    {
        try
        {
            byte[] descriptor = ReadBounded(OwnedPath(packageDirectory, "descriptor.json"));
            CheckSuiteReport validation = ValidateDocument("suite", descriptor);
            if (!validation.Valid)
            {
                return validation;
            }

            using JsonDocument document = Parse(descriptor);
            List<(string Path, byte[] Bytes)> files = [];
            foreach (string path in Strings(document.RootElement, "payload").Order(StringComparer.Ordinal))
            {
                files.Add((path, ReadBounded(OwnedPath(packageDirectory, "payload/" + path))));
            }

            string[] expectedFiles = ["descriptor.json", "package.json", .. files.Select(file => "payload/" + file.Path)];
            string[] actualFiles = [.. EnumeratePackageFiles(packageDirectory)];
            if (!actualFiles.Order(StringComparer.Ordinal).SequenceEqual(expectedFiles.Order(StringComparer.Ordinal)))
            {
                return Report("HXS003");
            }

            using JsonDocument index = Parse(ReadBounded(OwnedPath(packageDirectory, "package.json")));
            JsonElement root = index.RootElement;
            byte[] content = IndexContent(descriptor, files);
            string digest = Digest(content);
            bool valid = HasProperties(root, "schema", "suiteDigest", "content") && Text(root, "schema") == "hexalith.check-package.v1"
                && Text(root, "suiteDigest") == digest && root.GetProperty("content").GetRawText() == Encoding.UTF8.GetString(content);
            return valid ? new CheckSuiteReport(true, [], digest) : Report("HXS003");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Report("HXS003");
        }
    }

    /// <summary>Accepts evidence only against executor-owned invocation, package, observed release and deadline.</summary>
    /// <param name="packageDirectory">The retained suite package.</param>
    /// <param name="trustedInvocation">The executor's original invocation, never a suite-provided substitute.</param>
    /// <param name="result">The untrusted suite result, or null for missing output.</param>
    /// <param name="authenticatedObservedReleaseDigest">The executor-authenticated served release observation.</param>
    /// <param name="receivedAt">The executor's reception time.</param>
    /// <returns>A passing report only for complete, matching, timely hosted evidence.</returns>
    public static CheckSuiteReport EvaluateResult(string packageDirectory, byte[] trustedInvocation, byte[]? result, string authenticatedObservedReleaseDigest, DateTimeOffset receivedAt)
    {
        CheckSuiteReport package = ValidatePackage(packageDirectory);
        if (!package.Valid || !ValidateDocument("invocation", trustedInvocation).Valid || result is null || !ValidateDocument("result", result).Valid)
        {
            return Report("HXS004");
        }

        using JsonDocument invocationDocument = Parse(trustedInvocation);
        using JsonDocument resultDocument = Parse(result);
        using JsonDocument descriptorDocument = Parse(ReadBounded(Path.Combine(packageDirectory, "descriptor.json")));
        JsonElement invocation = invocationDocument.RootElement;
        JsonElement observed = resultDocument.RootElement;
        JsonElement[] checks = [.. descriptorDocument.RootElement.GetProperty("checks").EnumerateArray().Where(check => Text(check, "id") == Text(invocation, "checkId"))];
        if (checks.Length != 1 || _identityFields.Any(field => !Equivalent(invocation.GetProperty(field), observed.GetProperty(field))))
        {
            return Report("HXS004");
        }

        if (Text(invocation, "suiteDigest") != package.SuiteDigest
            || Text(invocation, "descriptorDigest") != Digest(ReadBounded(Path.Combine(packageDirectory, "descriptor.json"))))
        {
            return Report("HXS004");
        }

        if (Text(invocation, "servedReleaseDigest") != authenticatedObservedReleaseDigest || Text(invocation, "executionMode") != "hosted"
            || Text(observed, "outcome") != "passed" || !observed.GetProperty("complete").GetBoolean()
            || !Strings(checks[0], "environments").Contains(Text(invocation, "environment"), StringComparer.Ordinal)
            || Text(checks[0], "tenancy") != Text(invocation, "syntheticTenantMode")
            || (Text(checks[0], "effect") == "synthetic-write" && Text(invocation, "syntheticTenantMode") != "run-scoped-synthetic"))
        {
            return Report("HXS004");
        }

        bool validTimes = Date(invocation, "deadlineAt", out DateTimeOffset deadline) && Date(invocation, "startedAt", out DateTimeOffset started)
            && Date(observed, "startedAt", out DateTimeOffset actualStarted) && Date(observed, "completedAt", out DateTimeOffset completed)
            && deadline - started <= TimeSpan.FromSeconds(checks[0].GetProperty("timeoutSeconds").GetDouble())
            && actualStarted >= started && completed <= deadline && completed <= receivedAt && receivedAt <= deadline;
        return validTimes ? new CheckSuiteReport(true, [], package.SuiteDigest) : Report("HXS004");
    }

    /// <summary>Creates a stable sanitized report.</summary>
    /// <param name="diagnostics">The diagnostics input.</param>
    /// <returns>The validated operation value.</returns>
    internal static CheckSuiteReport Report(params string[] diagnostics) => new(diagnostics.Length == 0, [.. diagnostics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

    /// <summary>Parses bounded JSON while rejecting duplicate properties.</summary>
    /// <param name="bytes">The bytes input.</param>
    /// <returns>The validated operation value.</returns>
    /// <exception cref="JsonException">The input violates the static contract.</exception>
    internal static JsonDocument Parse(byte[] bytes)
    {
        JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (HasDuplicateProperties(document.RootElement))
        {
            document.Dispose();
            throw new JsonException("Duplicate property.");
        }

        return document;
    }

    /// <summary>Reads a required schema-validated string.</summary>
    /// <param name="element">The element input.</param>
    /// <param name="name">The name input.</param>
    /// <returns>The validated operation value.</returns>
    internal static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;

    /// <summary>Reads a required schema-validated string array.</summary>
    /// <param name="element">The element input.</param>
    /// <param name="name">The name input.</param>
    /// <returns>The validated operation value.</returns>
    internal static string[] Strings(JsonElement element, string name) => [.. element.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];

    /// <summary>Requires the exact closed property set.</summary>
    /// <param name="element">The element input.</param>
    /// <param name="names">The names input.</param>
    /// <returns>The validated operation value.</returns>
    internal static bool HasProperties(JsonElement element, params string[] names) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal));

    /// <summary>Resolves a confined path without traversing links.</summary>
    /// <param name="root">The root input.</param>
    /// <param name="relative">The relative input.</param>
    /// <returns>The validated operation value.</returns>
    /// <exception cref="IOException">The input violates the static contract.</exception>
    internal static string OwnedPath(string root, string relative)
    {
        if (!SafePath(relative))
        {
            throw new IOException("Unsafe package path.");
        }

        string current = Path.GetFullPath(root);
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Linked root.");
        }

        // Inspect every parent, including ancestors of root, before traversing an owned path.
        RequireUnlinkedAncestors(current);

        foreach (string segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked payload.");
            }
        }

        return current;
    }

    /// <summary>Reads a bounded local file.</summary>
    /// <param name="path">The path input.</param>
    /// <returns>The validated operation value.</returns>
    /// <exception cref="IOException">The input violates the static contract.</exception>
    internal static byte[] ReadBounded(string path)
    {
        return new FileInfo(path).Length > _maximumBytes
            ? throw new IOException("Oversize document.")
            : File.ReadAllBytes(path);
    }

    private static bool SafePath(string path) => PathPattern().IsMatch(path) && path.Split('/').All(segment => segment is not ("." or "..") && !segment.EndsWith('.') && !segment.Contains(':', StringComparison.Ordinal));

    private static bool Date(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        string text = Text(root, name);
        return TimestampPattern().IsMatch(text)
            && DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static void RequireUnlinkedAncestors(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Linked ancestor.");
                }
            }
            catch (FileNotFoundException)
            {
                // New destination directories are allowed; their existing ancestry is still inspected.
            }
            catch (DirectoryNotFoundException)
            {
                // Continue until the existing parent chain is reached.
            }
        }
    }

    private static bool HasDuplicateProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || element.EnumerateObject().Any(property => HasDuplicateProperties(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicateProperties),
        JsonValueKind.Undefined or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => false,
        _ => false,
    };

    private static bool Equivalent(JsonElement left, JsonElement right) => left.ValueKind == JsonValueKind.Object && right.ValueKind == JsonValueKind.Object
        ? left.EnumerateObject().Count() == right.EnumerateObject().Count() && left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out JsonElement value) && Equivalent(property.Value, value))
        : left.GetRawText() == right.GetRawText();

    private static IEnumerable<string> EnumeratePackageFiles(string directory, string prefix = "")
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked package entry.");
            }

            string relative = prefix + Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                foreach (string file in EnumeratePackageFiles(entry, relative + "/"))
                {
                    yield return file;
                }
            }
            else
            {
                yield return relative;
            }
        }
    }

    private static byte[] IndexContent(byte[] descriptor, List<(string Path, byte[] Bytes)> files) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        descriptor = new { sha256 = Digest(descriptor), size = descriptor.Length },
        payload = files.Select(file => new { path = file.Path, sha256 = Digest(file.Bytes), size = file.Bytes.Length }).ToArray(),
    });

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}
