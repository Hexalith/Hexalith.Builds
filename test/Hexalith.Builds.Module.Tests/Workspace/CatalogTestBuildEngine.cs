// <copyright file="CatalogTestBuildEngine.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Collections;

using Microsoft.Build.Framework;

/// <summary>Captures errors emitted by the catalog MSBuild task.</summary>
internal sealed class CatalogTestBuildEngine : IBuildEngine
{
    /// <inheritdoc />
    public bool ContinueOnError => false;

    /// <inheritdoc />
    public int LineNumberOfTaskNode => 0;

    /// <inheritdoc />
    public int ColumnNumberOfTaskNode => 0;

    /// <inheritdoc />
    public string ProjectFileOfTaskNode => string.Empty;

    /// <summary>Gets the last logged build error.</summary>
    public BuildErrorEventArgs? Error { get; private set; }

    /// <inheritdoc />
    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;

    /// <inheritdoc />
    public void LogCustomEvent(CustomBuildEventArgs e)
    {
    }

    /// <inheritdoc />
    public void LogErrorEvent(BuildErrorEventArgs e) => Error = e;

    /// <inheritdoc />
    public void LogMessageEvent(BuildMessageEventArgs e)
    {
    }

    /// <inheritdoc />
    public void LogWarningEvent(BuildWarningEventArgs e)
    {
    }
}
