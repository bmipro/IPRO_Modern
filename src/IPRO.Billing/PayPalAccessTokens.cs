namespace IPRO.Billing;

// 630 (582's item 4): one PayPal access token per app, kept until shortly before PayPal says it expires.
// Every PayPal call used to fetch a fresh token first: the hourly reconcile asked for one per subscription,
// and every notice posted to /billing/webhook (anyone can post one) made iPro ask PayPal for a token before
// the signature check. Registered as a singleton in both apps; a service built without one (tests) keeps
// its own, so nothing is shared between test cases.
public sealed class PayPalAccessTokens
{
    // Asked again this long before PayPal's own expiry, so a token never runs out mid-request.
    internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private sealed record Entry(string Key, string Token, DateTime RenewAtUtc);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTime> _utcNow;
    private volatile Entry? _entry;

    public PayPalAccessTokens() : this(() => DateTime.UtcNow) { }

    internal PayPalAccessTokens(Func<DateTime> utcNow) => _utcNow = utcNow;

    // key: the PayPal address and client id, so a sandbox token is never sent to live PayPal or the reverse.
    // fetch: asks PayPal for a new token; returns it with PayPal's expires_in (seconds).
    public async Task<string> GetAsync(string key, Func<Task<(string Token, int ExpiresInSeconds)>> fetch)
    {
        if (Current(key) is { } token) return token;

        await _gate.WaitAsync();
        try
        {
            if (Current(key) is { } fresh) return fresh;

            var (newToken, expiresIn) = await fetch();
            if (string.IsNullOrEmpty(newToken)) return newToken;

            // PayPal gives about nine hours. A short or missing expires_in keeps the token for a minute at most.
            var keepFor = TimeSpan.FromSeconds(Math.Max(expiresIn, 0)) - RenewBefore;
            if (keepFor <= TimeSpan.Zero) keepFor = TimeSpan.FromSeconds(Math.Clamp(expiresIn, 0, 60));

            _entry = new Entry(key, newToken, _utcNow() + keepFor);
            return newToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Current(string key)
    {
        var entry = _entry;
        return entry != null && entry.Key == key && _utcNow() < entry.RenewAtUtc ? entry.Token : null;
    }
}
