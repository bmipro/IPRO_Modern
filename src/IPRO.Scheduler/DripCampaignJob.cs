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

    // Hourly: every due enrollment, each taken under a claim so an overlapping run -- or the
    // one-off run enqueued at enrolment -- cannot mail it too. (TODO 448.)
    private async Task ProcessEnrollmentsAsync()
    {
        var now = DateTime.UtcNow;
        var exhausted = await DripEnrollmentClaims.FailExhaustedAsync(_db, now);
        if (exhausted > 0)
        {
            _logger.LogWarning("Drip: {Count} enrollment(s) stopped after repeated interrupted processing.", exhausted);
        }

        var dueIds = await DripEnrollmentClaims.Due(_db, now)
            .OrderBy(e => e.NextSendAt)
            .Take(100)
            .Select(e => e.Id)
            .ToListAsync();

        foreach (var id in dueIds)
        {
            if (!await ProcessEnrollmentAsync(id))
            {
                // Failure bookkeeping itself could not be saved: the tracker is cleared and the
                // batch stops. The remaining enrollments run next tick.
                break;
            }
        }
    }

    // One enrollment, right now. Enqueued by the enrol action so "send immediately" is true rather
    // than "on the next hourly tick". The claim makes this safe against the hourly run; a step that
    // is not yet due is simply refused by the claim and nothing happens.
    [Hangfire.Queue("drip")]
    public async Task RunEnrollmentAsync(int enrollmentId)
    {
        await ProcessEnrollmentAsync(enrollmentId);
    }

    // CLAIM FIRST, LOAD SECOND. Returns false only when the failure bookkeeping could not be
    // persisted -- the one case where the caller must stop its batch. The claim is released as
    // soon as an outcome (a send, or its failure) is on disk; if that never happens the claim goes
    // stale, is taken over with the attempt counter bumped, and after MaxAttempts the enrollment is
    // named Failed by the next hourly run rather than silently excluded forever.
    private async Task<bool> ProcessEnrollmentAsync(int enrollmentId)
    {
        var now = DateTime.UtcNow;
        var held = await DripEnrollmentClaims.TryClaimAsync(_db, enrollmentId, now);
        if (held == null) return true;

        SendClaims.ForgetTracked<DripCampaignEnrollment>(_db, enrollmentId);
        var enrollment = await _db.DripCampaignEnrollments
            .Include(e => e.Client)
            .Include(e => e.DripCampaign)
            .FirstOrDefaultAsync(e => e.Id == enrollmentId);
        if (enrollment == null)
        {
            return true;
        }

        var persisted = false;
        try
        {
            if (!enrollment.DripCampaign.IsActive)
            {
                // Paused between enrolment and this run. Nothing to do; the claim is released below
                // and the hourly run's Due() filter keeps it off the batch until the campaign resumes.
                persisted = true;
                return true;
            }

            var steps = await _db.DripCampaignSteps
                .Where(s => s.DripCampaignId == enrollment.DripCampaignId)
                .OrderBy(s => s.SortOrder)
                .ToListAsync();

            if (enrollment.NextStepIndex >= steps.Count)
            {
                enrollment.Status = DripCampaignEnrollmentStatus.Completed;
                enrollment.CompletedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                persisted = true;
                return true;
            }

            if (_consent.IsSuppressed(enrollment.Client, EmailChannel.DripCampaign))
            {
                enrollment.Status = DripCampaignEnrollmentStatus.Cancelled;
                enrollment.CancelledAt = DateTime.UtcNow;   // M12: the CASL "when did we stop" answer
                enrollment.LastError = "Client has unsubscribed; enrollment cancelled.";
                await _db.SaveChangesAsync();
                persisted = true;
                _logger.LogInformation(
                    "Drip enrollment {EnrollmentId} cancelled: client {ClientId} has opted out.",
                    enrollment.Id, enrollment.ClientId);
                return true;
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

            if (sendResult == null)
            {
                HandleSendFailure(enrollment, transient: true, "Dispatcher had nothing to send for this step.");
                await _db.SaveChangesAsync();
                persisted = true;
                return true;
            }

            if (!sendResult.Success)
            {
                HandleSendFailure(enrollment, sendResult.IsTransient, sendResult.Message);
                await _db.SaveChangesAsync();
                persisted = true;
                return true;
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

            // Persist THIS enrollment's advance before moving on: a crash after the send but before
            // the save would otherwise re-mail this step on the next run.
            await _db.SaveChangesAsync();
            persisted = true;
            return true;
        }
        catch (Exception ex)
        {
            HandleSendFailure(enrollment, transient: true, ex.Message);
            _logger.LogError(ex, "Drip campaign enrollment {EnrollmentId} send attempt failed (attempt {Attempts})", enrollment.Id, enrollment.SendAttempts);
            try
            {
                await _db.SaveChangesAsync();
                persisted = true;
                return true;
            }
            catch (Exception saveEx)
            {
                _db.ChangeTracker.Clear();
                _logger.LogError(saveEx,
                    "Could not persist the failure bookkeeping for drip enrollment {EnrollmentId}; tracker cleared and the batch stopped -- the remaining enrollments run next tick.",
                    enrollment.Id);
                return false;
            }
        }
        finally
        {
            if (persisted)
            {
                await DripEnrollmentClaims.ReleaseAsync(_db, enrollmentId, held.Value, resetAttempts: true);
            }
        }
    }

    private const int MaxSendAttempts = 5;

    // H13: LastError is varchar(1000) (IPRODbContext HasMaxLength). ex.Message is unbounded --
    // an EF inner-exception chain or a SendGrid response body easily exceeds 1000 chars, and an
    // untruncated write made the CATCH's own SaveChangesAsync throw "Data too long", aborting
    // the whole job; Hangfire then retried the batch with the same poison row at its head.
    private const int LastErrorMaxLength = 1000;

    private static string Truncate(string message) =>
        message.Length <= LastErrorMaxLength ? message : message[..LastErrorMaxLength];

    internal static void HandleSendFailure(IPRO.Entities.DripCampaignEnrollment enrollment, bool transient, string message)
    {
        enrollment.LastError = Truncate(message);
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
            enrollment.LastError = Truncate($"Gave up after {MaxSendAttempts} attempts. Last error: {message}");
            return;
        }
        // H13: back off instead of staying due. A still-due failing row sat at position 1 of
        // EVERY batch (the query orders by NextSendAt), so one poison enrollment ate the head of
        // each hourly run until its cap engaged. One hour per attempt already made keeps the
        // pacing aligned with the hourly tick while pushing the row behind healthy ones.
        enrollment.NextSendAt = DateTime.UtcNow.AddHours(enrollment.SendAttempts);
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
