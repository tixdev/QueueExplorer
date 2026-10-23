using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DynaQueueExplorer.Msmq;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMsmqExplorer(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MsmqOptions>(configuration.GetSection(MsmqOptions.SectionName));
        services.AddSingleton<MsmqAgentClients>();
        services.AddSingleton<MsmqSnapshotService>();
        services.AddHostedService(sp => sp.GetRequiredService<MsmqSnapshotService>());
        return services;
    }
}
