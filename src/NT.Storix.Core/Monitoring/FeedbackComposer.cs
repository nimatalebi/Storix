using System.Text;

namespace NT.Storix.Core.Monitoring;

public enum FeedbackKind
{
    BugReport,
    FeatureRequest,
    Question,
    Other,
}

public sealed record Feedback(FeedbackKind Kind, string Title, string Message, string? ContactEmail = null, bool IncludeSystemInfo = true);

/// <summary>
/// Turns user feedback into a pre-filled GitHub issue link or a <c>mailto:</c> link.
/// Nothing is sent automatically: the user reviews and submits it in the browser or mail client.
/// </summary>
public static class FeedbackComposer
{
    // Keep links well below the limits of browsers, GitHub and common mail clients.
    internal const int MaxIssueBodyLength = 6000;
    internal const int MaxMailBodyLength = 1500;

    public static string BuildIssueUrl(Feedback feedback)
    {
        var query = new StringBuilder()
            .Append("title=").Append(Uri.EscapeDataString(Title(feedback)))
            .Append("&body=").Append(Uri.EscapeDataString(Truncate(Body(feedback), MaxIssueBodyLength)));

        if (Label(feedback.Kind) is { } label)
        {
            query.Append("&labels=").Append(Uri.EscapeDataString(label));
        }

        return $"{StorixInfo.NewIssueUrl}?{query}";
    }

    public static string BuildMailtoUrl(Feedback feedback) =>
        $"mailto:{StorixInfo.ContactEmail}?subject={Uri.EscapeDataString($"[Storix] {Title(feedback)}")}" +
        $"&body={Uri.EscapeDataString(Truncate(Body(feedback), MaxMailBodyLength))}";

    public static string Title(Feedback feedback)
    {
        var title = string.IsNullOrWhiteSpace(feedback.Title) ? "Feedback" : feedback.Title.Trim();
        return $"{Prefix(feedback.Kind)}: {title}";
    }

    public static string Body(Feedback feedback)
    {
        var body = new StringBuilder()
            .AppendLine(string.IsNullOrWhiteSpace(feedback.Message) ? "(no description)" : feedback.Message.Trim());

        if (!string.IsNullOrWhiteSpace(feedback.ContactEmail))
        {
            body.AppendLine().Append("Contact: ").AppendLine(feedback.ContactEmail.Trim());
        }

        if (feedback.IncludeSystemInfo)
        {
            body.AppendLine().AppendLine("---").AppendLine(StorixInfo.SystemSummary);
        }

        return body.ToString().TrimEnd();
    }

    private static string Prefix(FeedbackKind kind) => kind switch
    {
        FeedbackKind.BugReport => "Bug",
        FeedbackKind.FeatureRequest => "Feature",
        FeedbackKind.Question => "Question",
        _ => "Feedback",
    };

    private static string? Label(FeedbackKind kind) => kind switch
    {
        FeedbackKind.BugReport => "bug",
        FeedbackKind.FeatureRequest => "enhancement",
        FeedbackKind.Question => "question",
        _ => null,
    };

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 15)] + "\n\n[truncated]";
}
