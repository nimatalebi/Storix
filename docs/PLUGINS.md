# Writing a Storix plugin

Plugins add **sources** (something to back up) and **destinations** (somewhere to store backups). A plugin is a
.NET 10 class library that references `NT.Storix.Core` and contains one or more public classes implementing
`ISourcePlugin` or `IDestinationPlugin` (namespace `NT.Storix.Core.Plugins`) with a parameterless constructor.

A complete example is in [`samples/NT.Storix.Plugins.Sample`](../samples/NT.Storix.Plugins.Sample).

## Project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- Shared with the host: never ship NT.Storix.Core.dll with the plugin. -->
    <ProjectReference Include="path\to\NT.Storix.Core.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
  </ItemGroup>
</Project>
```

## Contract

```csharp
public sealed class MyDestination : IDestinationPlugin
{
    public string Id => "acme.storage";              // stored in job definitions: keep it stable
    public string DisplayName => "ACME storage";
    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("Bucket", "Bucket name", Required: true),
        new("ApiKey", "API key", Secret: true, Required: true),
    ];

    public IBackupDestination Create(PluginContext context) =>
        new AcmeDestination(context.Require("Bucket"), context.Require("ApiKey"), context.MaxUploadKBps);
}
```

- A **source** returns the files to archive from `IBackupSource.PrepareAsync`. Write temporary files (dumps,
  exports) into `SourceContext.StagingDirectory` and mark them `DeleteAfterRun`.
- A **destination** implements `IBackupDestination`: `ListAsync`, `UploadAsync`, `DownloadAsync`, `DeleteAsync`
  and `TestAsync`. Storix verifies every upload by listing the destination and comparing sizes, so `ListAsync`
  must return exact sizes. Upload to a temporary name and rename when the protocol allows it.
- Settings marked `Secret` are entered in the *Secret settings* field, encrypted in the database like every other
  secret, removed from exports without a passphrase and never logged. Do not log them yourself.
- Throw exceptions with a clear message on failure; Storix retries according to the job's retry policy.

## Installing

Copy the plugin DLL (and its own dependencies, if any) into a `plugins` folder next to the Storix programs:

```
C:\Program Files\Storix\plugins\Acme.Storix.dll
C:\Program Files\Storix\plugins\Acme.Storix\Acme.Storix.dll   (a sub-folder with the same name also works)
/opt/storix/plugins/...                                      (Linux)
```

Restart the service. Each plugin is loaded into its own load context. Plugins run inside the service with its
privileges, so the folder must only be writable by administrators (the default under Program Files and /opt),
and only install plugins you trust.

In the job editor choose the **Plugin** source or destination kind, enter the plugin id, the settings (one
`name=value` per line) and the secret settings (`name=value; name2=value2`).
