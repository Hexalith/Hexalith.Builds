// <copyright file="SelectedNativeTestReport.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.TestReports;

using Microsoft.Win32.SafeHandles;

/// <summary>Inspects bounded selected-test reports through one safe handle, then removes their raw output.</summary>
internal static class SelectedNativeTestReport
{
    /// <summary>The maximum selected report size, independent of the legacy report contract.</summary>
    public const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>Reads, inspects and redacts a selected report on every process exit.</summary>
    /// <param name="path">The owned report path.</param>
    /// <param name="workspace">The owned workspace.</param>
    /// <param name="secrets">The exact handoff values that cannot be retained.</param>
    /// <param name="cancellationToken">The shared stopping and diagnostic bound.</param>
    /// <returns>Only inspectable, sanitized bytes, including safe failed reports.</returns>
    internal static async Task<(NativeTestReportLoadResult Report, byte[]? Bytes)> ReadAsync(string path, string workspace, IReadOnlyList<string> secrets, CancellationToken cancellationToken)
    {
        bool mayUnlink = false;
        try
        {
            mayUnlink = IsOwnedParent(path, workspace);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOwnedRegularPath(path, workspace))
            {
                return Refused("HXT002");
            }

            FileStream stream = OpenWithoutBlocking(path);
            await using (stream.ConfigureAwait(false))
            {
                if (!stream.CanSeek || stream.Length > MaximumBytes)
                {
                    return Refused("HXT002");
                }

                CompositionWorkspace.CreatePrivateDirectory(workspace);
                CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
                File.Delete(path);
                MemoryStream output = new();
                await using (output.ConfigureAwait(false))
                {
                    byte[] buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (output.Length + count > MaximumBytes)
                        {
                            return Refused("HXT002");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }

                    byte[] bytes = output.ToArray();
                    cancellationToken.ThrowIfCancellationRequested();
                    (NativeTestReportLoadResult Report, byte[]? Bytes) inspected = Inspect(path, bytes, secrets);
                    cancellationToken.ThrowIfCancellationRequested();
                    return inspected;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            return Refused("HXT002");
        }
        finally
        {
            // Unlink the raw entry only; never rewrite a report path or a link target.
            if (mayUnlink && IsOwnedParent(path, workspace) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Captures private app and sidecar credentials before executing the selected child.</summary>
    /// <param name="path">The readiness-owned access file.</param>
    /// <param name="workspace">The owned workspace.</param>
    /// <param name="cancellationToken">The execution cancellation.</param>
    /// <returns>The exact handed-off values, or null when the file cannot be inspected safely.</returns>
    internal static async Task<IReadOnlyList<string>?> ReadAccessSecretsAsync(string path, string workspace, CancellationToken cancellationToken)
    {
        try
        {
            if (Path.GetFullPath(path) != Path.Combine(Path.GetFullPath(workspace), "platform-components", "retention", "access.json") || !IsOwnedRegularPath(path, workspace))
            {
                return null;
            }

            FileStream stream = OpenWithoutBlocking(path);
            await using (stream.ConfigureAwait(false))
            {
                if (!stream.CanSeek || stream.Length is <= 0 or > 65536)
                {
                    return null;
                }

                byte[] bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(bytes);
                List<string> secrets = [];
                foreach (JsonProperty service in document.RootElement.EnumerateObject())
                {
                    foreach (string name in new[] { "APP_API_TOKEN", "DAPR_API_TOKEN" })
                    {
                        string? value = service.Value.GetProperty(name).GetString();
                        if (string.IsNullOrEmpty(value))
                        {
                            return null;
                        }

                        secrets.Add(value);
                    }
                }

                return secrets.Count == 6 ? secrets : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Reads detailed native observations using the same bounded safe custody and secret policy as TRX.</summary>
    /// <param name="path">The fixed native observation path.</param>
    /// <param name="workspace">The owning workspace.</param>
    /// <param name="runId">The required selected run.</param>
    /// <param name="secrets">The complete handoff credential set.</param>
    /// <param name="cancellationToken">The shared stopping bound.</param>
    /// <returns>Inspectable native details, or null on every refusal.</returns>
    internal static async Task<byte[]?> ReadObservationsAsync(string path, string workspace, string runId, IReadOnlyList<string> secrets, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsOwnedRegularPath(path, workspace))
            {
                return null;
            }

            FileStream stream = OpenWithoutBlocking(path);
            await using (stream.ConfigureAwait(false))
            {
                if (!stream.CanSeek || stream.Length is <= 0 or > 1048576)
                {
                    return null;
                }

                byte[] bytes = new byte[(int)stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(bytes);
                JsonElement root = document.RootElement;
                return root.ValueKind != JsonValueKind.Object || root.GetProperty("runId").GetString() != runId || root.GetProperty("status").GetString() != "functional-observations-passed" || root.GetProperty("qualificationCredit").ValueKind != JsonValueKind.False || Unsafe(root) ? null : bytes;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
        finally
        {
            if (IsOwnedParent(path, workspace) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        bool Unsafe(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Any(property => ManifestSecretDetector.ContainsSecret(property.Name)
                || secrets.Any(secret => !string.IsNullOrEmpty(secret) && property.Name.Contains(secret, StringComparison.Ordinal)) || Unsafe(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().Any(Unsafe),
            JsonValueKind.String => ManifestSecretDetector.ContainsSecret(value.GetString()!) || secrets.Any(secret => !string.IsNullOrEmpty(secret) && value.GetString()!.Contains(secret, StringComparison.Ordinal)),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true,
        };
    }

    /// <summary>Opens a held read handle without following links or blocking on a replaced Unix FIFO.</summary>
    /// <param name="path">The caller-validated owned path.</param>
    /// <returns>The held stream, whose seekability and positive length the caller must check.</returns>
    /// <exception cref="IOException">The path could not be opened safely.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned FileStream owns the SafeFileHandle; constructor failure disposes it explicitly.")]
    internal static FileStream OpenWithoutBlocking(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
        }

        int flags = OperatingSystem.IsMacOS() ? 0x4 | 0x100 | 0x1000000 : 0x800 | 0x20000 | 0x80000;
        int descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            throw new IOException("The selected report could not be opened safely.");
        }

        SafeFileHandle handle = new(descriptor, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.Read, 8192, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "This small nonblocking Unix open does not require enabling unsafe generated code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "Unix open requires UTF-8 bytes, explicitly marshaled as LPUTF8Str.")]
    [DllImport("libc", EntryPoint = "open", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static (NativeTestReportLoadResult Report, byte[]? Bytes) Inspect(string path, byte[] bytes, IReadOnlyList<string> secrets)
    {
        XDocument document;
        try
        {
            using MemoryStream xml = new(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return Refused(ContainsEncodedSecret(bytes, secrets) ? "HXT008" : "HXT002");
        }

        string decodedXml = document.ToString(SaveOptions.DisableFormatting);
        if (secrets.Any(secret => !string.IsNullOrEmpty(secret) && decodedXml.Contains(secret, StringComparison.Ordinal))
            || document.DescendantNodes().OfType<XComment>().Select(comment => comment.Value)
                .Concat(document.DescendantNodes().OfType<XProcessingInstruction>().Select(instruction => instruction.Data)).Any(ManifestSecretDetector.ContainsSecret)
            || document.Descendants().SelectMany(element => element.Attributes().Select(attribute => attribute.Value).Concat(element.Nodes().OfType<XText>().Select(node => node.Value)))
            .Any(value => secrets.Any(secret => value.Contains(secret, StringComparison.Ordinal)) || ManifestSecretDetector.ContainsSecret(value)))
        {
            return Refused("HXT008");
        }

        byte[]? sanitized = NativeTestReportRedactor.Redact(bytes);
        return sanitized is null ? Refused("HXT002") : (NativeTestReportLoader.LoadBytes(path, sanitized), sanitized);
    }

    private static bool IsOwnedRegularPath(string path, string workspace) =>
        IsOwnedParent(path, workspace) && File.Exists(path) && new FileInfo(path).LinkTarget is null
        && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) == 0;

    private static bool IsOwnedParent(string path, string workspace)
    {
        string root = Path.GetFullPath(workspace);
        string relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return false;
        }

        DirectoryInfo? current = new(Path.GetDirectoryName(path)!);
        while (current is not null)
        {
            if (current.LinkTarget is not null || (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return false;
            }

            if (current.FullName == root)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool ContainsEncodedSecret(byte[] bytes, IReadOnlyList<string> secrets)
    {
        foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false, true), new UnicodeEncoding(false, false, true), new UnicodeEncoding(true, false, true), new UTF32Encoding(false, false, true), new UTF32Encoding(true, false, true) })
        {
            try
            {
                string value = encoding.GetString(bytes);
                if (secrets.Any(secret => value.Contains(secret, StringComparison.Ordinal)) || ManifestSecretDetector.ContainsSecret(value))
                {
                    return true;
                }
            }
            catch (DecoderFallbackException)
            {
                // Uninspectable reports are dropped regardless of whether a secret can be named.
            }
        }

        return false;
    }

    private static (NativeTestReportLoadResult Report, byte[]? Bytes) Refused(string rule) =>
        (new NativeTestReportLoadResult(null, new ToolDiagnostic(rule, ToolPhase.Test, ToolFailureCategory.ProductOrTest, "The selected native report is missing, unsafe, uninspectable, or contains prohibited material.", "report")), null);
}