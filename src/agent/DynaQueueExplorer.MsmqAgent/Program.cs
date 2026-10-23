using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DynaQueueExplorer.Core.Msmq;
using DynaQueueExplorer.MsmqAgent;
using DynaQueueExplorer.MsmqAgent.Msmq;
using Microsoft.Extensions.Options;

// Runs as a Windows service (sc.exe create …) or from a console for testing.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Host.UseWindowsService(o => o.ServiceName = "DynaQueueExplorer MSMQ Agent");
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<QueueLister>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
builder.Services.AddProblemDetails();

var app = builder.Build();
var options = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
if (options.ApiKey.Length < 24)
    throw new InvalidOperationException("Agent:ApiKey must be set to a secret of at least 24 characters (appsettings.Local.json or Agent__ApiKey).");
var expectedKey = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey));

// Every call carries the shared key; compared in constant time.
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers[MsmqAgentApi.KeyHeader].ToString();
    if (!CryptographicOperations.FixedTimeEquals(expectedKey, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next(context);
});

app.MapGet("/api/health", () => Results.Ok(new { machine = Environment.MachineName, machines = AllowedMachines(options) }));

app.MapGet("/api/machines/{machine}/queues", (string machine, QueueLister lister, CancellationToken ct) =>
    Run(machine, options, () => lister.List(machine), ct));

app.MapPost("/api/machines/{machine}/browse", (string machine, MsmqBrowseRequest request, CancellationToken ct) =>
    Run(machine, options, () =>
    {
        if (!IsBrowsable(request.Queue))
            throw new ArgumentException($"'{request.Queue}' cannot be browsed: only private and dead-letter queues of the machine can.");
        var formatName = $"DIRECT=OS:{machine}\\{request.Queue}";
        var messages = MessagePeeker.Peek(formatName, Math.Clamp(request.Count, 1, options.MaxBrowseCount), Math.Clamp(request.MaxBodyBytes, 0, options.MaxBodyBytes));
        return new MsmqBrowseResponse(machine, request.Queue, messages);
    }, ct));

app.Run();

// COM calls block: they run on the thread pool, and errors come back as problem details with the MSMQ error name.
static async Task<IResult> Run<T>(string machine, AgentOptions options, Func<T> work, CancellationToken ct)
{
    if (!AllowedMachines(options).Contains(machine, StringComparer.OrdinalIgnoreCase))
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Machine not allowed",
            detail: $"The agent is not configured to read {machine} (Agent:Machines).");
    try
    {
        return Results.Ok(await Task.Run(work, ct));
    }
    catch (MsmqException e)
    {
        return Results.Problem(statusCode: e.AccessDenied ? StatusCodes.Status403Forbidden : StatusCodes.Status502BadGateway,
            title: e.AccessDenied ? "MSMQ refused the agent's account" : "MSMQ error", detail: e.Message);
    }
    catch (ArgumentException e)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad request", detail: e.Message);
    }
}

static IReadOnlyList<string> AllowedMachines(AgentOptions options) =>
    options.Machines.Count > 0 ? options.Machines : [Environment.MachineName];

// private$\name or the system dead-letter queues; never a full format name (which could point anywhere).
static bool IsBrowsable(string queue) =>
    !queue.Contains('=') && !queue.Contains(':') &&
    (queue.StartsWith("private$\\", StringComparison.OrdinalIgnoreCase) && queue.IndexOf('\\', 9) < 0
     || queue.Equals("system$;deadxact", StringComparison.OrdinalIgnoreCase)
     || queue.Equals("system$;deadletter", StringComparison.OrdinalIgnoreCase));
