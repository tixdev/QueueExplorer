using DynaQueueExplorer.IbmMq.Client;
using DynaQueueExplorer.IbmMq.Snapshots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DynaQueueExplorer.IbmMq;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIbmMqExplorer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IbmMqOptions>(configuration.GetSection(IbmMqOptions.SectionName));
        services.AddSingleton<IbmMqConnections>();
        services.AddSingleton<MqBrowser>();
        services.AddSingleton<MqSnapshotService>();
        services.AddHostedService(sp => sp.GetRequiredService<MqSnapshotService>());
        return services;
    }
}
