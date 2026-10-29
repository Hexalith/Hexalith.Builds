// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

nint probe = NativeLibrary.Load(args[0]);
Assembly tooling = typeof(CompositionDocumentStore).Assembly;
NativeLibrary.SetDllImportResolver(tooling, (name, _, _) => name == "libc" ? probe : 0);
string directory = args[1];
string scenario = args[2];
Directory.CreateDirectory(directory);
string metadata = Path.Combine(directory, "metadata-read.json");
if (scenario is "eintr" or "document-flush-eintr")
{
    await CompositionDocumentStore.WriteAsync(metadata, new { status = "committed" }, CancellationToken.None).ConfigureAwait(false);
    if (scenario == "eintr")
    {
        Marshal.GetDelegateForFunctionPointer<Action>(NativeLibrary.GetExport(probe, "arm_interrupt"))();
    }

#pragma warning disable S3011 // The isolated test harness invokes only this fixed metadata reader to inject native faults.
    MethodInfo read = typeof(CompositionDocumentStore).GetMethod("ReadIfExistsAsync", BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(JsonDocument));
#pragma warning restore S3011
    using JsonDocument? document = await ((Task<JsonDocument?>)read.Invoke(null, [metadata, CancellationToken.None])!).ConfigureAwait(false);
    if (document?.RootElement.GetProperty("status").GetString() != "committed")
    {
        throw new InvalidOperationException("Interrupted sharing did not recover the committed document.");
    }
}
else
{
    bool documentPublication = scenario.StartsWith("document-", StringComparison.Ordinal);
    string destination = documentPublication ? metadata : Path.Combine(directory, "archive.json");
    byte[] original = "{\"status\":\"original\"}"u8.ToArray();
    await File.WriteAllBytesAsync(destination, original).ConfigureAwait(false);
    bool refused = false;
    using CancellationTokenSource publication = new();
    if (scenario == "document-flush-cancelled")
    {
        publication.CancelAfter(TimeSpan.FromMilliseconds(500));
    }

    try
    {
        if (documentPublication)
        {
            await CompositionDocumentStore.WriteAsync(destination, new { status = "replacement" }, publication.Token).ConfigureAwait(false);
        }
        else
        {
#pragma warning disable S3011 // The isolated test harness invokes only this fixed snapshot publisher to verify storage failure handling.
            MethodInfo snapshot = tooling.GetType("Hexalith.Builds.Tooling.Runtime.NativeTestExecutor")!.GetMethod("WriteSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
#pragma warning restore S3011
            await ((Task)snapshot.Invoke(null, [destination, directory, "{\"status\":\"replacement\"}"u8.ToArray(), publication.Token])!).ConfigureAwait(false);
        }
    }
    catch (IOException)
    {
        refused = true;
    }
    catch (OperationCanceledException) when (publication.IsCancellationRequested)
    {
        refused = true;
    }

    if (!refused || !(await File.ReadAllBytesAsync(destination).ConfigureAwait(false)).SequenceEqual(original)
        || Directory.EnumerateFiles(directory, "*.tmp").Any())
    {
        throw new InvalidOperationException("A failed durable flush must refuse publication and preserve the prior destination bytes.");
    }
}

Console.WriteLine("Passed: " + scenario);