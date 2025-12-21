# CLAUDE.md - AI Assistant Instructions for Hexalith Projects

This file provides guidance for AI assistants (Claude, Copilot, Cursor, etc.) working with Hexalith .NET applications built using Domain-Driven Design (DDD) architecture.

## Technology Stack

- **.NET 10+** - Latest .NET framework
- **C# 14+** - Latest C# language features
- **DAPR 1.16+** - Distributed Application Runtime for microservices
- **Microsoft Fluent UI Blazor** - UI component library for Blazor applications
- **XUnit + Shouldly** - Unit testing framework and assertion library

## Hexalith Ecosystem

The Hexalith ecosystem consists of multiple interconnected repositories:

| Repository | Description |
|------------|-------------|
| [Hexalith](https://github.com/Hexalith/Hexalith) | Core framework and shared components |
| [Hexalith.Domains](https://github.com/Hexalith/Hexalith.Domains) | Domain models and business logic |
| [Hexalith.PolymorphicSerializations](https://github.com/Hexalith/Hexalith.PolymorphicSerializations) | Polymorphic JSON serialization support |
| [Hexalith.IdentityStores](https://github.com/Hexalith/Hexalith.IdentityStores) | Identity and authentication stores |
| [Hexalith.Builds](https://github.com/Hexalith/Hexalith.Builds) | Build configurations and CI/CD templates |
| [HexalithApp](https://github.com/Hexalith/HexalithApp) | Main application templates |
| [Hexalith.NetAspire](https://github.com/Hexalith/Hexalith.NetAspire) | .NET Aspire integration |
| [Hexalith.Security](https://github.com/Hexalith/Hexalith.Security) | Security and authorization components |

## Commit Message Guidelines

**All commit messages MUST follow the [Angular Conventional Commits](https://github.com/angular/angular/blob/main/contributing-docs/commit-message-guidelines.md) specification** for semantic-release automated version management and package publishing.

### Commit Message Format

```text
<type>(<scope>): <short description>

<optional body>

<optional footer>
```

### Types

| Type | Description | Version Bump |
|------|-------------|--------------|
| `feat` | New feature | Minor |
| `fix` | Bug fix | Patch |
| `docs` | Documentation only | None |
| `style` | Code style (formatting, whitespace) | None |
| `refactor` | Code refactoring (no feature/fix) | None |
| `perf` | Performance improvements | Patch |
| `test` | Adding or modifying tests | None |
| `build` | Build system or dependencies | None |
| `ci` | CI/CD configuration | None |
| `chore` | Miscellaneous maintenance | None |

### Rules

1. Use imperative mood in short description (e.g., "add" not "added")
2. Start description with lowercase (unless proper noun)
3. No period at end of short description
4. Keep short description under 50 characters
5. Wrap body at 72 characters
6. Use `BREAKING CHANGE:` in footer for breaking changes (triggers major version)

### Examples

```text
feat(auth): add user authentication endpoint

Implement JWT-based authentication with refresh token support.
Includes validation middleware and token generation service.

Closes #123
```

```text
fix(orders): correct tax calculation for international orders

BREAKING CHANGE: tax calculation now requires country code parameter
```

```text
refactor(domain): simplify aggregate root base class
```

## Domain-Driven Design Architecture

### Project Structure

Hexalith modules follow a **vertical slice architecture** with separate NuGet packages per layer. Each module (e.g., `Hexalith.Documents`) is organized as follows:

```text

{ModuleName}/
├── AspireHost/                         # .NET Aspire orchestration host
├── HexalithApp/                        # Application templates (submodule)
├── Hexalith.Builds/                    # Build configuration (submodule)
├── src/
│   ├── examples/
│   │   └── Hexalith.{Module}.Example/                       # Example implementation
│   ├── libraries/                                           # NuGet package libraries
│   │   ├── Domain/                                          # Domain layer packages
│   │   │   ├── Hexalith.{Module}/                           # Aggregate roots, entities, state
│   │   │   ├── Hexalith.{Module}.Abstractions/              # Domain interfaces, value objects
│   │   │   └── Hexalith.{Module}.Events/                    # Domain events
│   │   ├── Application/                                     # Application layer packages
│   │   │   ├── Hexalith.{Module}.Commands/                  # CQRS command definitions
│   │   │   ├── Hexalith.{Module}.Requests/                  # Queries & view models
│   │   │   ├── Hexalith.{Module}.Application/               # Command & query handlers
│   │   │   ├── Hexalith.{Module}.Application.Abstractions/  # Application interfaces
│   │   │   └── Hexalith.{Module}.Projections/               # Read model projections
│   │   ├── Infrastructure/                                  # Infrastructure layer packages
│   │   │   ├── Hexalith.{Module}.Servers/                   # Shared server utilities
│   │   │   ├── Hexalith.{Module}.ApiServer/                 # REST API controllers
│   │   │   ├── Hexalith.{Module}.WebServer/                 # Web server implementation
│   │   │   └── Hexalith.{Module}.WebApp/                    # Blazor web application
│   │   └── Presentation/                                    # Presentation layer packages
│   │       ├── Hexalith.{Module}.UI.Components/             # Reusable Blazor components
│   │       ├── Hexalith.{Module}.UI.Pages/                  # Blazor page components
│   │       └── Hexalith.{Module}.Localizations/             # language resources
│   └── servers/                        # Docker/deployment projects
└── test/
    └── Hexalith.{Module}.Tests/        # Unit & integration tests
```

### Layer Organization by Package

Each layer is a separate NuGet package with clear responsibilities:

| Package | Layer | Contents |
|---------|-------|----------|
| `Hexalith.{Module}` | Domain | Aggregate roots, entities, value objects, state |
| `Hexalith.{Module}.Abstractions` | Domain | Domain nterfaces, shared value objects |
| `Hexalith.{Module}.Events` | Domain | Domain events |
| `Hexalith.{Module}.Commands` | Application | Command definitions, validators |
| `Hexalith.{Module}.Requests` | Application | Query definitions, view models |
| `Hexalith.{Module}.Application` | Application | Command & query handlers, services |
| `Hexalith.{Module}.Projections` | Application | Event projections, read model handlers |
| `Hexalith.{Module}.Servers` | Infrastructure | Shared server utilities |
| `Hexalith.{Module}.ApiServer` | Infrastructure | REST API controllers, modules |
| `Hexalith.{Module}.WebServer` | Infrastructure | Web server implementation |
| `Hexalith.{Module}.WebApp` | Infrastructure | Blazor web application |
| `Hexalith.{Module}.UI.Components` | Presentation | Reusable Blazor component library |
| `Hexalith.{Module}.UI.Pages` | Presentation | Page-level Blazor components |
| `Hexalith.{Module}.Localizations` | Domain | i18n resources |

### Package Dependency Flow

```text
Presentation (UI.Components, UI.Pages, Localizations)
    ↓
Infrastructure (Servers, ApiServer, WebServer, WebApp)
    ↓
Application (Commands, Requests, Handlers, Projections)
    ↓
Domain (Aggregates, Events)
    ↓
Abstractions (value objects & interfaces)
```

## C# Coding Standards

### Primary Constructors

Use primary constructors for classes and records when possible:

```csharp
// Preferred: Primary constructor
public sealed class OrderService(
    IOrderRepository repository,
    IEventPublisher publisher) : IOrderService
{
    public async Task<Order> GetAsync(string id, CancellationToken ct)
        => await repository.GetAsync(id, ct);
}

// Avoid: Traditional constructor with field assignments
public sealed class OrderService : IOrderService
{
    private readonly IOrderRepository _repository;
    private readonly IEventPublisher _publisher;

    public OrderService(IOrderRepository repository, IEventPublisher publisher)
    {
        _repository = repository;
        _publisher = publisher;
    }
}
```

### XML Documentation

Use XML documentation for all public, protected, and internal members:

```csharp
/// <summary>
/// Handles order placement operations.
/// </summary>
/// <param name="repository">The order repository.</param>
/// <param name="publisher">The event publisher.</param>
public sealed class OrderCommandHandler(
    IOrderRepository repository,
    IEventPublisher publisher)
{
    /// <summary>
    /// Places a new order for a customer.
    /// </summary>
    /// <param name="command">The place order command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when command is null.</exception>
    public async Task<Order> HandleAsync(
        PlaceOrder command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Implementation
    }
}
```

### Record Properties Documentation

For records with primary constructors, document properties using `<param>` tags:

```csharp
/// <summary>
/// Represents a customer in the system.
/// </summary>
/// <param name="Id">The unique customer identifier.</param>
/// <param name="Email">The customer's email address.</param>
/// <param name="Name">The customer's full name.</param>
/// <param name="CreatedAt">When the customer was created.</param>
public sealed record Customer(
    string Id,
    string Email,
    string Name,
    DateTimeOffset CreatedAt);
```

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Interfaces | Prefix with `I` | `IOrderRepository` |
| Async methods | Suffix with `Async` | `GetOrderAsync` |
| Event handlers | Suffix with `Handler` | `OrderPlacedHandler` |
| Commands | Imperative verb | `PlaceOrder`, `CancelOrder` |
| Events | Past tense | `OrderPlaced`, `OrderCancelled` |
| Value objects | Noun | `Money`, `Address`, `Email` |
| Aggregates | Domain noun | `Order`, `Customer`, `Product` |

### Error Handling

- Use `ArgumentException.ThrowIfNullOrWhiteSpace()` for string validation
- Use `ArgumentNullException.ThrowIfNull()` for null checks
- Create domain-specific exceptions for business rule violations
- Use Result pattern for expected failures

```csharp
public sealed class InsufficientStockException(
    string productId,
    int requested,
    int available)
    : DomainException($"Product {productId}: requested {requested}, available {available}")
{
    public string ProductId { get; } = productId;
    public int Requested { get; } = requested;
    public int Available { get; } = available;
}
```

## Testing Standards

### Unit Tests with XUnit and Shouldly

```csharp
public sealed class OrderTests
{
    [Fact]
    public void Create_WithValidData_ShouldReturnOrder()
    {
        // Arrange
        var customerId = "customer-123";
        var items = new[] { new OrderItem("product-1", 2, new Money(10m, "USD")) };

        // Act
        var order = Order.Create(customerId, items);

        // Assert
        order.CustomerId.ShouldBe(customerId);
        order.Items.Count.ShouldBe(1);
        order.Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_WithInvalidCustomerId_ShouldThrow(string? customerId)
    {
        // Arrange
        var items = Array.Empty<OrderItem>();

        // Act & Assert
        Should.Throw<ArgumentException>(() => Order.Create(customerId!, items));
    }
}
```

### Test Organization

```text
test/
└── Hexalith.{Module}.Tests/    # All tests for the module
    ├── {Aggregate}/            # Tests organized by aggregate
    │   ├── {Command}Tests.cs   # Command tests
    │   ├── {Event}Tests.cs     # Command tests
    │   ├── {Query}Tests.cs     # Query tests
    │   └── {Aggregate}Tests.cs # Aggregate tests
    └── ...
```

## Build Configuration

This project uses centralized build configuration from `Hexalith.Builds`:

- `Hexalith.Build.props` - Common build properties
- `Hexalith.Package.props` - NuGet package properties
- `Directory.Packages.props` - Centralized package versions

## Start the application

```bash
cd AspireHost
dotnet run
```

## Additional Resources

- [Hexalith Documentation](https://github.com/Hexalith/Hexalith)
- [DAPR Documentation](https://docs.dapr.io/)
- [Fluent UI Blazor](https://www.fluentui-blazor.net/)
- [Commit Guidelines](https://github.com/angular/angular/blob/main/contributing-docs/commit-message-guidelines.md)
