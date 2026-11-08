// <copyright file="StdinChildProgress.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;

using Xunit;

/// <summary>Retains only fixed stages and actual scheduler facts for the private stdin child.</summary>
internal static class StdinChildProgress
{
    private static readonly Lock _gate = new();

    private static readonly HashSet<string> _stages = new(StringComparer.Ordinal)
    {
        "unobserved", "child-fact", "workspace-create", "workspace-created", "input-open", "input-opened",
        "listener-create", "listener-created", "await-read", "read-requested", "signal-dispose", "signal-disposed",
        "wrapper-dispose", "wrapper-disposed", "raw-dispose", "raw-disposed", "next-listener-create",
        "next-listener-created", "readiness-write", "readiness-written", "await-stop-command", "stop-command-received",
        "next-listener-dispose", "next-listener-disposed", "workspace-cleanup", "child-complete",
        "read-call", "read-pending", "read-completed", "input-dispose", "input-disposed",
    };

    private static string _driverStage = "unobserved";

    private static string _ioStage = "unobserved";

    private static bool? _retained;

    /// <summary>Gets the private child-only progress directory variable.</summary>
    internal static string DirectoryVariable => "HEXALITH_TEST_STDIN_STAGE_DIRECTORY";

    /// <summary>Records a fixed stage before or after its actual operation; it never signals readiness.</summary>
    /// <param name="stage">The fixed stage name.</param>
    /// <param name="reader">Whether this is the independent reader/disposer observation.</param>
    /// <param name="retain">The current controlled retention setting, when starting an iteration.</param>
    /// <exception cref="ArgumentException">The stage is not in the fixed allowlist.</exception>
    internal static void Mark(string stage, bool reader = false, bool? retain = null)
    {
        string? directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (directory is null)
        {
            return;
        }

        if (!_stages.Contains(stage))
        {
            throw new ArgumentException("Unknown stdin child stage.", nameof(stage));
        }

        lock (_gate)
        {
            if (reader)
            {
                _ioStage = stage;
            }
            else
            {
                _driverStage = stage;
            }

            _retained = retain ?? _retained;
            Persist(directory);
        }
    }

    /// <summary>Reads only bounded fixed fields, including the last driver and I/O stages.</summary>
    /// <param name="directory">The fresh parent-owned fixture directory.</param>
    /// <returns>The safe actual observation, or an explicitly unobserved state.</returns>
    internal static string Describe(string directory)
    {
        try
        {
            FileInfo file = new(Path.Combine(directory, "child-progress.json"));
            if (!file.Exists || file.LinkTarget is not null || file.Length > 1024)
            {
                return "stage=unobserved";
            }

            byte[] bytes = File.ReadAllBytes(file.FullName);
            if (bytes.Length > 1024)
            {
                return "stage=unobserved";
            }

            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 3 });
            JsonElement value = document.RootElement;
            string? driver = value.GetProperty("driverStage").GetString();
            string? io = value.GetProperty("ioStage").GetString();
            string? runtime = value.GetProperty("runtime").GetString();
            int processors = value.GetProperty("processors").GetInt32();
            int minimum = value.GetProperty("minimumWorkers").GetInt32();
            int maximum = value.GetProperty("maximumWorkers").GetInt32();
            JsonElement retain = value.GetProperty("retained");
            return driver is null || io is null || !_stages.Contains(driver) || !_stages.Contains(io)
                || runtime is null || runtime.Length > 32 || !Version.TryParse(runtime, out Version? version)
                || processors is < 1 or > 65536 || minimum < 0 || maximum < 1 || minimum > maximum
                || retain.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                    ? "stage=unobserved"
                    : $"driver={driver};io={io};retained={retain.GetRawText()};runtime={version};processors={processors};workers={minimum}/{maximum}";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return "stage=unobserved";
        }
    }

    /// <summary>Retains the bounded observation in the actual test result before fixture deletion.</summary>
    /// <param name="directory">The fresh parent-owned fixture directory.</param>
    /// <param name="exited">The actual child exit state.</param>
    internal static void Report(string directory, bool exited) =>
        TestContext.Current.TestOutputHelper?.WriteLine($"stdin-child-progress childExited={exited};{Describe(directory)}");

    private static void Persist(string directory)
    {
        ThreadPool.GetMinThreads(out int minimum, out _);
        ThreadPool.GetMaxThreads(out int maximum, out _);
        string contents = JsonSerializer.Serialize(new
        {
            driverStage = _driverStage,
            ioStage = _ioStage,
            retained = _retained,
            runtime = Environment.Version.ToString(),
            processors = Environment.ProcessorCount,
            minimumWorkers = minimum,
            maximumWorkers = maximum,
        });
        string temporary = Path.Combine(directory, "child-progress.tmp");
        File.WriteAllText(temporary, contents);
        File.Move(temporary, Path.Combine(directory, "child-progress.json"), overwrite: true);
    }
}
