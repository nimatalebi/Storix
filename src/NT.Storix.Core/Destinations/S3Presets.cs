using NT.Storix.Core.Models;

namespace NT.Storix.Core.Destinations;

/// <summary>Endpoint presets for popular S3-compatible providers. <c>{region}</c> and <c>{account}</c> are placeholders.</summary>
public sealed record S3Preset(string Name, string? ServiceUrlTemplate, string DefaultRegion, bool ForcePathStyle, string Hint)
{
    public static IReadOnlyList<S3Preset> All { get; } =
    [
        new("Amazon S3", null, "us-east-1", false, "Use the bucket's region."),
        new("Cloudflare R2", "https://{account}.r2.cloudflarestorage.com", "auto", true, "Replace {account} with your Cloudflare account id."),
        new("Wasabi", "https://s3.{region}.wasabisys.com", "us-east-1", false, "Use the bucket's region, e.g. eu-central-1."),
        new("Backblaze B2", "https://s3.{region}.backblazeb2.com", "us-west-004", false, "Region is shown on the bucket page, e.g. eu-central-003."),
        new("DigitalOcean Spaces", "https://{region}.digitaloceanspaces.com", "fra1", false, "Region is the Spaces datacenter, e.g. fra1, ams3."),
        new("Arvan Cloud", "https://s3.ir-thr-at1.arvanstorage.ir", "ir-thr-at1", true, "Use the endpoint shown in the Arvan panel."),
        new("MinIO (self-hosted)", "http://localhost:9000", "us-east-1", true, "Replace with your MinIO address."),
    ];

    public void ApplyTo(S3Options options, string? region = null)
    {
        var effectiveRegion = string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim();
        options.Region = effectiveRegion;
        options.ForcePathStyle = ForcePathStyle;
        options.ServiceUrl = ServiceUrlTemplate?.Replace("{region}", effectiveRegion, StringComparison.Ordinal);
    }

    public override string ToString() => Name;
}
