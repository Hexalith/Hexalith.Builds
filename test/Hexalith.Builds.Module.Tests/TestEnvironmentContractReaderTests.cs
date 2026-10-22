// <copyright file="TestEnvironmentContractReaderTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.TestEnvironment;

using Json.Schema;

using Shouldly;

using Xunit;

/// <summary>Verifies offline contract consumption, future population and atomic metadata-only refusals.</summary>
public sealed class TestEnvironmentContractReaderTests
{
    /// <summary>Checks the shipped samples against public and embedded schemas and consumes detached endpoints.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishedEmbeddedSchemasAndDetachedSamplesAgree(bool result)
    {
        string identity = result ? TestEnvironmentContractReader.ResultSchema : TestEnvironmentContractReader.DescriptorSchema;
        string text = File.ReadAllText(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "schemas", identity + ".json"));
        using Stream embedded = typeof(TestEnvironmentContractReader).Assembly.GetManifestResourceStream($"Hexalith.Builds.Tooling.TestEnvironment.{identity}.json").ShouldNotBeNull();
        using StreamReader reader = new(embedded);
        reader.ReadToEnd().ShouldBe(text);
        using JsonDocument schemaDocument = JsonDocument.Parse(text);
        schemaDocument.RootElement.GetProperty("$schema").GetString().ShouldBe("https://json-schema.org/draft/2020-12/schema");
        ContainsOnlyLocalReferences(schemaDocument.RootElement).ShouldBeTrue();
        JsonObject sample = Sample(result);
        Schema(result).Evaluate(JsonSerializer.SerializeToElement(sample), new EvaluationOptions { RequireFormatValidation = true }).IsValid.ShouldBeTrue();
        TestEnvironmentContractReadResult read = Read(sample, result);
        read.IsValid.ShouldBeTrue();
        read.Diagnostics.ShouldBeEmpty();
        JsonElement document = read.Document.ShouldNotBeNull();
        document.GetProperty("bindings").GetProperty("run").GetProperty("environmentId").GetString().ShouldBe("sample-environment");
        document.GetProperty("bindings").GetProperty("environment").GetProperty("resources")[0].GetProperty("endpoints")[0].GetProperty("uri").GetString().ShouldBe("http://127.0.0.1:18080");
        document.GetProperty("bindings").GetProperty("candidate").GetProperty("state").GetString().ShouldBe("not-yet-populated");
    }

    /// <summary>Missing, malformed and foreign versions refuse with only this document kind's supported version.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    /// <param name="version">The replacement version, or null to omit it.</param>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "hexalith.test-environment.v2")]
    [InlineData(true, "hexalith.test-result.v2")]
    [InlineData(false, "hexalith.test-result.v1")]
    [InlineData(true, "hexalith.test-environment.v1")]
    [InlineData(false, "private-input-value")]
    public void UnsupportedVersionsNameSupportedVersions(bool result, string? version)
    {
        JsonObject sample = Sample(result);
        if (version is null)
        {
            _ = sample.Remove("schema");
        }
        else
        {
            sample["schema"] = version;
        }

        TestEnvironmentContractReadResult read = Read(sample, result);
        AssertRefused(read, "HXT101");
        read.Diagnostics.Single().Message.ShouldContain(result ? TestEnvironmentContractReader.ResultSchema : TestEnvironmentContractReader.DescriptorSchema);
        read.Diagnostics.Single().Message.ShouldNotContain("private-input-value");
    }

    /// <summary>Both contracts fail closed for absent, unknown or wrongly typed policies.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    /// <param name="policy">The replacement JSON, or the missing sentinel.</param>
    [Theory]
    [InlineData(false, "<missing>")]
    [InlineData(true, "<missing>")]
    [InlineData(false, "null")]
    [InlineData(true, "42")]
    [InlineData(false, "\"other-policy\"")]
    [InlineData(true, "\"other-policy\"")]
    public void PolicyRefusalsAreAtomic(bool result, string policy)
    {
        JsonObject sample = Sample(result);
        Change(sample, "policyId", policy);
        AssertRefused(Read(sample, result), "HXT102");
    }

    /// <summary>Closed typed objects reject missing fields, misspellings and invented reserved values.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    /// <param name="path">The mutated field path.</param>
    /// <param name="json">The replacement JSON, or the missing sentinel.</param>
    [Theory]
    [InlineData(false, "bindings.run.runId", "<missing>")]
    [InlineData(true, "bindings.environment.catalog.commit", "\"main\"")]
    [InlineData(false, "bindings.environment.mode", "\"hosted\"")]
    [InlineData(true, "bindings.environment.resources", "{}")]
    [InlineData(false, "bindings.environment.resources", "[{\"resourceId\":\"api\",\"moduleId\":\"module\",\"endpoints\":[{\"name\":\"http\",\"protocol\":\"http\",\"uri\":\"relative\"}]}]")]
    [InlineData(false, "bindings.tool", "{}")]
    [InlineData(true, "bindings.candidate", "null")]
    [InlineData(false, "bindings.candidate", "{\"state\":\"not-yet-populated\",\"classification\":\"candidate\"}")]
    [InlineData(true, "bindings.artifacts.loaded", "{\"state\":\"populated\",\"items\":[{}]}")]
    [InlineData(false, "bindings.suite.declared", "{\"state\":\"populated\"}")]
    [InlineData(true, "bindings.reuseLineage", "{\"state\":\"populated\",\"producingRunId\":\"run\",\"producingDecisionId\":\"decision\",\"reuseAllowed\":true,\"sourceRunIds\":[],\"sourceDecisionIds\":[]}")]
    [InlineData(false, "bindings.deadlines.startup.originAt", "\"2026-10-22T12:00:00+00:00\"")]
    [InlineData(true, "bindings.deadlines.startup.originAt", "\"2026-02-30T12:00:00Z\"")]
    [InlineData(false, "private-input-key", "\"private-input-value\"")]
    public void InvalidShapesNeverExposePartialDocumentsOrInputMetadata(bool result, string path, string json)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonObject sample = Sample(result);
        Change(sample, path, json);
        Schema(result).Evaluate(JsonSerializer.SerializeToElement(sample), new EvaluationOptions { RequireFormatValidation = true }).IsValid.ShouldBeFalse();
        TestEnvironmentContractReadResult read = Read(sample, result);
        AssertRefused(read, "HXT104");
        JsonSerializer.Serialize(read.Diagnostics).ShouldNotContain("private-input");
    }

    /// <summary>Exactly the four terminal outcomes validate independently of cleanup disposition.</summary>
    /// <param name="outcome">The required terminal outcome.</param>
    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("withdrawn")]
    public void FourOutcomesRemainIndependentOfCleanup(string outcome)
    {
        foreach (string disposition in new[] { "not-requested", "pending", "in-progress", "incomplete", "complete" })
        {
            JsonObject sample = Sample(true);
            sample["terminal"]!["outcome"] = outcome;
            sample["cleanup"] = JsonNode.Parse($$"""
                {"state":"populated","disposition":"{{disposition}}","attemptId":"cleanup-attempt","observedAt":"2026-10-22T12:00:30Z","ownedResourceIds":[],"leftoverResourceIds":[],"failures":[],"diagnosticIds":[]}
                """);
            if (disposition == "not-requested")
            {
                _ = sample["cleanup"]!.AsObject().Remove("attemptId");
            }

            Read(sample, true).IsValid.ShouldBeTrue();
        }
    }

    /// <summary>Legacy or unknown terminal values are never translated into valid outcomes.</summary>
    /// <param name="outcome">The unsupported outcome.</param>
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("retained")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidTerminalOutcomesAreRefused(string? outcome)
    {
        JsonObject sample = Sample(true);
        sample["terminal"]!["outcome"] = outcome;
        AssertRefused(Read(sample, true), "HXT104");
    }

    /// <summary>Omission stays distinct from explicit requests, including the default and both boundaries.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    /// <param name="requested">The requested hold, or null for omission.</param>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 300)]
    [InlineData(true, 1800)]
    [InlineData(false, 14400)]
    [InlineData(true, 14400)]
    public void ValidHoldRequestsPreserveOriginalAcceptanceAndExpiry(bool result, int? requested)
    {
        JsonObject sample = Sample(result);
        _ = AddHold(sample, requested);
        TestEnvironmentContractReadResult read = Read(sample, result);
        read.IsValid.ShouldBeTrue();
        JsonElement hold = read.Document!.Value.GetProperty("bindings").GetProperty("attachments").GetProperty("holds")[0];
        hold.TryGetProperty("requestedHoldSeconds", out _).ShouldBe(requested.HasValue);
        hold.GetProperty("effectiveHoldSeconds").GetInt32().ShouldBe(requested ?? 1800);
        hold.GetProperty("acceptedAt").GetString().ShouldBe("2026-10-22T12:00:00Z");
    }

    /// <summary>Malformed requests fail before defaulting, clamping or returning an accepted hold.</summary>
    /// <param name="request">The invalid JSON request.</param>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("14401")]
    [InlineData("\"\"")]
    [InlineData("\"300\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("1e100")]
    public void InvalidHoldRequestsFailForBothContracts(string request)
    {
        foreach (bool result in new[] { false, true })
        {
            JsonObject sample = Sample(result);
            AddHold(sample, null)["requestedHoldSeconds"] = JsonNode.Parse(request);
            AssertRefused(Read(sample, result), "HXT104");
        }
    }

    /// <summary>Readers also enforce cross-field hold and deadline arithmetic that JSON Schema cannot compare.</summary>
    /// <param name="path">The inconsistent field.</param>
    /// <param name="json">The replacement JSON.</param>
    [Theory]
    [InlineData("bindings.attachments.holds", "[{\"originalAttachmentRequestId\":\"request\",\"environmentId\":\"environment\",\"ownerRunId\":\"owner\",\"attachedRunId\":\"attached\",\"requestedHoldSeconds\":300,\"effectiveHoldSeconds\":1800,\"acceptedAt\":\"2026-10-22T12:00:00Z\",\"expiresAt\":\"2026-10-22T12:30:00Z\",\"end\":{\"state\":\"active\"}}]")]
    [InlineData("bindings.deadlines.startup.expiresAt", "\"2026-10-22T12:11:00Z\"")]
    [InlineData("bindings.deadlines.startup.override", "{\"state\":\"declared\",\"requestedSeconds\":700,\"justification\":\"fixture justification\"}")]
    public void InconsistentTimingIsRefused(string path, string json)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (bool result in new[] { false, true })
        {
            JsonObject sample = Sample(result);
            Change(sample, path, json);
            AssertRefused(Read(sample, result), "HXT107");
        }
    }

    /// <summary>Early actual hold end never changes the originally accepted expiry.</summary>
    [Fact]
    public void ActualHoldEndAndIndependentExecutionDeadlineRemainSeparate()
    {
        JsonObject sample = Sample(true);
        JsonObject hold = AddHold(sample, 300);
        hold["end"] = JsonNode.Parse("{\"state\":\"ended\",\"endedAt\":\"2026-10-22T12:00:30Z\",\"reason\":\"terminal-outcome\"}");
        sample["bindings"]!["deadlines"]!["execution"] = JsonNode.Parse($$"""
            {"state":"populated","origin":"test-execution-started","originAt":"2026-10-22T12:00:10Z","effectiveSeconds":600,"expiresAt":"2026-10-22T12:10:10Z","actualDurationSeconds":20,"configurationContentIdentity":"{{Hash('a')}}"}
            """);
        Read(sample, true).IsValid.ShouldBeTrue();
        hold["expiresAt"] = "2026-10-22T12:00:30Z";
        AssertRefused(Read(sample, true), "HXT107");
    }

    /// <summary>Strict parsing rejects duplicates, comments, trailing commas, invalid UTF-8 and excessive depth.</summary>
    /// <param name="fault">The strict-JSON fault.</param>
    [Theory]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-nested")]
    [InlineData("comment")]
    [InlineData("trailing-comma")]
    [InlineData("malformed")]
    [InlineData("invalid-utf8")]
    [InlineData("deep")]
    [InlineData("non-finite")]
    [InlineData("invalid-unicode-name")]
    [InlineData("invalid-unicode-value")]
    public void InvalidStrictJsonIsAtomicAndMetadataOnly(string fault)
    {
        string text = fault switch
        {
            "duplicate-root" => "{\"private-input-key\":1,\"private-input-key\":2}",
            "duplicate-nested" => "{\"nested\":[{\"private-input-key\":1,\"private-input-key\":2}]}",
            "comment" => "{/* private-input-value */}",
            "trailing-comma" => "{\"private-input-key\":1,}",
            "malformed" => "{private-input-value",
            "deep" => new string('[', 65) + "0" + new string(']', 65),
            "non-finite" => "{\"private-input-key\":1e9999}",
            "invalid-unicode-name" => "{\"\\uD800\":1}",
            "invalid-unicode-value" => "{\"value\":\"\\uD800\"}",
            _ => "{}",
        };
        byte[] bytes = fault == "invalid-utf8" ? [0xFF, 0xFE, 0xFA] : Encoding.UTF8.GetBytes(text);
        foreach (bool result in new[] { false, true })
        {
            TestEnvironmentContractReadResult read = ReadBytes(bytes, result);
            AssertRefused(read, "HXT103");
            JsonSerializer.Serialize(read.Diagnostics).ShouldNotContain("private-input");
        }
    }

    /// <summary>The byte boundary is inclusive; a UTF-8 BOM is allowed and counted.</summary>
    [Fact]
    public void InputByteBoundAndBomAreEnforced()
    {
        byte[] sample = Encoding.UTF8.GetBytes(Sample(false).ToJsonString());
        byte[] exact = new byte[TestEnvironmentContractReader.MaximumDocumentBytes];
        exact.AsSpan().Fill((byte)' ');
        sample.CopyTo(exact, 0);
        ReadBytes(exact, false).IsValid.ShouldBeTrue();
        AssertRefused(ReadBytes(new byte[TestEnvironmentContractReader.MaximumDocumentBytes + 1], false), "HXT105");
        ReadBytes([0xEF, 0xBB, 0xBF, .. sample], false).IsValid.ShouldBeTrue();
    }

    /// <summary>Unreadable paths never disclose sensitive path text in refusal metadata.</summary>
    [Fact]
    public void FileErrorsAreMetadataOnlyAndCancellationIsHonored()
    {
        TestEnvironmentContractReadResult read = TestEnvironmentContractReader.ReadDescriptor(Path.Combine(Path.GetTempPath(), "private-input-value", "missing.json"), TestContext.Current.CancellationToken);
        AssertRefused(read, "HXT106");
        JsonSerializer.Serialize(read.Diagnostics).ShouldNotContain("private-input");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        _ = Should.Throw<OperationCanceledException>(() => TestEnvironmentContractReader.ReadDescriptor("unused", cancellation.Token));
    }

    /// <summary>All structured reserved bindings can be populated later under the same v1 version.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    /// <param name="kind">Candidate PR, release or explicitly diagnostic baseline.</param>
    [Theory]
    [InlineData(false, "pr-merge")]
    [InlineData(true, "pr-merge")]
    [InlineData(false, "release")]
    [InlineData(true, "release")]
    [InlineData(false, "baseline-only")]
    [InlineData(true, "baseline-only")]
    public void FuturePopulatedBindingsRemainV1WithoutGrantingQualification(bool result, string kind)
    {
        JsonObject sample = Sample(result);
        Populate(sample, kind);
        Schema(result).Evaluate(JsonSerializer.SerializeToElement(sample), new EvaluationOptions { RequireFormatValidation = true }).IsValid.ShouldBeTrue();
        Read(sample, result).IsValid.ShouldBeTrue();
        if (kind != "baseline-only")
        {
            sample["bindings"]!["reuseLineage"]!["sourceDecisionIds"]!.AsArray().Add("another-decision");
            AssertRefused(Read(sample, result), "HXT104");
        }
    }

    /// <summary>Deadline budgets use exact ticks rather than binary floating point or rounded decimals.</summary>
    /// <param name="seconds">The exact JSON duration in seconds.</param>
    /// <param name="expiry">The corresponding representable UTC expiry.</param>
    [Theory]
    [InlineData("0.57", "2026-10-22T12:00:00.5700000Z")]
    [InlineData("5.7e-1", "2026-10-22T12:00:00.5700000Z")]
    [InlineData("0.5700000000000000000000000000000", "2026-10-22T12:00:00.5700000Z")]
    [InlineData("0.0000001", "2026-10-22T12:00:00.0000001Z")]
    public void ExactDeadlineBudgetsAreAcceptedForBothContracts(string seconds, string expiry)
    {
        foreach (bool result in new[] { false, true })
        {
            foreach (string kind in new[] { "startup", "execution" })
            {
                JsonObject sample = Sample(result);
                SetDeadline(sample, kind, seconds, expiry);
                Read(sample, result).IsValid.ShouldBeTrue();
            }
        }
    }

    /// <summary>Positive budgets below tick precision or between ticks cannot round into valid deadlines.</summary>
    /// <param name="seconds">The unrepresentable JSON budget.</param>
    /// <param name="expiry">An expiry that a lossy conversion could incorrectly accept.</param>
    [Theory]
    [InlineData("1e-40", "2026-10-22T12:00:00Z")]
    [InlineData("0.00000001", "2026-10-22T12:00:00Z")]
    [InlineData("0.00000011", "2026-10-22T12:00:00.0000001Z")]
    [InlineData("0.5700000000000000000000000000001", "2026-10-22T12:00:00.5700000Z")]
    public void UnrepresentableDeadlineBudgetsAreRefusedForBothContracts(string seconds, string expiry)
    {
        foreach (bool result in new[] { false, true })
        {
            foreach (string kind in new[] { "startup", "execution" })
            {
                JsonObject sample = Sample(result);
                SetDeadline(sample, kind, seconds, expiry);
                AssertRefused(Read(sample, result), "HXT107");
            }
        }
    }

    /// <summary>Distinct tiny override values cannot become equal through decimal underflow.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MismatchedTinyStartupOverridesAreRefused(bool result)
    {
        JsonObject sample = Sample(result);
        SetDeadline(sample, "startup", "1e-40", "2026-10-22T12:00:00Z");
        sample["bindings"]!["deadlines"]!["startup"]!["override"]!["requestedSeconds"] = JsonNode.Parse("2e-40");
        AssertRefused(Read(sample, result), "HXT107");
    }

    /// <summary>An execution-only expiry mismatch refuses without any startup or hold inconsistency.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutionDeadlineExpiryMismatchIsAtomic(bool result)
    {
        JsonObject sample = Sample(result);
        SetDeadline(sample, "execution", "600", "2026-10-22T12:10:01Z");
        AssertRefused(Read(sample, result), "HXT107");
    }

    /// <summary>Execution without a declared deadline can still record its origin and actual duration.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndeclaredExecutionDeadlinePreservesTimingObservations(bool result)
    {
        JsonObject sample = Sample(result);
        Read(sample, result).IsValid.ShouldBeTrue();
        JsonObject execution = new()
        {
            ["state"] = "not-declared",
            ["origin"] = "test-execution-started",
            ["originAt"] = "2026-10-22T12:00:10Z",
            ["actualDurationSeconds"] = 0.57m,
        };
        sample["bindings"]!["deadlines"]!["execution"] = execution;
        Schema(result).Evaluate(JsonSerializer.SerializeToElement(sample), new EvaluationOptions { RequireFormatValidation = true }).IsValid.ShouldBeTrue();
        Read(sample, result).IsValid.ShouldBeTrue();
        execution.ContainsKey("expiresAt").ShouldBeFalse();
        execution["expiresAt"] = "2026-10-22T12:10:10Z";
        AssertRefused(Read(sample, result), "HXT104");
    }

    /// <summary>Identifiers exclude control characters at either boundary as well as internally.</summary>
    /// <param name="identity">The invalid identity.</param>
    [Theory]
    [InlineData("\u0000sample")]
    [InlineData("sample\u0000")]
    [InlineData("\u007fsample")]
    [InlineData("sample\u007f")]
    [InlineData("\u0080sample")]
    [InlineData("sample\u009f")]
    [InlineData("sam\u001fple")]
    public void BoundaryControlCharactersAreRefusedForBothContracts(string identity)
    {
        foreach (bool result in new[] { false, true })
        {
            JsonObject sample = Sample(result);
            sample["bindings"]!["run"]!["runId"] = identity;
            Schema(result).Evaluate(JsonSerializer.SerializeToElement(sample), new EvaluationOptions { RequireFormatValidation = true }).IsValid.ShouldBeFalse();
            AssertRefused(Read(sample, result), "HXT104");
        }
    }

    /// <summary>UTC seconds stay within the timestamp precision supported by DateTimeOffset.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeapSecondsAreRefusedBySchemaAndReader(bool result)
    {
        JsonObject sample = Sample(result);
        sample["bindings"]!["deadlines"]!["startup"]!["originAt"] = "2016-12-31T23:59:60Z";
        Schema(result).Evaluate(JsonSerializer.SerializeToElement(sample), new EvaluationOptions { RequireFormatValidation = true }).IsValid.ShouldBeFalse();
        AssertRefused(Read(sample, result), "HXT104");
    }

    /// <summary>Cleanup needs an attempt identity only once an attempt is represented.</summary>
    /// <param name="disposition">The cleanup state.</param>
    /// <param name="validWithoutAttempt">Whether an absent attempt identity is valid.</param>
    [Theory]
    [InlineData("not-requested", true)]
    [InlineData("pending", false)]
    [InlineData("in-progress", false)]
    [InlineData("incomplete", false)]
    [InlineData("complete", false)]
    public void CleanupAttemptIdentityTracksActualCleanup(string disposition, bool validWithoutAttempt)
    {
        JsonObject sample = Sample(true);
        sample["cleanup"] = JsonNode.Parse($$"""
            {"state":"populated","disposition":"{{disposition}}","observedAt":"2026-10-22T12:00:30Z","ownedResourceIds":[],"leftoverResourceIds":[],"failures":[],"diagnosticIds":[]}
            """);
        TestEnvironmentContractReadResult read = Read(sample, true);
        if (validWithoutAttempt)
        {
            read.IsValid.ShouldBeTrue();
        }
        else
        {
            AssertRefused(read, "HXT104");
        }

        sample["cleanup"]!["attemptId"] = "cleanup-attempt";
        Read(sample, true).IsValid.ShouldBeTrue();
    }

    /// <summary>A hold cannot expire early or end before acceptance, while an immediate own end is valid.</summary>
    /// <param name="reason">The recorded hold-end reason.</param>
    /// <param name="endedAt">The actual end time.</param>
    /// <param name="valid">Whether the end respects the immutable hold.</param>
    [Theory]
    [InlineData("expired", "2026-10-22T12:04:59Z", false)]
    [InlineData("expired", "2026-10-22T12:05:00Z", true)]
    [InlineData("terminal-outcome", "2026-10-22T11:59:59Z", false)]
    [InlineData("terminal-outcome", "2026-10-22T12:00:00Z", true)]
    public void HoldEndRespectsAcceptanceAndExpiryForBothContracts(string reason, string endedAt, bool valid)
    {
        foreach (bool result in new[] { false, true })
        {
            JsonObject sample = Sample(result);
            JsonObject hold = AddHold(sample, 300);
            hold["end"] = new JsonObject { ["state"] = "ended", ["reason"] = reason, ["endedAt"] = endedAt };
            TestEnvironmentContractReadResult read = Read(sample, result);
            if (valid)
            {
                read.IsValid.ShouldBeTrue();
                read.Document!.Value.GetProperty("bindings").GetProperty("attachments").GetProperty("holds")[0].GetProperty("expiresAt").GetString().ShouldBe("2026-10-22T12:05:00Z");
            }
            else
            {
                AssertRefused(read, "HXT107");
            }
        }
    }

    /// <summary>Endpoint selection refuses duplicate resource IDs and duplicate names within one resource.</summary>
    /// <param name="scope">The ambiguous identity scope.</param>
    [Theory]
    [InlineData("resource")]
    [InlineData("endpoint")]
    public void AmbiguousEndpointIdentitiesAreAtomicForBothContracts(string scope)
    {
        foreach (bool result in new[] { false, true })
        {
            JsonObject sample = Sample(result);
            JsonArray resources = sample["bindings"]!["environment"]!["resources"]!.AsArray();
            if (scope == "resource")
            {
                resources.Add(resources[0]!.DeepClone());
            }
            else
            {
                JsonArray endpoints = resources[0]!["endpoints"]!.AsArray();
                JsonNode duplicate = endpoints[0]!.DeepClone();
                duplicate["uri"] = "http://127.0.0.1:18081";
                endpoints.Add(duplicate);
            }

            AssertRefused(Read(sample, result), "HXT104");
        }
    }

    /// <summary>The same endpoint name can be used by distinct resource identities.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EndpointNamesAreScopedToTheirResource(bool result)
    {
        JsonObject sample = Sample(result);
        JsonArray resources = sample["bindings"]!["environment"]!["resources"]!.AsArray();
        JsonNode second = resources[0]!.DeepClone();
        second["resourceId"] = "second-resource";
        resources.Add(second);
        Read(sample, result).IsValid.ShouldBeTrue();
    }

    /// <summary>Every diagnostic reference resolves to a unique classified document-local diagnostic.</summary>
    /// <param name="result">Whether to read a terminal result.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticIdsResolveWithoutFetchingOpaqueContents(bool result)
    {
        JsonObject sample = Sample(result);
        AddDiagnostic(sample, result);
        Read(sample, result).IsValid.ShouldBeTrue();
    }

    /// <summary>Dangling references and duplicate top-level IDs cannot produce ambiguous diagnostics.</summary>
    /// <param name="fault">The unresolved or ambiguous diagnostic binding.</param>
    [Theory]
    [InlineData("dangling")]
    [InlineData("duplicate")]
    public void InvalidDiagnosticBindingsAreAtomicForBothContracts(string fault)
    {
        foreach (bool result in new[] { false, true })
        {
            JsonObject sample = Sample(result);
            AddDiagnostic(sample, result);
            if (fault == "dangling")
            {
                sample["bindings"]!["environment"]!["readiness"]!["resources"]![0]!["diagnosticIds"] = new JsonArray("missing-diagnostic");
            }
            else
            {
                JsonArray diagnostics = sample["diagnostics"]!.AsArray();
                diagnostics.Add(diagnostics[0]!.DeepClone());
            }

            AssertRefused(Read(sample, result), "HXT104");
        }
    }

    private static void SetDeadline(JsonObject sample, string kind, string seconds, string expiry)
    {
        string origin = kind == "startup" ? "environment-requested" : "test-execution-started";
        JsonObject deadline = JsonNode.Parse($$"""
            {"state":"populated","origin":"{{origin}}","originAt":"2026-10-22T12:00:00Z","effectiveSeconds":{{seconds}},"expiresAt":"{{expiry}}"}
            """)!.AsObject();
        if (kind == "startup")
        {
            deadline["override"] = JsonNode.Parse($$"""
                {"state":"declared","requestedSeconds":{{seconds}},"justification":"Contract precision fixture"}
                """);
        }
        else
        {
            deadline["configurationContentIdentity"] = Hash('a');
        }

        sample["bindings"]!["deadlines"]![kind] = deadline;
    }

    private static void AddDiagnostic(JsonObject sample, bool result)
    {
        sample["diagnostics"] = JsonNode.Parse($$"""
            [{"id":"classified-diagnostic","classification":"internal","redacted":true,"reference":"https://127.0.0.1:1/opaque-never-fetched","contentIdentity":"{{Hash('a')}}"}]
            """);
        sample["bindings"]!["environment"]!["readiness"]!["resources"]![0]!["diagnosticIds"] = new JsonArray("classified-diagnostic");
        if (result)
        {
            sample["terminal"]!["diagnosticIds"] = new JsonArray("classified-diagnostic");
        }
    }

    private static void Populate(JsonObject sample, string kind)
    {
        JsonNode bindings = sample["bindings"]!;
        string qualification = $$"""
            {"state":"populated","referenceId":"fictional-contract-test-reference","toolVersion":"0.0.0-fixture","toolContentIdentity":"{{Hash('a')}}","policyId":"{{TestEnvironmentContractReader.PolicyId}}"}
            """;
        bindings["tool"] = JsonNode.Parse($$"""
            {"state":"populated","name":"hexalith-module","version":"0.0.0-fixture","contentIdentity":"{{Hash('a')}}","buildsAdoption":{{qualification}},"platformAcceptance":{{qualification}},"integrationQualification":{{qualification}}}
            """);
        bindings["environment"]!["mode"] = "package";
        bindings["environment"]!["executionContext"] = JsonNode.Parse("{\"kind\":\"ci\",\"repository\":\"fixture/repository\",\"workflowRunId\":\"fixture-workflow\",\"workflowAttempt\":1,\"jobId\":\"fixture-job\",\"runnerKind\":\"github-hosted\",\"operatingSystem\":\"linux\",\"disposable\":true}");
        string pr = "{\"id\":\"fixture-pr\",\"headCommit\":\"" + new string('a', 40) + "\",\"baseCommit\":\"" + new string('b', 40) + "\",\"mergeResultCommit\":\"" + new string('c', 40) + "\"}";
        if (kind == "baseline-only")
        {
            bindings["candidate"] = JsonNode.Parse("{\"state\":\"populated\",\"classification\":\"baseline-only\",\"reason\":\"Contract fixture only; no runtime qualification\"}");
        }
        else
        {
            bindings["candidate"] = JsonNode.Parse($$"""
                {"state":"populated","classification":"candidate","repository":"fixture/repository","moduleId":"fixture-module","committedRevision":"{{new string('c', 40)}}"}
                """);
            bindings["decision"] = JsonNode.Parse($$"""
                {"state":"populated","requestId":"fixture-decision","issuer":{"id":"fixture-gate","authenticationReference":"fictional-reference"},"repository":"fixture/repository","moduleId":"fixture-module","policyId":"{{TestEnvironmentContractReader.PolicyId}}","kind":"{{kind}}","targetCommit":"{{new string('c', 40)}}","requestState":"pending","producingRunId":"sample-owner-run","producingAttemptId":"sample-attempt-1","attemptSequence":1}
                """);
            if (kind == "pr-merge")
            {
                bindings["candidate"]!["pr"] = JsonNode.Parse(pr);
                bindings["decision"]!["pr"] = JsonNode.Parse(pr);
            }

            bindings["reuseLineage"] = JsonNode.Parse("{\"state\":\"populated\",\"producingRunId\":\"sample-owner-run\",\"producingDecisionId\":\"fixture-decision\",\"reuseAllowed\":false,\"sourceRunIds\":[],\"sourceDecisionIds\":[]}");
        }

        bindings["artifacts"]!["expected"] = JsonNode.Parse($$"""
            {"state":"populated","items":[{"artifactId":"fixture-package","moduleId":"fixture-module","kind":"extension","version":"0.0.0-fixture","repository":"fixture/repository","committedRevision":"{{new string('c', 40)}}","configuration":"Release","contentIdentity":"{{Hash('b')}}"}]}
            """);
        bindings["artifacts"]!["loaded"] = JsonNode.Parse($$"""
            {"state":"populated","items":[{"artifactId":"fixture-package","moduleId":"fixture-module","consumerKind":"composed-host","consumerId":"fixture-host","contentIdentity":"{{Hash('b')}}"}]}
            """);
        bindings["suite"]!["declared"] = JsonNode.Parse($$"""
            {"state":"populated","suiteId":"fixture-suite","suiteContentIdentity":"{{Hash('a')}}","configurationContentIdentity":"{{Hash('b')}}","requiredTestSetContentIdentity":"{{Hash('c')}}","requiredTestIds":["fixture-test"]}
            """);
        bindings["suite"]!["completion"] = JsonNode.Parse($$"""
            {"state":"populated","executedSuiteContentIdentity":"{{Hash('a')}}","executedConfigurationContentIdentity":"{{Hash('b')}}","requiredTestSetContentIdentity":"{{Hash('c')}}","counts":{"required":1,"executed":1,"completed":1,"passed":1,"failed":0,"skipped":0},"tests":[{"testId":"fixture-test","outcome":"passed"}]}
            """);
        sample["diagnostics"] = JsonNode.Parse($$"""
            [{"id":"fixture-diagnostic","classification":"internal","redacted":true,"reference":"fixture/diagnostic.txt","contentIdentity":"{{Hash('a')}}"}]
            """);
    }

    private static string Hash(char value) => "sha256:" + new string(value, 64);

    private static JsonObject AddHold(JsonObject sample, int? requested)
    {
        int seconds = requested ?? 1800;
        JsonObject hold = new()
        {
            ["originalAttachmentRequestId"] = "sample-original-request",
            ["environmentId"] = "sample-environment",
            ["ownerRunId"] = "sample-owner-run",
            ["attachedRunId"] = "sample-attached-run",
            ["acceptedAt"] = "2026-10-22T12:00:00Z",
            ["effectiveHoldSeconds"] = seconds,
            ["expiresAt"] = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["end"] = new JsonObject { ["state"] = "active" },
        };
        if (requested.HasValue)
        {
            hold["requestedHoldSeconds"] = requested.Value;
        }

        sample["bindings"]!["attachments"] = new JsonObject { ["state"] = "populated", ["holds"] = new JsonArray(hold) };
        return hold;
    }

    private static void Change(JsonObject sample, string path, string json)
    {
        string[] names = path.Split('.');
        JsonNode node = sample;
        foreach (string name in names[..^1])
        {
            node = node[name]!;
        }

        if (json == "<missing>")
        {
            _ = node.AsObject().Remove(names[^1]);
        }
        else
        {
            node[names[^1]] = JsonNode.Parse(json);
        }
    }

    private static JsonObject Sample(bool result) => JsonNode.Parse(File.ReadAllText(Path.Combine(
        PlatformManifestTestWorkspace.RepositoryRoot, "test/fixtures/module/test-environment-v1", result ? "result.json" : "descriptor.json")))!.AsObject();

    private static JsonSchema Schema(bool result)
    {
        string identity = result ? TestEnvironmentContractReader.ResultSchema : TestEnvironmentContractReader.DescriptorSchema;
        return JsonSchema.FromFile(
            Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "schemas", identity + ".json"),
            new BuildOptions { SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => throw new InvalidOperationException("Contract tests cannot fetch remote schemas.") } });
    }

    private static bool ContainsOnlyLocalReferences(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(property => property.Name == "$ref"
            ? property.Value.GetString()!.StartsWith("#/$defs/", StringComparison.Ordinal)
            : ContainsOnlyLocalReferences(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(ContainsOnlyLocalReferences),
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };

    private static TestEnvironmentContractReadResult Read(JsonNode document, bool result) => ReadBytes(Encoding.UTF8.GetBytes(document.ToJsonString()), result);

    private static TestEnvironmentContractReadResult ReadBytes(byte[] bytes, bool result)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            return result
                ? TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken)
                : TestEnvironmentContractReader.ReadDescriptor(path, TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertRefused(TestEnvironmentContractReadResult read, string rule)
    {
        read.IsValid.ShouldBeFalse();
        read.Document.ShouldBeNull();
        read.Diagnostics.ShouldHaveSingleItem().RuleId.ShouldBe(rule);
    }
}