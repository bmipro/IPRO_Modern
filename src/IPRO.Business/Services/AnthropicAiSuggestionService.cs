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

    private const string SocialPostSystemPrompt =
        "You draft a single social media post for a financial/insurance advisor's business page. " +
        "Keep it under 280 characters so it works everywhere, professional but warm in tone, no more than one emoji, " +
        "no hashtag spam (at most 2 relevant hashtags), no financial promises or guarantees. " +
        "No preamble, no quotation marks, just the post text.";

    private const string NewsletterSystemPrompt =
        "You draft a newsletter for a financial/insurance advisor to send to their clients. " +
        "Write a compelling subject line and a warm, professional body of 300-500 words in 2-4 short paragraphs. " +
        "Format the body as simple HTML using only <p> and <strong> tags - no headings, images, links, or lists. " +
        "No financial promises or guarantees, no preamble. " +
        "Respond in exactly this format and nothing else:\n" +
        "SUBJECT: <subject line>\n" +
        "BODY:\n<html body>";

    public Task<AiActionReasonResult> GenerateActionReasonAsync(string situation, CancellationToken cancellationToken = default) =>
        CallAnthropicAsync(SystemPrompt, situation, 60, cancellationToken);

    public Task<AiActionReasonResult> DraftSocialPostAsync(string topic, CancellationToken cancellationToken = default) =>
        CallAnthropicAsync(SocialPostSystemPrompt, $"Topic: {topic}", 120, cancellationToken);

    private const string BlogPostSystemPrompt =
        "You draft a blog article for a financial/insurance advisor's public website. " +
        "Write an engaging title, a one-to-two sentence summary for the blog listing, and a body of 500-800 words. " +
        "Format the body as simple HTML using only <p>, <strong> and <h3> tags - no images, links, or lists. " +
        "Educational and professional in tone; the advisor will review and edit before publishing. " +
        "Never make financial promises or guarantees, never give advice specific to an individual, no preamble. " +
        "Respond in exactly this format and nothing else:\nTITLE: <title>\nSUMMARY: <summary>\nBODY:\n<html body>";

    // The blog draft rides the same client, auth and usage recording as the newsletter draft; only
    // the prompt and the parse differ. Drafts are ALWAYS reviewed by the agent before publishing --
    // the button fills the editor, it never publishes -- because the authors are regulated advisers
    // and the author of record has to be the human.
    public async Task<AiBlogPostDraftResult> DraftBlogPostAsync(string topic, CancellationToken cancellationToken = default)
    {
        var result = await CallAnthropicAsync(BlogPostSystemPrompt, $"Topic: {topic}", 1400, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Reason))
        {
            return new AiBlogPostDraftResult(null, null, null, result.InputTokens, result.OutputTokens);
        }
        return ParseBlogDraft(result.Reason, result.InputTokens, result.OutputTokens);
    }

    // Internal for tests: the HTTP client above has no seam, so the parse -- the part that can
    // actually be wrong in interesting ways -- is proven directly. This is its only call site.
    internal static AiBlogPostDraftResult ParseBlogDraft(string text, int inputTokens, int outputTokens)
    {
        var bodyIndex = text.IndexOf("BODY:", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex < 0)
        {
            // The model ignored the format: hand the whole text to the agent as body to salvage.
            return new AiBlogPostDraftResult(null, null, text, inputTokens, outputTokens);
        }

        var head = text[..bodyIndex];
        var body = text[(bodyIndex + "BODY:".Length)..].Trim();

        var summaryIndex = head.IndexOf("SUMMARY:", StringComparison.OrdinalIgnoreCase);
        string? summary = null;
        if (summaryIndex >= 0)
        {
            summary = head[(summaryIndex + "SUMMARY:".Length)..].Trim();
            head = head[..summaryIndex];
        }

        var titleIndex = head.IndexOf("TITLE:", StringComparison.OrdinalIgnoreCase);
        var title = titleIndex >= 0 ? head[(titleIndex + "TITLE:".Length)..].Trim() : head.Trim();

        return new AiBlogPostDraftResult(
            string.IsNullOrWhiteSpace(title) ? null : title,
            summary, body, inputTokens, outputTokens);
    }

    public async Task<AiNewsletterDraftResult> DraftNewsletterAsync(string topic, CancellationToken cancellationToken = default)
    {
        var result = await CallAnthropicAsync(NewsletterSystemPrompt, $"Topic: {topic}", 900, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Reason))
        {
            return new AiNewsletterDraftResult(null, null, result.InputTokens, result.OutputTokens);
        }

        var text = result.Reason;
        var bodyIndex = text.IndexOf("BODY:", StringComparison.OrdinalIgnoreCase);
        if (bodyIndex < 0)
        {
            return new AiNewsletterDraftResult(null, text, result.InputTokens, result.OutputTokens);
        }

        var subjectPart = text[..bodyIndex];
        var subjectIndex = subjectPart.IndexOf("SUBJECT:", StringComparison.OrdinalIgnoreCase);
        var subject = subjectIndex >= 0
            ? subjectPart[(subjectIndex + "SUBJECT:".Length)..].Trim()
            : subjectPart.Trim();
        var body = text[(bodyIndex + "BODY:".Length)..].Trim();

        return new AiNewsletterDraftResult(subject, body, result.InputTokens, result.OutputTokens);
    }

    private async Task<AiActionReasonResult> CallAnthropicAsync(string systemPrompt, string userMessage, int maxTokens, CancellationToken cancellationToken)
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
                max_tokens = maxTokens,
                system = systemPrompt,
                messages = new[] { new { role = "user", content = userMessage } }
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
