// <copyright file="CheckSuiteCommands.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Cli;

using System.CommandLine;
using System.Text.Json;

using Hexalith.Builds.Tooling.CheckSuites;
using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>Exposes static suite packaging, validation and publication coverage commands.</summary>
internal static class CheckSuiteCommands
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Creates the check-suite command group.</summary>
    /// <param name="writer">The sanitized output destination.</param>
    /// <returns>The command group.</returns>
    internal static Command Create(TextWriter writer)
    {
        Command root = new("check-suite", "Packages and validates check contracts without executing module code.");
        Command package = new("package", "Creates an immutable v1 directory package; destination must not exist.");
        Argument<string> descriptor = new("descriptor");
        Option<string> payload = new("--payload-root") { Required = true };
        Option<string> destination = new("--destination") { Required = true };
        Option<string> packageOutput = Output();
        package.Arguments.Add(descriptor);
        package.Options.Add(payload);
        package.Options.Add(destination);
        package.Options.Add(packageOutput);
        package.SetAction(parse => Write(writer, parse.GetValue(packageOutput), () => CheckSuiteContract.Package(parse.GetValue(descriptor)!, parse.GetValue(payload)!, parse.GetValue(destination)!)));
        root.Subcommands.Add(package);
        Command validate = new("validate", "Validates exact retained bytes or a closed v1 JSON document.");
        Argument<string> path = new("path");
        Option<string> kind = new("--kind") { DefaultValueFactory = _ => "package" };
        _ = kind.AcceptOnlyFromAmong("package", "suite", "invocation", "result");
        Option<string> validateOutput = Output();
        validate.Arguments.Add(path);
        validate.Options.Add(kind);
        validate.Options.Add(validateOutput);
        validate.SetAction(parse => Write(writer, parse.GetValue(validateOutput), () => parse.GetValue(kind) == "package"
            ? CheckSuiteContract.ValidatePackage(parse.GetValue(path)!)
            : CheckSuiteContract.ValidateDocument(parse.GetValue(kind)!, CheckSuiteContract.ReadBounded(parse.GetValue(path)!))));
        root.Subcommands.Add(validate);
        Command coverage = new("coverage", "Refuses unresolved reference publication coverage.");
        Argument<string> registry = new("registry");
        Option<string> repository = new("--repository") { Required = true };
        Option<string> packages = new("--packages") { Required = true };
        Option<string> coverageOutput = Output();
        coverage.Arguments.Add(registry);
        coverage.Options.Add(repository);
        coverage.Options.Add(packages);
        coverage.Options.Add(coverageOutput);
        coverage.SetAction(parse => Write(writer, parse.GetValue(coverageOutput), () => ReferenceSuiteCoverage.Validate(parse.GetValue(registry)!, parse.GetValue(repository)!, parse.GetValue(packages)!)));
        root.Subcommands.Add(coverage);
        return root;
    }

    private static Option<string> Output()
    {
        Option<string> option = new("--output") { DefaultValueFactory = _ => "human" };
        _ = option.AcceptOnlyFromAmong("human", "json");
        return option;
    }

    private static int Write(TextWriter writer, string? output, Func<CheckSuiteReport> operation)
    {
        CheckSuiteReport report;
        try
        {
            report = operation();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            report = new CheckSuiteReport(false, ["HXS001"]);
        }

        string status = report.Valid ? "passed" : "failed";
        string digest = report.SuiteDigest is null ? string.Empty : " " + report.SuiteDigest;
        writer.WriteLine(output == "json"
            ? JsonSerializer.Serialize(report, _jsonOptions)
            : status + ": " + string.Join(",", report.Diagnostics) + digest);
        return report.Valid ? 0 : (int)ToolExitCode.EvidenceSchemaOrPolicy;
    }
}
