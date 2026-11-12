# .NET 11 migration

Active Builds projects, published tools, runner hosts, the CI sample and executable
module fixtures target `net11.0`, using SDK 11.0.100. Install .NET 11 before
installing the tools. Package schemas and host payloads now live beneath
`tools/net11.0/any`; schema identities and backward-compatible document formats
are unchanged. This is a breaking runtime requirement for tool consumers.

## Dependency selections

The shared catalog advances OAuth providers, ASP.NET Core (including Authorization
and SignalR), Identity Stores and Immutable to 11.0.0. MSBuild tasks and tests use
18.12.2. ASP.NET Core OpenApi 11 requires Microsoft.OpenApi `[3.11.0,4.0.0)`;
select OpenApi 3.11.0 and Swashbuckle 11.0.0 together. Package metadata was checked
against NuGet.org during migration. Consumers of the shared catalog must retarget
their hosts before adopting these framework-dependent package selections.

`Microsoft.Extensions.Identity.Http` 10.0.9 and `Serilog.Sinks.Browser` 8.0.0
returned 404 from the official NuGet V3 flat-container indexes. A tracked active
source scan found only their central declarations, with no PackageReference
consumer or implementation to migrate. Remove these unused entries; no replacement
is needed. Historical audit observations remain historical evidence.

## Retained compatibility

- Workspace MSBuild fixtures deliberately retain .NET 10 inputs, including
  multi-target identity disagreement controls.
- Catalog metadata tests retain older dependency groups and nearest-compatible
  framework cases. Release fixtures retain net9.0/net10.0 multi-target cases.
- Byte-bound acceptance artifacts, prior runtime-toolchain policies, qualification
  records and release evidence retain their original SDK/framework identities.
- SDK 10.0.404 remains installed alongside SDK 11 in relevant CI jobs for offline
  older-framework reference packs and runtimes. It does not select the active SDK.
- CommunityToolkit.Aspire.Hosting.Dapr remains 13.6.2-beta.959 because the published
  EventStore 4.2.0 package requires that integration. Aspire Keycloak remains on its
  published preview channel. Neither hold is removed by retargeting to .NET 11.
- AngleSharp remains 1.10.2 for bunit 2.15.2's BeforeToggle implementation;
  the host framework change does not establish compatibility with AngleSharp 2.

## Validation and publication

The migration retains warnings-as-errors, package auditing, all CI lanes, release
security checks and environment protections. Local Debug builds and individual
product test projects validate the source. Exact-main CI and the shared release
workflow validate Release artifacts before publication. Final release and NuGet
installation receipts are reported with the completed migration.
