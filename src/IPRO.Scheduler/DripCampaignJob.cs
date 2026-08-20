using IPRO.Business.Services;
using IPRO.DataAccess.Repositories;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class DripCampaignJob
{
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;
    private readonly NewsLetterDispatcher _dispatcher;
    private readonly IEmailConsentService _consent;
    private readonly ILogger<DripCampaignJob> _logger;

    public DripCampaignJob(IUnitOfWork uow, IPRODbContext db, NewsLetterDispatcher dispatcher, IEmailConsentService consent, ILogger<DripCampaignJob> logger)
    {
        _uow = uow; _db = db; _dispatcher = dispatcher; _consent = consent; _logger = logger;
    }

    public async Task RunAsync()
    {
        // Truth first (JOBS-1): cancel enrollments of clients who are already suppressed, however
        // long ago that happened, so the campaign screens stop showing Active rows that will never
        // mail. The per-send IsSuppressed check below stays -- it catches an opt-out that lands
        // between this sweep and the send.
        var swept = await _consent.CancelSuppressedDripEnrollmentsAsync();
        if (swept > 0)
        {
            _logger.LogInformation("Drip sweep cancelled {Count} enrollment(s) of unsubscribed clients.", swept);
        }

        await ProcessEnrollmentsAsync();
        await ProcessLegacySchedulerRowsAsync();
    }

    private async Task ProcessEnrollmentsAsync()
    {
        var dueEnrollments = await _db.DripCampaignEnrollments
            .Include(e => e.Client)
            .Include(e => e.DripCampaign)
            .Where(e => e.Status == DripCampaignEnrollmentStatus.Active &&
                        e.NextSendAt <= DateTime.UtcNow &&
                        e.DripCampaign.IsActive)
            .OrderBy(e => e.NextSendAt)
            .Take(100)
            .ToListAsync();

        foreach (var enrollment in dueEnrollments)
        {
            try
            {
                var steps = await _db.DripCampaignSteps
                    .Where(s => s.DripCampaignId == enrollment.DripCampaignId)
                    .OrderBy(s => s.SortOrder)
                    .ToListAsync();

                if (enrollment.NextStepIndex >= steps.Count)
                {
                    enrollment.Status = DripCampaignEnrollmentStatus.Completed;
                    enrollment.CompletedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    continue;
                }

                // Drip was the one outbound path that never asked. There was no EmailChannel member
                // for it, so the omission was invisible to anyone reading EmailConsentService, and
                // an unsubscribed client kept receiving every remaining step -- the most plausible
                // in-product source of the spam complaints behind the shared-IP reputation problem
                // (2026-08-14 ultra-audit). Cancelled, not Failed: they asked to stop, this is not
                // an error to retry.
                if (_consent.IsSuppressed(enrollment.Client, EmailChannel.DripCampaign))
                {
                    enrollment.Status = DripCampaignEnrollmentStatus.Cancelled;
                    enrollment.LastError = "Client has unsubscribed; enrollment cancelled.";
                    await _db.SaveChangesAsync();
                    _logger.LogInformation(
                        "Drip enrollment {EnrollmentId} cancelled: client {ClientId} has opted out.",
                        enrollment.Id, enrollment.ClientId);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(enrollment.UnsubscribeToken))
                {
                    enrollment.UnsubscribeToken = Guid.NewGuid().ToString("N");
                }

                var clientName = $"{enrollment.Client.FirstName} {enrollment.Client.LastName}".Trim();
                var sendResult = await _dispatcher.DispatchDripStepAsync(
                    enrollment.DripCampaignId,
                    enrollment.NextStepIndex,
                    enrollment.Client.Email,
                    string.IsNullOrWhiteSpace(clientName) ? enrollment.Client.Email : clientName,
                    enrollment.UnsubscribeToken,
                    enrollment.Id);

                // JOBS-7 (fixed 2026-08-20): a failed send must not advance the enrollment -- the
                // client silently skipped the step while the error was blanked. JOBS-8: a
                // transient failure retries on later ticks instead of killing the campaign; the
                // attempt counter stops a permanent problem from retrying forever.
                if (sendResult != null && !sendResult.Success)
                {
                    HandleSendFailure(enrollment, sendResult.IsTransient, sendResult.Message);
                    await _db.SaveChangesAsync();
                    continue;
                }

                enrollment.SendAttempts = 0;
                enrollment.LastSentAt = DateTime.UtcNow;
                enrollment.LastError = string.Empty;
                enrollment.NextStepIndex++;

                if (enrollment.NextStepIndex >= steps.Count)
                {
                    enrollment.Status = DripCampaignEnrollmentStatus.Completed;
                    enrollment.CompletedAt = DateTime.UtcNow;
                }
                else
                {
                    var nextStep = steps[enrollment.NextStepIndex];
                    enrollment.NextSendAt = DateTime.UtcNow.AddDays(Math.Max(0, nextStep.DelayDays));
                }

                // Persist THIS enrollment's advance immediately. The single SaveChangesAsync after
                // the loop meant a transient failure at the end discarded every advance in the
                // batch, and Hangfire's default retry (up to 10 attempts) then re-sent up to 100
                // steps to clients who had already received them. DidYouKnowEmailDispatchJob fixed
                // this shape for itself; its neighbours never got the same treatment.
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // JOBS-8 (fixed 2026-08-20): an unexpected exception used to mark the enrollment
                // Failed PERMANENTLY on the first occurrence -- one hiccup and that client's
                // campaign stopped forever, with no retry and no screen to notice it on.
                // Exceptions get the same capped transient retry as a transient send failure.
                HandleSendFailure(enrollment, transient: true, ex.Message);
                _logger.LogError(ex, "Drip campaign enrollment {EnrollmentId} send attempt failed (attempt {Attempts})", enrollment.Id, enrollment.SendAttempts);
                await _db.SaveChangesAsync();
            }
        }
    }

    private const int MaxSendAttempts = 5;

    internal static void HandleSendFailure(IPRO.Entities.DripCampaignEnrollment enrollment, bool transient, string message)
    {
        enrollment.LastError = message;
        if (!transient)
        {
            // SendGrid answered no (bad address, rejected payload): retrying would be spam.
            enrollment.Status = DripCampaignEnrollmentStatus.Failed;
            return;
        }
        enrollment.SendAttempts++;
        if (enrollment.SendAttempts >= MaxSendAttempts)
        {
            enrollment.Status = DripCampaignEnrollmentStatus.Failed;
            enrollment.LastError = $"Gave up after {MaxSendAttempts} attempts. Last error: {message}";
        }
        // Otherwise stay Active: NextSendAt is already due, so the next hourly tick retries.
    }

    private async Task ProcessLegacySchedulerRowsAsync()
    {
        var pending = await _uow.Schedulers.FindAsync(s =>
            s.Type == SchedulerType.DripCampaign &&
            s.Status == SchedulerStatus.Pending &&
            s.ScheduledAt <= DateTime.UtcNow);

        foreach (var task in pending)
        {
            try
            {
                var agent = await _uow.AgentUsers.GetByIdAsync(task.AgentUserId);
                if (agent == null) continue;
                await _dispatcher.DispatchDripStepAsync(task.ReferenceId, 0, agent.Email, $"{agent.FirstName} {agent.LastName}");
                task.Status = SchedulerStatus.Completed;
                task.ExecutedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                task.Status = SchedulerStatus.Failed;
                task.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Drip job failed for scheduler {Id}", task.Id);
            }
            _uow.Schedulers.Update(task);
        }
        await _uow.SaveChangesAsync();
    }
}
