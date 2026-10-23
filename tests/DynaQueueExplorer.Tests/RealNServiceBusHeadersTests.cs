using System.Text.Json;
using DynaQueueExplorer.NServiceBus;

namespace DynaQueueExplorer.Tests;

/// <summary>
/// Headers captured from messages that NServiceBus 10.2 moved to the error queue
/// (tools/NsbSimulator), so the decoder is checked against the real wire format.
/// </summary>
public class RealNServiceBusHeadersTests
{
    private static Dictionary<string, string?> Load(string name) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;

    [Fact]
    public void Business_failure_with_exception_data()
    {
        var info = NServiceBusMessageDecoder.TryDecode(Load("nsb10-payment-declined.headers.json"));

        Assert.NotNull(info);
        Assert.Equal(NServiceBusMessageKind.FailedMessage, info.Kind);
        Assert.Equal("Publish", info.Intent);
        Assert.Equal("10.2.9", info.NServiceBusVersion);
        Assert.Equal(["OrderPlaced", "IOrderEvent"], info.MessageTypes.Select(t => t.Name));
        Assert.Equal("Simulation.Sales", info.OriginatingEndpoint);
        Assert.NotNull(info.TimeSent);

        var failure = info.Failure!;
        Assert.Equal("Simulation.Billing.PaymentDeclinedException", failure.ExceptionType);
        Assert.Equal("Simulation.Billing", failure.FailedQueue);
        Assert.Equal("Simulation.Billing.OrderPlacedHandler", failure.Handler);
        Assert.NotNull(failure.TimeOfFailure);
        // NServiceBus removes the retry counter when moving to the error queue; only the timestamp survives.
        Assert.Null(failure.DelayedRetries);
        Assert.NotNull(failure.LastDelayedRetryAt);
        Assert.Equal(["Amount", "CustomerId", "OrderId"], failure.Data.Keys);

        Assert.Equal("Exception data", info.HeaderGroups[0].Name);
        Assert.Equal(3, info.HeaderGroups[0].Headers.Count);
        Assert.DoesNotContain(info.HeaderGroups.Single(g => g.Name == "Failure").Headers, h => h.Label == "OrderId");
        Assert.Contains(info.HeaderGroups.Single(g => g.Name == "Failure").Headers, h => h is { Key: "NServiceBus.ExceptionInfo.Data.Handler type", Label: "Handler type" });
    }

    [Fact]
    public void Saga_failure_is_identified_by_the_failing_handler()
    {
        var info = NServiceBusMessageDecoder.TryDecode(Load("nsb10-saga-failure.headers.json"))!;

        Assert.Equal("Simulation.Shipping.ShippingPolicy", info.Failure!.Handler);
        Assert.Empty(info.Failure.Data);
        Assert.DoesNotContain(info.HeaderGroups, g => g.Name == "Exception data");
    }
}
