using DynaQueueExplorer.IbmMq.Client;
using DynaQueueExplorer.RabbitMq.Management;
using RabbitMQ.Client.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace DynaQueueExplorer.Api.Http;

public sealed class ExplorerExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        // RabbitMQ refusing the service account is a configuration problem of the explorer, not a missing permission
        // of the user (that would be a 403 from our own RBAC): answer 502 with an explicit title.
        var (status, title, detail) = exception switch
        {
            RabbitMqApiException { IsAccessDenied: true } e => (StatusCodes.Status502BadGateway, "RabbitMQ refused the service account",
                $"The RabbitMQ account used by the explorer lacks a permission or tag for this operation. {e.Message}"),
            RabbitMqApiException e => (StatusCodes.Status502BadGateway, "RabbitMQ request failed", e.Message),
            OperationInterruptedException { ShutdownReason.ReplyCode: 403 } e => (StatusCodes.Status502BadGateway, "RabbitMQ refused the service account",
                $"The RabbitMQ account used by the explorer lacks a vhost permission (configure/write/read) for this operation: {e.ShutdownReason?.ReplyText}"),
            OperationInterruptedException e => (StatusCodes.Status502BadGateway, "RabbitMQ operation failed", e.ShutdownReason?.ReplyText ?? e.Message),
            BrokerUnreachableException e => (StatusCodes.Status502BadGateway, "RabbitMQ is unreachable over AMQP", e.InnerException?.Message ?? e.Message),
            IbmMqException { IsNotAuthorized: true } e => (StatusCodes.Status502BadGateway, "IBM MQ refused the service account",
                $"The IBM MQ account used by the explorer lacks an authority for this operation. {e.Message}"),
            IbmMqException e => (StatusCodes.Status502BadGateway, "IBM MQ request failed", e.Message),
            KeyNotFoundException e => (StatusCodes.Status404NotFound, "Not found", e.Message),
            _ => (0, null, null),
        };
        if (status == 0)
            return false;

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
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
