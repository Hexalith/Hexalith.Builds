// <copyright file="SupportedPlatformPinsCatalogTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json.Nodes;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Verifies the embedded catalog is strict and every pin facade reads its selections.</summary>
public sealed class SupportedPlatformPinsCatalogTests
{
    /// <summary>Verifies the catalog defaults and embedded package facades remain aligned.</summary>
    [Fact]
    public void CatalogDefaultsMatchSupportedPlatformPins()
    {
        XDocument catalog = XDocument.Load(Path.Combine(CompositionTestFiles.RepositoryRoot(), "Props", "Directory.Packages.props"));
        catalog.Descendants("HexalithEventStoreVersion").Single().Value.Trim().ShouldBe(SupportedPlatformPins.EventStoreVersion);
        catalog.Descendants("HexalithFrontComposerVersion").Single().Value.Trim().ShouldBe(SupportedPlatformPins.FrontComposerVersion);
        catalog.Descendants("HexalithAspireAppHostSdkVersion").Single().Value.Trim().ShouldBe(CompositionToolchainPins.AspireAppHostSdkVersion);
        catalog.Descendants("HexalithDaprRuntimeVersion").Single().Value.Trim().ShouldBe(SupportedPlatformPins.DaprRuntimeVersion);
        catalog.Descendants("HexalithDaprCliVersion").Single().Value.Trim().ShouldBe(CompositionToolchainPins.DaprCliVersion);
    }

    /// <summary>Verifies both facades read the same embedded catalog and data records keep value equality.</summary>
    [Fact]
    public void FacadesUseEmbeddedCatalog()
    {
        PlatformVersionCatalog catalog = PlatformVersionCatalog.Current;
        SupportedPlatformPins.EventStoreVersion.ShouldBe(catalog.EventStoreVersion);
        SupportedPlatformPins.FrontComposerVersion.ShouldBe(catalog.FrontComposerVersion);
        SupportedPlatformPins.DaprSdkVersion.ShouldBe(catalog.DaprSdkVersion);
        SupportedPlatformPins.DaprRuntimeVersion.ShouldBe(catalog.DaprRuntimeVersion);
        CompositionToolchainPins.DaprCliVersion.ShouldBe(catalog.DaprCliVersion);
        CompositionToolchainPins.AspireAppHostSdkVersion.ShouldBe(catalog.AspireAppHostSdkVersion);
        CompositionToolchainPins.RedisImageReference.ShouldBe($"{catalog.RedisImage}:{catalog.RedisImageTag}@{catalog.RedisImageDigest}");
        PlatformVersionCatalog.Parse(Snapshot()).ShouldBe(catalog);
        (catalog with { EventStoreVersion = "0.0.1" }).ShouldNotBe(catalog);
    }

    /// <summary>Verifies required snapshot fields fail without a fallback.</summary>
    /// <param name="field">The field to remove.</param>
    [Theory]
    [InlineData("eventStoreVersion")]
    [InlineData("frontComposerVersion")]
    [InlineData("daprSdkVersion")]
    [InlineData("aspireHostingVersion")]
    [InlineData("aspireHostingDaprVersion")]
    [InlineData("aspireAppHostSdkVersion")]
    [InlineData("daprRuntimeVersion")]
    [InlineData("daprCliVersion")]
    [InlineData("redisImage")]
    [InlineData("redisImageTag")]
    [InlineData("redisImageDigest")]
    [InlineData("eventStoreHostingDaprRange")]
    public void MissingSelectionNamesField(string field)
    {
        JsonObject snapshot = JsonNode.Parse(Snapshot())!.AsObject();
        snapshot.Remove(field).ShouldBeTrue();
        Should.Throw<InvalidDataException>(() => PlatformVersionCatalog.Parse(snapshot.ToJsonString())).Message.ShouldContain(field);
    }

    /// <summary>Verifies duplicate catalog keys fail even when their values agree.</summary>
    [Fact]
    public void DuplicateSelectionNamesField()
    {
        string duplicate = Snapshot().Replace("{", "{\"daprSdkVersion\":\"0.0.1\",", StringComparison.Ordinal);
        Should.Throw<InvalidDataException>(() => PlatformVersionCatalog.Parse(duplicate)).Message.ShouldContain("daprSdkVersion");
    }

    /// <summary>Verifies malformed version, image and dependency fields fail.</summary>
    /// <param name="field">The selected field.</param>
    /// <param name="value">The malformed value.</param>
    [Theory]
    [InlineData("daprSdkVersion", "bad")]
    [InlineData("aspireAppHostSdkVersion", "")]
    [InlineData("redisImage", "image with spaces")]
    [InlineData("redisImage", "Docker.io/library/redis")]
    [InlineData("redisImage", "docker.io//library/redis")]
    [InlineData("redisImage", "/redis")]
    [InlineData("redisImage", "redis/")]
    [InlineData("redisImage", "docker.io/library/redis..bad")]
    [InlineData("redisImage", "docker.io/library/redis___bad")]
    [InlineData("redisImage", "docker.io/library/redis.-bad")]
    [InlineData("redisImage", "docker.io/library/redis.")]
    [InlineData("daprSdkVersion", "1.2.3-beta.01")]
    [InlineData("daprSdkVersion", "1.2.3.bad")]
    [InlineData("eventStoreHostingDaprRange", "[14.0.0,13.0.0]")]
    [InlineData("eventStoreHostingDaprRange", "[bad,14)")]
    [InlineData("redisImageTag", "bad tag")]
    [InlineData("redisImageDigest", "sha256:bad")]
    [InlineData("eventStoreHostingDaprRange", "bad")]
    [InlineData("unexpectedField", "value")]
    [InlineData("schemaVersion", "2")]
    public void MalformedSelectionFails(string field, string value)
    {
        JsonObject snapshot = JsonNode.Parse(Snapshot())!.AsObject();
        snapshot[field] = value;
        Should.Throw<InvalidDataException>(() => PlatformVersionCatalog.Parse(snapshot.ToJsonString())).Message.ShouldContain(field);
    }

    /// <summary>Accepts Docker repository separators in offline snapshots.</summary>
    /// <param name="image">The valid repository name.</param>
    [Theory]
    [InlineData("docker.io/library/redis.good")]
    [InlineData("docker.io/library/redis_good")]
    [InlineData("docker.io/library/redis__good")]
    [InlineData("docker.io/library/redis---good")]
    public void ValidDockerSeparatorsLoadOffline(string image)
    {
        JsonObject snapshot = JsonNode.Parse(Snapshot())!.AsObject();
        snapshot["redisImage"] = image;
        PlatformVersionCatalog.Parse(snapshot.ToJsonString()).RedisImage.ShouldBe(image);
    }

    /// <summary>Accepts valid NuGet dependency ranges in offline snapshots.</summary>
    /// <param name="range">The NuGet range spelling.</param>
    /// <param name="toolkit">The compatible fixed synthetic Toolkit selection.</param>
    [Theory]
    [InlineData("13.*", "13.6.0")]
    [InlineData("[13,14)", "13.6.0")]
    [InlineData("[13.5.1-beta.767]", "13.5.1-beta.767")]
    [InlineData("[13.5.1-beta.767,13.6.0)", "13.5.1-beta.767")]
    public void ValidNuGetRangesLoadOffline(string range, string toolkit)
    {
        JsonObject snapshot = JsonNode.Parse(Snapshot())!.AsObject();
        snapshot["eventStoreHostingDaprRange"] = range;
        snapshot["aspireHostingDaprVersion"] = toolkit;
        PlatformVersionCatalog.Parse(snapshot.ToJsonString()).EventStoreHostingDaprRange.ShouldBe(range);
    }

    /// <summary>Rejects a serialized selected Toolkit outside the EventStore dependency range.</summary>
    [Fact]
    public void IncompatibleSerializedHostingPairFailsOffline()
    {
        JsonObject snapshot = JsonNode.Parse(Snapshot())!.AsObject();
        snapshot["eventStoreVersion"] = "3.109.0";
        snapshot["aspireHostingDaprVersion"] = "13.5.1-beta.766";
        snapshot["eventStoreHostingDaprRange"] = "[13.5.1-beta.767,13.6.0)";
        string diagnostic = Should.Throw<InvalidDataException>(() => PlatformVersionCatalog.Parse(snapshot.ToJsonString())).Message;
        diagnostic.ShouldContain("Hexalith.EventStore.Aspire/3.109.0");
        diagnostic.ShouldContain("CommunityToolkit.Aspire.Hosting.Dapr/13.5.1-beta.766");
        diagnostic.ShouldContain("[13.5.1-beta.767,13.6.0)");
    }

    /// <summary>Verifies release/prerelease identity equality ignores build metadata.</summary>
    [Fact]
    public void VersionIdentityIgnoresOnlyBuildMetadata()
    {
        PlatformVersionCatalog.VersionsEqual("13.6.0-preview.1+first", "13.6.0-preview.1+second").ShouldBeTrue();
        PlatformVersionCatalog.VersionsEqual("13.6.0-preview.1", "13.6.0").ShouldBeFalse();
        PlatformVersionCatalog.VersionsEqual("malformed", "13.6.0").ShouldBeFalse();
        PlatformVersionCatalog.VersionsEqual("13.6.0-beta.01", "13.6.0-beta.1").ShouldBeFalse();
    }

    /// <summary>Verifies hints are derived from the snapshot.</summary>
    [Fact]
    public void RuntimeHintUsesCatalogSelections()
    {
        ModuleManifest manifest = new(
            "hexalith.module-manifest.v1",
            "tests",
            [],
            new PlatformPins(SupportedPlatformPins.EventStoreVersion, "0.0.1", "0.0.1", SupportedPlatformPins.FrontComposerVersion),
            null,
            new Dictionary<string, ModuleProfile>());
        RuntimePrerequisiteCheck result = RuntimePrerequisiteGate.Check(manifest);
        _ = result.Diagnostic.ShouldNotBeNull();
        _ = result.Diagnostic.Hint.ShouldNotBeNull();
        result.Diagnostic.Hint.ShouldContain(SupportedPlatformPins.DaprRuntimeVersion);
        result.Diagnostic.Hint.ShouldContain(SupportedPlatformPins.DaprSdkVersion);
    }

    private static string Snapshot()
    {
        using Stream stream = typeof(PlatformVersionCatalog).Assembly.GetManifestResourceStream("Hexalith.Builds.Tooling.Manifest.platform-version-catalog.json")!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
