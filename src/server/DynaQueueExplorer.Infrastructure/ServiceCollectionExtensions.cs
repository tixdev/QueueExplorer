using DynaQueueExplorer.Core.Auditing;
using DynaQueueExplorer.Core.Security;
using DynaQueueExplorer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynaQueueExplorer.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddExplorerPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<ExplorerDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        services.AddMemoryCache();
        services.AddScoped<IPermissionStore, SqlPermissionStore>();
        services.AddScoped<IAuditLog, SqlAuditLog>();
        return services;
    }
}
