using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.Core.Messages;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Api.Http;

public static class BrokerSupport
{
    /// <summary>Error code returned when an endpoint is called for a broker that does not implement it yet.</summary>
    public const string NotSupported = "not-supported-for-broker";

    /// <summary>
    /// Marks an endpoint as implemented for RabbitMQ only. Calls for another broker's connection (IBM MQ, MSMQ) get a
    /// 400 that names the feature, instead of the misleading "unknown cluster" the RabbitMQ services would answer.
    /// </summary>
    public static RouteHandlerBuilder RabbitMqOnly(this RouteHandlerBuilder builder, string feature) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var broker = http.GetRouteValue("cluster") is string cluster
                ? http.RequestServices.GetRequiredService<IOptions<ClustersOptions>>().Value.Items.FirstOrDefault(c => c.Name == cluster)?.Broker
                : null;
            if (broker is null || broker == BrokerTypes.RabbitMq)
                return await next(context);

            var permissions = await http.RequestServices.GetRequiredService<CurrentUser>().GetPermissionsAsync(http.RequestAborted);
            if (permissions.ActionsAnywhereIn((string)http.GetRouteValue("cluster")!).Count == 0)
                return Results.NotFound();
            var label = BrokerTypes.Label(broker);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Not supported for {label}",
                detail: $"{feature}: not available for {label} connections yet.", extensions: new Dictionary<string, object?> { ["code"] = NotSupported });
        });
}
