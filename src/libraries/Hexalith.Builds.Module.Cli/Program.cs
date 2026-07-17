// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Hexalith.Builds.ModuleTool.Cli;

return await ModuleCommandApplication.InvokeAsync(
    args,
    Console.Out,
    Console.Error,
    CancellationToken.None).ConfigureAwait(false);