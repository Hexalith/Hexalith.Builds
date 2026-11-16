// <copyright file="ManagedBuildAuthorityVerifier.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Runtime.ExceptionServices;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Uses package-owned Python perimeter code to authenticate fixed delivered state.</summary>
internal static class ManagedBuildAuthorityVerifier
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Authenticates acceptance independently of a caller-authored receipt.</summary>
    /// <param name="authority">The locally bound receipt.</param>
    /// <param name="authorityPath">The closed authority file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The completed accepted-delivery verification.</returns>
    /// <exception cref="InvalidDataException">The packaged verifier is unavailable or refuses authority.</exception>
    /// <exception cref="IOException">The private verifier workspace cannot be removed.</exception>
    internal static async Task VerifyAsync(ManagedBuildAuthority authority, string authorityPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityPath);
        string temporary = Path.Combine(Path.GetTempPath(), "hexalith-managed-authority-" + Guid.NewGuid().ToString("N"));
        CompositionWorkspace.CreatePrivateDirectory(temporary);
        Exception? failure = null;
        try
        {
            string scripts = Path.Combine(temporary, "Github", "lib");
            string schemas = Path.Combine(temporary, "schemas");
            _ = Directory.CreateDirectory(scripts);
            _ = Directory.CreateDirectory(schemas);
            foreach (string name in new[] { "module_authority.py", "ci_config.py", "expected_state.py", "perimeter.py", "perimeter_config.py", "hexalith.ci.v1.json", "hexalith.perimeter.v1.json" })
            {
                Stream resource = typeof(ManagedBuildAuthorityVerifier).Assembly.GetManifestResourceStream("Hexalith.Builds.Tooling.ManagedAuthority." + name)
                    ?? throw new InvalidDataException("Packaged managed authority verifier is unavailable.");
                await using (resource.ConfigureAwait(false))
                {
                    string path = Path.Combine(name.EndsWith(".json", StringComparison.Ordinal) ? schemas : scripts, name);
                    FileStream target = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await using (target.ConfigureAwait(false))
                    {
                        await resource.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            string interpreter = OperatingSystem.IsWindows() ? "python" : "python3";
            string sealedAuthority = Path.Combine(temporary, "authority.json");
            await File.WriteAllTextAsync(sealedAuthority, JsonSerializer.Serialize(authority, _jsonOptions), cancellationToken).ConfigureAwait(false);
            CompositionProcessResult result = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(interpreter, ["-I", Path.Combine(scripts, "module_authority.py"), "--authority", sealedAuthority], authority.ModuleRoot, null),
                TimeSpan.FromMinutes(5),
                cancellationToken).ConfigureAwait(false);
            if (!result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0)
            {
                throw new InvalidDataException("Accepted delivery could not be authenticated by the packaged fixed-transport perimeter verifier.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }

        if (!await CompositionWorkspace.TryDeleteAsync(temporary, CancellationToken.None).ConfigureAwait(false))
        {
            throw new IOException("Managed authority verifier scratch could not be removed.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
