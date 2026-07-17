// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Hexalith.Builds.Evidence.Cli;
using Hexalith.Builds.Tooling.Diagnostics;

return await ToolCommandHost.RunWithConsoleCancellationAsync(cancellationToken =>
    EvidenceCommandApplication.InvokeAsync(
        args,
        Console.Out,
        Console.Error,
        cancellationToken)).ConfigureAwait(false);
