using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DynaQueueExplorer.Api.Auth;
using DynaQueueExplorer.Api.Endpoints;
using DynaQueueExplorer.Api.Http;
using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.IbmMq;
using DynaQueueExplorer.Infrastructure;
using DynaQueueExplorer.Infrastructure.Persistence;
using DynaQueueExplorer.Msmq;
using DynaQueueExplorer.RabbitMq;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Machine-specific settings (real cluster credentials, connection strings) live in a git-ignored file that
// overrides appsettings*.json; environment variables still override it (e.g. Clusters__0__Password).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRabbitMqExplorer(builder.Configuration);
builder.Services.AddIbmMqExplorer(builder.Configuration);
builder.Services.AddMsmqExplorer(builder.Configuration);
builder.Services.AddExplorerPersistence(builder.Configuration.GetConnectionString("Explorer")
                                        ?? throw new InvalidOperationException("Missing connection string 'Explorer'."));

builder.Services.Configure<LocalUsersOptions>(o => builder.Configuration.GetSection(LocalUsersOptions.SectionName).Bind(o.Users));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddSingleton<ConditionalJson>();
builder.Services.AddSingleton<FailureAnalysis>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "dqe.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        // An API answers with status codes, never with redirects to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ExplorerExceptionHandler>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ExplorerDbContext>();
    await db.Database.MigrateAsync();
    await DevelopmentSeeder.SeedAsync(db, CancellationToken.None);
}
else if (app.Configuration.GetSection(LocalUsersOptions.SectionName).GetChildren().Any())
{
    app.Logger.LogWarning("Hardcoded local users are configured outside Development. They are meant for the test phase only.");
}

app.UseExceptionHandler();
app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RequireClientHeaderMiddleware>();

app.MapAuthEndpoints();
app.MapClusterEndpoints();
app.MapMessageOperationEndpoints();
app.MapOverviewEndpoints();
app.MapStagingEndpoints();
app.MapAuditEndpoints();
// The default fallback skips paths whose last segment looks like a file name, but cluster routes contain queue
// names with dots (/c/local/queues/sales/Sales.OrderService): those always get the SPA. A catch-all would also
// shadow the static assets, because the static file middleware ignores requests that matched an endpoint.
app.MapFallback("api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("c/{**path}", "index.html");
app.MapFallbackToFile("index.html");

app.Run();
