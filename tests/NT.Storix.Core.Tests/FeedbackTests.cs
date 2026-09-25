using NT.Storix.Core.Monitoring;

namespace NT.Storix.Core.Tests;

public class FeedbackTests
{
    [Fact]
    public void Issue_url_is_prefilled_and_encoded()
    {
        var url = FeedbackComposer.BuildIssueUrl(new Feedback(FeedbackKind.BugReport, "SFTP & resume", "Upload stops at 50%", IncludeSystemInfo: false));

        Assert.StartsWith(StorixInfo.NewIssueUrl + "?", url);
        Assert.Contains("title=Bug%3A%20SFTP%20%26%20resume", url);
        Assert.Contains("body=Upload%20stops%20at%2050%25", url);
        Assert.EndsWith("&labels=bug", url);
    }

    [Fact]
    public void Mailto_url_targets_project_email()
    {
        var url = FeedbackComposer.BuildMailtoUrl(new Feedback(FeedbackKind.Question, "Restore", "How do I restore?", "me@example.com"));

        Assert.StartsWith($"mailto:{StorixInfo.ContactEmail}?subject=", url);
        Assert.Contains(Uri.EscapeDataString("[Storix] Question: Restore"), url);
        Assert.Contains(Uri.EscapeDataString("me@example.com"), url);
        Assert.Contains(Uri.EscapeDataString(StorixInfo.SystemSummary), url);
    }

    [Fact]
    public void Long_messages_are_truncated()
    {
        var feedback = new Feedback(FeedbackKind.Other, "", new string('x', 20_000));

        var mailBody = Uri.UnescapeDataString(FeedbackComposer.BuildMailtoUrl(feedback).Split("&body=")[1]);
        Assert.True(mailBody.Length <= FeedbackComposer.MaxMailBodyLength);
        Assert.EndsWith("[truncated]", mailBody);

        Assert.DoesNotContain("labels=", FeedbackComposer.BuildIssueUrl(feedback));
        Assert.Equal("Feedback: Feedback", FeedbackComposer.Title(feedback));
    }
}
