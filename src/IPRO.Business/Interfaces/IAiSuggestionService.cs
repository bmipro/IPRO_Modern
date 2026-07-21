namespace IPRO.Business.Interfaces;

public record AiActionReasonResult(string? Reason, int InputTokens, int OutputTokens)
{
    public static readonly AiActionReasonResult Empty = new(null, 0, 0);
}

public interface IAiSuggestionService
{
    Task<AiActionReasonResult> GenerateActionReasonAsync(string situation, CancellationToken cancellationToken = default);
}
