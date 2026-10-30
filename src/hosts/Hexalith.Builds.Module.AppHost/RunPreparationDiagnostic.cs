// <copyright file="RunPreparationDiagnostic.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using System.Globalization;
using System.Reflection;

/// <summary>Preserves the failed startup stage while exporting only bounded diagnostic identity metadata.</summary>
internal static class RunPreparationDiagnostic
{
    /// <summary>Formats exception type, fixed stage and a validated CLR assembly name without messages or paths.</summary>
    /// <param name="failure">The actual failure, retained only in process memory.</param>
    /// <param name="stage">The closed runner stage identifier.</param>
    /// <returns>The support-safe startup failure status.</returns>
    internal static string SupportSafeStatus(Exception failure, string stage)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentOutOfRangeException.ThrowIfNotEqual(stage is "writer-cutover" or "owner-preparation", true);
        string status = stage == "writer-cutover" && failure is TimeoutException or InvalidOperationException && IsKnownCutoverStatus(failure.Message)
            ? failure.Message
            : "fault-" + failure.GetType().Name;
        status += ";stage=" + stage;
        if (failure is FileNotFoundException { FileName: { } fileName } && fileName.Contains(", Version=", StringComparison.Ordinal))
        {
            try
            {
                string? name = new AssemblyName(fileName).Name;
                if (name is { Length: > 0 and <= 128 } && char.IsAsciiLetter(name[0])
                    && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_'))
                {
                    status += ";assembly=" + name;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FileLoadException)
            {
                // Arbitrary filenames and malformed identities have no exportable assembly identity.
            }
        }

        return status;
    }

    private static bool IsKnownCutoverStatus(string value)
    {
        if (value is "cutover-timeout;endpoint-unallocated" or "cutover-timeout;endpoint-unavailable"
            or "cutover-timeout;health-body-incomplete" or "cutover-timeout;health-request-timeout")
        {
            return true;
        }

        string? prefix = null;
        if (value.StartsWith("cutover-timeout;health-HTTP-", StringComparison.Ordinal))
        {
            prefix = "cutover-timeout;health-HTTP-";
        }
        else if (value.StartsWith("cutover-activation-HTTP-", StringComparison.Ordinal))
        {
            prefix = "cutover-activation-HTTP-";
        }

        return prefix is not null && value.Length == prefix.Length + 3
            && int.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int status)
            && status is >= 100 and <= 599;
    }
}
