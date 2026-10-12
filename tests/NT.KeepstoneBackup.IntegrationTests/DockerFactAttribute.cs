namespace NT.KeepstoneBackup.IntegrationTests;

/// <summary>
/// Integration tests need Docker and are opt-in: set <c>KEEPSTONEBACKUP_INTEGRATION_TESTS=1</c> to run them.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "KEEPSTONEBACKUP_INTEGRATION_TESTS";

    public DockerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Integration test: set {EnvironmentVariable}=1 and make sure Docker is running.";
        }
    }
}
