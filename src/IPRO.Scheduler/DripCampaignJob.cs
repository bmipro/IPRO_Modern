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
    private readonly ILogger<DripCampaignJob> _logger;

    public DripCampaignJob(IUnitOfWork uow, IPRODbContext db, NewsLetterDispatcher dispatcher, ILogger<DripCampaignJob> logger)
    {
        _uow = uow; _db = db; _dispatcher = dispatcher; _logger = logger;
    }

    public async Task RunAsync()
    {
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
                    continue;
                }

                var clientName = $"{enrollment.Client.FirstName} {enrollment.Client.LastName}".Trim();
                await _dispatcher.DispatchDripStepAsync(
                    enrollment.DripCampaignId,
                    enrollment.NextStepIndex,
                    enrollment.Client.Email,
                    string.IsNullOrWhiteSpace(clientName) ? enrollment.Client.Email : clientName);

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
            }
            catch (Exception ex)
            {
                enrollment.Status = DripCampaignEnrollmentStatus.Failed;
                enrollment.LastError = ex.Message;
                _logger.LogError(ex, "Drip campaign enrollment {EnrollmentId} failed", enrollment.Id);
            }
        }

        await _db.SaveChangesAsync();
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
