// <copyright file="CompositionWorkspace.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Creates and removes runner-owned directories.
/// </summary>
public static class CompositionWorkspace
{
    /// <summary>
    /// Gets the default per-user root for run workspaces.
    /// </summary>
    /// <returns>The directory path.</returns>
    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "hexalith-builds",
        "g4-workspaces");

    /// <summary>
    /// Creates a directory readable only by the current user where the platform supports it.
    /// </summary>
    /// <param name="path">The absolute directory path.</param>
    public static void CreatePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(path);
            return;
        }

        _ = Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Removes a directory tree with bounded retries.
    /// </summary>
    /// <param name="path">The directory path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the directory no longer exists.</returns>
    public static async Task<bool> TryDeleteAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        return !Directory.Exists(path);
    }
}