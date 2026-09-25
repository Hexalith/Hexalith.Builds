// <copyright file="NativeTestReportRedactor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// Reduces a native TRX report to run metadata before it is retained: host, user, absolute-path, and captured-output values are removed.
/// </summary>
internal static class NativeTestReportRedactor
{
    private const string _redactedRunName = "native";

    private static readonly string[] _removedAttributes = ["computerName", "runDeploymentRoot", "runUser"];

    private static readonly string[] _fileNameAttributes = ["codeBase", "storage"];

    private static readonly string[] _removedElements = ["CollectorDataEntries", "Output", "ResultFiles", "RunInfos"];

    /// <summary>
    /// Redacts a TRX report.
    /// </summary>
    /// <param name="reportBytes">The native report bytes.</param>
    /// <returns>The redacted UTF-8 report, or <see langword="null"/> when the report is not well-formed XML.</returns>
    public static byte[]? Redact(byte[] reportBytes)
    {
        ArgumentNullException.ThrowIfNull(reportBytes);

        XDocument document;
        try
        {
            using MemoryStream input = new(reportBytes, writable: false);
            document = XDocument.Load(input, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }

        if (document.Root is not { } root)
        {
            return null;
        }

        root.Descendants()
            .Where(element => _removedElements.Contains(element.Name.LocalName, StringComparer.Ordinal))
            .ToList()
            .ForEach(element => element.Remove());
        foreach (XElement element in root.DescendantsAndSelf())
        {
            element.Attributes()
                .Where(attribute => _removedAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                .ToList()
                .ForEach(attribute => attribute.Remove());
            foreach (XAttribute attribute in element.Attributes().Where(attribute => _fileNameAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal)))
            {
                attribute.Value = attribute.Value[(attribute.Value.LastIndexOfAny(['/', '\\']) + 1)..];
            }
        }

        if (root.Attribute("name") is { } name)
        {
            name.Value = _redactedRunName;
        }

        using MemoryStream output = new();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, NewLineChars = "\n" }))
        {
            document.Save(writer);
        }

        return output.ToArray();
    }
}
