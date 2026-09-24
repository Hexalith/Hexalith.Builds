// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Hexalith.Builds.ModuleHosts.UiHost;
using Hexalith.Builds.ModuleHosts.UiHost.Components;
using Hexalith.FrontComposer.Shell.Extensions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.FluentUI.AspNetCore.Components;

// Builds-owned generic FrontComposer host. The runner-owned AppHost names the module UI marker
// assemblies and types; this host loads them and registers each one through the FrontComposer
// AddHexalithDomain<T> seam. No module supplies a host, port, credential, or topology.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Run-scoped host: no data-protection key ring is persisted outside the run.
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAuthorization();
builder.Services.AddRazorComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddHexalithFrontComposerQuickstart();
UiMarkerRegistrar.RegisterConfiguredMarkers(builder.Services, builder.Configuration);
builder.Services.AddHealthChecks();

WebApplication app = builder.Build();

app.UseAntiforgery();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>();

await app.RunAsync().ConfigureAwait(false);