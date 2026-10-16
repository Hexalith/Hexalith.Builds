// <copyright file="CompositionStartupDeadlineResolver.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;
using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Selects the effective startup deadline from module declarations.
/// </summary>
public static class CompositionStartupDeadlineResolver
{
    /// <summary>
    /// Resolves the default deadline, or the largest finite declared override.
    /// </summary>
    /// <param name="declarations">The validated modules, or null for a v1 manifest.</param>
    /// <param name="fallback">The deadline used when no module declares a finite override.</param>
    /// <returns>The effective deadline. Non-finite and non-positive overrides are ignored.</returns>
    public static CompositionEffectiveDeadline Resolve(IReadOnlyList<PlatformModuleDeclaration>? declarations, TimeSpan fallback)
    {
        double fallbackSeconds = double.IsFinite(fallback.TotalSeconds) && fallback.TotalSeconds > 0
            ? fallback.TotalSeconds
            : SupportedPlatformManifestSchemas.DefaultStartupTimeoutSeconds;
        (double Seconds, string ModuleId, string Justification)? selected = null;
        if (declarations is not null)
        {
            foreach (PlatformModuleDeclaration declaration in declarations)
            {
                if (!TryReadOverride(declaration.Declaration, out double seconds, out string moduleId, out string justification))
                {
                    continue;
                }

                if (selected is null || seconds > selected.Value.Seconds)
                {
                    selected = (seconds, moduleId, justification);
                }
            }
        }

        return selected is { } winner
            ? new CompositionEffectiveDeadline(winner.Seconds, true, winner.Justification, winner.ModuleId)
            : new CompositionEffectiveDeadline(fallbackSeconds, false, null, null);
    }

    /// <summary>Formats the deadline for a bounded diagnostic, without a log dump.</summary>
    /// <param name="deadline">The effective deadline.</param>
    /// <returns>A short deadline clause.</returns>
    public static string Describe(CompositionEffectiveDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        string seconds = deadline.TimeoutSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        if (!deadline.IsOverride)
        {
            return "deadline='" + seconds + "s'";
        }

        string module = string.IsNullOrWhiteSpace(deadline.ModuleId) ? "-" : deadline.ModuleId;
        string text = "deadline='" + seconds + "s' override module='" + module + "'";
        if (!string.IsNullOrWhiteSpace(deadline.Justification))
        {
            text += " justification='" + Bound(deadline.Justification) + "'";
        }

        return text;
    }

    /// <summary>Reports whether a declared duration is positive, finite, and convertible to a <see cref="TimeSpan"/>.</summary>
    /// <param name="seconds">The declared duration in seconds.</param>
    /// <returns>True when <see cref="TimeSpan.FromSeconds(double)"/> accepts the value.</returns>
    internal static bool IsRepresentableTimeout(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return false;
        }

        try
        {
            _ = TimeSpan.FromSeconds(seconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadOverride(JsonElement module, out double seconds, out string moduleId, out string justification)
    {
        seconds = 0;
        moduleId = string.Empty;
        justification = string.Empty;
        if (module.ValueKind != JsonValueKind.Object
            || !module.TryGetProperty("lifecycle", out JsonElement lifecycle)
            || lifecycle.ValueKind != JsonValueKind.Object
            || !lifecycle.TryGetProperty("startup", out JsonElement startup)
            || startup.ValueKind != JsonValueKind.Object
            || !startup.TryGetProperty("override", out JsonElement startupOverride)
            || startupOverride.ValueKind != JsonValueKind.Object
            || !startupOverride.TryGetProperty("timeoutSeconds", out JsonElement timeout)
            || timeout.ValueKind != JsonValueKind.Number
            || !timeout.TryGetDouble(out seconds)
            || !IsRepresentableTimeout(seconds)
            || !module.TryGetProperty("identity", out JsonElement identity)
            || identity.ValueKind != JsonValueKind.Object
            || !identity.TryGetProperty("moduleId", out JsonElement moduleElement)
            || moduleElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(moduleElement.GetString()))
        {
            return false;
        }

        moduleId = moduleElement.GetString()!;
        if (startupOverride.TryGetProperty("justification", out JsonElement justificationElement)
            && justificationElement.ValueKind == JsonValueKind.String)
        {
            justification = justificationElement.GetString() ?? string.Empty;
        }

        return true;
    }

    private static string Bound(string value)
    {
        string safe = new([.. value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/' or '.' or ':' or ';' or '=' or ' ')
            .Take(120),
        ]);
        return string.IsNullOrWhiteSpace(safe) ? "-" : safe;
    }
}
