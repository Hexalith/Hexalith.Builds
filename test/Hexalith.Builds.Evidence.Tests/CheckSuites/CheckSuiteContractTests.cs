// <copyright file="CheckSuiteContractTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Tests.CheckSuites;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Evidence.Cli;
using Hexalith.Builds.Tooling.CheckSuites;

using Shouldly;

using Xunit;

/// <summary>Exercises immutable bytes, closed contracts, trusted evidence and static command refusals.</summary>
public sealed class CheckSuiteContractTests
{
    /// <summary>Proves deterministic output and payload/descriptor tamper detection without executing code.</summary>
    [Fact]
    public void PackagesAreReproducibleAndTamperingIsRefused()
    {
        string root = Temporary();
        try
        {
            string fixture = Fixture();
            CheckSuiteReport first = CheckSuiteContract.Package(Path.Combine(fixture, "suite.json"), fixture, Path.Combine(root, "one"));
            CheckSuiteReport second = CheckSuiteContract.Package(Path.Combine(fixture, "suite.json"), fixture, Path.Combine(root, "two"));
            first.Valid.ShouldBeTrue();
            first.SuiteDigest.ShouldBe(second.SuiteDigest);
            File.ReadAllBytes(Path.Combine(root, "one", "package.json")).ShouldBe(File.ReadAllBytes(Path.Combine(root, "two", "package.json")));
            CheckSuiteContract.ValidatePackage(Path.Combine(root, "one")).Valid.ShouldBeTrue();
            File.AppendAllText(Path.Combine(root, "one", "payload", "runner.py"), "\n# tampered");
            CheckSuiteContract.ValidatePackage(Path.Combine(root, "one")).Valid.ShouldBeFalse();
            File.AppendAllText(Path.Combine(root, "two", "descriptor.json"), " ");
            CheckSuiteContract.ValidatePackage(Path.Combine(root, "two")).Valid.ShouldBeFalse();
            CheckSuiteContract.Package(Path.Combine(fixture, "suite.json"), fixture, Path.Combine(root, "two")).Valid.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Rejects schema extensions, empty checks, duplicate identifiers and unsafe paths.</summary>
    /// <param name="mutation">The invalid source mutation.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("path")]
    [InlineData("owner")]
    [InlineData("token")]
    [InlineData("production-write")]
    [InlineData("staging-write")]
    [InlineData("fractional-timeout")]
    [InlineData("large-timeout")]
    [InlineData("zero-timeout")]
    public void InvalidSuitesAreRefused(string mutation)
    {
        JsonObject suite = Suite();
        if (mutation == "empty")
        {
            suite["checks"] = new JsonArray();
        }
        else if (mutation == "duplicate")
        {
            suite["checks"]!.AsArray().Add(suite["checks"]![0]!.DeepClone());
        }
        else if (mutation == "path")
        {
            suite["payload"] = new JsonArray("../runner.py");
        }
        else if (mutation == "owner")
        {
            _ = suite.Remove("owner");
        }
        else if (mutation == "token")
        {
            suite["token"] = "secret-must-not-appear";
        }
        else if (mutation == "production-write")
        {
            suite["checks"]![0]!["effect"] = "synthetic-write";
        }
        else if (mutation == "staging-write")
        {
            suite["checks"]![0]!["environments"] = new JsonArray("staging");
            suite["checks"]![0]!["effect"] = "synthetic-write";
        }
        else if (mutation == "fractional-timeout")
        {
            suite["checks"]![0]!["timeoutSeconds"] = 30.5;
        }
        else if (mutation == "large-timeout")
        {
            suite["checks"]![0]!["timeoutSeconds"] = 3601;
        }
        else if (mutation == "zero-timeout")
        {
            suite["checks"]![0]!["timeoutSeconds"] = 0;
        }

        CheckSuiteReport report = CheckSuiteContract.ValidateDocument("suite", Bytes(suite));
        report.Valid.ShouldBeFalse();
        JsonSerializer.Serialize(report).ShouldNotContain("secret-must-not-appear");
    }

    /// <summary>Rejects duplicate JSON properties independently of JSON Schema.</summary>
    [Fact]
    public void DuplicatePropertiesAreRefused() => CheckSuiteContract.ValidateDocument("suite", Encoding.UTF8.GetBytes("{\"schema\":\"hexalith.check-suite.v1\",\"schema\":\"hexalith.check-suite.v1\"}")).Valid.ShouldBeFalse();

    /// <summary>Rejects links and unexpected package files.</summary>
    [Fact]
    public void SymlinksAndExtraFilesAreRefused()
    {
        string root = Temporary();
        try
        {
            string package = Path.Combine(root, "package");
            CheckSuiteContract.Package(Path.Combine(Fixture(), "suite.json"), Fixture(), package).Valid.ShouldBeTrue();
            File.WriteAllText(Path.Combine(package, "extra"), "unexpected");
            CheckSuiteContract.ValidatePackage(package).Valid.ShouldBeFalse();
            File.Delete(Path.Combine(package, "extra"));
            File.Delete(Path.Combine(package, "payload", "runner.py"));
            _ = File.CreateSymbolicLink(Path.Combine(package, "payload", "runner.py"), Path.Combine(Fixture(), "runner.py"));
            CheckSuiteContract.ValidatePackage(package).Valid.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Every executor-owned identity and nonpassing outcome independently prevents admission.</summary>
    /// <param name="mutation">The evidence mutation.</param>
    /// <param name="accepted">The expected admission decision.</param>
    [Theory]
    [InlineData("none", true)]
    [InlineData("offset", true)]
    [InlineData("fresh-install", true)]
    [InlineData("checkId", false)]
    [InlineData("attemptId", false)]
    [InlineData("invocationId", false)]
    [InlineData("environment", false)]
    [InlineData("suiteDigest", false)]
    [InlineData("descriptorDigest", false)]
    [InlineData("paired-suite-digest", false)]
    [InlineData("paired-descriptor-digest", false)]
    [InlineData("profileDigest", false)]
    [InlineData("configurationDigest", false)]
    [InlineData("servedReleaseDigest", false)]
    [InlineData("workingBaseline", false)]
    [InlineData("failed", false)]
    [InlineData("skipped", false)]
    [InlineData("timed-out", false)]
    [InlineData("incomplete", false)]
    [InlineData("partial", false)]
    [InlineData("late", false)]
    [InlineData("observed-release", false)]
    [InlineData("missing", false)]
    [InlineData("controlled", false)]
    public void OnlyCompleteMatchingHostedEvidencePasses(string mutation, bool accepted)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        string root = Temporary();
        try
        {
            string package = Path.Combine(root, "package");
            CheckSuiteReport packaged = CheckSuiteContract.Package(Path.Combine(Fixture(), "suite.json"), Fixture(), package);
            packaged.Valid.ShouldBeTrue();
            JsonObject invocation = Invocation(packaged.SuiteDigest!, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(package, "descriptor.json")))));
            JsonObject result = Result(invocation);
            CheckSuiteContract.ValidateDocument("invocation", Bytes(invocation)).Valid.ShouldBeTrue("invocation schema");
            CheckSuiteContract.ValidateDocument("result", Bytes(result)).Valid.ShouldBeTrue("result schema");
            string release = invocation["servedReleaseDigest"]!.GetValue<string>();
            DateTimeOffset received = DateTimeOffset.Parse("2026-10-22T00:00:05Z", System.Globalization.CultureInfo.InvariantCulture);
            if (mutation == "fresh-install")
            {
                invocation["workingBaseline"] = null;
                result["workingBaseline"] = null;
            }
            else if (mutation == "offset")
            {
                invocation["startedAt"] = "2026-10-22T02:00:00+02:00";
                invocation["deadlineAt"] = "2026-10-22T02:00:30+02:00";
                result["startedAt"] = "2026-10-22T02:00:00+02:00";
                result["completedAt"] = "2026-10-22T02:00:04+02:00";
            }
            else if (mutation is "failed" or "skipped" or "timed-out" or "incomplete")
            {
                result["outcome"] = mutation;
            }
            else if (mutation == "partial")
            {
                result["complete"] = false;
            }
            else if (mutation == "late")
            {
                received = received.AddMinutes(1);
            }
            else if (mutation == "observed-release")
            {
                release = Digest('e');
            }
            else if (mutation == "controlled")
            {
                invocation["executionMode"] = "controlled";
                result["executionMode"] = "controlled";
            }
            else if (mutation == "workingBaseline")
            {
                result["workingBaseline"]!["attemptId"] = "other-attempt";
            }
            else if (mutation is "paired-suite-digest" or "paired-descriptor-digest")
            {
                string field = mutation == "paired-suite-digest" ? "suiteDigest" : "descriptorDigest";
                invocation[field] = Digest('e');
                result[field] = Digest('e');
            }
            else if (mutation is not ("none" or "missing"))
            {
                string replacement = mutation == "environment" ? "production" : "other";
                result[mutation] = mutation.EndsWith("Digest", StringComparison.Ordinal) ? Digest('e') : replacement;
            }

            CheckSuiteReport evaluated = CheckSuiteContract.EvaluateResult(package, Bytes(invocation), mutation == "missing" ? null : Bytes(result), release, received);
            evaluated.Valid.ShouldBe(accepted, string.Join(",", evaluated.Diagnostics));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The CLI refuses malformed and unavailable inputs without leaking tokens or running payloads.</summary>
    /// <returns>The asynchronous test.</returns>
    [Fact]
    public async Task CommandReportsStableSanitizedRefusalsAsync()
    {
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            StringWriter error = new();
            await using (error.ConfigureAwait(true))
            {
                int code = await EvidenceCommandApplication.InvokeAsync(["check-suite", "validate", "missing-secret-token", "--kind", "suite", "--output", "json"], output, error, TestContext.Current.CancellationToken).ConfigureAwait(true);
                code.ShouldBe(6);
                output.ToString().ShouldContain("HXS001");
                output.ToString().ShouldNotContain("missing-secret-token");
                error.ToString().ShouldBeEmpty();
                _ = output.GetStringBuilder().Clear();
                code = await EvidenceCommandApplication.InvokeAsync(["check-suite", "package", Path.Combine(Fixture(), "suite.json"), "--output", "json"], output, error, TestContext.Current.CancellationToken).ConfigureAwait(true);
                code.ShouldBe(1);
                output.ToString().ShouldContain("HXC001");
            }
        }
    }

    /// <summary>Publication rejects missing owners, source mappings, checks and explicitly blocked positive coverage.</summary>
    /// <param name="mutation">The coverage mutation.</param>
    /// <param name="eligible">The expected publication decision.</param>
    [Theory]
    [InlineData("none", true)]
    [InlineData("owner", false)]
    [InlineData("source", false)]
    [InlineData("check", false)]
    [InlineData("mapping", false)]
    [InlineData("blocked", false)]
    [InlineData("denial", false)]
    [InlineData("missing-module", false)]
    [InlineData("source-flow", false)]
    [InlineData("source-smoke-suite", false)]
    public void ReferencePublicationRequiresAllOwnedSourceMappings(string mutation, bool eligible)
    {
        string root = Temporary();
        try
        {
            string packages = Path.Combine(root, "packages");
            _ = Directory.CreateDirectory(Path.Combine(root, ".github"));
            File.WriteAllText(Path.Combine(root, ".github", "CODEOWNERS"), "* @jpiquot\n");
            File.Copy(Path.Combine(Fixture(), "runner.py"), Path.Combine(root, "runner.py"));
            JsonArray entries = [];
            foreach (string module in new[] { "parties", "eventstore", "tenants", "memories", "mcpcli" })
            {
                JsonObject descriptor = Suite();
                descriptor["module"] = module;
                descriptor["suiteId"] = module + "-reference";
                descriptor["source"] = module + ".json";
                JsonObject e2e = descriptor["checks"]![0]!.DeepClone().AsObject();
                e2e["id"] = "fixture.e2e";
                e2e["kind"] = "e2e";
                e2e["environments"] = new JsonArray("staging");
                _ = e2e.Remove("cadenceSeconds");
                descriptor["checks"]!.AsArray().Add(e2e);
                if (module == "tenants" && mutation == "denial")
                {
                    e2e["assertion"] = "denial";
                }

                File.WriteAllBytes(Path.Combine(root, module + "-suite.json"), Bytes(descriptor));
                File.WriteAllText(Path.Combine(root, module + ".json"), "{\"modules\":[{\"identity\":{\"moduleId\":\"" + module + "\"},\"lifecycle\":{\"criticalFlows\":[{\"name\":\"flow\",\"surface\":\"fixture-api\",\"e2eChecks\":[\"fixture.e2e\"]}],\"smokeSurfaces\":[]},\"surfaces\":{\"interfaces\":[{\"name\":\"fixture-api\"}]}}]}");
                if (module == "tenants" && mutation is "source-flow" or "source-smoke-suite")
                {
                    JsonObject source = JsonNode.Parse(File.ReadAllBytes(Path.Combine(root, module + ".json")))!.AsObject();
                    JsonObject lifecycle = source["modules"]![0]!["lifecycle"]!.AsObject();
                    if (mutation == "source-flow")
                    {
                        lifecycle["criticalFlows"]!.AsArray().Add(new JsonObject { ["name"] = "unmapped-flow", ["surface"] = "fixture-api", ["operations"] = new JsonArray("read") });
                    }
                    else
                    {
                        lifecycle["smokeSurfaces"]!.AsArray().Add(new JsonObject { ["name"] = "smoke", ["surface"] = "fixture-api", ["suite"] = "wrong-reference" });
                    }

                    File.WriteAllBytes(Path.Combine(root, module + ".json"), Bytes(source));
                }

                CheckSuiteContract.Package(Path.Combine(root, module + "-suite.json"), root, Path.Combine(packages, module, "1.0.0")).Valid.ShouldBeTrue();
                JsonObject flow = new() { ["name"] = "flow", ["surface"] = "fixture-api", ["checks"] = new JsonArray("fixture.e2e"), ["status"] = "resolved", ["reason"] = "none" };
                JsonObject smoke = new() { ["name"] = "smoke", ["surface"] = "fixture-api", ["checks"] = new JsonArray("fixture.read"), ["status"] = "resolved", ["reason"] = "none" };
                JsonObject entry = new()
                {
                    ["module"] = module,
                    ["owner"] = "@jpiquot",
                    ["source"] = module + ".json",
                    ["sourceKind"] = "manifest",
                    ["suite"] = module + "-reference",
                    ["descriptor"] = module + "-suite.json",
                    ["package"] = module + "/1.0.0",
                    ["criticalFlows"] = new JsonArray(flow),
                    ["smokeSurfaces"] = new JsonArray(smoke),
                };
                if (module == "tenants")
                {
                    if (mutation == "owner")
                    {
                        _ = entry.Remove("owner");
                    }
                    else if (mutation == "source")
                    {
                        File.Delete(Path.Combine(root, module + ".json"));
                    }
                    else if (mutation == "check")
                    {
                        flow["checks"] = new JsonArray("unknown.check");
                    }
                    else if (mutation == "mapping")
                    {
                        entry["criticalFlows"] = new JsonArray();
                    }
                    else if (mutation == "blocked")
                    {
                        flow["status"] = "blocked";
                        flow["reason"] = "positive-administrator-coverage";
                    }
                }

                if (module != "tenants" || mutation != "missing-module")
                {
                    entries.Add(entry);
                }
            }

            JsonObject registry = new() { ["schema"] = "hexalith.reference-check-registry.v1", ["modules"] = entries };
            string registryPath = Path.Combine(root, "registry.json");
            File.WriteAllBytes(registryPath, Bytes(registry));
            ReferenceSuiteCoverage.Validate(registryPath, root, packages).Valid.ShouldBe(eligible);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Mathematically integral timeout encodings preserve the finite check budget.</summary>
    /// <param name="representation">The JSON number representation.</param>
    [Theory]
    [InlineData("30.0")]
    [InlineData("3e1")]
    [InlineData("300e-1")]
    public void IntegralTimeoutRepresentationsAreAccepted(string representation)
    {
        string root = Temporary();
        try
        {
            string descriptor = Path.Combine(root, "suite.json");
            string fixture = File.ReadAllText(Path.Combine(Fixture(), "suite.json"));
            File.WriteAllText(descriptor, fixture.Replace("\"timeoutSeconds\":30", "\"timeoutSeconds\":" + representation, StringComparison.Ordinal));
            string package = Path.Combine(root, "package");
            CheckSuiteReport packaged = CheckSuiteContract.Package(descriptor, Fixture(), package);
            packaged.Valid.ShouldBeTrue();
            string descriptorDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(descriptor)));
            JsonObject invocation = Invocation(packaged.SuiteDigest!, descriptorDigest);
            JsonObject result = Result(invocation);
            DateTimeOffset received = DateTimeOffset.Parse("2026-10-22T00:00:05Z", System.Globalization.CultureInfo.InvariantCulture);
            CheckSuiteContract.EvaluateResult(package, Bytes(invocation), Bytes(result), Digest('a'), received).Valid.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Neither linked source descriptors nor linked destination ancestry may write packages.</summary>
    /// <param name="link">The linked path under test.</param>
    [Theory]
    [InlineData("descriptor")]
    [InlineData("descriptor-ancestor")]
    [InlineData("destination-ancestor")]
    public void PackagingRejectsLinkedSourcesAndDestinations(string link)
    {
        string root = Temporary();
        try
        {
            string owned = Path.Combine(root, "owned");
            _ = Directory.CreateDirectory(owned);
            string descriptor = Path.Combine(owned, "suite.json");
            File.Copy(Path.Combine(Fixture(), "suite.json"), descriptor);
            string destination = Path.Combine(root, "package");
            if (link == "descriptor")
            {
                string linked = Path.Combine(root, "linked.json");
                _ = File.CreateSymbolicLink(linked, descriptor);
                descriptor = linked;
            }
            else
            {
                string linked = Path.Combine(root, "linked");
                _ = Directory.CreateSymbolicLink(linked, owned);
                if (link == "descriptor-ancestor")
                {
                    descriptor = Path.Combine(linked, "suite.json");
                }
                else
                {
                    destination = Path.Combine(linked, "new-parent", "package");
                }
            }

            CheckSuiteContract.Package(descriptor, Fixture(), destination).Valid.ShouldBeFalse();
            Directory.Exists(Path.Combine(owned, "new-parent")).ShouldBeFalse();
            Directory.Exists(Path.Combine(root, "package")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>All schemas reject trailing line feeds in identities, digests and paths.</summary>
    /// <param name="kind">The closed document kind.</param>
    /// <param name="field">The string or path to mutate.</param>
    [Theory]
    [InlineData("suite", "suiteId")]
    [InlineData("suite", "source")]
    [InlineData("suite", "entrypoint")]
    [InlineData("suite", "payload")]
    [InlineData("suite", "checkId")]
    [InlineData("suite", "owner")]
    [InlineData("suite", "version")]
    [InlineData("invocation", "invocationId")]
    [InlineData("invocation", "suiteDigest")]
    [InlineData("result", "checkId")]
    [InlineData("result", "descriptorDigest")]
    public void TrailingNewlinesAreRefused(string kind, string field)
    {
        JsonObject invocation = Invocation(Digest('a'), Digest('b'));
        JsonObject document = kind == "suite" ? Suite() : invocation;
        if (kind == "result")
        {
            document = Result(invocation);
        }

        if (field == "payload")
        {
            document["payload"]![0] = "runner.py\n";
        }
        else if (kind == "suite" && field == "checkId")
        {
            document["checks"]![0]!["id"] = "fixture.read\n";
        }
        else
        {
            document[field] = document[field]!.GetValue<string>() + "\n";
        }

        CheckSuiteContract.ValidateDocument(kind, Bytes(document)).Valid.ShouldBeFalse();
    }

    /// <summary>The confined-path guard itself rejects filenames ending with a line feed.</summary>
    [Fact]
    public void ConfinedPathsRejectTrailingNewlines()
    {
        string root = Temporary();
        try
        {
            File.WriteAllText(Path.Combine(root, "runner.py\n"), "payload");
            _ = Should.Throw<IOException>(() => CheckSuiteContract.OwnedPath(root, "runner.py\n"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Naive timestamps cannot acquire local or assumed UTC meaning.</summary>
    /// <param name="kind">The closed document kind.</param>
    /// <param name="field">The timestamp field.</param>
    [Theory]
    [InlineData("invocation", "startedAt")]
    [InlineData("invocation", "deadlineAt")]
    [InlineData("result", "startedAt")]
    [InlineData("result", "completedAt")]
    public void NaiveTimestampsAreRefused(string kind, string field)
    {
        JsonObject invocation = Invocation(Digest('a'), Digest('b'));
        JsonObject document = kind == "invocation" ? invocation : Result(invocation);
        document[field] = "2026-10-22T00:00:00";
        CheckSuiteContract.ValidateDocument(kind, Bytes(document)).Valid.ShouldBeFalse();
    }

    /// <summary>The document CLI refuses oversized input without reflecting its path or contents.</summary>
    /// <returns>The asynchronous test.</returns>
    [Fact]
    public async Task OversizedDocumentIsRefusedWithSanitizedDiagnosticsAsync()
    {
        string root = Temporary();
        try
        {
            string path = Path.Combine(root, "secret-token-must-not-appear.json");
            await File.WriteAllBytesAsync(path, new byte[(4 * 1024 * 1024) + 1], TestContext.Current.CancellationToken).ConfigureAwait(true);
            StringWriter output = new();
            await using (output.ConfigureAwait(true))
            {
                StringWriter error = new();
                await using (error.ConfigureAwait(true))
                {
                    int code = await EvidenceCommandApplication.InvokeAsync(["check-suite", "validate", path, "--kind", "suite", "--output", "json"], output, error, TestContext.Current.CancellationToken).ConfigureAwait(true);
                    code.ShouldBe(6);
                    output.ToString().ShouldContain("HXS001");
                    output.ToString().ShouldNotContain("secret-token-must-not-appear");
                    error.ToString().ShouldBeEmpty();
                }
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Fixture()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "test", "fixtures", "check-suites")))
        {
            current = current.Parent;
        }

        return Path.Combine(current!.FullName, "test", "fixtures", "check-suites");
    }

    private static string Temporary()
    {
        string root = Path.Combine(Path.GetTempPath(), "hexalith-check-contract-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(root);
        return root;
    }

    private static JsonObject Suite() => JsonNode.Parse(File.ReadAllBytes(Path.Combine(Fixture(), "suite.json")))!.AsObject();

    private static byte[] Bytes(JsonObject value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    private static JsonObject Invocation(string suiteDigest, string descriptorDigest) => new()
    {
        ["schema"] = "hexalith.check-invocation.v1",
        ["invocationId"] = "invocation-one",
        ["checkId"] = "fixture.read",
        ["attemptId"] = "attempt-one",
        ["environment"] = "staging",
        ["executionMode"] = "hosted",
        ["servedReleaseDigest"] = Digest('a'),
        ["suiteDigest"] = suiteDigest,
        ["descriptorDigest"] = descriptorDigest,
        ["profileDigest"] = Digest('b'),
        ["configurationDigest"] = Digest('c'),
        ["workingBaseline"] = new JsonObject
        {
            ["attemptId"] = "baseline-one",
            ["releaseDigest"] = Digest('d'),
            ["profileDigest"] = Digest('b'),
            ["configurationDigest"] = Digest('c'),
        },
        ["verificationEndpoint"] = "https://verification.example",
        ["syntheticTenant"] = "synthetic-one",
        ["syntheticTenantMode"] = "existing-synthetic",
        ["fixturePartyId"] = "synthetic-party",
        ["fixtureCaseId"] = "synthetic-case",
        ["externalEffectsDisabled"] = true,
        ["startedAt"] = "2026-10-22T00:00:00Z",
        ["deadlineAt"] = "2026-10-22T00:00:30Z",
    };

    private static JsonObject Result(JsonObject invocation)
    {
        JsonObject result = invocation.DeepClone().AsObject();
        foreach (string field in new[] { "verificationEndpoint", "syntheticTenant", "syntheticTenantMode", "fixturePartyId", "fixtureCaseId", "externalEffectsDisabled", "deadlineAt" })
        {
            _ = result.Remove(field);
        }

        result["schema"] = "hexalith.check-result.v1";
        result["outcome"] = "passed";
        result["complete"] = true;
        result["completedAt"] = "2026-10-22T00:00:04Z";
        result["diagnostics"] = new JsonArray();
        return result;
    }
}
