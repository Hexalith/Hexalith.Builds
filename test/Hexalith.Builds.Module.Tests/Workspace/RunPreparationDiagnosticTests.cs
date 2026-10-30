// <copyright file="RunPreparationDiagnosticTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.ModuleHosts.AppHost;

using Shouldly;

using Xunit;

/// <summary>Verifies late dependency diagnostics preserve identity without exporting arbitrary exception content.</summary>
public sealed class RunPreparationDiagnosticTests
{
    /// <summary>Verifies a real CLR display identity exports only its bounded assembly name.</summary>
    [Fact]
    public void ReportsStageAndAssemblyIdentityWithoutExceptionMessage()
    {
        FileNotFoundException failure = new(
            "sensitive-message",
            "Microsoft.IdentityModel.Tokens, Version=8.19.2.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
        RunPreparationDiagnostic.SupportSafeStatus(failure, "owner-preparation").ShouldBe("fault-FileNotFoundException;stage=owner-preparation;assembly=Microsoft.IdentityModel.Tokens");
    }

    /// <summary>Verifies filenames and malformed identities cannot export credential-bearing text.</summary>
    /// <param name="fileName">The untrusted missing filename.</param>
    [Theory]
    [InlineData("/private/sensitive-secret.dll")]
    [InlineData("sensitive-secret")]
    [InlineData("/private/sensitive-secret, Version=1.0.0.0")]
    [InlineData("sensitive secret, Version=1.0.0.0")]
    public void RefusesArbitraryFilenameMetadata(string fileName)
    {
        FileNotFoundException failure = new("sensitive-message", fileName);
        RunPreparationDiagnostic.SupportSafeStatus(failure, "writer-cutover").ShouldBe("fault-FileNotFoundException;stage=writer-cutover");
    }

    /// <summary>Verifies only closed machine statuses can retain a cutover failure message.</summary>
    /// <param name="stage">The owning startup stage.</param>
    /// <param name="message">The actual exception message.</param>
    /// <param name="expected">The permitted diagnostic.</param>
    [Theory]
    [InlineData("writer-cutover", "cutover-timeout;health-request-timeout", "cutover-timeout;health-request-timeout")]
    [InlineData("writer-cutover", "cutover-timeout;health-HTTP-503", "cutover-timeout;health-HTTP-503")]
    [InlineData("writer-cutover", "cutover-activation-HTTP-403", "cutover-activation-HTTP-403")]
    [InlineData("writer-cutover", "cutover-sensitive-secret", "fault-InvalidOperationException")]
    [InlineData("writer-cutover", "cutover-timeout;health-HTTP-401 sensitive-secret", "fault-InvalidOperationException")]
    [InlineData("owner-preparation", "cutover-sensitive-secret", "fault-InvalidOperationException")]
    public void PreservesOnlyClosedCutoverStatuses(string stage, string message, string expected) => RunPreparationDiagnostic.SupportSafeStatus(new InvalidOperationException(message), stage).ShouldBe(expected + ";stage=" + stage);
}
