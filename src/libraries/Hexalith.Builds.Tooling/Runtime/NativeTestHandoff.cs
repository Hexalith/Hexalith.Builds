// <copyright file="NativeTestHandoff.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;

/// <summary>
/// The stable environment contract through which the runner hands its composed endpoints and identity to native tests.
/// </summary>
public static class NativeTestHandoff
{
    /// <summary>The runner invocation identity.</summary>
    public const string RunId = CompositionEnvironment.RunId;

    /// <summary>The primary EventStore endpoint.</summary>
    public const string EventStoreUrl = "HEXALITH_G4_EVENTSTORE_URL";

    /// <summary>The second EventStore instance endpoint, when the profile composes one.</summary>
    public const string PeerEventStoreUrl = "HEXALITH_G4_EVENTSTORE_PEER_URL";

    /// <summary>The FrontComposer UI endpoint, when the manifest declares a UI.</summary>
    public const string UiUrl = "HEXALITH_G4_UI_URL";

    /// <summary>The run-unique tenant.</summary>
    public const string Tenant = "HEXALITH_G4_TENANT";

    /// <summary>The run-unique resource namespace.</summary>
    public const string ResourceNamespace = "HEXALITH_G4_RESOURCE_NAMESPACE";

    /// <summary>The ordinal-sorted, comma-separated module domains.</summary>
    public const string Domains = "HEXALITH_G4_DOMAINS";

    /// <summary>The run-scoped development bearer token; never retained by the runner.</summary>
    public const string AccessToken = "HEXALITH_G4_ACCESS_TOKEN";

    /// <summary>The absolute path to the private, validated v1 environment descriptor.</summary>
    public const string TestEnvironmentPath = "HEXALITH_TEST_ENVIRONMENT_PATH";

    /// <summary>The explicitly supplied private endpoint credential file, separate from the v1 descriptor.</summary>
    public const string EndpointCredentialsPath = "HEXALITH_TEST_ENDPOINT_CREDENTIALS_PATH";

    /// <summary>The fixed owner-workspace file name for private endpoint credentials.</summary>
    public const string EndpointCredentialsFileName = "native-endpoint-credentials.json";

    /// <summary>The strict private channel version, independent of the credential-free v1 environment schema.</summary>
    public const string EndpointCredentialsSchema = "hexalith.test-endpoint-credentials.v1";

    /// <summary>
    /// Creates the handoff for one ready run. The signing key is never part of it.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <param name="readiness">The validated readiness document.</param>
    /// <param name="accessToken">The run-scoped bearer token.</param>
    /// <returns>The environment values.</returns>
    /// <exception cref="InvalidDataException">An active selected execution lacks its validated owned descriptor.</exception>
    public static IReadOnlyDictionary<string, string> Create(CompositionRunPlan plan, CompositionReadiness readiness, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readiness);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            [RunId] = plan.RunId,
            [EventStoreUrl] = readiness.EventStoreEndpoint.AbsoluteUri,
            [Tenant] = plan.TenantNamespace,
            [ResourceNamespace] = plan.ResourceNamespace,
            [Domains] = string.Join(',', plan.Modules.Select(module => module.Domain).Order(StringComparer.Ordinal)),
            [AccessToken] = accessToken,
        };
        if (readiness.NativeEndpointCredentialsPath is { } credentialPath)
        {
            if (credentialPath != Path.Combine(Path.GetFullPath(plan.Workspace), EndpointCredentialsFileName))
            {
                throw new InvalidDataException("The private endpoint credential channel must use the fixed owned workspace path.");
            }

            values[EndpointCredentialsPath] = credentialPath;
        }

        if (plan.Ports.SecondEventStoreHttp is int peer)
        {
            values[PeerEventStoreUrl] = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{peer}/");
        }

        if (readiness.UiEndpoint is Uri ui)
        {
            values[UiUrl] = ui.AbsoluteUri;
        }

        if (plan.SelectedTest is { } selected)
        {
            if (plan.ExecutionTiming is not null)
            {
                string descriptorPath = Path.GetFullPath(Path.Combine(plan.Workspace, "test-environment.json"));
                TestEnvironment.TestEnvironmentContractReadResult descriptor = TestEnvironment.TestEnvironmentContractReader.ReadDescriptor(descriptorPath);
                if (!descriptor.IsValid || descriptor.Document!.Value.GetProperty("bindings").GetProperty("run").GetProperty("runId").GetString() != plan.RunId)
                {
                    throw new InvalidDataException("A validated descriptor for the owned execution is required.");
                }

                values[TestEnvironmentPath] = descriptorPath;
            }

            values[Domains] = readiness.NativeTestScope is { } nativeScope ? string.Join(',', nativeScope.Domains.Order(StringComparer.Ordinal)) : selected.ModuleId;
            values["HEXALITH_G4_TEST_NAME"] = selected.Name;
            values["HEXALITH_G4_TEST_OWNER"] = selected.ModuleId;
            values["HEXALITH_G4_TEST_MANIFEST_HASH"] = selected.ManifestHash;
            values["HEXALITH_G4_TEST_ENTRY_HASH"] = selected.EntryHash;
            values["HEXALITH_G4_TEST_LIFETIME_SECONDS"] = selected.ExecutionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            values["HEXALITH_G4_TEST_STOP_SECONDS"] = selected.StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            values["HEXALITH_G4_EXECUTION_DEADLINE_UTC"] = (plan.ExecutionTiming?.DeadlineUtc ?? selected.ExecutionDeadlineUtc).ToString("O", CultureInfo.InvariantCulture);
            values["HEXALITH_G4_CREDENTIAL_EXPIRY_UTC"] = (plan.ExecutionTiming?.CredentialExpiryUtc ?? selected.CredentialExpiryUtc).ToString("O", CultureInfo.InvariantCulture);
            values["HEXALITH_G4_TEST_MODE"] = selected.Mode;
            if (selected.DaprRuntimeVersion is { } runtime)
            {
                values["HEXALITH_G4_DAPR_RUNTIME_VERSION"] = runtime;
            }

            values["HEXALITH_G4_PLAN_PATH"] = Path.Combine(plan.Workspace, "plan.json");
            values["HEXALITH_G4_READINESS_PATH"] = plan.ReadinessPath;
            values["HEXALITH_G4_SOURCE_MAPPING_PATH"] = Path.Combine(plan.Workspace, "source-mapping", "mapping.json");
        }

        return values;
    }
}
