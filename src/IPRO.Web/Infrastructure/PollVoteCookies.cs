namespace IPRO.Web.Infrastructure;

// 450: one vote per browser per poll. A cookie is the honest tool here -- it stops the casual
// double click and the "vote again" refresh; it is not a defence against someone determined,
// which is what the per-survey hourly cap and the anonymity floor are for.
public static class PollVoteCookies
{
    public const string Name = "ipro_poll_voted";

    public static HashSet<int> Read(HttpRequest request)
    {
        var set = new HashSet<int>();
        var raw = request.Cookies[Name];
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out var id)) set.Add(id);
        return set;
    }

    public static void Append(HttpRequest request, HttpResponse response, int surveyId)
    {
        var set = Read(request);
        set.Add(surveyId);
        response.Cookies.Append(Name, string.Join(",", set.OrderBy(i => i).Take(200)), new CookieOptions
        {
            HttpOnly = true,
            Secure = request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            Path = "/"
        });
    }
}
