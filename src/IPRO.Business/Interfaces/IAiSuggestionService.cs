namespace IPRO.Business.Interfaces;

public record AiActionReasonResult(string? Reason, int InputTokens, int OutputTokens)
{
    public static readonly AiActionReasonResult Empty = new(null, 0, 0);
}

public record AiNewsletterDraftResult(string? Subject, string? BodyHtml, int InputTokens, int OutputTokens)
{
    public static readonly AiNewsletterDraftResult Empty = new(null, null, 0, 0);
}

public interface IAiSuggestionService
{
    Task<AiActionReasonResult> GenerateActionReasonAsync(string situation, CancellationToken cancellationToken = default);
    Task<AiActionReasonResult> DraftSocialPostAsync(string topic, CancellationToken cancellationToken = default);
    Task<AiNewsletterDraftResult> DraftNewsletterAsync(string topic, CancellationToken cancellationToken = default);
}
