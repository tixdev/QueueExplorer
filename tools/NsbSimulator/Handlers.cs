using System.Collections.Concurrent;
using System.Net.Sockets;
using NServiceBus;
using Simulation.Messages;

namespace Simulation.Sales
{
    public class PlaceOrderHandler : IHandleMessages<PlaceOrder>
    {
        public Task Handle(PlaceOrder message, IMessageHandlerContext context)
        {
            if (message.Scenario == Scenario.NullReference)
            {
                OrderLine? missing = message.Lines.FirstOrDefault(l => l.Sku == "does-not-exist");
                _ = missing!.Quantity; // A real-looking bug: dereferencing a lookup that can miss.
            }

            return context.Publish(new OrderPlaced(message.OrderId, message.CustomerId, message.Amount, message.Currency, message.Scenario,
                DateTimeOffset.UtcNow));
        }
    }
}

namespace Simulation.Billing
{
    public class PaymentDeclinedException(Guid orderId, string reason) : Exception($"Payment for order {orderId} was declined: {reason}.")
    {
        public Guid OrderId { get; } = orderId;
    }

    public class OrderPlacedHandler : IHandleMessages<OrderPlaced>
    {
        // Attempts per message, to make transient failures succeed after a few immediate retries.
        private static readonly ConcurrentDictionary<string, int> Attempts = new();

        public async Task Handle(OrderPlaced message, IMessageHandlerContext context)
        {
            switch (message.Scenario)
            {
                case Scenario.Transient when Attempts.AddOrUpdate(context.MessageId, 1, (_, n) => n + 1) <= 2:
                    throw new TimeoutException("Deadlock victim while reserving funds; the transaction can be retried.");

                case Scenario.PaymentDeclined:
                    var declined = new PaymentDeclinedException(message.OrderId, "insufficient funds");
                    declined.Data["OrderId"] = message.OrderId.ToString();
                    declined.Data["CustomerId"] = message.CustomerId.ToString();
                    declined.Data["Amount"] = $"{message.Amount} {message.Currency}";
                    throw declined;

                case Scenario.GatewayTimeout:
                    throw new HttpRequestException("Payment gateway did not answer within 30 seconds.",
                        new SocketException((int)SocketError.TimedOut));
            }

            await context.Publish(new OrderBilled(message.OrderId, $"PAY-{Random.Shared.Next(100000, 999999)}"));
        }
    }
}

namespace Simulation.Shipping
{
    public class ShippingPolicyData : ContainSagaData
    {
        public Guid OrderId { get; set; }
        public bool Placed { get; set; }
        public bool Billed { get; set; }
    }

    /// <summary>Ships an order once it has been both placed and billed.</summary>
    public class ShippingPolicy : Saga<ShippingPolicyData>,
        IAmStartedByMessages<OrderPlaced>,
        IAmStartedByMessages<OrderBilled>
    {
        protected override void ConfigureHowToFindSaga(SagaPropertyMapper<ShippingPolicyData> mapper) =>
            mapper.MapSaga(s => s.OrderId)
                .ToMessage<OrderPlaced>(m => m.OrderId)
                .ToMessage<OrderBilled>(m => m.OrderId);

        public Task Handle(OrderPlaced message, IMessageHandlerContext context)
        {
            if (message.Scenario == Scenario.SagaFailure)
                throw new InvalidOperationException($"Shipping address for order {message.OrderId} failed validation: postcode '000' is not valid.");

            Data.Placed = true;
            return TryShip();
        }

        public Task Handle(OrderBilled message, IMessageHandlerContext context)
        {
            Data.Billed = true;
            return TryShip();
        }

        private Task TryShip()
        {
            if (Data.Placed && Data.Billed)
                MarkAsComplete();
            return Task.CompletedTask;
        }
    }
}
