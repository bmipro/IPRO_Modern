namespace IPRO.Business.Interfaces;

public record AiActionReasonResult(string? Reason, int InputTokens, int OutputTokens)
{
    public static readonly AiActionReasonResult Empty = new(null, 0, 0);
}

public record AiNewsletterDraftResult(string? Subject, string? BodyHtml, int InputTokens, int OutputTokens)
{
    public static readonly AiNewsletterDraftResult Empty = new(null, null, 0, 0);
}

// A blog post draft: title, a one-or-two-sentence summary for listings, and the body HTML. Same
// contract as the newsletter draft -- a null Title means the model ignored the format and the
// whole text is in BodyHtml for the agent to salvage by hand.
public record AiBlogPostDraftResult(string? Title, string? Summary, string? BodyHtml, int InputTokens, int OutputTokens)
{
    public static readonly AiBlogPostDraftResult Empty = new(null, null, null, 0, 0);
}

public interface IAiSuggestionService
{
    Task<AiActionReasonResult> GenerateActionReasonAsync(string situation, CancellationToken cancellationToken = default);
    Task<AiActionReasonResult> DraftSocialPostAsync(string topic, CancellationToken cancellationToken = default);
    Task<AiNewsletterDraftResult> DraftNewsletterAsync(string topic, CancellationToken cancellationToken = default);
    Task<AiBlogPostDraftResult> DraftBlogPostAsync(string topic, CancellationToken cancellationToken = default);
}
