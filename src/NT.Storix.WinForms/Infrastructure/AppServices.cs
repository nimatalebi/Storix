using NT.Storix.Core;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Persistence;
using NT.Storix.Core.Security;

namespace NT.Storix.WinForms.Infrastructure;

/// <summary>Core services used by the manager UI. It shares the same database as the Windows service.</summary>
internal sealed class AppServices
{
    private AppServices(StorixDatabase database, ISecretProtector protector)
    {
        Database = database;
        Jobs = new JobRepository(database, protector);
        Runs = new RunRepository(database);
        Settings = new SettingsRepository(database, protector);
    }

    public StorixDatabase Database { get; }

    public JobRepository Jobs { get; }

    public RunRepository Runs { get; }

    public SettingsRepository Settings { get; }

    public IDestinationFactory Destinations { get; } = new DestinationFactory();

    public static AppServices Create() =>
        new(new StorixDatabase(StorixPaths.DatabasePath), new MachineSecretProtector());
}
