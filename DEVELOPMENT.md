# Hexalith.Builds Development Guidance

This document contains repository-specific development guidance that does not
belong in the shared AI assistant entry points. Read it before changing
Hexalith build automation, shared .NET conventions, or release configuration.

## Hexalith.Builds References in GitHub Actions

When adding or updating a GitHub workflow or action reference to a
`Hexalith.Builds` action or reusable workflow, use the current `main` branch:

```text
Hexalith/Hexalith.Builds/<action-path>@main
```

This is an intentional exception to the third-party action SHA-pinning rule.
Do not pin `Hexalith.Builds` actions or reusable workflows to a release tag or
commit SHA.

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
| `chore` | Maintenance | None |

Follow these rules:

1. Write the subject in imperative mood, beginning with lowercase unless it is
   a proper noun.
2. Keep the subject below 50 characters and omit its final period.
3. Wrap the body at 72 characters.
4. For a breaking change, include a `BREAKING CHANGE:` footer; it triggers a
   major version release.

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
