using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.IbmMq.Snapshots;

namespace DynaQueueExplorer.Api.Http;

public static class BrokerSupport
{
    /// <summary>Error code returned when an endpoint is called for a broker that does not implement it yet.</summary>
    public const string NotSupported = "not-supported-for-broker";

    /// <summary>
    /// Marks an endpoint as implemented for RabbitMQ only. Calls for an IBM MQ connection get a 400 that names the
    /// feature, instead of the misleading "unknown cluster" the RabbitMQ services would answer.
    /// </summary>
    public static RouteHandlerBuilder RabbitMqOnly(this RouteHandlerBuilder builder, string feature) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.GetRouteValue("cluster") is not string cluster || !http.RequestServices.GetRequiredService<MqSnapshotService>().Exists(cluster))
                return await next(context);

            var permissions = await http.RequestServices.GetRequiredService<CurrentUser>().GetPermissionsAsync(http.RequestAborted);
            if (permissions.ActionsAnywhereIn(cluster).Count == 0)
                return Results.NotFound();
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not supported for IBM MQ",
                detail: $"{feature}: not available for IBM MQ connections yet.", extensions: new Dictionary<string, object?> { ["code"] = NotSupported });
        });
}
