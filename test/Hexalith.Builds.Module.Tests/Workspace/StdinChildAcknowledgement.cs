// <copyright file="StdinChildAcknowledgement.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

/// <summary>Exchanges one explicit ready token in the owned stdin control's fresh private directory.</summary>
internal static class StdinChildAcknowledgement
{
    /// <summary>Gets the child-only parent-bound token variable.</summary>
    internal static string TokenVariable => "HEXALITH_TEST_STDIN_READY_TOKEN";

    /// <summary>Publishes only after the child has completed its real readiness predicate.</summary>
    /// <param name="directory">The fresh parent-owned control directory.</param>
    /// <param name="token">The exact one-time parent token.</param>
    /// <param name="cancellationToken">The control cancellation.</param>
    /// <returns>The atomic acknowledgement write.</returns>
    internal static async Task PublishAsync(string directory, string token, CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(directory, "stdin-ready.tmp");
        await File.WriteAllTextAsync(temporary, token, cancellationToken).ConfigureAwait(true);
        File.Move(temporary, Path.Combine(directory, "stdin-ready.token"));
    }

    /// <summary>Waits for the exact dedicated record; telemetry and console output never signal readiness.</summary>
    /// <param name="directory">The fresh parent-owned control directory.</param>
    /// <param name="token">The exact one-time parent token.</param>
    /// <param name="cancellationToken">The single bounded control cancellation.</param>
    /// <exception cref="InvalidDataException">The dedicated record is linked, malformed or belongs to another parent.</exception>
    /// <returns>The explicit child acknowledgement.</returns>
    internal static async Task WaitAsync(string directory, string token, CancellationToken cancellationToken)
    {
        FileInfo file = new(Path.Combine(directory, "stdin-ready.token"));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Refresh();
            if (file.LinkTarget is not null)
            {
                throw new InvalidDataException("The stdin control acknowledgement must be an owned regular file.");
            }

            if (file.Exists)
            {
                if (file.Length != token.Length)
                {
                    throw new InvalidDataException("The stdin control acknowledgement has an invalid size.");
                }

                string actual = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(true);
                if (!string.Equals(actual, token, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The stdin control acknowledgement does not match its parent.");
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(true);
        }
    }
}
