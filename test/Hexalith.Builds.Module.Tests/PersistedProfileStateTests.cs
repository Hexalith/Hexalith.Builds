// <copyright file="PersistedProfileStateTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies that the public persisted executor rejects incomplete and conflicting physical state.
/// </summary>
public sealed class PersistedProfileStateTests
{
    private static readonly PersistedProfileModule _module = new("p0-orders", "PlaceOrder", "OrderPlaced", "p0-orders-summary", 3, 5);

    /// <summary>Checks the supported full fixture can be loaded only without an external filter.</summary>
    [Fact]
    public void FullFixtureLoadsOnlyForItsDeclaredProfile()
    {
        string manifestPath = Path.Combine(RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        ModuleManifest manifest = ModuleManifestLoader.Load(manifestPath).Manifest.ShouldNotBeNull();

        PersistedProfileLoader.TryLoad(manifest, manifestPath, "full", null).ShouldNotBeNull().Modules.Count.ShouldBe(2);
        PersistedProfileLoader.TryLoad(manifest, manifestPath, "live", null).ShouldBeNull();
        PersistedProfileLoader.TryLoad(manifest, manifestPath, "full", "name~OnlyOne").ShouldBeNull();
    }

    /// <summary>Checks event, metadata, and projection state must all agree.</summary>
    [Fact]
    public void PhysicalSnapshotRejectsAbsentStaleWrongSequenceAndTenantState()
    {
        JsonElement expectedEvent = Event("tenant-a", "p0-orders", "aggregate-a", "OrderPlaced", 1, 3);
        JsonElement expectedProjection = Projection("tenant-a", "aggregate-a", 1, 3);

        Matches(expectedEvent, expectedProjection, 1).ShouldBeTrue();
        Matches(null, expectedProjection, 1).ShouldBeFalse();
        Matches(expectedEvent, null, 1).ShouldBeFalse();
        Matches(expectedEvent, expectedProjection, null).ShouldBeFalse();
        Matches(expectedEvent, expectedProjection, 2).ShouldBeFalse();
        Matches(expectedEvent, Projection("tenant-a", "aggregate-a", 0, 0), 1).ShouldBeFalse();
        Matches(expectedEvent, Projection("tenant-b", "aggregate-a", 1, 3), 1).ShouldBeFalse();
        Matches(Event("tenant-b", "p0-orders", "aggregate-a", "OrderPlaced", 1, 3), expectedProjection, 1).ShouldBeFalse();
        Matches(Event("tenant-a", "p0-orders", "aggregate-a", "Unexpected", 1, 3), expectedProjection, 1).ShouldBeFalse();
        Matches(Event("tenant-a", "p0-orders", "aggregate-a", "OrderPlaced", 2, 3), expectedProjection, 1).ShouldBeFalse();
    }

    private static bool Matches(JsonElement? eventState, JsonElement? projection, long? currentSequence) =>
        PersistedProfileExecutor.StateMatches(eventState, projection, currentSequence, _module, "tenant-a", "aggregate-a", 1, 3);

    private static JsonElement Event(string tenant, string domain, string aggregateId, string eventType, int sequence, int quantity) =>
        JsonSerializer.SerializeToElement(new
        {
            eventTypeName = eventType,
            sequenceNumber = sequence,
            tenantId = tenant,
            domain,
            aggregateId,
            payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { Quantity = quantity, Sequence = sequence })),
        });

    private static JsonElement Projection(string tenant, string aggregateId, int sequence, int total) =>
        JsonSerializer.SerializeToElement(new { TenantId = tenant, AggregateId = aggregateId, Sequence = sequence, Count = sequence, TotalQuantity = total });

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hexalith.Builds.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The Builds repository root was not found.");
    }
}
