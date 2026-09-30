using System.Collections.Concurrent;
using System.Globalization;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPRO.Email;

// 531: one SES tenant per adviser. Under SES's Standard reputation policy a tenant whose mail bounces
// or draws complaints is paused on its own, so one adviser's bad list never stops another adviser's
// invoices -- what iPro told Amazon when it asked for production access. A tenant needs both sending
// addresses and both configuration sets associated before it can send; this does that once per
// adviser per process (SES answers "already exists" after a restart, which is fine).
//
// A tenant SES will not create (a missing permission, a service hiccup) never stops the email: it
// goes without a tenant, the failure is logged, and the next attempt waits ten minutes.
public sealed class SesTenants
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SesTenants> _logger;
    private readonly ConcurrentDictionary<int, bool> _ready = new();
    private readonly ConcurrentDictionary<int, DateTime> _retryAfter = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SesTenants(IOptions<EmailSettings> settings, ILogger<SesTenants> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public static string NameFor(int agentId) => $"adviser-{agentId.ToString(CultureInfo.InvariantCulture)}";

    public static IReadOnlyList<string> ResourceArns(SesSettings ses) => new[]
    {
        $"arn:aws:ses:{ses.Region}:{ses.AccountId}:identity/{ses.NotifyDomain}",
        $"arn:aws:ses:{ses.Region}:{ses.AccountId}:identity/{ses.NewsDomain}",
        $"arn:aws:ses:{ses.Region}:{ses.AccountId}:configuration-set/{ses.NotifyConfigurationSet}",
        $"arn:aws:ses:{ses.Region}:{ses.AccountId}:configuration-set/{ses.NewsConfigurationSet}",
    };

    // The tenant to send under, or null to send without one.
    public async Task<string?> EnsureAsync(IAmazonSimpleEmailServiceV2 client, int agentId, CancellationToken cancellationToken = default)
    {
        var ses = _settings.Ses;
        if (!ses.UseTenants || agentId <= 0 || string.IsNullOrWhiteSpace(ses.AccountId)) return null;

        var name = NameFor(agentId);
        if (_ready.ContainsKey(agentId)) return name;
        if (_retryAfter.TryGetValue(agentId, out var until) && until > DateTime.UtcNow) return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_ready.ContainsKey(agentId)) return name;

            try
            {
                await client.CreateTenantAsync(new CreateTenantRequest
                {
                    TenantName = name,
                    Tags = new List<Tag> { new() { Key = "agent_user_id", Value = agentId.ToString(CultureInfo.InvariantCulture) } }
                }, cancellationToken);
            }
            catch (AlreadyExistsException)
            {
                // Made before this process started.
            }

            foreach (var arn in ResourceArns(ses))
            {
                try
                {
                    await client.CreateTenantResourceAssociationAsync(
                        new CreateTenantResourceAssociationRequest { TenantName = name, ResourceArn = arn }, cancellationToken);
                }
                catch (Exception ex) when (ex is AlreadyExistsException or ConflictException)
                {
                    // Associated before this process started.
                }
            }

            _ready[agentId] = true;
            _retryAfter.TryRemove(agentId, out _);
            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _retryAfter[agentId] = DateTime.UtcNow.AddMinutes(10);
            _logger.LogWarning(ex, "SES tenant {Tenant} could not be set up; this adviser's email goes without a tenant for now.", name);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}

// 531: the account's maximum send rate (14 a second), kept across the whole process. A newsletter loop
// sends one after another; this spaces them so SES never has to refuse one. A throttle SES reports
// anyway holds every send for a moment.
public sealed class SesPacer
{
    private readonly EmailSettings _settings;
    private readonly object _lock = new();
    private DateTime _next = DateTime.MinValue;

    public SesPacer(IOptions<EmailSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task WaitTurnAsync(CancellationToken cancellationToken = default)
    {
        TimeSpan wait;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _settings.Ses.SendsPerSecond));
            var slot = _next > now ? _next : now;
            _next = slot + interval;
            wait = slot - now;
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
    }

    public void Hold(TimeSpan duration)
    {
        lock (_lock)
        {
            var until = DateTime.UtcNow + duration;
            if (until > _next) _next = until;
        }
    }
}
