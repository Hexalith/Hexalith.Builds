# CLAUDE.md - AI Assistant Instructions for Hexalith Projects

This file provides guidance for AI assistants (Claude, Copilot, Cursor, etc.) working with Hexalith .NET applications built using Domain-Driven Design (DDD) architecture.

## Technology Stack

- **.NET 9+** - Latest .NET framework
- **C# 13+** - Latest C# language features
- **DAPR 1.15+** - Distributed Application Runtime for microservices
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

```
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

```
feat(auth): add user authentication endpoint

Implement JWT-based authentication with refresh token support.
Includes validation middleware and token generation service.

Closes #123
```

```
fix(orders): correct tax calculation for international orders

BREAKING CHANGE: tax calculation now requires country code parameter
```

```
refactor(domain): simplify aggregate root base class
```

## Domain-Driven Design Architecture

### Project Structure

```
src/
├── Domain/                     # Domain Layer (innermost)
│   ├── Aggregates/            # Aggregate roots
│   ├── Entities/              # Domain entities
│   ├── ValueObjects/          # Value objects
│   ├── Events/                # Domain events
│   ├── Commands/              # Domain commands
│   └── Services/              # Domain services
├── Application/               # Application Layer
│   ├── Commands/              # Command handlers
│   ├── Queries/               # Query handlers
│   ├── Services/              # Application services
│   └── DTOs/                  # Data transfer objects
├── Infrastructure/            # Infrastructure Layer
│   ├── Persistence/           # Database implementations
│   ├── Messaging/             # Message bus implementations
│   └── External/              # External service integrations
└── Presentation/              # Presentation Layer (outermost)
    ├── Api/                   # REST/GraphQL APIs
    └── UI/                    # Blazor UI components
```

### DDD Patterns

#### Aggregates

- Aggregates are the consistency boundary
- Only reference other aggregates by ID
- Keep aggregates small and focused
- Use factory methods for complex creation logic

```csharp
/// <summary>
/// Order aggregate root managing order lifecycle.
/// </summary>
/// <param name="Id">The unique order identifier.</param>
/// <param name="CustomerId">The customer who placed the order.</param>
/// <param name="Items">The order line items.</param>
public sealed record Order(
    string Id,
    string CustomerId,
    IReadOnlyList<OrderItem> Items) : IAggregateRoot
{
    public static Order Create(string customerId, IEnumerable<OrderItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        return new Order(
            Id: Guid.NewGuid().ToString(),
            CustomerId: customerId,
            Items: items.ToList());
    }
}
```

#### Value Objects

- Immutable by design
- Equality based on values, not identity
- Use records for simple value objects

```csharp
/// <summary>
/// Represents a monetary value with currency.
/// </summary>
/// <param name="Amount">The monetary amount.</param>
/// <param name="Currency">The ISO currency code.</param>
public sealed record Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public Money Add(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException("Cannot add different currencies");
        return this with { Amount = Amount + other.Amount };
    }
}
```

#### Domain Events

- Use past tense naming (e.g., `OrderPlaced`, `PaymentReceived`)
- Events are immutable facts
- Include all relevant data for event handlers

```csharp
/// <summary>
/// Event raised when an order is placed.
/// </summary>
/// <param name="OrderId">The order identifier.</param>
/// <param name="CustomerId">The customer identifier.</param>
/// <param name="TotalAmount">The order total amount.</param>
/// <param name="OccurredAt">When the event occurred.</param>
public sealed record OrderPlaced(
    string OrderId,
    string CustomerId,
    Money TotalAmount,
    DateTimeOffset OccurredAt) : IDomainEvent;
```

#### Commands

- Use imperative naming (e.g., `PlaceOrder`, `CancelOrder`)
- Commands are requests that may fail
- Validate commands before processing

```csharp
/// <summary>
/// Command to place a new order.
/// </summary>
/// <param name="CustomerId">The customer placing the order.</param>
/// <param name="Items">The items to order.</param>
public sealed record PlaceOrder(
    string CustomerId,
    IReadOnlyList<OrderItemRequest> Items) : ICommand;
```

### CQRS Pattern

- Separate read and write models
- Commands modify state, queries read state
- Use MediatR or similar for handler dispatch

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

```
test/
├── Domain.Tests/           # Domain layer unit tests
├── Application.Tests/      # Application layer tests
├── Infrastructure.Tests/   # Infrastructure integration tests
└── Api.Tests/             # API integration tests
```

## DAPR Integration

### State Management

```csharp
public sealed class DaprOrderRepository(DaprClient daprClient) : IOrderRepository
{
    private const string StoreName = "statestore";

    public async Task<Order?> GetAsync(string id, CancellationToken ct)
        => await daprClient.GetStateAsync<Order>(StoreName, id, cancellationToken: ct);

    public async Task SaveAsync(Order order, CancellationToken ct)
        => await daprClient.SaveStateAsync(StoreName, order.Id, order, cancellationToken: ct);
}
```

### Pub/Sub Messaging

```csharp
public sealed class DaprEventPublisher(DaprClient daprClient) : IEventPublisher
{
    private const string PubSubName = "pubsub";

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : IDomainEvent
        => await daprClient.PublishEventAsync(PubSubName, typeof(TEvent).Name, @event, ct);
}
```

## Blazor UI Guidelines

### Fluent UI Components

Use Microsoft Fluent UI Blazor components for consistent UI:

```razor
@using Microsoft.FluentUI.AspNetCore.Components

<FluentCard>
    <FluentStack Orientation="Orientation.Vertical" VerticalGap="10">
        <FluentTextField @bind-Value="@_orderNumber" Label="Order Number" />
        <FluentButton Appearance="Appearance.Accent" OnClick="@SubmitOrder">
            Place Order
        </FluentButton>
    </FluentStack>
</FluentCard>
```

### Component Structure

```
Components/
├── Layout/                # Layout components
├── Shared/                # Shared/reusable components
├── Pages/                 # Page components
└── Features/              # Feature-specific components
    └── Orders/
        ├── OrderList.razor
        ├── OrderDetail.razor
        └── OrderForm.razor
```

## Build Configuration

This project uses centralized build configuration from `Hexalith.Builds`:

- `Hexalith.Build.props` - Common build properties
- `Hexalith.Package.props` - NuGet package properties
- `Directory.Packages.props` - Centralized package versions

## Additional Resources

- [Hexalith Documentation](https://github.com/Hexalith/Hexalith)
- [DAPR Documentation](https://docs.dapr.io/)
- [Fluent UI Blazor](https://www.fluentui-blazor.net/)
- [Angular Commit Guidelines](https://github.com/angular/angular/blob/main/contributing-docs/commit-message-guidelines.md)
