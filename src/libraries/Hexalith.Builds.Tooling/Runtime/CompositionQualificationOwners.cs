// <copyright file="CompositionQualificationOwners.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Validates and authenticates explicit additive qualification owner requests.</summary>
public static class CompositionQualificationOwners
{
    private const string _refusal = "Qualification owner selection is malformed, unsupported, or does not match this run.";

    /// <summary>Returns a defensive, canonically ordered set of known reference owners.</summary>
    /// <param name="owners">The explicit additional owners; null means no expansion.</param>
    /// <returns>The immutable canonical set.</returns>
    /// <exception cref="InvalidDataException">An owner is unknown, malformed or repeated.</exception>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? owners)
    {
        if (owners is null || owners.Count == 0)
        {
            return (ImmutableArray<string>)[];
        }

        ValidateOwners(owners);
        return Canonical(owners);
    }

    /// <summary>Binds an expanded request to its issued run, source and composition identities.</summary>
    /// <param name="plan">The complete plan metadata.</param>
    /// <param name="signingKey">The existing per-run in-memory signing key.</param>
    /// <returns>The plan with a purpose-bound authentication tag, or the unchanged default plan.</returns>
    /// <exception cref="InvalidDataException">The expanded plan is incomplete or unsupported.</exception>
    public static CompositionRunPlan Bind(CompositionRunPlan plan, string signingKey)
    {
        ArgumentNullException.ThrowIfNull(plan);
        IReadOnlyList<string> owners = Normalize(plan.QualificationOwners);
        if (owners.Count == 0)
        {
            return plan.QualificationOwnersBinding is null ? plan : throw new InvalidDataException(_refusal);
        }

        ValidateContext(plan);
        CompositionRunPlan canonical = plan with { QualificationOwners = owners };
        return canonical with { QualificationOwnersBinding = Convert.ToHexStringLower(Tag(canonical, signingKey)) };
    }

    /// <summary>Verifies expansion before the AppHost performs composition or starts resources.</summary>
    /// <param name="plan">The received plan.</param>
    /// <param name="signingKey">The per-run key supplied privately by the parent.</param>
    /// <param name="required">Whether the legitimate parent launch requires owner expansion.</param>
    /// <exception cref="InvalidDataException">The request was removed, modified, replayed or not authenticated.</exception>
    public static void Verify(CompositionRunPlan plan, string? signingKey, bool required)
    {
        ArgumentNullException.ThrowIfNull(plan);
        IReadOnlyList<string> owners = Normalize(plan.QualificationOwners);
        if (owners.Count == 0 && plan.QualificationOwnersBinding is null && !required)
        {
            return;
        }

        ValidateContext(plan);
        if (owners.Count == 0 || plan.QualificationOwnersBinding is not { Length: 64 } binding
            || !owners.SequenceEqual(plan.QualificationOwners!) || string.IsNullOrEmpty(signingKey))
        {
            throw new InvalidDataException(_refusal);
        }

        byte[] expected = Tag(plan, signingKey);
        if (binding.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(_refusal);
        }

        byte[] actual = Convert.FromHexString(binding);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidDataException(_refusal);
        }
    }

    private static void ValidateOwners(IReadOnlyList<string> owners)
    {
        if (owners.Count > 3 || owners.Any(owner => owner is not ("eventstore" or "tenants" or "memories"))
            || owners.Distinct(StringComparer.Ordinal).Count() != owners.Count)
        {
            throw new InvalidDataException(_refusal);
        }
    }

    private static ImmutableArray<string> Canonical(IReadOnlyList<string> owners) => [.. owners.Order(StringComparer.Ordinal)];

    private static void ValidateContext(CompositionRunPlan plan)
    {
        if (!plan.IsPlatformComposition || plan.SelectedTest is null || plan.CandidatePolicy is not null
            || !CompositionRunPlanFactory.IsRunId(plan.RunId) || !CompositionRunPlanFactory.IsRunId(plan.AttemptId)
            || !CompositionRunPlanFactory.IsRunId(plan.EnvironmentId) || string.IsNullOrWhiteSpace(plan.PlatformActiveModule)
            || string.IsNullOrWhiteSpace(plan.SourceMappingHash) || plan.PlatformIdentity is null
            || string.IsNullOrWhiteSpace(plan.CompositionContentIdentity) || string.IsNullOrWhiteSpace(plan.PlatformDeclarationHash))
        {
            throw new InvalidDataException(_refusal);
        }
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != 48)
        {
            throw new InvalidDataException(_refusal);
        }
    }

    private static byte[] Tag(CompositionRunPlan plan, string signingKey)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(signingKey);
        }
        catch (FormatException)
        {
            throw new InvalidDataException(_refusal);
        }

        try
        {
            ValidateKey(key);
            return HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(new
            {
                purpose = "hexalith.qualification-owners.v1",
                plan.Schema,
                plan.RunId,
                plan.AttemptId,
                plan.EnvironmentId,
                plan.PlatformActiveModule,
                plan.QualificationOwners,
                plan.PlatformDeclarationPaths,
                plan.PlatformDeclarationHash,
                plan.SourceMappingHash,
                plan.PlatformIdentity,
                plan.CompositionContentIdentity,
                plan.SelectedTest,
                plan.SelectedTests,
            }));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
