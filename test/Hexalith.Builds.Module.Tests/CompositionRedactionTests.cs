// <copyright file="CompositionRedactionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the per-run signing key and minted tokens never reach retained runner documents.
/// </summary>
public sealed class CompositionRedactionTests
{
    /// <summary>
    /// Verifies signing keys are fresh 384-bit values.
    /// </summary>
    [Fact]
    public void SigningKeysAreFreshAndStrong()
    {
        string first = CompositionSigningKey.Create();

        Convert.FromBase64String(first).Length.ShouldBe(48);
        CompositionSigningKey.Create().ShouldNotBe(first);
    }

    /// <summary>
    /// Verifies plan, state, and readiness documents and the session text never contain the key or a token.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task RetainedDocumentsAndSessionTextNeverContainKeyOrTokenAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string key = CompositionSigningKey.Create();
            string token = CompositionTokenFactory.Create(
                key,
                new CompositionTokenRequest("tester", ["tenant-a"], ["p0-orders"], ["command:submit"], false, TimeSpan.FromMinutes(5)),
                DateTimeOffset.UtcNow);
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            CompositionReadiness readiness = new(
                CompositionReadiness.SupportedSchema,
                plan.RunId,
                new Uri("http://localhost:1"),
                new Uri("http://localhost:2"),
                [new CompositionResourceReadiness("eventstore", "project", "Healthy")]);
            CompositionRunState state = new(
                CompositionRunState.SupportedSchema,
                plan.RunId,
                CompositionRunStatus.Ready,
                "ABCDEF",
                plan.Workspace,
                1,
                DateTimeOffset.UnixEpoch,
                null,
                DateTimeOffset.UnixEpoch);
            string planPath = Path.Combine(root, "plan.json");
            await CompositionDocumentStore.WriteAsync(planPath, plan, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await CompositionDaprComponentRenderer.WriteAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(true);
            using Process process = Process.GetCurrentProcess();
            CompositionRunSession session = new(plan, readiness, key, process);

            string[] retained =
            [
                await File.ReadAllTextAsync(planPath, TestContext.Current.CancellationToken).ConfigureAwait(true),
                await File.ReadAllTextAsync(plan.StateStoreComponentPath, TestContext.Current.CancellationToken).ConfigureAwait(true),
                await File.ReadAllTextAsync(plan.PubSubComponentPath, TestContext.Current.CancellationToken).ConfigureAwait(true),
                await File.ReadAllTextAsync(plan.DaprConfigPath, TestContext.Current.CancellationToken).ConfigureAwait(true),
                CompositionDocumentStore.Serialize(state),
                CompositionDocumentStore.Serialize(readiness),
                session.ToString(),
            ];

            foreach (string text in retained)
            {
                text.ShouldNotContain(key);
                text.ShouldNotContain(token);
                text.ShouldNotContain(token.Split('.')[2]);
            }

            session.ToString().ShouldContain("[redacted]");
            session.SigningKey.ShouldBe(key);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies that the diagnostic mirror records stream metadata without retaining resource output.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DiagnosticMirrorDoesNotRetainSensitiveResourceOutputAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string path = Path.Combine(root, "apphost.log");
            string[] sensitive =
            [
                "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZWVkZWQifQ.seededsignature",
                "password=seeded-credential",
                "Authorization: Bearer seeded-access-token",
                "{\"customer\":\"seeded-private-payload\"}",
            ];
            ProcessStartInfo start = new(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
            start.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
            start.ArgumentList.Add(OperatingSystem.IsWindows()
                ? "echo %HEXALITH_MIRROR_SEED% & echo %HEXALITH_MIRROR_SEED% 1>&2"
                : "printf '%s\\n' \"$HEXALITH_MIRROR_SEED\" >&2; printf '%s\\n' \"$HEXALITH_MIRROR_SEED\"");
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.UseShellExecute = false;
            start.Environment["HEXALITH_MIRROR_SEED"] = string.Join(" ", sensitive);
            using Process process = new() { StartInfo = start };
            TaskCompletionSource<bool> stdoutDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> stderrDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    stdoutDone.SetResult(true);
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is null)
                {
                    stderrDone.SetResult(true);
                }
            };
            CompositionDiagnosticMirror.Attach(process, path);
            process.Start().ShouldBeTrue();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            string retained = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
            retained.ShouldContain("stdout");
            retained.ShouldContain("stderr");
            foreach (string value in sensitive)
            {
                retained.ShouldNotContain(value);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies minted tokens are HS256-signed with the run key and carry the requested claims.
    /// </summary>
    [Fact]
    public void TokensAreSignedWithTheRunKeyAndCarryClaims()
    {
        string key = CompositionSigningKey.Create();
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        string token = CompositionTokenFactory.Create(
            key,
            new CompositionTokenRequest("tester", ["tenant-a", "tenant-b"], ["p0-orders"], ["command:submit", "query:read"], true, TimeSpan.FromMinutes(5)),
            now);

        string[] parts = token.Split('.');
        parts.Length.ShouldBe(3);
        string expected = Encode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])));
        parts[2].ShouldBe(expected);
        using JsonDocument header = JsonDocument.Parse(Decode(parts[0]));
        header.RootElement.GetProperty("alg").GetString().ShouldBe("HS256");
        using JsonDocument payload = JsonDocument.Parse(Decode(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().ShouldBe(CompositionEnvironment.TokenIssuer);
        payload.RootElement.GetProperty("aud").GetString().ShouldBe(CompositionEnvironment.TokenAudience);
        payload.RootElement.GetProperty("sub").GetString().ShouldBe("tester");
        payload.RootElement.GetProperty("tenants").GetString().ShouldBe("tenant-a tenant-b");
        payload.RootElement.GetProperty("domains").GetString().ShouldBe("p0-orders");
        payload.RootElement.GetProperty("permissions").GetString().ShouldBe("command:submit query:read");
        payload.RootElement.GetProperty("role").GetString().ShouldBe("GlobalAdministrator");
        payload.RootElement.GetProperty("exp").GetInt64().ShouldBe(now.AddMinutes(5).ToUnixTimeSeconds());
    }

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }
}
