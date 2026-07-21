namespace IPRO.Business.Interfaces;

public interface IAiSuggestionService
{
    Task<string?> GenerateActionReasonAsync(string situation, CancellationToken cancellationToken = default);
}
