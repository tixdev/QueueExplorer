using DynaQueueExplorer.NServiceBus;

namespace DynaQueueExplorer.Tests;

public class NServiceBusMessageDecoderTests
{
    private static Dictionary<string, string?> Headers(params (string Key, string? Value)[] headers) =>
        headers.ToDictionary(h => h.Key, h => h.Value);

    [Fact]
    public void Plain_messages_are_not_recognized() =>
        Assert.Null(NServiceBusMessageDecoder.TryDecode(Headers(("x-source", "curl"), ("traceparent", "00-abc"))));

    [Fact]
    public void Decodes_a_published_event()
    {
        var info = NServiceBusMessageDecoder.TryDecode(Headers(
            ("NServiceBus.MessageId", "m-1"),
            ("NServiceBus.MessageIntent", "Publish"),
            ("NServiceBus.EnclosedMessageTypes",
                "Sales.Messages.Events.OrderPlaced, Sales.Messages, Version=1.4.0.0, Culture=neutral, PublicKeyToken=null;" +
                "Sales.Messages.Events.IOrderEvent, Sales.Messages, Version=1.4.0.0, Culture=neutral, PublicKeyToken=null"),
            ("NServiceBus.TimeSent", "2026-10-22 14:03:11:482913 Z"),
            ("NServiceBus.OriginatingEndpoint", "Sales.Api"),
            ("NServiceBus.ContentType", "application/json"),
            ("$.diagnostics.hostdisplayname", "web-01"),
            ("traceparent", "00-abc")));

        Assert.NotNull(info);
        Assert.Equal(NServiceBusMessageKind.Message, info.Kind);
        Assert.Equal("Publish", info.Intent);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 14, 3, 11, TimeSpan.Zero).AddTicks(4829130), info.TimeSent);
        Assert.Equal("web-01", info.OriginatingHost);
        Assert.Null(info.Failure);

        Assert.Collection(info.MessageTypes,
            t =>
            {
                Assert.Equal("OrderPlaced", t.Name);
                Assert.Equal("Sales.Messages.Events", t.Namespace);
                Assert.Equal("Sales.Messages", t.Assembly);
                Assert.Equal("1.4.0.0", t.Version);
            },
            t => Assert.Equal("IOrderEvent", t.Name));

        Assert.Equal(["Diagnostics", "NServiceBus", "Other"], info.HeaderGroups.Select(g => g.Name));
        Assert.Contains(info.HeaderGroups[1].Headers, h => h is { Key: "NServiceBus.MessageIntent", Label: "MessageIntent" });
    }

    [Fact]
    public void Decodes_a_failed_message()
    {
        var info = NServiceBusMessageDecoder.TryDecode(Headers(
            ("NServiceBus.MessageId", "m-2"),
            ("NServiceBus.ExceptionInfo.ExceptionType", "System.TimeoutException"),
            ("NServiceBus.ExceptionInfo.Message", "timed out"),
            ("NServiceBus.ExceptionInfo.StackTrace", "at X.Handle()"),
            ("NServiceBus.FailedQ", "Sales.OrderService"),
            ("NServiceBus.TimeOfFailure", "2026-10-22 14:05:00:000000 Z"),
            ("NServiceBus.ProcessingEndpoint", "Sales.OrderService"),
            ("NServiceBus.Retries", "3")));

        Assert.NotNull(info);
        Assert.Equal(NServiceBusMessageKind.FailedMessage, info.Kind);
        Assert.NotNull(info.Failure);
        Assert.Equal("System.TimeoutException", info.Failure.ExceptionType);
        Assert.Equal("Sales.OrderService", info.Failure.FailedQueue);
        Assert.Equal(3, info.Failure.DelayedRetries);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 14, 5, 0, TimeSpan.Zero), info.Failure.TimeOfFailure);
        Assert.Equal("Failure", info.HeaderGroups[0].Name);
        Assert.Contains(info.HeaderGroups[0].Headers, h => h is { Key: "NServiceBus.ExceptionInfo.Message", Label: "Message" });
    }

    [Theory]
    [InlineData("Subscribe", NServiceBusMessageKind.Subscription)]
    [InlineData("Unsubscribe", NServiceBusMessageKind.Subscription)]
    public void Classifies_subscription_messages(string intent, NServiceBusMessageKind expected) =>
        Assert.Equal(expected, NServiceBusMessageDecoder.TryDecode(Headers(("NServiceBus.MessageId", "m"), ("NServiceBus.MessageIntent", intent)))!.Kind);

    [Fact]
    public void Classifies_control_messages() =>
        Assert.Equal(NServiceBusMessageKind.ControlMessage,
            NServiceBusMessageDecoder.TryDecode(Headers(("NServiceBus.ControlMessage", "True")))!.Kind);

    [Fact]
    public void Nested_and_generic_type_names()
    {
        var types = NServiceBusMessageDecoder.ParseMessageTypes(
            "Sales.Contracts+Commands+PlaceOrder, Sales;Sales.Envelope`1[[Sales.Order, Sales, Version=1.0.0.0]], Sales, Version=2.0.0.0");

        Assert.Equal(2, types.Count);
        Assert.Equal("Contracts.Commands.PlaceOrder", types[0].Name);
        Assert.Equal("Sales", types[0].Namespace);
        Assert.Equal("Envelope`1[[Sales.Order, Sales, Version=1.0.0.0]]", types[1].Name);
        Assert.Equal("Sales", types[1].Namespace);
        Assert.Equal("Sales", types[1].Assembly);
        Assert.Equal("2.0.0.0", types[1].Version);
    }

    [Fact]
    public void Invalid_dates_are_ignored() => Assert.Null(NServiceBusMessageDecoder.ParseWireDate("not a date"));
}
