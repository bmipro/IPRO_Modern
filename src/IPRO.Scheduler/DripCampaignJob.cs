using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

public class DripCampaignJob
{
    private readonly IUnitOfWork _uow;
    private readonly NewsLetterDispatcher _dispatcher;
    private readonly ILogger<DripCampaignJob> _logger;

    public DripCampaignJob(IUnitOfWork uow, NewsLetterDispatcher dispatcher, ILogger<DripCampaignJob> logger)
    {
        _uow = uow; _dispatcher = dispatcher; _logger = logger;
    }

    public async Task RunAsync()
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
