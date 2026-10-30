// <copyright file="CandidateGateFixture.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.CandidatePolicy;
using Hexalith.Builds.Tooling.TestEnvironment;

/// <summary>Local signed HTTP observations, never live GitHub or qualification evidence.</summary>
internal sealed class CandidateGateFixture : HttpMessageHandler
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "candidate-fixture-" + Guid.NewGuid().ToString("N"));
    private int _runReads;

    /// <summary>Initializes a new instance of the <see cref="CandidateGateFixture"/> class.</summary>
    public CandidateGateFixture()
    {
        _ = Directory.CreateDirectory(_directory);
        Configuration = new("Hexalith/Hexalith.Builds/.github/workflows/candidate-policy.yml@" + new string('f', 40), new string('f', 40), "candidate-policy", ".github/workflows/ci.yml", "sha256:" + Convert.ToHexStringLower(SHA256.HashData("trusted workflow"u8)), "integration", "Integration");
        Payload = new JsonObject
        {
            ["policyId"] = TestEnvironmentContractReader.PolicyId,
            ["repository"] = "Hexalith/Orders",
            ["moduleId"] = "orders",
            ["gateRunId"] = "101",
            ["attemptSequence"] = 1,
            ["runId"] = Guid.NewGuid().ToString("N"),
            ["attemptId"] = Guid.NewGuid().ToString("N"),
            ["kind"] = "pr-merge",
            ["targetCommit"] = Merge,
            ["epoch"] = "2026-10-01T00:00:00Z",
            ["pr"] = new JsonObject { ["id"] = "7", ["headCommit"] = Head, ["baseCommit"] = Base, ["mergeResultCommit"] = Merge },
        };
        Client = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("http://fixture.invalid/") };
        Gate = new CandidateGateClient(Client, Configuration, "fixture-read-token");
    }

    /// <summary>Gets the exact committed merge result.</summary>
    public static string Merge => new('a', 40);

    /// <summary>Gets the exact head.</summary>
    public static string Head => new('b', 40);

    /// <summary>Gets the exact base.</summary>
    public static string Base => new('c', 40);

    /// <summary>Gets trusted host configuration.</summary>
    public CandidateGateConfiguration Configuration { get; }

    /// <summary>Gets the local transport.</summary>
    public HttpClient Client { get; }

    /// <summary>Gets the real gate implementation using fixture transport.</summary>
    public CandidateGateClient Gate { get; }

    /// <summary>Gets the mutable issuance before signing.</summary>
    public JsonObject Payload { get; }

    /// <summary>Gets or sets the authenticated current attempt.</summary>
    public int CurrentAttempt { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether a newer request has registered, irrespective of conclusion.</summary>
    public bool NewerRequest { get; set; }

    /// <summary>Gets or sets current PR state.</summary>
    public string PrState { get; set; } = "open";

    /// <summary>Gets or sets the latest reopen epoch.</summary>
    public string? ReopenEpoch { get; set; }

    /// <summary>Gets or sets a value indicating whether the head has changed.</summary>
    public bool ChangedHead { get; set; }

    /// <summary>Gets or sets a value indicating whether the base branch tip has changed.</summary>
    public bool ChangedBase { get; set; }

    /// <summary>Gets or sets a value indicating whether merge parents are reversed.</summary>
    public bool WrongParents { get; set; }

    /// <summary>Gets or sets a value indicating whether required reviewers remain configured.</summary>
    public bool Approved { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether authenticated observations are unavailable.</summary>
    public bool Unavailable { get; set; }

    /// <summary>Gets or sets a value indicating whether the issuance claims a fabricated workflow.</summary>
    public bool FabricatedIssuer { get; set; }

    /// <summary>Gets or sets current workflow status.</summary>
    public string RunStatus { get; set; } = "in_progress";

    /// <summary>Gets or sets a value indicating whether an authenticated reviewer approved issuance.</summary>
    public bool ReviewApproved { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the current attempt changes during the final refetch.</summary>
    public bool AdvanceOnFinalRefetch { get; set; }

    /// <summary>Gets or sets the current PR epoch event type.</summary>
    public string EpochEvent { get; set; } = "reopened";

    /// <summary>Gets or sets the authenticated workflow conclusion.</summary>
    public string Conclusion { get; set; } = "success";

    /// <summary>Gets or sets the current workflow dispatch identity.</summary>
    public string GateRunId { get; set; } = "101";

    /// <summary>Gets the actual CI representation used by the runner fixture.</summary>
    public JsonElement Context => JsonSerializer.SerializeToElement(new { kind = "ci", repository = "Hexalith/Orders", workflowRunId = GateRunId, workflowAttempt = CurrentAttempt, jobId = "integration", runnerKind = "github-hosted", operatingSystem = "Linux", disposable = true });

    /// <summary>Signs the request with this fixture's RSA issuer.</summary>
    /// <returns>The private envelope path.</returns>
    public string Sign()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(Payload);
        JsonObject claims = new()
        {
            ["iss"] = "https://token.actions.githubusercontent.com",
            ["aud"] = "hexalith-candidate-v1:" + Convert.ToHexStringLower(SHA256.HashData(payload)),
            ["job_workflow_ref"] = FabricatedIssuer ? "attacker/workflow@main" : Configuration.WorkflowRef,
            ["job_workflow_sha"] = Configuration.WorkflowSha,
            ["environment"] = Configuration.Environment,
            ["repository"] = "Hexalith/Orders",
            ["run_id"] = Payload["gateRunId"]!.GetValue<string>(),
            ["run_attempt"] = Payload["attemptSequence"]!.ToString(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10,
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300,
        };
        string input = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture" })) + "." + Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        string token = input + "." + Encode(_rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { payload = Convert.ToBase64String(payload), token }));
        return path;
    }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = request.RequestUri!.AbsolutePath;
        object result;
        if (path.EndsWith("/jwks", StringComparison.Ordinal))
        {
            if (request.Headers.Authorization is not null)
            {
                throw new InvalidOperationException("API credential leaked to OIDC issuer.");
            }

            RSAParameters parameters = _rsa.ExportParameters(false);
            result = new { keys = new[] { new { kid = "fixture", kty = "RSA", n = Encode(parameters.Modulus!), e = Encode(parameters.Exponent!) } } };
        }
        else
        {
            if (Unavailable || request.Headers.Authorization?.Parameter != "fixture-read-token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (path.EndsWith("/runs/" + GateRunId, StringComparison.Ordinal) && ++_runReads == 2 && AdvanceOnFinalRefetch)
            {
                CurrentAttempt++;
            }

            result = path switch
            {
                var value when value.EndsWith("/approvals", StringComparison.Ordinal) => ReviewApproved ? new[] { new { state = "approved", user = new { id = 1 }, environments = new[] { new { name = "candidate-policy" } } } } : [],
                var value when value.EndsWith("/jobs", StringComparison.Ordinal) => new { jobs = new[] { new { name = "Integration", runner_group_name = "GitHub Actions", labels = new[] { "Linux" } } } },
                var value when value.Contains("/actions/runs/", StringComparison.Ordinal) => new { id = long.Parse(GateRunId, System.Globalization.CultureInfo.InvariantCulture), repository = new { full_name = "Hexalith/Orders" }, run_attempt = CurrentAttempt, run_number = 10, path = ".github/workflows/ci.yml", head_sha = Merge, created_at = "2026-10-10T00:00:00Z", workflow_id = 1, status = RunStatus, conclusion = Conclusion, @event = Payload["kind"]!.GetValue<string>() == "release" ? "workflow_dispatch" : "pull_request", pull_requests = new[] { new { number = 7, head = new { sha = Head }, @base = new { sha = Base } } } },
                var value when value.Contains("/contents/", StringComparison.Ordinal) => new { encoding = "base64", content = Convert.ToBase64String("trusted workflow"u8) },
                var value when value.Contains("/environments/", StringComparison.Ordinal) => new { protection_rules = new[] { new { type = "required_reviewers", reviewers = Approved ? new[] { new { id = 1 } } : [] } } },
                var value when value.Contains("/git/commits/", StringComparison.Ordinal) => new { sha = Merge, parents = new[] { new { sha = WrongParents ? Head : Base }, new { sha = WrongParents ? Base : Head } } },
                var value when value.Contains("/git/ref/", StringComparison.Ordinal) => new { @object = new { sha = ChangedBase ? Head : Base } },
                var value when value.EndsWith("/pulls/7", StringComparison.Ordinal) => new { state = PrState, merged = false, created_at = "2026-10-01T00:00:00Z", head = new { sha = ChangedHead ? Base : Head }, @base = new { sha = Base, @ref = "main" }, merge_commit_sha = Merge },
                var value when value.EndsWith("/events", StringComparison.Ordinal) => ReopenEpoch is null ? [] : new object[] { new { @event = EpochEvent, id = ReopenEpoch, created_at = "2026-10-11T00:00:00Z" } },
                var value when value.Contains("/actions/workflows/", StringComparison.Ordinal) => new { workflow_runs = NewerRequest ? new[] { new { run_number = 11, status = "queued", conclusion = (string?)null, pull_requests = new[] { new { number = 7 } } } } : [] },
                _ => throw new InvalidOperationException("Unexpected fixture path: " + path),
            };
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") });
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Client.Dispose();
            _rsa.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        base.Dispose(disposing);
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}