using NT.KeepstoneBackup.Core;
using NT.KeepstoneBackup.Service;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

Directory.CreateDirectory(KeepstoneBackupPaths.LogsDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(KeepstoneBackupPaths.LogsDirectory, "keepstonebackup-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    // Runs as a Windows service or a systemd unit (Type=notify); both are no-ops when started from a console.
    builder.Services.AddWindowsService(options => options.ServiceName = KeepstoneBackupPaths.ServiceName);
    builder.Services.AddSystemd();
    builder.Services.AddSerilog();
    builder.Services.AddKeepstoneBackupCore();
    builder.Services.AddHostedService<KeepstoneBackupWorker>();
    builder.Services.AddHostedService<MetricsServer>();
    builder.Services.AddHostedService<PipeServer>();

    // Warnings and errors also go to the Windows Event Log (Application log, source "KeepstoneBackup").
    if (OperatingSystem.IsWindows())
    {
        EventLogSetup.Add(builder.Logging);
    }

    // OpenTelemetry traces (backup runs and uploads) when an OTLP endpoint is configured.
    var otlp = new NT.KeepstoneBackup.Core.Persistence.SettingsRepository(
        new NT.KeepstoneBackup.Core.Persistence.KeepstoneBackupDatabase(KeepstoneBackupPaths.DatabasePath), new NT.KeepstoneBackup.Core.Security.MachineSecretProtector()).Get().Observability.OtlpEndpoint;
    if (!string.IsNullOrWhiteSpace(otlp))
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("keepstonebackup", serviceVersion: NT.KeepstoneBackup.Core.KeepstoneBackupInfo.Version))
            .WithTracing(tracing => tracing
                .AddSource(NT.KeepstoneBackup.Core.Monitoring.KeepstoneBackupTelemetry.SourceName)
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlp)));
    }

    // Give running backups time to stop gracefully (they are marked as interrupted otherwise).
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

    var host = builder.Build();
    NT.KeepstoneBackup.Core.Security.OAuthTokenStore.UseDatabase(
        host.Services.GetRequiredService<NT.KeepstoneBackup.Core.Persistence.SettingsRepository>(),
        host.Services.GetRequiredService<NT.KeepstoneBackup.Core.Security.ISecretProtector>());
    Log.Information("KeepstoneBackup service starting. Data folder: {DataFolder}", KeepstoneBackupPaths.DataDirectory);
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "KeepstoneBackup service terminated unexpectedly.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
