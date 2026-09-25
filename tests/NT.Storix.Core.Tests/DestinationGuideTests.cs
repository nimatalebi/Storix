using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Tests;

public class DestinationGuideTests
{
    [Fact]
    public void Every_destination_kind_has_a_complete_guide_in_both_languages()
    {
        foreach (var kind in Enum.GetValues<DestinationKind>())
        {
            var guide = DestinationGuides.For(kind);
            Assert.NotEmpty(guide.Steps);
            var texts = guide.Steps.Append(guide.Title).Concat(guide.Links.Select(l => l.Label)).Concat(guide.Note is null ? [] : [guide.Note]);
            Assert.All(texts, t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.En), $"{kind}: English text missing.");
                Assert.False(string.IsNullOrWhiteSpace(t.Fa), $"{kind}: Persian text missing.");
            });
            Assert.All(guide.Links, l => Assert.StartsWith("https://", l.Url));
        }
    }

    [Fact]
    public void Google_drive_guide_follows_the_sign_in_mode()
    {
        var destination = new DestinationDefinition { Kind = DestinationKind.GoogleDrive };

        destination.GoogleDrive.AuthMode = GoogleDriveAuthMode.ServiceAccount;
        Assert.Contains("service account", DestinationGuides.For(destination).Title.En);
        destination.GoogleDrive.AuthMode = GoogleDriveAuthMode.UserAccount;
        Assert.Contains(DestinationGuides.For(destination).Steps, s => s.En.Contains("In production"));
    }
}
