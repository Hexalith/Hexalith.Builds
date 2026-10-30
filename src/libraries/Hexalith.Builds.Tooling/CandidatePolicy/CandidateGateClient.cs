// <copyright file="CandidateGateClient.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CandidatePolicy;

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.TestEnvironment;

/// <summary>Authenticates GitHub-issued requests and independently refetches their current authority.</summary>
/// <param name="http">The HTTPS transport (replaceable by local HTTP fixtures).</param>
/// <param name="configuration">Trusted host enrollment, independent of request documents.</param>
/// <param name="githubToken">The read-only GitHub API credential.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S1075:Refactor your code not to use hardcoded absolute paths or URIs.", Justification = "Public GitHub API and OIDC issuer endpoints are fixed security authorities.")]
public sealed class CandidateGateClient(HttpClient http, CandidateGateConfiguration configuration, string githubToken)
{
    private static readonly HttpClient _transport = new(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true }) { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Loads independently provisioned host enrollment and authentication. There is no default enrolled issuer.</summary>
    /// <returns>The authenticated client.</returns>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    public static CandidateGateClient FromHost()
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("HEXALITH_CANDIDATE_GATE_CONFIG") ?? throw new CandidatePolicyException("issuer-not-enrolled");
            string credential = Environment.GetEnvironmentVariable("HEXALITH_CANDIDATE_GITHUB_TOKEN") ?? throw new CandidatePolicyException("authentication-unavailable");
            CandidateGateConfiguration config = JsonSerializer.Deserialize<CandidateGateConfiguration>(ReadFile(path)) ?? throw new CandidatePolicyException("issuer-not-enrolled");
            return new CandidateGateClient(_transport, config, credential);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CandidatePolicyException("issuer-not-enrolled");
        }
    }

    /// <summary>Authenticates a signed issuance and current request/attempt state; saved JSON alone grants no authority.</summary>
    /// <param name="requestPath">The signed request envelope path.</param>
    /// <param name="cancellationToken">The operation cancellation.</param>
    /// <returns>The immutable existing v1 bindings.</returns>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    public async Task<CandidatePolicyBinding> ResolveAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(githubToken) || !configuration.WorkflowRef.StartsWith("Hexalith/Hexalith.Builds/.github/workflows/candidate-policy.yml@", StringComparison.Ordinal)
                || !Commit(configuration.WorkflowSha) || !configuration.CallerWorkflowPath.StartsWith(".github/workflows/", StringComparison.Ordinal)
                || configuration.Environment != "candidate-policy")
            {
                throw new CandidatePolicyException("issuer-not-enrolled");
            }

            JsonElement envelope = Parse(ReadFile(requestPath));
            byte[] payloadBytes = Convert.FromBase64String(S(envelope, "payload"));
            JsonElement payload = Parse(payloadBytes);
            string token = S(envelope, "token");
            JsonElement claims = await AuthenticateAsync(token, payloadBytes, cancellationToken).ConfigureAwait(false);
            string repository = S(payload, "repository");
            if (!Repository(repository)
                || S(payload, "policyId") != TestEnvironmentContractReader.PolicyId || S(claims, "repository") != repository)
            {
                throw new CandidatePolicyException("policy-or-repository-mismatch");
            }

            string gateRun = S(claims, "run_id");
            int sequence = int.Parse(S(claims, "run_attempt"), NumberStyles.None, CultureInfo.InvariantCulture);
            if (!gateRun.All(char.IsAsciiDigit) || sequence <= 0 || payload.GetProperty("attemptSequence").GetInt32() != sequence
                || S(payload, "gateRunId") != gateRun || !Runtime.CompositionRunPlanFactory.IsRunId(S(payload, "runId"))
                || !Runtime.CompositionRunPlanFactory.IsRunId(S(payload, "attemptId")) || S(payload, "runId") == S(payload, "attemptId"))
            {
                throw new CandidatePolicyException("producer-mismatch");
            }

            string prefix = "repos/" + repository;
            JsonElement run = await GetAsync(prefix + "/actions/runs/" + gateRun, cancellationToken).ConfigureAwait(false);
            if (S(run.GetProperty("repository"), "full_name") != repository || run.GetProperty("id").ToString() != gateRun
                || run.GetProperty("run_attempt").GetInt32() != sequence)
            {
                throw new CandidatePolicyException("latest-issued-attempt-required");
            }

            if (S(run, "path").Split('@')[0] != configuration.CallerWorkflowPath)
            {
                throw new CandidatePolicyException("issuer-workflow-mismatch");
            }

            JsonElement workflow = await GetAsync(prefix + "/contents/" + configuration.CallerWorkflowPath + "?ref=" + S(run, "head_sha"), cancellationToken).ConfigureAwait(false);
            if (S(workflow, "encoding") != "base64" || Hash(Convert.FromBase64String(S(workflow, "content"))) != configuration.CallerContentIdentity)
            {
                throw new CandidatePolicyException("issuer-workflow-mismatch");
            }

            JsonElement environment = await GetAsync(prefix + "/environments/" + configuration.Environment, cancellationToken).ConfigureAwait(false);
            if (!environment.GetProperty("protection_rules").EnumerateArray().Any(rule => S(rule, "type") == "required_reviewers" && rule.GetProperty("reviewers").GetArrayLength() > 0))
            {
                throw new CandidatePolicyException("approval-unavailable");
            }

            JsonElement reviews = await GetAsync(prefix + "/actions/runs/" + gateRun + "/approvals", cancellationToken).ConfigureAwait(false);
            JsonElement[] applicableReviews = [.. reviews.EnumerateArray().Where(review => review.GetProperty("environments").EnumerateArray().Any(item => S(item, "name") == configuration.Environment))];
            if (applicableReviews.Length == 0 || applicableReviews.Any(review => S(review, "state") != "approved" || review.GetProperty("user").GetProperty("id").GetInt64() <= 0))
            {
                throw new CandidatePolicyException("approval-unavailable");
            }

            string kind = S(payload, "kind");
            string target = S(payload, "targetCommit");
            if (!Commit(target))
            {
                throw new CandidatePolicyException("committed-candidate-required");
            }

            JsonElement commit = await GetAsync(prefix + "/git/commits/" + target, cancellationToken).ConfigureAwait(false);
            if (S(commit, "sha") != target)
            {
                throw new CandidatePolicyException("committed-candidate-required");
            }

            if (kind == "pr-merge")
            {
                JsonElement pr = payload.GetProperty("pr");
                string number = S(pr, "id");
                if (!number.All(char.IsAsciiDigit) || number.Length == 0 || S(run, "event") != "pull_request"
                    || !run.GetProperty("pull_requests").EnumerateArray().Any(item => item.GetProperty("number").ToString() == number))
                {
                    throw new CandidatePolicyException("pr-binding-mismatch");
                }

                JsonElement current = await GetAsync(prefix + "/pulls/" + number, cancellationToken).ConfigureAwait(false);
                if (S(current, "state") != "open" || current.GetProperty("merged").GetBoolean())
                {
                    throw new CandidatePolicyException("request-not-pending");
                }

                string epoch = S(current, "created_at");
                foreach (JsonElement item in (await PagesAsync(prefix + "/issues/" + number + "/events", null, cancellationToken).ConfigureAwait(false)).Where(item => S(item, "event") is "reopened" or "base_ref_changed"))
                {
                    epoch = item.GetProperty("id").ToString();
                    if (item.GetProperty("created_at").GetDateTimeOffset() >= run.GetProperty("created_at").GetDateTimeOffset())
                    {
                        throw new CandidatePolicyException("fresh-request-required");
                    }
                }

                if (epoch != S(payload, "epoch"))
                {
                    throw new CandidatePolicyException("fresh-request-required");
                }

                JsonElement currentBase = current.GetProperty("base");
                JsonElement baseRef = await GetAsync(prefix + "/git/ref/heads/" + Uri.EscapeDataString(S(currentBase, "ref")), cancellationToken).ConfigureAwait(false);
                string[] parents = [.. commit.GetProperty("parents").EnumerateArray().Select(parent => S(parent, "sha"))];
                JsonElement originalPr = run.GetProperty("pull_requests").EnumerateArray().Single(item => item.GetProperty("number").ToString() == number);
                if (S(run, "head_sha") != target || S(originalPr.GetProperty("head"), "sha") != S(pr, "headCommit")
                    || S(originalPr.GetProperty("base"), "sha") != S(pr, "baseCommit") || S(current.GetProperty("head"), "sha") != S(pr, "headCommit") || S(currentBase, "sha") != S(pr, "baseCommit")
                    || S(baseRef.GetProperty("object"), "sha") != S(pr, "baseCommit") || S(current, "merge_commit_sha") != target
                    || S(pr, "mergeResultCommit") != target || parents.Length != 2 || parents[0] != S(pr, "baseCommit") || parents[1] != S(pr, "headCommit"))
                {
                    throw new CandidatePolicyException("pr-binding-mismatch");
                }

                JsonElement[] candidates = await PagesAsync(prefix + "/actions/workflows/" + run.GetProperty("workflow_id").ToString() + "/runs?event=pull_request", "workflow_runs", cancellationToken).ConfigureAwait(false);
                if (candidates.Any(newer => newer.GetProperty("run_number").GetInt64() > run.GetProperty("run_number").GetInt64()
                    && newer.GetProperty("pull_requests").EnumerateArray().Any(item => item.GetProperty("number").ToString() == number)))
                {
                    throw new CandidatePolicyException("latest-issued-attempt-required");
                }
            }
            else if (kind != "release" || S(run, "event") != "workflow_dispatch" || S(run, "head_sha") != target)
            {
                throw new CandidatePolicyException("release-authority-mismatch");
            }

            if (kind == "release" && S(run, "status") == "completed")
            {
                throw new CandidatePolicyException("request-not-pending");
            }

            // Re-read after the multi-call observation so a concurrent rerun cannot retain an old attempt.
            JsonElement refreshed = await GetAsync(prefix + "/actions/runs/" + gateRun, cancellationToken).ConfigureAwait(false);
            if (refreshed.GetProperty("run_attempt").GetInt32() != sequence)
            {
                throw new CandidatePolicyException("latest-issued-attempt-required");
            }

            if (S(refreshed, "status") == "completed" && S(refreshed, "conclusion") != "success")
            {
                throw new CandidatePolicyException("attempt-not-successful");
            }

            string requestId = "github:" + repository + ":" + gateRun;
            JsonObject candidate = new()
            {
                ["state"] = "populated",
                ["classification"] = "candidate",
                ["repository"] = repository,
                ["moduleId"] = S(payload, "moduleId"),
                ["committedRevision"] = target,
            };
            JsonObject decision = new()
            {
                ["state"] = "populated",
                ["requestId"] = requestId,
                ["issuer"] = JsonSerializer.SerializeToNode(new { id = configuration.WorkflowRef, authenticationReference = Hash(Encoding.UTF8.GetBytes(token)) }),
                ["repository"] = repository,
                ["moduleId"] = S(payload, "moduleId"),
                ["policyId"] = TestEnvironmentContractReader.PolicyId,
                ["kind"] = kind,
                ["targetCommit"] = target,
                ["requestState"] = "pending",
                ["producingRunId"] = S(payload, "runId"),
                ["producingAttemptId"] = S(payload, "attemptId"),
                ["attemptSequence"] = sequence,
            };
            if (kind == "pr-merge")
            {
                candidate["pr"] = JsonNode.Parse(payload.GetProperty("pr").GetRawText());
                decision["pr"] = candidate["pr"]!.DeepClone();
            }

            return new CandidatePolicyBinding(S(payload, "runId"), S(payload, "attemptId"), sequence, JsonSerializer.SerializeToElement(candidate), JsonSerializer.SerializeToElement(decision), JsonSerializer.SerializeToElement(new { state = "populated", producingRunId = S(payload, "runId"), producingDecisionId = requestId, reuseAllowed = false, sourceRunIds = Array.Empty<string>(), sourceDecisionIds = Array.Empty<string>() }));
        }
        catch (CandidatePolicyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or CryptographicException or ArgumentException or OverflowException or OperationCanceledException)
        {
            throw new CandidatePolicyException("authority-unavailable");
        }
    }

    /// <summary>Checks the actual runner context against authenticated GitHub job observations.</summary>
    /// <param name="binding">The authenticated decision.</param>
    /// <param name="context">The actual runner context.</param>
    /// <param name="cancellationToken">The operation cancellation.</param>
    /// <returns>The completed verification.</returns>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    /// <exception cref="ArgumentNullException">A required collaborator is absent.</exception>
    public async Task VerifyExecutionAsync(CandidatePolicyBinding binding, JsonElement context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        CandidatePolicyValidator.RequireCi(context);
        string repository = S(binding.Candidate, "repository");
        string gateRun = S(binding.Decision, "requestId")[("github:" + repository + ":").Length..];
        if (S(context, "repository") != repository || S(context, "workflowRunId") != gateRun || context.GetProperty("workflowAttempt").GetInt32() != binding.AttemptSequence || S(context, "jobId") != configuration.ConsumerJobId)
        {
            throw new CandidatePolicyException("same-ci-job-required");
        }

        try
        {
            JsonElement[] jobs = await PagesAsync("repos/" + repository + "/actions/runs/" + gateRun + "/attempts/" + binding.AttemptSequence.ToString(CultureInfo.InvariantCulture) + "/jobs", "jobs", cancellationToken).ConfigureAwait(false);
            if (!jobs.Any(job => S(job, "name") == configuration.ConsumerJobName && S(job, "runner_group_name") == "GitHub Actions"
                && job.GetProperty("labels").EnumerateArray().Any(label => label.GetString() == "Linux")))
            {
                throw new CandidatePolicyException("github-hosted-linux-required");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CandidatePolicyException("authority-unavailable");
        }
    }

    private static byte[] ReadFile(string path)
    {
        using FileStream file = Runtime.SelectedNativeTestReport.OpenWithoutBlocking(path);
        if (!file.CanSeek || file.Length is <= 0 or > 1024 * 1024)
        {
            throw new CandidatePolicyException("request-unreadable");
        }

        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static JsonElement Parse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        CheckKeys(document.RootElement);
        return document.RootElement.Clone();
    }

    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                {
                    throw new CandidatePolicyException("duplicate-request-field");
                }

                CheckKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                CheckKeys(item);
            }
        }
    }

    private static string S(JsonElement value, string property) => value.GetProperty(property).GetString() ?? throw new CandidatePolicyException("authority-unavailable");

    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (value.Length % 4)) % 4));

    private static bool Commit(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    private static bool Repository(string value) => value.Split('/') is { Length: 2 } parts && parts.All(part => part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-'));

    private async Task<JsonElement> AuthenticateAsync(string token, byte[] payload, CancellationToken cancellationToken)
    {
        string[] parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new CandidatePolicyException("issuer-authentication-required");
        }

        JsonElement header = Parse(Decode(parts[0]));
        JsonElement claims = Parse(Decode(parts[1]));
        JsonElement jwks = await GetAsync("https://token.actions.githubusercontent.com/.well-known/jwks", cancellationToken, authenticated: false).ConfigureAwait(false);
        JsonElement key = jwks.GetProperty("keys").EnumerateArray().Single(item => S(item, "kid") == S(header, "kid"));
        using RSA rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Decode(S(key, "n")), Exponent = Decode(S(key, "e")) });
        if (S(header, "alg") != "RS256" || S(key, "kty") != "RSA" || !rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            || S(claims, "iss") != "https://token.actions.githubusercontent.com" || S(claims, "aud") != "hexalith-candidate-v1:" + Hash(payload)[7..]
            || S(claims, "job_workflow_ref") != configuration.WorkflowRef || S(claims, "job_workflow_sha") != configuration.WorkflowSha || S(claims, "environment") != configuration.Environment
            || claims.GetProperty("iat").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60
            || claims.GetProperty("exp").GetInt64() <= claims.GetProperty("iat").GetInt64())
        {
            throw new CandidatePolicyException("issuer-authentication-required");
        }

        // This is an archived issuance attestation, never a bearer access credential. Current state is always fetched afresh.
        return claims;
    }

    private async Task<JsonElement[]> PagesAsync(string path, string? collection, CancellationToken cancellationToken)
    {
        List<JsonElement> items = [];
        for (int page = 1; page <= 100; page++)
        {
            JsonElement response = await GetAsync(path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "per_page=100&page=" + page.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            JsonElement array = collection is null ? response : response.GetProperty(collection);
            items.AddRange(array.EnumerateArray());
            if (array.GetArrayLength() < 100)
            {
                return [.. items];
            }
        }

        throw new CandidatePolicyException("authority-pagination-incomplete");
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken, bool authenticated = true)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.UserAgent.ParseAdd("hexalith-candidate-policy/1");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable streamLifetime = stream.ConfigureAwait(false);
        MemoryStream buffer = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable bufferLifetime = buffer.ConfigureAwait(false);
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > 1024 * 1024)
            {
                throw new CandidatePolicyException("authority-response-too-large");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Parse(buffer.ToArray());
    }
}
