using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.RabbitMq.Amqp;
using DynaQueueExplorer.RabbitMq.Snapshots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DynaQueueExplorer.RabbitMq;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRabbitMqExplorer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ClustersOptions>(o => configuration.GetSection(ClustersOptions.SectionName).Bind(o.Items));
        services.Configure<RabbitMqOptions>(configuration.GetSection(RabbitMqOptions.SectionName));
        services.AddSingleton<ClusterClients>();
        services.AddSingleton<ClusterSnapshotService>();
        services.AddHostedService(sp => sp.GetRequiredService<ClusterSnapshotService>());
        services.AddSingleton<AmqpConnections>();
        services.AddHostedService(sp => sp.GetRequiredService<AmqpConnections>());
        services.AddSingleton<MessageOperations>();
        return services;
    }
}
