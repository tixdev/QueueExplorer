using System.Text.Json.Serialization;

namespace DynaQueueExplorer.RabbitMq.Management;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RmqOverview))]
[JsonSerializable(typeof(List<RmqNode>))]
[JsonSerializable(typeof(List<RmqVhost>))]
[JsonSerializable(typeof(List<RmqQueue>))]
[JsonSerializable(typeof(RmqQueue))]
[JsonSerializable(typeof(List<RmqConsumer>))]
[JsonSerializable(typeof(List<RmqExchange>))]
[JsonSerializable(typeof(List<RmqBinding>))]
[JsonSerializable(typeof(List<RmqShovelStatus>))]
[JsonSerializable(typeof(List<RmqParameter>))]
[JsonSerializable(typeof(RmqGetRequest))]
[JsonSerializable(typeof(List<RmqGetMessage>))]
internal sealed partial class RmqJsonContext : JsonSerializerContext;
