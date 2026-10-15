using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Caching.Memory;

namespace IPRO.Web.Infrastructure;

/// <summary>
/// 578: a public form the server turned down (wrong security answer, a missing field) goes back to the
/// visitor as they left it, with the message on that form only and each problem beside its field
/// (WCAG 3.3.1, 3.3.3 and 3.3.7). It used to come back empty, on every form of the page at once.
/// What was typed stays on the server for 15 minutes; the redirect carries only a token in TempData.
/// The security answer and the hidden spam fields are never kept, and each value is cut to its field's
/// own limit. The forms are kept in a store of their own with a fixed size: a turned-down post costs the
/// server no database work, so a flood of them must not be able to fill its memory. The message and the
/// fields to fix also travel in TempData (they are short), so when the store is full, or the server has
/// restarted, a turned-down form still says what to fix; it only comes back empty, as it always did.
/// Only the first matching form of a page takes it (one message, one place for focus to go).
/// </summary>
public sealed class PublicFormRetry
{
    public const string TempDataKey = "PublicFormRetry";
    public const string NoteKey = "PublicFormRetryNote";
    private const string ShownKey = "PublicFormRetryShown";
    public const string TestimonialForm = "testimonial";
    private const string CachePrefix = "public-form-retry:";
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(15);
    /// <summary>About 8 MB of text: some 400 contact forms, or 60 long custom forms, at one time.</summary>
    public const long StoreCharacters = 4_000_000;
    private static readonly MemoryCache Store = new(new MemoryCacheOptions { SizeLimit = StoreCharacters });

    public sealed record FieldProblem(string Field, string Label, string Text);

    /// <summary>The message box drawn above a turned-down form (Views/PublicWebsite/_PublicFormProblems);
    /// <paramref name="HidePanelId"/> is a panel the form replaces, hidden again (Did you know?'s cards).</summary>
    public sealed record Box(PublicFormRetry Retry, string FormId, string? HidePanelId);

    /// <summary>Which form on the page this is (see <see cref="LeadForm"/>, <see cref="CustomForm"/>).</summary>
    public string Form { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool Consent { get; init; }
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    /// <summary>A custom form's answers by field id; a checkbox question can have several.</summary>
    public Dictionary<int, List<string>> Answers { get; } = new();
    public List<FieldProblem> Problems { get; } = new();

    public static string LeadForm(string submissionType, int? blockId) =>
        $"lead:{(submissionType ?? string.Empty).ToLowerInvariant()}:{blockId}";

    /// <summary>A lead form's name, the same on the way in (the post) and out (the view).</summary>
    public static string LeadForm(Models.WebsiteLeadFormViewModel form, string submissionType) =>
        LeadForm(submissionType, form.BlockId
            ?? (submissionType == IPRO.Entities.WebsiteLeadTypes.LeadMagnet ? form.LeadMagnetBlockId
                : submissionType == IPRO.Entities.WebsiteLeadTypes.DidYouKnow ? form.DidYouKnowBlockId
                : null));

    public static string CustomForm(int websiteFormId) => $"form:{websiteFormId}";

    public static string AnswerField(int fieldId) => $"answer-{fieldId}";

    public PublicFormRetry Keep(string field, string? value, int maxLength)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length > 0) Values[field] = text.Length > maxLength ? text[..maxLength] : text;
        return this;
    }

    public PublicFormRetry Problem(string field, string label, string text)
    {
        if (Problems.All(p => p.Field != field)) Problems.Add(new FieldProblem(field, label, text));
        return this;
    }

    public string? Value(string field) => Values.TryGetValue(field, out var value) ? value : null;

    public string? Answer(int fieldId) => Answers.TryGetValue(fieldId, out var values) ? values.FirstOrDefault() : null;

    public bool Chose(int fieldId, string option) => Answers.TryGetValue(fieldId, out var values) && values.Contains(option, StringComparer.Ordinal);

    public FieldProblem? ProblemWith(string field) => Problems.FirstOrDefault(p => p.Field == field);

    // For the views: attribute values that Razor leaves out when they are null.
    public static string? Invalid(PublicFormRetry? retry, string field) => retry?.ProblemWith(field) != null ? "true" : null;

    public static string? DescribedBy(PublicFormRetry? retry, string formId, string field, string? helpId = null)
    {
        var ids = new List<string>();
        if (helpId != null) ids.Add(helpId);
        if (retry?.ProblemWith(field) != null) ids.Add(ErrorId(formId, field));
        return ids.Count == 0 ? null : string.Join(' ', ids);
    }

    public static string ErrorId(string formId, string field) => $"{formId}-{field}-error";

    public void Save(ITempDataDictionary tempData) => Save(tempData, Store);

    private sealed record Note(string Form, string Message, List<FieldProblem> Problems);

    // The store is a parameter for the tests, which try a small one.
    public void Save(ITempDataDictionary tempData, MemoryCache store)
    {
        var key = CachePrefix + Guid.NewGuid().ToString("N");
        // A size-limited cache turns away an entry that does not fit. Then the oldest tenth goes (the least
        // likely still to be read) and, if it still does not fit, the form keeps only its message.
        if (!Put(store, key, this))
        {
            store.Compact(0.1);
            if (!Put(store, key, this))
            {
                var bare = new PublicFormRetry { Form = Form, Message = Message };
                bare.Problems.AddRange(Problems);
                Put(store, key, bare);
            }
        }
        tempData[TempDataKey] = key[CachePrefix.Length..];
        tempData[NoteKey] = JsonSerializer.Serialize(new Note(Form, Message, Problems.Take(30)
            .Select(p => p with { Label = p.Label.Length > 120 ? p.Label[..120] : p.Label }).ToList()));
    }

    /// <summary>
    /// The kept form, when the one a view is about to draw is the form that was turned down: in full from
    /// the store, or its message and fields to fix from TempData when the store no longer has it.
    /// <paramref name="request"/> (HttpContext.Items) lets only the first matching form of a page take it.
    /// </summary>
    public static PublicFormRetry? Find(ITempDataDictionary tempData, string form, IDictionary<object, object?>? request = null, MemoryCache? store = null)
    {
        // Both keys are read here, every time: a key left unread would ride on to the next page and bring
        // the message back after the form had been sent.
        var token = tempData[TempDataKey] as string;
        var note = tempData[NoteKey] as string;
        if (request != null && request.ContainsKey(ShownKey)) return null;
        var kept = !string.IsNullOrEmpty(token) && (store ?? Store).TryGetValue(CachePrefix + token, out PublicFormRetry? stored)
            ? stored
            : FromNote(note);
        if (kept?.Form != form) return null;
        if (request != null) request[ShownKey] = true;
        return kept;
    }

    private static PublicFormRetry? FromNote(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var note = JsonSerializer.Deserialize<Note>(json);
            if (note == null) return null;
            var retry = new PublicFormRetry { Form = note.Form ?? string.Empty, Message = note.Message ?? string.Empty };
            retry.Problems.AddRange(note.Problems ?? new List<FieldProblem>());
            return retry;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Put(MemoryCache store, string key, PublicFormRetry retry)
    {
        store.Set(key, retry, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = KeptFor, Size = retry.Characters() });
        return store.TryGetValue(key, out _);
    }

    private long Characters() => 100 + Form.Length + Message.Length
        + Values.Sum(v => v.Key.Length + v.Value.Length)
        + Answers.Sum(a => 10 + a.Value.Sum(v => v.Length + 10))
        + Problems.Sum(p => p.Field.Length + p.Label.Length + p.Text.Length);
}
