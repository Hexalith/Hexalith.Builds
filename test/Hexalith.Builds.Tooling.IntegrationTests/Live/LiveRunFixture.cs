// <copyright file="LiveRunFixture.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using Hexalith.Builds.Tooling.Runtime;

using Xunit;

/// <summary>
/// Starts one shared live run for the persistence, Dapr, identity, and FrontComposer proofs.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1515:Consider making public types internal", Justification = "xUnit class fixtures of public test classes must be public.")]
public sealed class LiveRunFixture : IAsyncLifetime
{
    private LiveEngineScope? _scope;

    /// <summary>
    /// Gets the ready session, when the live lane is enabled and the run started.
    /// </summary>
    internal CompositionRunSession? Session { get; private set; }

    /// <summary>
    /// Gets the start failure, when the run did not become ready.
    /// </summary>
    internal string? StartFailure { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!LiveGate.IsEnabled)
        {
            return;
        }

        _scope = new LiveEngineScope("shared");
        using CancellationTokenSource bound = new(TimeSpan.FromMinutes(8));
        CompositionStartResult result = await _scope.StartAsync(bound.Token).ConfigureAwait(false);
        Session = result.Session;
        StartFailure = result.Session is null ? CompositionDocumentStore.Serialize(result.Result) : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_scope is not null)
        {
            if (Session is not null)
            {
                using CancellationTokenSource bound = new(TimeSpan.FromMinutes(5));
                _ = await _scope.Engine.DownAsync(Session, bound.Token).ConfigureAwait(false);
            }

            await _scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the ready session or fails the calling test with the start diagnostics.
    /// </summary>
    /// <returns>The session.</returns>
    /// <exception cref="InvalidOperationException">The shared run did not become ready.</exception>
    internal CompositionRunSession RequireSession()
    {
        LiveGate.SkipUnlessEnabled();
        return Session ?? throw new InvalidOperationException("The shared live run did not become ready: " + StartFailure);
    }
}