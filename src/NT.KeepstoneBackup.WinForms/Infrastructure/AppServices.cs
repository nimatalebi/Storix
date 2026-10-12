using NT.KeepstoneBackup.Core;
using NT.KeepstoneBackup.Core.Destinations;
using NT.KeepstoneBackup.Core.Persistence;
using NT.KeepstoneBackup.Core.Security;

namespace NT.KeepstoneBackup.WinForms.Infrastructure;

/// <summary>Core services used by the manager UI. It shares the same database as the Windows service.</summary>
internal sealed class AppServices
{
    private AppServices(KeepstoneBackupDatabase database, ISecretProtector protector)
    {
        Database = database;
        Jobs = new JobRepository(database, protector);
        Runs = new RunRepository(database);
        Settings = new SettingsRepository(database, protector);
        SqlBackups = new SqlBackupRepository(database);
        Audit = new AuditRepository(database);
        OAuthTokenStore.UseDatabase(Settings, protector);
    }

    public KeepstoneBackupDatabase Database { get; }

    public JobRepository Jobs { get; }

    public RunRepository Runs { get; }

    public SettingsRepository Settings { get; }

    public SqlBackupRepository SqlBackups { get; }

    public AuditRepository Audit { get; }

    public IDestinationFactory Destinations { get; } = new DestinationFactory();

    public static AppServices Create() =>
        new(new KeepstoneBackupDatabase(KeepstoneBackupPaths.DatabasePath), new MachineSecretProtector());
}
