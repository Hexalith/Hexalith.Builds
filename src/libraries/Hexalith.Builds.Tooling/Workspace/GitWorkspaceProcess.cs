// <copyright file="GitWorkspaceProcess.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Runs bounded Git inspections and recorded-gitlink submodule operations.</summary>
internal static class GitWorkspaceProcess
{
    /// <summary>Runs Git without inherited recursive-submodule configuration.</summary>
    /// <param name="directory">The Git working directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="arguments">The Git arguments.</param>
    /// <returns>The bounded process result.</returns>
    internal static Task<CompositionProcessResult> RunAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        string[] command = ["-c", "submodule.recurse=false", "-C", directory, .. arguments];
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        string[] transportVariables =
        [
            "GIT_ALLOW_PROTOCOL",
            "GIT_CONFIG_COUNT",
            "GIT_CONFIG_GLOBAL",
            "GIT_CONFIG_NOSYSTEM",
            "GIT_CONFIG_PARAMETERS",
            "GIT_CONFIG_SYSTEM",
            "GIT_SSH",
            "GIT_SSH_COMMAND",
            "GIT_ASKPASS",
            "GIT_TERMINAL_PROMPT",
            "XDG_CONFIG_HOME",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "http_proxy", "https_proxy", "all_proxy", "no_proxy",
            "GIT_HTTP_PROXY_AUTHMETHOD", "GIT_PROXY_COMMAND",
            "GIT_SSL_CAINFO", "GIT_SSL_CAPATH", "GIT_SSL_NO_VERIFY", "GIT_SSL_CERT", "GIT_SSL_KEY",
            "SSH_AUTH_SOCK",
            "SSH_ASKPASS",
            "SSH_ASKPASS_REQUIRE",
        ];
        foreach (string variable in transportVariables)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
            {
                environment[variable] = value;
            }
        }

        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            string name = (string)variable.Key;
            if ((name.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal)
                || name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal))
                && variable.Value is string value)
            {
                environment[name] = value;
            }
        }

        return CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("git", command, directory, environment),
            TimeSpan.FromMinutes(2),
            cancellationToken);
    }
}
