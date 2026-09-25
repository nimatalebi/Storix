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
        services.AddSingleton<SqlBackupRepository>();
        services.AddSingleton(sp => new BackupJobRunner(
            sp.GetRequiredService<RunRepository>(),
            sp.GetRequiredService<SettingsRepository>(),
            sp.GetRequiredService<ISourceFactory>(),
            sp.GetRequiredService<IDestinationFactory>(),
            sp.GetServices<INotifier>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackupJobRunner>>(),
            SharedHttp.Client,
            sp.GetRequiredService<SqlBackupRepository>()));
        services.AddSingleton<RestoreDrillRunner>();
        services.AddSingleton(sp => new BackupScheduler(
            sp.GetRequiredService<JobRepository>(),
            sp.GetRequiredService<RunRepository>(),
            sp.GetRequiredService<SettingsRepository>(),
            sp.GetRequiredService<BackupJobRunner>(),
            sp.GetServices<INotifier>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackupScheduler>>(),
            sp.GetRequiredService<RestoreDrillRunner>()));
        services.AddSingleton<RestoreService>();
        return services;
    }
}
