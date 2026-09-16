using Microsoft.Extensions.Options;

namespace IPRO.Email;

// 491 (2026-09-16): the platform paces itself to Azure Communication Services' sending limits.
//
// Why: on the default limits a subscription may send 30 messages a minute and 100 an hour. Microsoft
// declined to raise them on 2026-09-16 ("insufficient sending history"; re-apply with 30 days of
// it), and the launch is on the 21st. Until then a newsletter to 150 clients would have delivered
// 30 in the first minute and had the other 120 rejected with 429 -- and the four blast loops wrote
// those rejections down as Failed, permanently. This object is the first line: every Azure send
// waits here for a slot, so a blast is slowed to the cap instead of being rejected by it. The
// dispatchers' pause path (PauseForRetryAsync) is the second line for a 429 that still gets through.
//
// How: one instance per process (a singleton; IPRO.Web is the only process that sends). It keeps
// the UTC time of every send in the last hour and, before each send, checks the last minute and the
// last hour against the limits. When a window is full it waits until the oldest send in that window
// has aged out, then records the new send and lets it go. A small reserve is kept out of reach of
// bulk mail so transactional messages -- a sign-in code, an invoice, a portal invite -- never queue
// behind a newsletter for an hour. ReportThrottled holds everything for the provider's Retry-After
// when a 429 arrives anyway (a limit that moved, another process). The limits are settings
// (Email__SendsPerMinute etc.), so the day Microsoft relents is a config change on both apps.
//
// The clock and the delay are injectable so the tests drive it in fake time; production uses
// DateTime.UtcNow and Task.Delay.
public sealed class EmailSendGate
{
    // The "ipro_entity" tag the six marketing dispatchers put on their sends. Everything untagged or
    // tagged otherwise (invoices, portal invites, sign-in mail, support) is transactional.
    private static readonly HashSet<string> BulkEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        "newsletter", "drip_step", "ecard", "eletter", "poll", "didyouknow"
    };

    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _lock = new(1, 1);
    // UTC send times within the last hour, oldest first. Trimmed on every visit.
    private readonly List<DateTime> _sends = new();
    private DateTime _holdUntil = DateTime.MinValue;

    public EmailSendGate(IOptions<EmailSettings> settings)
        : this(settings.Value.SendsPerMinute, settings.Value.SendsPerHour,
               settings.Value.TransactionalReservePerMinute, settings.Value.TransactionalReservePerHour,
               () => DateTime.UtcNow, Task.Delay)
    {
    }

    public EmailSendGate(int sendsPerMinute, int sendsPerHour, int reservePerMinute, int reservePerHour,
        Func<DateTime> now, Func<TimeSpan, CancellationToken, Task> delay)
    {
        SendsPerMinute = Math.Max(1, sendsPerMinute);
        SendsPerHour = Math.Max(1, sendsPerHour);
        // A reserve can never swallow the whole window: bulk mail always keeps at least one slot.
        TransactionalReservePerMinute = Math.Clamp(reservePerMinute, 0, SendsPerMinute - 1);
        TransactionalReservePerHour = Math.Clamp(reservePerHour, 0, SendsPerHour - 1);
        _now = now;
        _delay = delay;
    }

    public int SendsPerMinute { get; }
    public int SendsPerHour { get; }
    public int TransactionalReservePerMinute { get; }
    public int TransactionalReservePerHour { get; }

    public static bool IsBulk(IDictionary<string, string>? customArgs) =>
        customArgs != null
        && customArgs.TryGetValue("ipro_entity", out var entity)
        && !string.IsNullOrWhiteSpace(entity)
        && BulkEntities.Contains(entity.Trim());

    // The provider said 429 despite the pacing. Hold every send for as long as it asked, a minute
    // if it did not say; the hold only ever moves later, never earlier.
    public void ReportThrottled(TimeSpan? retryAfter)
    {
        var hold = retryAfter is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromSeconds(60);
        var until = _now() + hold;
        lock (_sends)
        {
            if (until > _holdUntil) _holdUntil = until;
        }
    }

    // Returns once a slot in both windows is free and has been taken for this send.
    public async Task WaitForSlotAsync(bool bulk, CancellationToken ct = default)
    {
        while (true)
        {
            TimeSpan wait;
            await _lock.WaitAsync(ct);
            try
            {
                var now = _now();
                _sends.RemoveAll(t => t <= now.AddHours(-1));

                var minuteLimit = bulk ? SendsPerMinute - TransactionalReservePerMinute : SendsPerMinute;
                var hourLimit = bulk ? SendsPerHour - TransactionalReservePerHour : SendsPerHour;
                var inMinute = _sends.Count(t => t > now.AddMinutes(-1));

                wait = TimeSpan.Zero;
                lock (_sends)
                {
                    if (_holdUntil > now) wait = _holdUntil - now;
                }
                // The entries inside each window are the newest ones in the list; the window frees
                // when its (count - limit + 1)-th oldest entry ages out, which sits at index
                // count - limit from the end of that window.
                if (inMinute >= minuteLimit)
                {
                    var frees = _sends[_sends.Count - minuteLimit].AddMinutes(1) - now;
                    if (frees > wait) wait = frees;
                }
                if (_sends.Count >= hourLimit)
                {
                    var frees = _sends[_sends.Count - hourLimit].AddHours(1) - now;
                    if (frees > wait) wait = frees;
                }

                if (wait <= TimeSpan.Zero)
                {
                    _sends.Add(now);
                    return;
                }
            }
            finally
            {
                _lock.Release();
            }

            // A hair past the boundary so the aged-out entry is really out on the next look.
            await _delay(wait + TimeSpan.FromMilliseconds(20), ct);
        }
    }
}
