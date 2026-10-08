// <copyright file="PlatformManifestTestWorkspace.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json.Nodes;

/// <summary>
/// Provides an isolated complete declaration and local executable reference for validation tests.
/// </summary>
internal sealed class PlatformManifestTestWorkspace : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="PlatformManifestTestWorkspace"/> class.</summary>
    public PlatformManifestTestWorkspace()
    {
        Root = CompositionTestFiles.CreateDirectory();
        File.WriteAllText(Path.Combine(Root, "README.md"), "Validation must never execute this file.");
        Document = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "test/fixtures/module/platform/valid.json")))!.AsObject();
    }

    /// <summary>Gets the Builds repository containing the published schema and fixture.</summary>
    /// <exception cref="DirectoryNotFoundException">The repository could not be located.</exception>
    public static string RepositoryRoot
    {
        get
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

            throw new DirectoryNotFoundException("Could not locate the Builds repository.");
        }
    }

    /// <summary>Gets the temporary workspace root.</summary>
    public string Root { get; }

    /// <summary>Gets the mutable test input.</summary>
    public JsonObject Document { get; }

    /// <summary>Replaces or removes a value addressed by dot-separated object keys and array indexes.</summary>
    /// <param name="path">The test field path.</param>
    /// <param name="json">The replacement JSON, or the missing sentinel.</param>
    public void Change(string path, string json)
    {
        string[] segments = path.Split('.');
        JsonNode node = Document;
        foreach (string segment in segments[..^1])
        {
            node = node is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]! : node[segment]!;
        }

        string property = segments[^1];
        if (json == "<missing>")
        {
            _ = node.AsObject().Remove(property);
        }
        else
        {
            node[property] = JsonNode.Parse(json);
        }
    }

    /// <summary>Saves the declaration under the specified local filename.</summary>
    /// <param name="name">The local filename.</param>
    /// <returns>The complete file path.</returns>
    public string Save(string name = "manifest.json")
    {
        string path = Path.Combine(Root, name);
        File.WriteAllText(path, Document.ToJsonString());
        return path;
    }

    /// <summary>Removes the isolated workspace.</summary>
    public void Dispose() => Directory.Delete(Root, true);

    /// <summary>Opens a FIFO for the callback so validation observes a non-seekable manifest.</summary>
    /// <param name="validate">The callback that receives the FIFO path.</param>
    /// <returns>A task that completes after the callback and the write end close.</returns>
    /// <exception cref="InvalidOperationException">mkfifo did not create the FIFO.</exception>
    internal static async Task UseNonSeekableManifestAsync(Func<string, Task> validate)
    {
        ArgumentNullException.ThrowIfNull(validate);
        string directory = CompositionTestFiles.CreateDirectory();
        string fifo = Path.Combine(directory, "manifest.fifo");
        using Process process = new();
        process.StartInfo.FileName = "mkfifo";
        process.StartInfo.ArgumentList.Add(fifo);
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        if (!process.Start())
        {
            throw new InvalidOperationException("mkfifo failed to start.");
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync().ConfigureAwait(false));
        }

        using ManualResetEventSlim release = new(false);
        Task writer = Task.Run(() =>
        {
            using FileStream stream = new(fifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The non-seekable manifest reader did not finish.");
            }
        });
        try
        {
            await validate(fifo).ConfigureAwait(false);
        }
        finally
        {
            release.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Directory.Delete(directory, true);
        }
    }
}