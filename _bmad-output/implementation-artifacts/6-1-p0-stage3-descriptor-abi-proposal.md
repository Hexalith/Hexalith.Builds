# 6.1-P0 Stage 3 descriptor ABI proposal

Status: **proposed; no owner approval or executable contract yet**  
Decision owner: Builds and Platform Owner (Jérôme Piquot)  
Selected dependency input: 2026-09-22 P1R acceptance, EventStore `3.106.0` at
`76051c70cbf868c40edc00ca0344fa5bd8879b69`, Builds selection
`ad52f350a2f0bc47849179ae17b4594dafff5363`. G-6 is an independent
qualification gate and currently does not approve G-4.

## Proposed `hexalith.module-descriptor.v1` ABI

1. Each `modules[].descriptorAssembly` names a built, repository-relative
   `.dll`. It exports exactly one public non-generic type with the exact full
   name `Hexalith.ModuleDescriptorV1` and a public static, parameterless
   `string Describe()` method. The return value is strict JSON with schema
   `hexalith.module-descriptor.v1`, `moduleId`, `domainServiceProject`, optional
   `apiProject`, and optional `uiAssembly` plus `uiMarkerType`. The method has
   no topology callback or access to runner credentials. All returned paths
   are canonical repository-relative paths. The runner rejects extra fields,
   duplicate keys, path escapes, missing files, schema drift, and a `moduleId`
   that differs from the manifest. A descriptor failure is a non-passing
   prerequisite or manifest diagnostic before any runtime mutation.
2. The optional manifest `ui.descriptorAssembly` exports
   `Hexalith.UiDescriptorV1.Describe()` with the same method signature. Its
   strict JSON names schema `hexalith.ui-descriptor.v1`, a UI project path,
   and the exact module UI assembly/marker type pairs. Every pair must refer
   to a declared module and a loadable marker type. No descriptor may choose
   ports, credentials, Dapr components, identity policy, persisted store, or
   cleanup behavior.
3. Builds owns the descriptor loader, strict validation, a generic AppHost,
   and a generic FrontComposer host. It resolves the selected EventStore
   project/package tuple, calls `AddHexalithEventStore` and
   `AddEventStoreDomainModule` for each declared project, and registers UI
   markers through FrontComposer's `AddHexalithDomain<T>` seam. The runner
   generates development credentials, assigns dynamic endpoints and a run ID
   to every resource, observes readiness and telemetry, and keeps all retained
   output metadata-only. No module code supplies an Aspire topology callback.
4. A descriptor assembly is loaded only from the validated checkout by a
   Builds-owned child process with no runner credentials. Its dependency
   resolution is confined to the descriptor's build output and the selected
   .NET runtime; incompatible dependencies fail closed. The child receives
   no topology handles and must return one JSON result within 10 seconds.
   The runner terminates a timed-out child before any Aspire resource starts.
5. A real two-module fixture must ship built descriptor DLLs, domain-service
   projects, and a FrontComposer UI marker. Live tests must prove both module
   hosts, EventStore persistence, Dapr, identity, UI registration, two
   concurrent run IDs, cancellation, and exact-run cleanup. `HXR003` remains
   the runner result until that implementation and evidence exist.

## Approval and implementation boundary

Approval must record this document's SHA-256, the current Builds revision,
ABI schema and entrypoint, Builds/Platform Owner name and date, and
EventStore/FrontComposer owner concurrence if their APIs require changes.
After approval, Builds can implement and test the adapter; approval alone does
not satisfy Stage 3.

Current source evidence: the manifest schema checks only a repository path;
the positive fixture's descriptor paths are JSON metadata. EventStore
`AddEventStoreDomainModule` requires an Aspire `ProjectResource`, and the
current FrontComposer UI host registers static Tenants/Parties marker types.
The selected P1R acceptance contains no descriptor decision. Local Aspire and
Dapr tools also differ from the selected G-6 tuple, so this host cannot supply
Stage 3 live evidence yet.
