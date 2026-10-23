using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NServiceBus;
using RabbitMQ.Client;
using Simulation.Messages;

// Runs real NServiceBus endpoints against the local RabbitMQ (vhost "simulation") and sends orders whose
// scenario makes some of them fail in different ways, so the error, audit and delay queues contain
// messages produced by NServiceBus itself.
//
//   dotnet run --project tools/NsbSimulator -- [--orders 100] [--rate 20] [--exit-after 90]
//
// Without --exit-after the endpoints keep running (and consuming) until Ctrl+C.

var options = SimulatorOptions.Parse(args);
await RabbitMqSetup.EnsureVhostAsync(options);

var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// Every failure would print a full stack trace; NServiceBus' own rolling file log keeps them instead.
builder.Logging.AddFilter("NServiceBus", LogLevel.None);
builder.Services.Configure<RollingLoggerProviderOptions>(o =>
{
    o.LogLevel = LogLevel.Warning;
    o.Directory = Path.Combine(AppContext.BaseDirectory, "logs");
});
builder.Services.AddSingleton(options);

// Several endpoints share this host: each one is registered under its name as keyed services.
foreach (var (name, register) in Endpoints.HandlerEndpoints)
    builder.Services.AddNServiceBusEndpoint(Endpoints.Create(name, options.AmqpUri, register), name);
builder.Services.AddNServiceBusEndpoint(Endpoints.Create(Endpoints.Client, options.AmqpUri), Endpoints.Client);
builder.Services.AddHostedService<OrderGenerator>();

await builder.Build().RunAsync();

sealed record SimulatorOptions(int Orders, int Rate, TimeSpan? ExitAfter, string Vhost, string AmqpBase, Uri ManagementUrl, string User, string Password)
{
    public string AmqpUri => $"{AmqpBase.TrimEnd('/')}/{Uri.EscapeDataString(Vhost)}";

    public static SimulatorOptions Parse(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        return new SimulatorOptions(
            int.Parse(Arg("--orders") ?? "100"),
            int.Parse(Arg("--rate") ?? "20"),
            Arg("--exit-after") is { } s ? TimeSpan.FromSeconds(int.Parse(s)) : null,
            Arg("--vhost") ?? "simulation",
            Arg("--amqp") ?? "amqp://dqe-admin:dqe-admin@localhost:5672",
            new Uri(Arg("--management") ?? "http://localhost:15672/"),
            Arg("--user") ?? "dqe-admin",
            Arg("--password") ?? "dqe-admin");
    }
}

static class RabbitMqSetup
{
    /// <summary>Creates the vhost (idempotent) and grants the user full permissions on it.</summary>
    public static async Task EnsureVhostAsync(SimulatorOptions options)
    {
        using var http = new HttpClient { BaseAddress = options.ManagementUrl };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.User}:{options.Password}")));
        var vhost = Uri.EscapeDataString(options.Vhost);
        (await http.PutAsJsonAsync($"api/vhosts/{vhost}", new { description = "NServiceBus failure simulation", default_queue_type = "quorum" }))
            .EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"api/permissions/{vhost}/{Uri.EscapeDataString(options.User)}", new { configure = ".*", write = ".*", read = ".*" }))
            .EnsureSuccessStatusCode();
        // error/audit get no NServiceBus policy: lift RabbitMQ 4's default delivery-limit of 20 so browsing them is safe.
        // Endpoint queues are left alone: the transport creates its own policy and rejects any other.
        var policy = new Dictionary<string, object>
        {
            ["pattern"] = "^(error|audit)$",
            ["apply-to"] = "quorum_queues",
            ["definition"] = new Dictionary<string, object> { ["delivery-limit"] = -1 },
            ["priority"] = 0,
        };
        (await http.PutAsJsonAsync($"api/policies/{vhost}/dqe.unlimited-delivery", policy)).EnsureSuccessStatusCode();
    }
}

static class Endpoints
{
    public const string Client = "Simulation.Client";

    /// <summary>
    /// The endpoints share one host, so assembly scanning is disabled and each endpoint registers its own
    /// handlers and sagas explicitly (required by NServiceBus 10 for multi-endpoint hosts).
    /// </summary>
    public static readonly Dictionary<string, Action<EndpointConfiguration>> HandlerEndpoints = new()
    {
        ["Simulation.Sales"] = c => c.AddHandler<Simulation.Sales.PlaceOrderHandler>(),
        ["Simulation.Billing"] = c => c.AddHandler<Simulation.Billing.OrderPlacedHandler>(),
        ["Simulation.Shipping"] = c => c.AddSaga<Simulation.Shipping.ShippingPolicy>(),
    };

    public static EndpointConfiguration Create(string name, string connectionString, Action<EndpointConfiguration>? register = null)
    {
        var config = new EndpointConfiguration(name);
        config.UseSerialization<SystemJsonSerializer>();
        config.UsePersistence<NonDurablePersistence>();
        config.SendFailedMessagesTo("error");
        config.AuditProcessedMessagesTo("audit");
        config.EnableInstallers();
        config.AssemblyScanner().Disable = true;

        var transport = new RabbitMQTransport(RoutingTopology.Conventional(QueueType.Quorum), connectionString);
        var routing = config.UseTransport(transport);
        routing.RouteToEndpoint(typeof(PlaceOrder), "Simulation.Sales");

        var recoverability = config.Recoverability();
        recoverability.Immediate(i => i.NumberOfRetries(2));
        recoverability.Delayed(d => d.NumberOfRetries(2).TimeIncrease(TimeSpan.FromSeconds(3)));

        if (register is null)
            config.SendOnly();
        else
            register(config);
        return config;
    }
}

/// <summary>Sends the orders once the endpoints are up, then keeps the host running or stops it.</summary>
sealed class OrderGenerator(
    [FromKeyedServices(Endpoints.Client)] IMessageSession session,
    SimulatorOptions options,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine($"Endpoints running in vhost '{options.Vhost}': {string.Join(", ", Endpoints.HandlerEndpoints.Keys)}, {Endpoints.Client} (send-only)");
        var sent = await Orders.SendAsync(session, options, stoppingToken);
        Console.WriteLine("Sent: " + string.Join(", ", sent.Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine("Failures go through 2 immediate + 2 delayed retries (3 s, 6 s) before reaching 'error'; poison messages go there at once.");
        Console.WriteLine($"NServiceBus logs (with stack traces): {Path.Combine(AppContext.BaseDirectory, "logs")}");

        if (options.ExitAfter is not { } exitAfter)
        {
            Console.WriteLine("Endpoints keep consuming; press Ctrl+C to stop.");
            return;
        }
        Console.WriteLine($"Stopping in {exitAfter.TotalSeconds:0} s…");
        await Task.Delay(exitAfter, stoppingToken);
        lifetime.StopApplication();
    }
}

static class Orders
{
    private static readonly (Scenario Scenario, int Weight)[] Mix =
    [
        (Scenario.Ok, 60),
        (Scenario.Transient, 14),
        (Scenario.PaymentDeclined, 8),
        (Scenario.GatewayTimeout, 6),
        (Scenario.NullReference, 5),
        (Scenario.SagaFailure, 4),
        (Scenario.Poison, 3),
    ];

    private static readonly string[] Skus = ["SKU-10422", "SKU-20871", "SKU-33105", "SKU-47720", "SKU-58113"];

    public static async Task<SortedDictionary<Scenario, int>> SendAsync(IMessageSession session, SimulatorOptions options, CancellationToken ct)
    {
        var random = new Random(4242);
        var sent = new SortedDictionary<Scenario, int>();
        var delay = TimeSpan.FromSeconds(1.0 / Math.Max(1, options.Rate));

        var factory = new ConnectionFactory { Uri = new Uri(options.AmqpUri) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        for (var i = 0; i < options.Orders && !ct.IsCancellationRequested; i++)
        {
            var scenario = Pick(random);
            var lines = Enumerable.Range(0, random.Next(1, 4))
                .Select(_ => new OrderLine(Skus[random.Next(Skus.Length)], random.Next(1, 6), Math.Round((decimal)random.NextDouble() * 200 + 5, 2)))
                .ToArray();
            var order = new PlaceOrder(Guid.NewGuid(), random.Next(1000, 99999), lines.Sum(l => l.Quantity * l.UnitPrice), "EUR", scenario, lines);

            if (scenario == Scenario.Poison)
                await PublishPoisonAsync(channel, order, ct);
            else
                await session.Send(order, ct);

            sent[scenario] = sent.GetValueOrDefault(scenario) + 1;
            await Task.Delay(delay, ct);
        }
        return sent;
    }

    private static Scenario Pick(Random random)
    {
        var roll = random.Next(Mix.Sum(m => m.Weight));
        foreach (var (scenario, weight) in Mix)
        {
            if (roll < weight)
                return scenario;
            roll -= weight;
        }
        return Scenario.Ok;
    }

    /// <summary>
    /// Sends an NServiceBus-shaped OrderPlaced with a truncated JSON body straight to Billing's queue,
    /// as a buggy or non-.NET publisher would: deserialization fails and NServiceBus skips all retries.
    /// </summary>
    private static async Task PublishPoisonAsync(IChannel channel, PlaceOrder order, CancellationToken ct)
    {
        var messageId = Guid.NewGuid().ToString();
        var properties = new BasicProperties
        {
            MessageId = messageId,
            CorrelationId = messageId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>
            {
                ["NServiceBus.MessageId"] = messageId,
                ["NServiceBus.CorrelationId"] = messageId,
                ["NServiceBus.ConversationId"] = Guid.NewGuid().ToString(),
                ["NServiceBus.MessageIntent"] = "Publish",
                ["NServiceBus.ContentType"] = "application/json",
                ["NServiceBus.EnclosedMessageTypes"] = typeof(OrderPlaced).AssemblyQualifiedName,
                ["NServiceBus.OriginatingEndpoint"] = "LegacyCheckout.Gateway",
                ["NServiceBus.OriginatingMachine"] = "legacy-gw-01",
                ["NServiceBus.TimeSent"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss:ffffff 'Z'"),
                ["NServiceBus.Version"] = "7.8.4",
            },
        };
        var body = Encoding.UTF8.GetBytes($"{{\"OrderId\":\"{order.OrderId}\",\"CustomerId\":{order.CustomerId},\"Amount\":\"12,50 EUR\",\"Curre");
        await channel.BasicPublishAsync("", "Simulation.Billing", mandatory: false, properties, body, ct);
    }
}
