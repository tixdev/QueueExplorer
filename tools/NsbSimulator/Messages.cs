namespace Simulation.Messages;

/// <summary>Drives which failure, if any, the handlers along the way simulate.</summary>
public enum Scenario
{
    Ok,
    /// <summary>Billing fails the first attempts, then succeeds thanks to immediate retries.</summary>
    Transient,
    /// <summary>Billing rejects the payment: business exception with data, ends up in the error queue.</summary>
    PaymentDeclined,
    /// <summary>Billing times out calling the payment gateway (exception with an inner exception).</summary>
    GatewayTimeout,
    /// <summary>Bug in the Sales handler.</summary>
    NullReference,
    /// <summary>The Shipping saga fails: failure headers include saga information.</summary>
    SagaFailure,
    /// <summary>A corrupted body: deserialization fails and the message skips retries.</summary>
    Poison,
}

public record PlaceOrder(Guid OrderId, int CustomerId, decimal Amount, string Currency, Scenario Scenario, OrderLine[] Lines) : ICommand;

public record OrderLine(string Sku, int Quantity, decimal UnitPrice);

public interface IOrderEvent : IEvent
{
    Guid OrderId { get; }
}

public record OrderPlaced(Guid OrderId, int CustomerId, decimal Amount, string Currency, Scenario Scenario, DateTimeOffset PlacedAt) : IOrderEvent;

public record OrderBilled(Guid OrderId, string PaymentReference) : IOrderEvent;
