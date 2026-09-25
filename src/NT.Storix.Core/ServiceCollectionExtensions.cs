using Microsoft.Extensions.DependencyInjection;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Engine;
using NT.Storix.Core.Monitoring;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStorixCore(this IServiceCollection services, string? databasePath = null)
    {
        services.AddSingleton(_ => new StorixDatabase(databasePath ?? StorixPaths.DatabasePath));
        services.AddSingleton<ISecretProtector, MachineSecretProtector>();
        services.AddSingleton<JobRepository>();
        services.AddSingleton<RunRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<ISourceFactory, SourceFactory>();
        services.AddSingleton<IDestinationFactory, DestinationFactory>();
        services.AddSingleton<INotifier, EmailNotifier>();
        services.AddSingleton<INotifier>(sp => new ChannelNotifier(
            sp.GetRequiredService<SettingsRepository>(), SharedHttp.Client, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ChannelNotifier>>()));
        services.AddSingleton<BackupJobRunner>();
        services.AddSingleton<BackupScheduler>();
        services.AddSingleton<RestoreService>();
        return services;
    }
}
