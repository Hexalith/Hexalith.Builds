// <copyright file="TestEnvironmentContractReader.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestEnvironment;

using System.Globalization;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

using Json.Schema;

/// <summary>Reads strict, bounded test contracts offline without running resources or accepting evidence.</summary>
public static class TestEnvironmentContractReader
{
    /// <summary>The only supported descriptor version.</summary>
    public const string DescriptorSchema = "hexalith.test-environment.v1";

    /// <summary>The only supported terminal-result version.</summary>
    public const string ResultSchema = "hexalith.test-result.v1";

    /// <summary>The exact immutable Platform policy required by both contracts.</summary>
    public const string PolicyId = "hexalith.platform.candidate-and-attachment-policy.v1";

    /// <summary>The maximum UTF-8 input size, including an optional BOM.</summary>
    public const int MaximumDocumentBytes = 1_048_576;

    /// <summary>The maximum JSON nesting depth.</summary>
    public const int MaximumDepth = 64;

    private static readonly JsonSchema _descriptor = LoadSchema(DescriptorSchema);
    private static readonly JsonSchema _result = LoadSchema(ResultSchema);

    /// <summary>Reads a descriptor and returns endpoints and identities only after complete validation.</summary>
    /// <param name="path">The local UTF-8 file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A complete detached document, or an atomic refusal.</returns>
    public static TestEnvironmentContractReadResult ReadDescriptor(string path, CancellationToken cancellationToken = default) =>
        Read(path, DescriptorSchema, _descriptor, cancellationToken);

    /// <summary>Reads a result without translating legacy statuses or inferring evidence eligibility.</summary>
    /// <param name="path">The local UTF-8 file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A complete detached document, or an atomic refusal.</returns>
    public static TestEnvironmentContractReadResult ReadResult(string path, CancellationToken cancellationToken = default) =>
        Read(path, ResultSchema, _result, cancellationToken);

    private static JsonSchema LoadSchema(string schema)
    {
        using Stream stream = typeof(TestEnvironmentContractReader).Assembly.GetManifestResourceStream(
            $"Hexalith.Builds.Tooling.TestEnvironment.{schema}.json")
            ?? throw new InvalidOperationException("A packaged test contract schema is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry
            {
                Fetch = (_, _) => throw new InvalidOperationException("Test contracts cannot fetch remote schemas."),
            },
        });
    }

    private static TestEnvironmentContractReadResult Read(string path, string version, JsonSchema schema, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            using FileStream stream = File.OpenRead(path);
            if (!stream.CanSeek || stream.Length > MaximumDocumentBytes)
            {
                return Refuse("HXT105", version, "$", "Supply a seekable UTF-8 JSON file at or below 1 MiB.");
            }

            byte[] buffer = new byte[MaximumDocumentBytes + 1];
            int count = 0;
            int read;
            while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
            {
                count += read;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (count > MaximumDocumentBytes)
            {
                return Refuse("HXT105", version, "$", "Keep the UTF-8 JSON file at or below 1 MiB.");
            }

            int offset = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            string content = new UTF8Encoding(false, true).GetString(buffer, offset, count - offset);
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = MaximumDepth });
            JsonElement root = document.RootElement;
            if (!HasValidValues(root) || JsonDuplicatePropertyValidator.FindDuplicateProperty(root) is not null)
            {
                return Refuse("HXT103", version, "$", "Use strict JSON with valid Unicode, unique property names and finite numbers.");
            }

            if (StringValue(root, "schema") != version)
            {
                return Refuse("HXT101", version, "schema", $"Unsupported or missing contract version. Supported versions: {version}.");
            }

            if (StringValue(root, "policyId") != PolicyId)
            {
                return Refuse("HXT102", version, "policyId", $"Use the exact supported policy: {PolicyId}.");
            }

            if (!HasPlainIntegers(root)
                || !schema.Evaluate(root, new EvaluationOptions { RequireFormatValidation = true }).IsValid
                || !HasUnambiguousBindings(root)
                || !HasConsistentRunBindings(root.GetProperty("bindings")))
            {
                return Refuse("HXT104", version, "$", "The document violates the contract shape, integer encoding or document-local identity/reference bindings.");
            }

            if (!HasConsistentTiming(root.GetProperty("bindings")) || !HasConsistentAttachedOutcome(root))
            {
                return Refuse("HXT107", version, "$", "Preserve hold requests, immutable acceptance/expiry bindings and attached outcomes, and independent effective deadline origins and justified overrides.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new TestEnvironmentContractReadResult(root.Clone(), []);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or OverflowException)
        {
            return Refuse("HXT103", version, "$", "Supply strict UTF-8 JSON, without comments or trailing commas, within 64 nesting levels.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return Refuse("HXT106", version, "$", "Supply an existing readable local contract file.");
        }
    }

    private static bool HasValidValues(JsonElement value)
    {
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Object => value.EnumerateObject().All(property => property.Name is not null && HasValidValues(property.Value)),
                JsonValueKind.Array => value.EnumerateArray().All(HasValidValues),
                JsonValueKind.Number => value.TryGetDouble(out double number) && double.IsFinite(number),
                JsonValueKind.String => value.GetString() is not null,
                JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
                _ => false,
            };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasConsistentTiming(JsonElement bindings)
    {
        JsonElement attachments = bindings.GetProperty("attachments");
        if (StringValue(attachments, "state") == "populated")
        {
            foreach (JsonElement hold in attachments.GetProperty("holds").EnumerateArray())
            {
                int effective = hold.GetProperty("effectiveHoldSeconds").GetInt32();
                int requested = hold.TryGetProperty("requestedHoldSeconds", out JsonElement request)
                    ? request.GetInt32()
                    : 1800;
                DateTimeOffset accepted = Timestamp(hold, "acceptedAt");
                DateTimeOffset expiry = Timestamp(hold, "expiresAt");
                if (effective != requested || expiry.Ticks - accepted.Ticks != effective * TimeSpan.TicksPerSecond)
                {
                    return false;
                }

                JsonElement end = hold.GetProperty("end");
                if (StringValue(end, "state") == "ended")
                {
                    DateTimeOffset ended = Timestamp(end, "endedAt");
                    bool expired = StringValue(end, "reason") == "expired";
                    if (ended < accepted || (expired ? ended < expiry : ended >= expiry))
                    {
                        return false;
                    }
                }
            }
        }

        JsonElement deadlines = bindings.GetProperty("deadlines");
        foreach (string kind in new[] { "startup", "execution" })
        {
            JsonElement deadline = deadlines.GetProperty(kind);
            if (StringValue(deadline, "state") != "populated")
            {
                continue;
            }

            if (!TryDeadlineTicks(deadline.GetProperty("effectiveSeconds"), out long ticks)
                || Timestamp(deadline, "expiresAt").Ticks - Timestamp(deadline, "originAt").Ticks != ticks)
            {
                return false;
            }

            if (kind == "startup")
            {
                JsonElement declaration = deadline.GetProperty("override");
                long requestedTicks = 600 * TimeSpan.TicksPerSecond;
                if (StringValue(declaration, "state") == "declared" && !TryDeadlineTicks(declaration.GetProperty("requestedSeconds"), out requestedTicks))
                {
                    return false;
                }

                if (ticks != requestedTicks)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasConsistentAttachedOutcome(JsonElement root)
    {
        JsonElement bindings = root.GetProperty("bindings");
        JsonElement run = bindings.GetProperty("run");
        JsonElement attachments = bindings.GetProperty("attachments");
        if (!root.TryGetProperty("terminal", out JsonElement terminal)
            || StringValue(run, "role") != "attached"
            || StringValue(attachments, "state") != "populated")
        {
            return true;
        }

        string? runId = StringValue(run, "runId");
        JsonElement hold = attachments.GetProperty("holds").EnumerateArray().First(item => StringValue(item, "attachedRunId") == runId);
        string? cause = StringValue(terminal, "cause");

        // Expiry is exclusive and the runner's record decides it: only hold expiry is recorded at or after expiresAt.
        if ((Timestamp(terminal, "recordedAt") >= Timestamp(hold, "expiresAt")) != (cause == "hold-expired"))
        {
            return false;
        }

        JsonElement end = hold.GetProperty("end");
        if (StringValue(end, "state") != "ended")
        {
            return true;
        }

        string? reason = StringValue(end, "reason");
        string? withdrawalCause = reason == "expired" ? "hold-expired" : reason;
        return reason == "terminal-outcome"
            ? StringValue(terminal, "outcome") != "withdrawn"
            : cause == withdrawalCause;
    }

    private static bool HasPlainIntegers(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(property =>
            (!(property.Name is "attemptSequence" or "workflowAttempt" or "requestedHoldSeconds" or "effectiveHoldSeconds"
                or "required" or "executed" or "completed" or "passed" or "failed" or "skipped")
                || property.Value.ValueKind != JsonValueKind.Number
                || property.Value.TryGetInt32(out _))
            && HasPlainIntegers(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(HasPlainIntegers),
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };

    private static bool HasConsistentRunBindings(JsonElement bindings)
    {
        JsonElement run = bindings.GetProperty("run");
        string runId = run.GetProperty("runId").GetString()!;
        string ownerId = run.GetProperty("ownerRunId").GetString()!;
        bool attached = StringValue(run, "role") == "attached";
        if (attached ? runId != StringValue(run, "attachedRunId") || runId == ownerId : runId != ownerId)
        {
            return false;
        }

        JsonElement attachments = bindings.GetProperty("attachments");
        if (StringValue(attachments, "state") != "populated")
        {
            return true;
        }

        bool includesCurrentRun = false;
        foreach (JsonElement hold in attachments.GetProperty("holds").EnumerateArray())
        {
            string? attachedId = StringValue(hold, "attachedRunId");
            if (StringValue(hold, "environmentId") != StringValue(run, "environmentId")
                || StringValue(hold, "ownerRunId") != ownerId || attachedId == ownerId)
            {
                return false;
            }

            includesCurrentRun |= attachedId == runId;
        }

        return !attached || includesCurrentRun;
    }

    private static bool TryDeadlineTicks(JsonElement value, out long ticks)
    {
        ticks = 0;
        string raw = value.GetRawText();
        int exponentIndex = raw.IndexOfAny(['e', 'E']);
        int exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            return false;
        }

        string significand = exponentIndex >= 0 ? raw[..exponentIndex] : raw;
        int pointIndex = significand.IndexOf('.', StringComparison.Ordinal);
        int fractionDigits = pointIndex >= 0 ? significand.Length - pointIndex - 1 : 0;
        string digits = significand.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0 || digits[0] == '-')
        {
            return false;
        }

        // Scale the exact JSON decimal to ticks without rounding or binary floating point.
        long scale = (long)exponent - fractionDigits + 7;
        if (scale < 0)
        {
            long retainedLength = digits.Length + scale;
            if (retainedLength <= 0 || digits.AsSpan((int)retainedLength).ContainsAnyExcept('0'))
            {
                return false;
            }

            digits = digits[..(int)retainedLength];
        }
        else if (scale > 0)
        {
            if (digits.Length + scale > 19)
            {
                return false;
            }

            digits += new string('0', (int)scale);
        }

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks > 0;
    }

    private static bool HasUnambiguousBindings(JsonElement root)
    {
        JsonElement bindings = root.GetProperty("bindings");
        JsonElement environment = bindings.GetProperty("environment");
        HashSet<string> resources = new(StringComparer.Ordinal);
        foreach (JsonElement resource in environment.GetProperty("resources").EnumerateArray())
        {
            if (!resources.Add(resource.GetProperty("resourceId").GetString()!))
            {
                return false;
            }

            HashSet<string> endpoints = new(StringComparer.Ordinal);
            if (resource.GetProperty("endpoints").EnumerateArray().Any(endpoint => !endpoints.Add(endpoint.GetProperty("name").GetString()!)
                || !Uri.TryCreate(endpoint.GetProperty("uri").GetString(), UriKind.Absolute, out _)))
            {
                return false;
            }
        }

        JsonElement readiness = environment.GetProperty("readiness");
        if (StringValue(readiness, "state") == "populated"
            && (!HasUniqueIds(readiness.GetProperty("resources"), "resourceId")
                || readiness.GetProperty("resources").EnumerateArray().Any(item => !resources.Contains(StringValue(item, "resourceId")!))))
        {
            return false;
        }

        JsonElement tasks = environment.GetProperty("startupTasks");
        JsonElement attachments = bindings.GetProperty("attachments");
        JsonElement completion = bindings.GetProperty("suite").GetProperty("completion");
        if ((StringValue(tasks, "state") == "populated" && !HasUniqueIds(tasks.GetProperty("tasks"), "taskId"))
            || (StringValue(attachments, "state") == "populated"
                && (!HasUniqueIds(attachments.GetProperty("holds"), "originalAttachmentRequestId")
                    || !HasUniqueIds(attachments.GetProperty("holds"), "attachedRunId")))
            || (StringValue(completion, "state") == "populated" && !HasUniqueIds(completion.GetProperty("tests"), "testId")))
        {
            return false;
        }

        if (root.TryGetProperty("termination", out JsonElement termination) && StringValue(termination, "state") == "populated")
        {
            HashSet<string> items = new(StringComparer.Ordinal);
            if (termination.GetProperty("ownedWork").EnumerateArray().Concat(termination.GetProperty("environmentAccess").EnumerateArray())
                .Any(item => !items.Add(StringValue(item, "id")!))
                || termination.GetProperty("failures").EnumerateArray().Any(id => !items.Contains(id.GetString()!)))
            {
                return false;
            }
        }

        if (root.TryGetProperty("cleanup", out JsonElement cleanup) && StringValue(cleanup, "state") == "populated")
        {
            HashSet<string> owned = new(cleanup.GetProperty("ownedResourceIds").EnumerateArray().Select(id => id.GetString()!), StringComparer.Ordinal);
            if (cleanup.GetProperty("failures").EnumerateArray().Any(id => !owned.Contains(id.GetString()!)))
            {
                return false;
            }
        }

        HashSet<string> diagnostics = new(StringComparer.Ordinal);
        return root.GetProperty("diagnostics").EnumerateArray().All(diagnostic => diagnostics.Add(diagnostic.GetProperty("id").GetString()!))
            && HasResolvedDiagnosticIds(root, diagnostics);
    }

    private static bool HasUniqueIds(JsonElement items, string property)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        return items.EnumerateArray().All(item => identities.Add(StringValue(item, property)!));
    }

    private static bool HasResolvedDiagnosticIds(JsonElement value, HashSet<string> diagnostics) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(property => property.Name == "diagnosticIds"
            ? property.Value.EnumerateArray().All(id => diagnostics.Contains(id.GetString()!))
            : HasResolvedDiagnosticIds(property.Value, diagnostics)),
        JsonValueKind.Array => value.EnumerateArray().All(item => HasResolvedDiagnosticIds(item, diagnostics)),
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };

    private static DateTimeOffset Timestamp(JsonElement value, string property) =>
        DateTimeOffset.Parse(value.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

    private static string? StringValue(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static TestEnvironmentContractReadResult Refuse(string rule, string version, string field, string message) =>
        new(null, Array.AsReadOnly<ToolDiagnostic>([new ToolDiagnostic(rule, ToolPhase.Manifest, ToolFailureCategory.Manifest, message, field, Source: version)]));
}