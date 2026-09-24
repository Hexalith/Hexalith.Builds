// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

if (args.Length > 0 && string.Equals(args[0], "--descriptor-child", StringComparison.Ordinal))
{
    return await ToolCommandHost.RunWithConsoleCancellationAsync(cancellationToken =>
        DescriptorChildWorker.RunAsync(args[1..], Console.Out, cancellationToken)).ConfigureAwait(false);
}

using StringWriter bufferedOutput = new();
int exitCode = await ToolCommandHost.RunWithConsoleCancellationAsync(cancellationToken =>
    ModuleCommandApplication.InvokeAsync(
        args,
        bufferedOutput,
        Console.Error,
        cancellationToken,
        typeof(ModuleCommandApplication).Assembly.Location)).ConfigureAwait(false);
if (bufferedOutput.GetStringBuilder().Length > 0)
{
    await Console.Out.WriteAsync(bufferedOutput.ToString()).ConfigureAwait(false);
}
else if (exitCode == (int)ToolExitCode.Cancelled)
{
    ToolDiagnostic diagnostic = new("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The invocation was cancelled.", "cancellation");
    ToolCommandResult result = new(
        "cancelled",
        ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, diagnostic.RuleId, ToolExitCode.Cancelled),
        [diagnostic]);
    await ToolDiagnosticFormatter.WriteAsync(Console.Out, result, ToolCommandHost.RequestedOutputFormat(args), CancellationToken.None).ConfigureAwait(false);
}

return exitCode;
