// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Hexalith.EventStore.DomainService;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.AddEventStoreDomainService();

WebApplication app = builder.Build();
app.UseEventStoreDomainService();

await app.RunAsync().ConfigureAwait(false);