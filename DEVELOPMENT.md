# Hexalith.Builds Development Guidance

This document contains repository-specific development guidance that does not
belong in the shared AI assistant entry points. Read it before changing
Hexalith build automation, shared .NET conventions, or release configuration.

## Hexalith.Builds References in GitHub Actions

For routine non-publication automation, use the current `main` branch:

```text
Hexalith/Hexalith.Builds/<action-path>@main
```

Publication workflows are the exception: pin the reusable release workflow to
a reviewed full commit SHA and pass that same literal as
`builds-execution-sha`. Do not use a branch, tag, expression, or repository
variable for release-tool identity.

## Running and debugging modules

`hexalith-module debug --manifest <declaration>` runs the active module and
root-declared source dependencies in Debug. It reports each project's launcher
process id (`dotnet run`); select the child application process in your debugger
to attach. A missing launcher id is reported by project name.

Startup uses one 600-second deadline from the request, including prerequisites
and the Platform model build. A justified module override must be from 0.001
through 3600 seconds; the largest declared override is used. Failed startup
reports each unready resource and a preserved diagnostics directory.
`hexalith-module down` stops only resources tagged for the selected run.

## Technology Stack

- .NET 10 or later and C# 14 or later
- Dapr 1.18 or later for distributed applications
- Microsoft Fluent UI Blazor for user-interface components
- xUnit with Shouldly for unit tests

## Commit Messages and Releases

All commits must follow Angular Conventional Commits so semantic-release can
calculate versions and publish packages correctly.

```text
<type>(<scope>): <short description>

<optional body>

<optional footer>
```

Supported types and their usual release effect are:

| Type | Use | Version bump |
| --- | --- | --- |
| `feat` | New feature | Minor |
| `fix` | Bug fix | Patch |
| `docs` | Documentation only | None |
| `style` | Formatting or whitespace | None |
| `refactor` | Refactoring without a feature or fix | None |
| `perf` | Performance improvement | Patch |
| `test` | Add or modify tests | None |
| `build` | Build system or dependency change | None |
| `ci` | CI/CD change | None |
| `revert` | Revert a previous commit | None |

Never use `chore`: `commitlint.config.mjs` rejects it, and it hides whether a
change was a fix, a build change or CI work.

Follow these rules:

1. Write the subject in imperative mood, beginning with lowercase unless it is
   a proper noun.
2. Aim for a subject under 50 characters and omit its final period.
3. Aim to wrap the body at 72 characters.
4. For a breaking change, include a `BREAKING CHANGE:` footer; it triggers a
   major version release.

Rules 2 and 3 are recommendations for readability. The enforced limits are the
`header-max-length` and `body-max-line-length` rules in `commitlint.config.mjs`,
both 200 characters; commitlint fails a commit only when it exceeds those.

For example:

```text
feat(auth): add user authentication endpoint

Implement JWT-based authentication with refresh token support.

Closes #123
```

```text
fix(orders): correct tax calculation for international orders

BREAKING CHANGE: tax calculation now requires country code parameter
```

## Domain-Driven Design Architecture

Hexalith modules use vertical-slice architecture. Each layer is a separate
NuGet package with a clear responsibility.

```text
{ModuleName}/
├── AspireHost/                         # Aspire orchestration host
├── HexalithApp/                        # Application templates
├── references/Hexalith.Builds/         # Build configuration
├── src/
│   ├── examples/                       # Example implementations
│   └── libraries/
│       ├── Domain/                     # Domain packages
│       ├── Application/                # Commands, queries, handlers
│       ├── Infrastructure/             # Servers and web/API projects
│       └── Presentation/               # UI components, pages, resources
└── test/                               # Unit and integration tests
```

The package dependency direction is always:

```text
Presentation (UI.Components, UI.Pages, Localizations)
    ↓
Infrastructure (Servers, ApiServer, WebServer, WebApp)
    ↓
Application (Commands, Requests, Handlers, Projections)
    ↓
Domain (Aggregates, Events)
    ↓
Abstractions (value objects and interfaces)
```

Typical package responsibilities:

| Package suffix | Layer | Responsibility |
| --- | --- | --- |
| none, `.Abstractions`, `.Events` | Domain | Aggregates, entities, value objects, domain interfaces and events |
| `.Commands`, `.Requests`, `.Application`, `.Projections` | Application | CQRS definitions, handlers, services, and read models |
| `.Servers`, `.ApiServer`, `.WebServer`, `.WebApp` | Infrastructure | Shared server utilities and web/API hosts |
| `.UI.Components`, `.UI.Pages`, `.Localizations` | Presentation | UI components, pages, and localization resources |

## C# Standards

Use primary constructors for classes and records when they make the dependency
or value shape clearer. Document every public, protected, and internal member
with XML documentation. For a record with a primary constructor, document its
properties through `<param>` tags, for example:

```csharp
/// <summary>Represents a customer in the system.</summary>
/// <param name="Id">The unique customer identifier.</param>
/// <param name="Email">The customer's email address.</param>
public sealed record Customer(string Id, string Email);
```

Use these naming conventions:

| Element | Convention | Example |
| --- | --- | --- |
| Interface | `I` prefix | `IOrderRepository` |
| Asynchronous method | `Async` suffix | `GetOrderAsync` |
| Event handler | `Handler` suffix | `OrderPlacedHandler` |
| Command | Imperative verb | `PlaceOrder` |
| Event | Past tense | `OrderPlaced` |
| Value object | Noun | `Money` |
| Aggregate | Domain noun | `Order` |

For validation and errors:

- Use `ArgumentException.ThrowIfNullOrWhiteSpace()` for string validation.
- Use `ArgumentNullException.ThrowIfNull()` for null checks.
- Create domain-specific exceptions for business-rule violations.
- Use a Result pattern for expected failures.

Use source-generated logging with `LoggerMessageAttribute` for high-frequency
logs. The method must be `static partial`, `ILogger` must be its first
parameter, and an `Exception` (if present) must be its second parameter before
the values. Declare the containing class `partial` and use named structured
placeholders such as `{OrderId}`.

## Testing

Use xUnit and Shouldly. Name test methods in PascalCase and organize tests by
aggregate and then command, event, query, or aggregate behavior:

```text
test/
└── Hexalith.{Module}.Tests/
    └── {Aggregate}/
        ├── {Command}Tests.cs
        ├── {Event}Tests.cs
        ├── {Query}Tests.cs
        └── {Aggregate}Tests.cs
```

## Build Configuration and Running Applications

Consuming modules use the centralized configuration from `Hexalith.Builds`:

- `Hexalith.Build.props` for common build properties.
- `Hexalith.Package.props` for NuGet package properties.
- `Props/Directory.Packages.props` for centralized package versions.

To run a consuming module's application, use its Aspire host:

```bash
cd AspireHost
dotnet run
```

## Module-owned v2 tests

A v2 module may add an optional `tests` collection beside `identity`, `runtime`,
`surfaces`, `integration`, and `lifecycle`. Select exactly one named test with
`hexalith-module test --manifest module.json --profile retention --mode source`.
Selection is validated before composition. For example:

```json
"tests": [
  {
    "name": "retention",
    "executor": "python3",
    "entry": "tools/verify_retention.py",
    "reportFormat": "trx",
    "executionTimeoutSeconds": 14400,
    "stopTimeoutSeconds": 900,
    "mode": "source"
  }
]
```

The contract is closed: executable names, arbitrary arguments, environment or
credential inputs cannot be supplied. Executors are `python3`, `vstest`, and
`mtp`. Entries must exist in the declaring module, end in `.py` or `.csproj`
respectively, and cannot traverse symlinks, nested repositories or `references`.
Python currently supports source mode only; its fixed invocation is
`python3 -E -s -B <owned-entry>`. Adjacent helper imports work. Only the selected
entry and declaration bytes are bound; this is not a hermetic Python dependency
or interpreter pin. The .NET executors keep their native TRX switches. Package
mode continues to use the existing package mapping for native .NET tests.
Filters, attachment to existing runs and hosted environment reuse are refused.

Python receives the existing `HEXALITH_G4_*` native-test handoff and writes the
native TRX report to `HEXALITH_G4_TEST_REPORT_PATH`. No stdout protocol substitutes
for that report. The existing TRX parser requires a `TestRun` containing one
`ResultSummary` and complete consistent `Counters`. The process must exit zero,
the total must be positive, every selected test must pass, and no failed,
skipped, inconclusive or not-executed work may earn a selected-test pass.
Reports containing handoff credentials are rejected and removed, without a
retained copy. Safe reports, including failed reports, remain in private durable
run diagnostics and may be retained beside `--evidence` artifacts.

The execution lifetime is explicitly 1–14,400 seconds from the initial request;
startup and setup consume it. A monotonic command cancellation timer prevents a
wall-clock reversal from extending that lifetime. The plan, state, handoff and
evidence bind the precise original UTC execution expiry. JWT expiry rounds up
to the next whole second (less than one additional second) so credentials cover
the full execution lifetime. Execution still stops at its precise deadline;
stopping uses the runner's own process/resource
ownership and does not require module credentials. A separate total stopping
bound is 1–900 seconds. The independent startup default remains 600 seconds and
legacy native tests retain their existing 900-second test limit.

Local source failures retain surviving resources until explicit `down --run-id`;
retained AppHosts ignore parent-input EOF, keep output-observation metadata, and survive startup
failure. CI failures and successful tests clean owned resources before rendering
results. Withdrawal never earns test completion, and a previously recorded
failure remains the first outcome. Cleanup errors and unavailable scans disclose
remaining or unknown resources. The durable `selected-test.json` and safe native
report survive successful cleanup. Expected Platform and mapping identities are
labelled as declarations; absent loaded candidate/backend observations stay null
and these runner records grant no qualification credit.

This prerequisite does not supply PostgreSQL composition, a pinned Python tool,
backend capability and clock binding, direct packaged-candidate alignment, or a
Memories adapter. Those integration requirements must be implemented and observed
before accepting a PostgreSQL retention qualification. Source Debug runner
results and offline process-double tests do not substitute for that evidence.

Selected native reports are limited to 4 MiB and inspected under at most 30
seconds of the single declared stopping allowance. Symlinks, special files,
malformed XML and uninspectable output are discarded. XML encoding is honored
before credential detection, including cancellation output. Retained AppHost
output is limited to 128 fixed metadata observations per process. Offline tests
exercise the actual retention wait and bounded stop delegates; real Aspire,
PostgreSQL and resource orchestration remain integration checks requiring a
separately authorized workload run.
