// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Hexalith.Builds.ModuleHosts.AppHost;

// Builds-owned AppHost. It composes exactly one G-4 run from the runner-written, metadata-only run plan:
// run-scoped Redis, Dapr placement and scheduler, the EventStore host, every module domain service, and the
// generic FrontComposer host. No module supplies topology, ports, credentials, Dapr components, or identity.
return await AppHostRunner.RunAsync(args).ConfigureAwait(false);