// <copyright file="CompositionProcessStartFailure.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Retains finite launch failure metadata without exception text or process inputs.</summary>
/// <param name="Kind">The bounded launch failure classification.</param>
/// <param name="NativeErrorCode">The native error code, only for a native launch exception.</param>
public sealed record CompositionProcessStartFailure(CompositionProcessStartFailureKind Kind, int? NativeErrorCode);