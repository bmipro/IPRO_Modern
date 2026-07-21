using System.Net.Http.Json;
using System.Text.Json;
using IPRO.Business.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Business.Services;

public class AnthropicAiSuggestionService : IAiSuggestionService
{
    private const string Model = "claude-haiku-4-5-20251001";
    private const string SystemPrompt =
        "You write a single short sentence (under 20 words) for a financial advisor's CRM dashboard, " +
        "explaining why a suggested action matters right now. Be specific to the situation given, not generic. " +
        "Do not restate names or repeat the action itself — add the underlying reasoning. " +
        "No preamble, no quotation marks, just the sentence.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AiSettings _settings;
    private readonly ILogger<AnthropicAiSuggestionService> _logger;

    public AnthropicAiSuggestionService(IOptions<AiSettings> settings, ILogger<AnthropicAiSuggestionService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<AiActionReasonResult> GenerateActionReasonAsync(string situation, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            return AiActionReasonResult.Empty;
        }

        try
        {
            var payload = new
            {
                model = Model,
                max_tokens = 60,
                system = SystemPrompt,
                messages = new[] { new { role = "user", content = situation } }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = JsonContent.Create(payload);

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Anthropic API returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                return AiActionReasonResult.Empty;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var text = json.GetProperty("content")[0].GetProperty("text").GetString();
            var inputTokens = json.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var inTok) ? inTok.GetInt32() : 0;
            var outputTokens = json.TryGetProperty("usage", out usage) && usage.TryGetProperty("output_tokens", out var outTok) ? outTok.GetInt32() : 0;
            var reason = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            return new AiActionReasonResult(reason, inputTokens, outputTokens);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anthropic API call failed.");
            return AiActionReasonResult.Empty;
        }
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(_settings.AnthropicApiKey)
        && !_settings.AnthropicApiKey.Contains("YOUR_ANTHROPIC", StringComparison.OrdinalIgnoreCase);
}
