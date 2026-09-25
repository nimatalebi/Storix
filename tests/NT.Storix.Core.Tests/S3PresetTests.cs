using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Tests;

public class S3PresetTests
{
    [Fact]
    public void Presets_fill_endpoint_region_and_path_style()
    {
        var options = new S3Options { ServiceUrl = "http://old" };

        S3Preset.All.Single(p => p.Name == "Wasabi").ApplyTo(options, "eu-central-1");
        Assert.Equal("https://s3.eu-central-1.wasabisys.com", options.ServiceUrl);
        Assert.Equal("eu-central-1", options.Region);

        S3Preset.All.Single(p => p.Name == "Amazon S3").ApplyTo(options);
        Assert.Null(options.ServiceUrl);
        Assert.False(options.ForcePathStyle);

        S3Preset.All.Single(p => p.Name.StartsWith("MinIO")).ApplyTo(options);
        Assert.True(options.ForcePathStyle);
    }
}
