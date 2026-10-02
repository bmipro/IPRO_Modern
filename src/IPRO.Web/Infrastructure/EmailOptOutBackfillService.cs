using IPRO.Business.Services;
using IPRO.DataAccess;

namespace IPRO.Web.Infrastructure;

// 540: runs EmailOptOutBackfill once, a few minutes after the web app starts -- after the schema repair
// that adds Clients.EmailOptOutSource, and a minute after the gallery backfill so the two do not share
// their first minute. Never on a bystander instance (Jobs__RecurringDisabled). Every suppression made
// since 538 has its reason, so what this looks at only ever shrinks; a run that finds nothing is one
// query. A failure is logged and never stops the app: the clients it did not reach read "Unsubscribed",
// exactly as before.
public sealed class EmailOptOutBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EmailOptOutBackfillService> _logger;

    public EmailOptOutBackfillService(IServiceScopeFactory scopes, ILogger<EmailOptOutBackfillService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IPRODbContext>();
            var consent = scope.ServiceProvider.GetRequiredService<IEmailConsentService>();
            await EmailOptOutBackfill.RunAsync(db, consent, _logger, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The suppression-reason backfill stopped; the clients it did not reach still read Unsubscribed and are tried again on the next start.");
        }
    }
}
