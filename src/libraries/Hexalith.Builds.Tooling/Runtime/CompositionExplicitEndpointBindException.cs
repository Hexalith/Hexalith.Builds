// <copyright file="CompositionExplicitEndpointBindException.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;

/// <summary>Identifies an explicit EventStore endpoint the local runner could not bind.</summary>
/// <param name="port">The requested loopback port.</param>
/// <param name="innerException">The original socket error and any rollback error.</param>
internal sealed class CompositionExplicitEndpointBindException(int port, Exception innerException)
    : IOException(string.Create(CultureInfo.InvariantCulture, $"The requested EventStore endpoint 127.0.0.1:{port} could not be bound."), innerException)
{
}