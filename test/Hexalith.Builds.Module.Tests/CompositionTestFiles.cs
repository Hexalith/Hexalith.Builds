// <copyright file="CompositionTestFiles.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

/// <summary>
/// Creates temporary directories, fake executables, and sample plans for composition unit tests.
/// </summary>
internal static class CompositionTestFiles
{
    /// <summary>
    /// A fixed, well-formed run identity.
    /// </summary>
    public const string RunId = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// Creates a unique temporary directory.
    /// </summary>
    /// <returns>The absolute directory path.</returns>
    public static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "hexalith-g4-unit-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Writes an executable POSIX shell script.
    /// </summary>
    /// <param name="path">The script path.</param>
    /// <param name="body">The script body after the shebang.</param>
    public static void WriteScript(string path, string body)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Creates a fake Dapr home whose binaries report the given versions when executed.
    /// </summary>
    /// <param name="root">The root directory.</param>
    /// <param name="cliVersion">The reported CLI version.</param>
    /// <param name="runtimeVersion">The reported runtime version.</param>
    /// <returns>The Dapr home.</returns>
    public static string CreateDaprHome(string root, string cliVersion, string runtimeVersion)
    {
        string home = Path.Combine(root, "dapr-home");
        WriteScript(Path.Combine(home, "tools", "dapr"), $"echo 'CLI version: {cliVersion} '\necho 'Runtime version: {runtimeVersion}'");
        WriteScript(Path.Combine(home, ".dapr", "bin", "daprd"), $"echo '{runtimeVersion}'");
        WriteScript(
            Path.Combine(home, ".dapr", "bin", "placement"),
            $"echo 'level=info msg=\"Starting Dapr Placement Service -- version {runtimeVersion} -- commit test\"'\nsleep 30");
        WriteScript(
            Path.Combine(home, ".dapr", "bin", "scheduler"),
            $"echo 'level=info msg=\"Starting Dapr Scheduler Service -- version {runtimeVersion} -- commit test\"'\nsleep 30");
        return home;
    }

    /// <summary>Creates a fake Aspire CLI reporting the embedded catalog selection.</summary>
    /// <param name="root">The temporary root.</param>
    /// <returns>The executable path.</returns>
    public static string CreateAspire(string root)
    {
        string path = Path.Combine(root, "aspire");
        WriteScript(path, $"[ \"$#\" -eq 1 ] && [ \"$1\" = '--version' ] || exit 64\necho '{CompositionToolchainPins.AspireAppHostSdkVersion.Split('+')[0]}+test-build'");
        return path;
    }

    /// <summary>
    /// Creates a fake Docker CLI that reports a server version.
    /// </summary>
    /// <param name="root">The root directory.</param>
    /// <returns>The Docker command path.</returns>
    public static string CreateDocker(string root)
    {
        string docker = Path.Combine(root, "docker");
        WriteScript(docker, "echo '29.0.0'");
        return docker;
    }

    /// <summary>
    /// Creates a sample run plan.
    /// </summary>
    /// <param name="root">The root directory.</param>
    /// <returns>The plan.</returns>
    public static CompositionRunPlan CreatePlan(string root) =>
        CompositionRunPlanFactory.Create(
            RunId,
            Path.Combine(root, "workspace", RunId),
            Path.Combine(root, "dapr-home"),
            new CompositionRunPorts(20001, 20002, 20003, 20004, 20005, 20006, 20007, 20008, 20009, 20010, 20011, 20012, 20013, [20014, 20015], 20016, 20017, 20018, [new(20019, 20020, 20021, 20022), new(20023, 20024, 20025, 20026)]),
            [
                new CompositionRunModule("p0-orders", "p0-orders", "p0-orders-app", Path.Combine(root, "orders", "Orders.csproj")),
                new CompositionRunModule("p0-inventory", "p0-inventory", "p0-inventory-app", Path.Combine(root, "inventory", "Inventory.csproj")),
            ],
            [
                new CompositionRunUiMarker("p0-orders", Path.Combine(root, "ui", "Ui.dll"), "Acme.OrdersMarker"),
                new CompositionRunUiMarker("p0-inventory", Path.Combine(root, "ui", "Ui.dll"), "Acme.InventoryMarker"),
            ]);

    /// <summary>
    /// Creates a fake Docker CLI that reports a server version and answers every other command with no containers.
    /// </summary>
    /// <param name="root">The root directory.</param>
    /// <returns>The Docker command path.</returns>
    public static string CreateStubDocker(string root)
    {
        string docker = Path.Combine(root, "stub-docker");
        WriteScript(docker, "case \"$1\" in\n  version) echo '29.0.0' ;;\n  *) exit 0 ;;\nesac");
        return docker;
    }

    /// <summary>Builds process doubles that execute on Windows as well as Unix.</summary>
    /// <param name="root">The temporary fixture root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Paths to the fake Aspire, Docker, Dapr home and native dotnet commands.</returns>
    public static async Task<(string Aspire, string Docker, string DaprHome, string NativeDotnet)> BuildPortableToolchainAsync(string root, CancellationToken cancellationToken)
    {
        string project = Path.Combine(root, "portable-toolchain");
        string outputDirectory = Path.Combine(project, "out");
        _ = Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(
            Path.Combine(project, "FakeTool.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><UseAppHost>true</UseAppHost></PropertyGroup></Project>",
            cancellationToken).ConfigureAwait(false);
        string program = """
            using System;
            using System.IO;
            using System.Linq;
            using System.Threading;
            string tool = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
            if (tool == "aspire") { Console.WriteLine("__ASPIRE__+test-build"); return 0; }
            if (tool == "stub-docker") { Console.WriteLine("29.0.0"); return 0; }
            if (tool == "dapr") { Console.WriteLine("CLI version: __DAPR_CLI__"); Console.WriteLine("Runtime version: __DAPR_RUNTIME__"); return 0; }
            if (tool == "daprd") { Console.WriteLine("__DAPR_RUNTIME__"); return 0; }
            if (tool == "placement" || tool == "scheduler")
            {
                Console.WriteLine("level=info msg=\"Starting Dapr " + (tool == "placement" ? "Placement" : "Scheduler") + " Service -- version __DAPR_RUNTIME__ -- commit test\"");
                Thread.Sleep(30000);
                return 0;
            }

            if (tool == "native-dotnet")
            {
                int resultIndex = Array.IndexOf(args, "--results-directory");
                string results = args[resultIndex + 1];
                string projectPath = args.First(argument => argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
                string capture = Path.Combine(Path.GetDirectoryName(projectPath)!, "native-environment.txt");
                File.WriteAllLines(capture,
                [
                    Environment.GetEnvironmentVariable("HEXALITH_SOURCE_MAPPING_HASH") ?? "",
                    Environment.GetEnvironmentVariable("CustomBeforeDirectoryBuildProps") ?? "",
                    Environment.GetEnvironmentVariable("CustomAfterMicrosoftCommonProps") ?? "",
                    Environment.GetEnvironmentVariable("CustomAfterDirectoryBuildTargets") ?? "",
                    Environment.GetEnvironmentVariable("AfterMicrosoftNETSdkTargets") ?? "",
                    Environment.GetEnvironmentVariable("Configuration") ?? "",
                    string.Join(' ', args),
                ]);
                Directory.CreateDirectory(results);
                File.WriteAllText(Path.Combine(results, "native.trx"), "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");
                return 0;
            }

            return 64;
            """
            .Replace("__ASPIRE__", CompositionToolchainPins.AspireAppHostSdkVersion.Split('+')[0], StringComparison.Ordinal)
            .Replace("__DAPR_CLI__", CompositionToolchainPins.DaprCliVersion, StringComparison.Ordinal)
            .Replace("__DAPR_RUNTIME__", CompositionToolchainPins.DaprRuntimeVersion, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), program, cancellationToken).ConfigureAwait(false);
        CompositionProcessResult built = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["build", project, "--configuration", "Debug", "-v:q", "--output", outputDirectory], root, null),
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        built.ExitCode.ShouldBe(0, built.Output);

        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string tools = Path.Combine(root, "portable-tools");
        string home = Path.Combine(root, "portable-dapr");
        string CopyTool(string name, string directory)
        {
            _ = Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, name + extension);
            File.Copy(Path.Combine(outputDirectory, "FakeTool" + extension), executable);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            foreach (string suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                File.Copy(Path.Combine(outputDirectory, "FakeTool" + suffix), Path.Combine(directory, "FakeTool" + suffix), overwrite: true);
            }

            return executable;
        }

        string aspire = CopyTool("aspire", tools);
        string docker = CopyTool("stub-docker", tools);
        string nativeDotnet = CopyTool("native-dotnet", tools);
        _ = CopyTool("dapr", Path.Combine(home, "tools"));
        string bin = Path.Combine(home, ".dapr", "bin");
        _ = CopyTool("daprd", bin);
        _ = CopyTool("placement", bin);
        _ = CopyTool("scheduler", bin);
        return (aspire, docker, home, nativeDotnet);
    }

    /// <summary>
    /// Builds a fake AppHost console assembly. It reads <c>mode.txt</c> next to itself: <c>exit</c> exits at once,
    /// <c>foreign</c> writes readiness for another run, and any other mode never writes readiness; all wait for stop.
    /// </summary>
    /// <param name="root">The root directory.</param>
    /// <param name="mode">The behavior mode.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The built assembly path.</returns>
    public static async Task<string> BuildFakeAppHostAsync(string root, string mode, CancellationToken cancellationToken)
    {
        string project = Path.Combine(root, "fake-apphost");
        _ = Directory.CreateDirectory(project);
        const string projectXml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>"
            + "<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(project, "FakeAppHost.csproj"), projectXml, cancellationToken).ConfigureAwait(false);
        const string readiness = "{\"schema\":\"hexalith.g4-run-readiness.v1\",\"runId\":\"ffffffffffffffffffffffffffffffff\","
            + "\"eventStoreEndpoint\":\"http://localhost:1\",\"uiEndpoint\":\"http://localhost:2\","
            + "\"resources\":[{\"name\":\"eventstore\",\"kind\":\"project\",\"state\":\"Healthy\"}]}";
        string program = "string mode = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, \"mode.txt\")).Trim();\n"
            + "if (mode == \"exit\") { return 7; }\n"
            + "string plan = Environment.GetEnvironmentVariable(\"HEXALITH_G4_PLAN\")!;\n"
            + "if (mode == \"capture\" || mode == \"ready\") { File.Copy(plan, Path.Combine(AppContext.BaseDirectory, \"captured-plan.json\")); "
            + "File.Copy(Path.Combine(Path.GetDirectoryName(plan)!, \"source-mapping\", \"mapping.json\"), "
            + "Path.Combine(AppContext.BaseDirectory, \"captured-mapping.json\")); "
            + "File.WriteAllText(Path.Combine(AppContext.BaseDirectory, \"captured-mapping-hash.txt\"), "
            + "Environment.GetEnvironmentVariable(\"HEXALITH_SOURCE_MAPPING_HASH\") ?? \"\"); if (mode == \"capture\") return 0; }\n"
            + "if (mode == \"ready\") { using var readyPlan = System.Text.Json.JsonDocument.Parse(File.ReadAllText(plan)); "
            + "var root = readyPlan.RootElement; File.WriteAllText(root.GetProperty(\"readinessPath\").GetString()!, "
            + "System.Text.Json.JsonSerializer.Serialize(new { schema = \"hexalith.g4-run-readiness.v1\", "
            + "runId = root.GetProperty(\"runId\").GetString(), eventStoreEndpoint = \"http://127.0.0.1:1\", "
            + "uiEndpoint = \"http://127.0.0.1:2\", resources = new[] { new { name = \"eventstore\", kind = \"project\", state = \"Healthy\" } } })); }\n"
            + "if (mode == \"foreign\") { File.WriteAllText(Path.Combine(Path.GetDirectoryName(plan)!, \"readiness.json\"), "
            + System.Text.Json.JsonSerializer.Serialize(readiness) + "); }\n"
            + "if (mode == \"cause\") { using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(plan)); "
            + "string runId = document.RootElement.GetProperty(\"runId\").GetString()!; "
            + "File.WriteAllText(Path.Combine(Path.GetDirectoryName(plan)!, \"startup-failure.json\"), "
            + "System.Text.Json.JsonSerializer.Serialize(new { schema = \"hexalith.g4-startup-failure.v1\", runId, "
            + "ruleId = \"HXR027\", resource = \"eventstore\", appId = \"eventstore\", status = \"cutover-activation-HTTP-403\" })); }\n"
            + "Console.In.ReadLine();\n"
            + "return 0;\n";
        await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), program, cancellationToken).ConfigureAwait(false);
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "build", project, "--configuration", "Debug", "-v:q", "--output", Path.Combine(project, "out") })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.ExitCode.ShouldBe(0, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Combine(project, "out", "mode.txt"), mode, cancellationToken).ConfigureAwait(false);
        return Path.Combine(project, "out", "FakeAppHost.dll");
    }

    /// <summary>
    /// Locates the repository root.
    /// </summary>
    /// <returns>The repository root.</returns>
    /// <exception cref="InvalidOperationException">The repository root cannot be found.</exception>
    public static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hexalith.Builds.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Hexalith.Builds repository root.");
    }

    /// <summary>
    /// Deletes a temporary directory best-effort.
    /// </summary>
    /// <param name="path">The directory.</param>
    public static void Delete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temporary cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temporary cleanup.
        }
    }
}
