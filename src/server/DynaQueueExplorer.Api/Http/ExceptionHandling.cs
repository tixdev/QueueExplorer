using DynaQueueExplorer.RabbitMq.Management;
using Microsoft.AspNetCore.Diagnostics;

namespace DynaQueueExplorer.Api.Http;

public sealed class ExplorerExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            RabbitMqApiException => (StatusCodes.Status502BadGateway, "RabbitMQ request failed"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            _ => (0, null),
        };
        if (status == 0)
            return false;

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = exception.Message },
        });
    }
}

/// <summary>
/// Rejects state-changing API calls without the client header. Browsers cannot add custom headers to
/// cross-site requests without a CORS preflight (which this API never allows), so this blocks CSRF
/// together with the SameSite=Strict cookie.
/// </summary>
public sealed class RequireClientHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-DQE-Client";

    public Task InvokeAsync(HttpContext http)
    {
        if (http.Request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(http.Request.Method)
            && !HttpMethods.IsHead(http.Request.Method)
            && !http.Request.Headers.ContainsKey(HeaderName))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return http.Response.WriteAsJsonAsync(new { title = $"Missing {HeaderName} header." });
        }
        return next(http);
    }
}
