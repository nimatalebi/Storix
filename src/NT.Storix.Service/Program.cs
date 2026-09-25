using NT.Storix.Core;
using NT.Storix.Service;
using Serilog;

Directory.CreateDirectory(StorixPaths.LogsDirectory);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(
        Path.Combine(StorixPaths.LogsDirectory, "storix-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = StorixPaths.ServiceName);
    builder.Services.AddSerilog();
    builder.Services.AddStorixCore();
    builder.Services.AddHostedService<StorixWorker>();

    // Give running backups time to stop gracefully (they are marked as interrupted otherwise).
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

    var host = builder.Build();
    NT.Storix.Core.Security.OAuthTokenStore.UseDatabase(
        host.Services.GetRequiredService<NT.Storix.Core.Persistence.SettingsRepository>(),
        host.Services.GetRequiredService<NT.Storix.Core.Security.ISecretProtector>());
    Log.Information("Storix service starting. Data folder: {DataFolder}", StorixPaths.DataDirectory);
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Storix service terminated unexpectedly.");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
